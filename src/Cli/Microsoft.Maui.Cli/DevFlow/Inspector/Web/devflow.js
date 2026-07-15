// DevFlow Web Inspector — Interaction Script
// Intercepts browser events and proxies them to the native app via the inspector server.
(function () {
  'use strict';

  const viewport = document.getElementById('app-viewport');
  const screenshot = document.getElementById('screenshot');
  // Fit-to-container scaling (responsive). #app-viewport holds the app at its fixed logical size;
  // #df-stage is sized to the scaled box and #app-viewport carries the CSS scale transform.
  const stage = document.getElementById('df-stage');
  const vpWrap = document.getElementById('df-viewport-wrap');
  let fitMode = true;   // default: scale down to fit (never upscale past 1:1)
  let scaleRaf = 0;
  let rootOffsetX = parseFloat(viewport.dataset.rootOffsetX) || 0;
  let rootOffsetY = parseFloat(viewport.dataset.rootOffsetY) || 0;

  // Determine base path for API calls (handles being served under /inspector/{id}/)
  const basePath = location.pathname.replace(/\/$/, '');
  // Per-inspector read token (N2 data tabs) injected into the page by InspectorServer. Same-origin
  // only — a cross-origin page can't set this custom header without a preflight the broker refuses.
  const inspectorToken = (document.querySelector('meta[name="devflow-inspector-token"]') || {}).content || '';
  function metaContent(name) {
    const meta = document.querySelector(`meta[name="${name}"]`);
    return meta && typeof meta.content === 'string' && meta.content ? meta.content : null;
  }
  const inspectorAgent = Object.freeze({
    id: metaContent('devflow-agent-id'),
    appName: metaContent('devflow-app-name'),
    platform: metaContent('devflow-platform'),
    port: Number(metaContent('devflow-agent-port')) || null,
  });
  // N4: a per-tab writer token identifies this session for the single-writer lock. A global fetch
  // wrapper stamps it on every same-origin /api/ call and flips to read-only on a writer 409.
  const writerToken = (window.crypto && crypto.randomUUID) ? crypto.randomUUID() : ('w' + Math.random().toString(36).slice(2) + Date.now());
  let leaseHolderKind = 'web';
  let leaseHolderLabel = 'Browser Inspector';
  let isWriter = false;
  let connected = true;   // a live app is reachable (derived from /api/state); gates the drive-actions
  const _origFetch = window.fetch.bind(window);
  window.fetch = async (url, opts) => {
    if (typeof url === 'string' && url.indexOf(basePath + '/api/') === 0) {
      opts = opts || {};
      const h = new Headers(opts.headers || {});
      h.set('X-DevFlow-Lease', writerToken);
      h.set('X-DevFlow-Writer', writerToken);
      h.set('X-DevFlow-Holder', leaseHolderKind);
      h.set('X-DevFlow-Label', leaseHolderLabel);
      opts = Object.assign({}, opts, { headers: h });
      const resp = await _origFetch(url, opts);
      // A writer 409 means we tried to drive the app while read-only. Flip to read-only AND hint how
      // to recover, so a silently-ignored tap/edit/gesture doesn't look broken (the drive-BUTTONS are
      // disabled in read-only, but interact-mode taps aren't, so this closes that loop).
      if (resp.status === 409) { resp.clone().json().then((j) => { if (j && j.reason === 'writer') { setWriterUi(false, true); setStatus('Read-only — another session is driving. Take control to interact.'); } }).catch(() => {}); }
      return resp;
    }
    return _origFetch(url, opts);
  };
  window.addEventListener('pagehide', () => {
    try {
      _origFetch(`${basePath}/api/control`, {
        method: 'POST',
        keepalive: true,
        headers: {
          'Content-Type': 'application/json',
          'X-DevFlow-Lease': writerToken,
          'X-DevFlow-Writer': writerToken,
          'X-DevFlow-Holder': leaseHolderKind,
          'X-DevFlow-Label': leaseHolderLabel,
        },
        body: JSON.stringify({ action: 'release' }),
      }).catch(() => {});
    } catch {}
  });

  let gesturePoints = [];
  let isGesturing = false;
  let isDragging = false;
  let refreshInProgress = false;

  // Inspector state (A: bounds/hover/select, B: tree). Declared early so the event
  // handlers registered below can read them (they only run after init has set them).
  let mode = 'interact';        // 'interact' (click drives app) | 'inspect' (click selects)
  let selectedId = null;
  let hoveredEl = null;
  let badgeEl = null;
  const collapsedIds = new Set();
  let lastTreeSig = '';
  // Feature C: workflow recording state.
  let recordingId = null;
  let recStepCount = 0;
  let recName = null;
  // Replay + checkpoint (return-to-start-route) state.
  let lastMarkdown = null;
  let checkpointRoute = null;
  let checkpointLabel = null;
  let replaying = false;

  // Convert browser (client) coordinates to app logical coordinates. The viewport may be fit-scaled
  // by a CSS transform (see applyScale), so getBoundingClientRect() returns the on-screen (scaled)
  // box. Map back into the app's own pixel space by the ratio of the logical size (data-width/height)
  // to the rendered size. This is scale-safe and reduces to (clientX - left) when shown 1:1.
  function toAppCoords(clientX, clientY) {
    const rect = viewport.getBoundingClientRect();
    const dw = parseFloat(viewport.dataset.width) || rect.width || 1;
    const dh = parseFloat(viewport.dataset.height) || rect.height || 1;
    const sx = rect.width ? dw / rect.width : 1;
    const sy = rect.height ? dh / rect.height : 1;
    return {
      x: (clientX - rect.left) * sx + rootOffsetX,
      y: (clientY - rect.top) * sy + rootOffsetY,
    };
  }

  // Refresh state via AJAX (no full page reload — avoids flash)
  async function refreshState() {
    if (refreshInProgress) return;
    refreshInProgress = true;
    try {
      const resp = await fetch(`${basePath}/api/state`);
      if (!resp.ok) { markConnected(false); return; }
      const state = await resp.json();
      markConnected(true);

      // Update screenshot without flash
      if (screenshot && state.screenshotUrl) {
        screenshot.src = state.screenshotUrl;
      }

      // Update viewport size if changed
      if (state.viewportWidth && state.viewportHeight) {
        viewport.style.width = state.viewportWidth + 'px';
        viewport.style.height = state.viewportHeight + 'px';
        viewport.dataset.width = state.viewportWidth;
        viewport.dataset.height = state.viewportHeight;
        applyScale();   // re-fit after an app resize/rotation changed the logical size
      }
      rootOffsetX = Number(state.rootOffsetX) || 0;
      rootOffsetY = Number(state.rootOffsetY) || 0;
      viewport.dataset.rootOffsetX = String(rootOffsetX);
      viewport.dataset.rootOffsetY = String(rootOffsetY);

      // Smart DOM diff — only update elements that changed, preserving hover/selection
      if (state.elements) {
        patchElements(state.elements);
        onElementsUpdated();
      }
    } catch (err) {
      markConnected(false);
      console.error('State refresh failed:', err);
    } finally {
      refreshInProgress = false;
    }
  }

  // Reachability of the live app, derived from /api/state. Toggling it re-evaluates the drive-action
  // buttons so they disable when the agent goes away (and re-enable when it returns).
  function markConnected(v) { if (connected !== v) { connected = v; updateFlowButtons(); } }

  // Keyed DOM diff: match elements by data-id, update in-place if changed
  function patchElements(newHtml) {
    // ─────────────────────────────────────────────────────────────────────────
    // XSS / trust boundary contract with the server.
    //
    // `newHtml` is parsed into the live DOM via `innerHTML`, so any HTML it
    // contains is executed (attributes, <script>, event handlers, etc.).
    // This is only safe because the server side (HtmlRenderer) is the SOLE
    // producer of `newHtml` and guarantees:
    //
    //   1. Element identifiers, types, and any user-controlled text reach this
    //      function only via `HttpUtility.HtmlAttributeEncode` (in attribute
    //      positions) or `HttpUtility.HtmlEncode` (in text positions), which
    //      neutralise `"`, `'`, `&`, `<`, `>`.
    //   2. No URL/JS context substitution happens server-side (no href/src/
    //      onclick built from app-provided strings), so attribute-escaping is
    //      sufficient — there is no executable context to escape into.
    //   3. The response is fetched same-origin from this very inspector page,
    //      gated by the broker's loopback + Origin-port check, so an attacker
    //      cannot substitute their own HTML at the network layer.
    //
    // If any of those invariants change (raw HTML pass-through, JSON-string
    // interpolation, cross-origin fetch, etc.), replace `innerHTML` here with
    // explicit DOM construction (`createElement` + `setAttribute`) before the
    // change ships — `innerHTML` parsing of server-controlled HTML is fragile
    // and silently turns from safe into XSS-vulnerable.
    // ─────────────────────────────────────────────────────────────────────────

    // Parse new elements into a temp container
    const temp = document.createElement('div');
    temp.innerHTML = newHtml;

    // Build map of new elements by data-id
    const newEls = temp.querySelectorAll('.devflow-element');
    const newMap = new Map();
    const newOrder = [];
    newEls.forEach(el => {
      const id = el.getAttribute('data-id');
      if (id) {
        newMap.set(id, el);
        newOrder.push(id);
      }
    });

    // Build map of existing elements
    const oldEls = viewport.querySelectorAll('.devflow-element');
    const oldMap = new Map();
    oldEls.forEach(el => {
      const id = el.getAttribute('data-id');
      if (id) oldMap.set(id, el);
    });

    // Remove elements that no longer exist
    oldMap.forEach((el, id) => {
      if (!newMap.has(id)) {
        el.remove();
      }
    });

    // Update existing elements in-place or insert new ones
    let prevEl = screenshot; // insert after screenshot
    for (const id of newOrder) {
      const newEl = newMap.get(id);
      const oldEl = oldMap.get(id);

      if (oldEl) {
        // Update only if style or attributes changed
        if (oldEl.getAttribute('style') !== newEl.getAttribute('style')) {
          oldEl.setAttribute('style', newEl.getAttribute('style'));
        }
        // Sync data attributes
        syncDataAttrs(oldEl, newEl);
        // Ensure correct order
        if (prevEl && prevEl.nextSibling !== oldEl) {
          prevEl.after(oldEl);
        }
        prevEl = oldEl;
      } else {
        // New element — insert after previous
        const clone = newEl.cloneNode(true);
        if (prevEl) {
          prevEl.after(clone);
        } else {
          viewport.appendChild(clone);
        }
        prevEl = clone;
      }
    }
  }

  // Sync data-* attributes from src to dst without replacing the element
  function syncDataAttrs(dst, src) {
    // Remove old data attrs not in src
    for (const attr of [...dst.attributes]) {
      if (attr.name.startsWith('data-') && !src.hasAttribute(attr.name)) {
        dst.removeAttribute(attr.name);
      }
    }
    // Set/update from src
    for (const attr of src.attributes) {
      if (attr.name.startsWith('data-') && dst.getAttribute(attr.name) !== attr.value) {
        dst.setAttribute(attr.name, attr.value);
      }
    }
  }

  // Debounced refresh — coalesce rapid calls
  let refreshTimer = null;
  function scheduleRefresh(delayMs) {
    if (refreshTimer) clearTimeout(refreshTimer);
    refreshTimer = setTimeout(() => {
      refreshTimer = null;
      refreshState();
    }, delayMs || 300);
  }
  if (screenshot) screenshot.addEventListener('error', () => scheduleRefresh(3000));

  // ── Click → Tap (with text-input awareness) ──
  // Element types that should open a text editor instead of just tapping.
  const TEXT_INPUT_TYPES = new Set([
    'Entry', 'Editor', 'SearchBar', 'SearchHandler',
    'TextField', 'TextBox', 'TextArea', 'TextView',
    'UITextField', 'UITextView',
    'EditText', 'NSTextField',
  ]);

  function isTextInput(el) {
    if (!el || !el.classList || !el.classList.contains('devflow-element')) return false;
    const type = el.dataset.type || '';
    if (TEXT_INPUT_TYPES.has(type)) return true;
    // Heuristic: traits often expose "TextInput" / "Editable"
    const traits = (el.dataset.traits || '').toLowerCase();
    return traits.includes('textinput') || traits.includes('editable');
  }

  // Overlay editor that we float on top of the clicked text element.
  let activeEditor = null;
  function closeEditor(commit) {
    if (!activeEditor) return;
    const editor = activeEditor;
    activeEditor = null;
    if (commit) {
      const elementId = editor.dataset.elementId;
      const text = editor.value;
      fetch(`${basePath}/api/fill`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ elementId, text }),
      }).then((resp) => {
        if (recordingId && resp && resp.ok) recordStepById('fill', elementId, { value: text });
        scheduleRefresh(300);
      }).catch(err => console.error('Fill failed:', err));
    }
    editor.remove();
  }

  function openEditor(targetEl) {
    closeEditor(false);
    const elementId = targetEl.getAttribute('data-id');
    if (!elementId) return;

    // Position with UNSCALED layout coords (offset*) relative to #app-viewport (the offsetParent).
    // The editor is appended inside #app-viewport, which may carry a CSS scale transform; using the
    // scaled getBoundingClientRect() here would double-apply the scale (~s^2). offsetLeft/Top/Width/
    // Height are in the app's own pre-transform pixel space, so the parent transform scales them.
    const left = targetEl.offsetLeft, top = targetEl.offsetTop;
    const w = targetEl.offsetWidth, h = targetEl.offsetHeight;
    const isMultiline = ['Editor', 'TextArea', 'TextView', 'UITextView'].includes(targetEl.dataset.type || '');
    const editor = document.createElement(isMultiline ? 'textarea' : 'input');
    if (!isMultiline) editor.type = 'text';
    editor.value = targetEl.dataset.text || targetEl.dataset.value || '';
    editor.dataset.elementId = elementId;
    Object.assign(editor.style, {
      position: 'absolute',
      left: left + 'px',
      top: top + 'px',
      width: w + 'px',
      height: h + 'px',
      zIndex: '10000',
      background: 'rgba(255,255,255,0.97)',
      color: '#000',
      border: '2px solid #4ec9b0',
      borderRadius: '2px',
      padding: '2px 4px',
      font: 'inherit',
      fontSize: Math.max(11, Math.min(20, h * 0.5)) + 'px',
      outline: 'none',
      boxSizing: 'border-box',
      resize: 'none',
    });

    editor.addEventListener('keydown', (ev) => {
      if (ev.key === 'Escape') {
        ev.preventDefault();
        closeEditor(false);
      } else if (ev.key === 'Enter' && !isMultiline) {
        ev.preventDefault();
        closeEditor(true);
      }
    });
    editor.addEventListener('blur', () => closeEditor(true));

    viewport.appendChild(editor);
    activeEditor = editor;
    // Use a microtask so the click that opened us doesn't immediately blur it.
    setTimeout(() => { editor.focus(); editor.select(); }, 0);
  }

  viewport.addEventListener('click', async (e) => {
    if (isDragging) return;
    // If the user clicks back into the active editor, ignore.
    if (activeEditor && (e.target === activeEditor || activeEditor.contains(e.target))) return;

    // setPointerCapture(viewport) makes e.target be the viewport itself for real
    // mouse clicks, so use elementFromPoint to find the actual element under the
    // cursor. Temporarily hide any active editor so it doesn't shadow the click.
    let underCursor = document.elementFromPoint(e.clientX, e.clientY);
    if (underCursor === viewport || underCursor === screenshot) {
      // Both are pointer-events:none / non-interactive overlays; fall back to e.target.
      underCursor = e.target;
    }

    // Inspect mode (or Alt/Shift+Click while in Interact) SELECTS the element under the
    // cursor instead of tapping the app — the entry point for the tree/bounds features (A + B).
    if (mode === 'inspect' || e.altKey || e.shiftKey) {
      const picked = underCursor && underCursor.closest ? underCursor.closest('.devflow-element') : null;
      selectElement(picked ? picked.getAttribute('data-id') : null);
      return;
    }

    let textEl = underCursor;
    while (textEl && textEl !== viewport && !isTextInput(textEl)) textEl = textEl.parentElement;
    if (textEl && textEl !== viewport && isTextInput(textEl)) {
      // Still send a tap so the native control gets focus on the app side.
      const { x: tx, y: ty } = toAppCoords(e.clientX, e.clientY);
      fetch(`${basePath}/api/tap`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ x: tx, y: ty }),
      }).catch(err => console.error('Tap failed:', err));
      openEditor(textEl);
      return;
    }

    // Capture the tap target BEFORE driving (a navigation can destroy it) so a recorded step
    // carries the durable selector of the element the user targeted. We drive by coordinate (works
    // for any element, interactive or not) and record the hit element — replay resolves by selector.
    const tapEl = underCursor && underCursor.closest ? underCursor.closest('.devflow-element') : null;
    const { x, y } = toAppCoords(e.clientX, e.clientY);

    try {
      const resp = await fetch(`${basePath}/api/tap`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ x, y })
      });
      if (recordingId) {
        if (tapEl && resp.ok) await recordStep('tap', tapEl);
        else if (!tapEl) setStatus('Tap not recorded — no element under the cursor.');
      }
      scheduleRefresh(400);
    } catch (err) {
      console.error('Tap failed:', err);
    }
  });

  // ── Wheel → Scroll ──
  let scrollAccumX = 0, scrollAccumY = 0;
  let scrollFlushTimer = null;
  let lastScrollX = 0, lastScrollY = 0;

  viewport.addEventListener('wheel', (e) => {
    e.preventDefault();
    scrollAccumX += e.deltaX;
    scrollAccumY += e.deltaY;
    lastScrollX = e.clientX;
    lastScrollY = e.clientY;

    if (scrollFlushTimer) clearTimeout(scrollFlushTimer);
    scrollFlushTimer = setTimeout(async () => {
      const { x, y } = toAppCoords(lastScrollX, lastScrollY);
      const dx = scrollAccumX, dy = scrollAccumY;
      scrollAccumX = 0;
      scrollAccumY = 0;
      scrollFlushTimer = null;

      try {
        const resp = await fetch(`${basePath}/api/scroll`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ x, y, deltaX: dx, deltaY: dy })
        });
        if (recordingId && resp.ok) recordStep('scroll', null, { dx, dy });
        scheduleRefresh(300);
      } catch (err) {
        console.error('Scroll failed:', err);
      }
    }, 100);
  }, { passive: false });

  // ── Pointer Drag → Gesture ──
  viewport.addEventListener('pointerdown', (e) => {
    const { x, y } = toAppCoords(e.clientX, e.clientY);
    gesturePoints = [{ x, y, sx: e.clientX, sy: e.clientY, t: Date.now() }];
    isGesturing = true;
    isDragging = false;
    viewport.setPointerCapture(e.pointerId);
  });

  viewport.addEventListener('pointermove', (e) => {
    if (!isGesturing) return;
    const { x, y } = toAppCoords(e.clientX, e.clientY);
    gesturePoints.push({ x, y, sx: e.clientX, sy: e.clientY, t: Date.now() });
    // Drag-vs-tap decision uses on-screen (CSS px) distance so it behaves the same at any fit scale.
    const s0 = gesturePoints[0];
    if (Math.hypot(e.clientX - s0.sx, e.clientY - s0.sy) > 8) isDragging = true;
  });

  viewport.addEventListener('pointerup', async (e) => {
    if (!isGesturing) return;
    isGesturing = false;

    if (gesturePoints.length >= 2) {
      const first = gesturePoints[0];
      const last = gesturePoints[gesturePoints.length - 1];
      // Threshold on screen distance (scale-independent); send app-coordinate points to the app.
      const screenDist = Math.hypot(last.sx - first.sx, last.sy - first.sy);

      if (mode === 'interact' && screenDist > 12) {
        try {
          await fetch(`${basePath}/api/gesture`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ points: gesturePoints.map((p) => ({ x: p.x, y: p.y, t: p.t })) })
          });
          scheduleRefresh(300);
          if (recordingId) setStatus('Gesture performed but not recorded (flow tests support tap/fill/scroll).');
        } catch (err) {
          console.error('Gesture failed:', err);
        }
      }
    }

    gesturePoints = [];
    setTimeout(() => { isDragging = false; }, 50);
  });

  // ── Live updates: a WebSocket to the broker-proxied /ws/events makes the mirror react instantly
  // to app-side changes. The 3s poll below stays as a zero-regression fallback and only refreshes
  // when the socket is NOT live (wsLive) — so if the WS never connects, behavior is exactly as before.
  let wsLive = false;
  let eventsWs = null;
  function connectEvents() {
    try {
      const wsUrl = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + basePath + '/ws/events';
      eventsWs = new WebSocket(wsUrl);
      eventsWs.onopen = () => { wsLive = true; };
      eventsWs.onmessage = () => { if (!document.hidden && !replaying) scheduleRefresh(150); };
      eventsWs.onclose = () => { wsLive = false; eventsWs = null; setTimeout(connectEvents, 3000); };
      eventsWs.onerror = () => { try { eventsWs && eventsWs.close(); } catch (e) { /* onclose reconnects */ } };
    } catch (e) { wsLive = false; setTimeout(connectEvents, 5000); }
  }
  connectEvents();

  // ── Periodic refresh for app-side changes (AJAX, no flash) — fallback when the WS isn't live ──
  let pollInterval = setInterval(() => {
    if (!document.hidden && !refreshTimer && !wsLive) {
      refreshState();
    }
  }, 3000);

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
      clearInterval(pollInterval);
      pollInterval = null;
    } else if (!pollInterval) {
      pollInterval = setInterval(() => {
        if (!refreshTimer && !wsLive) refreshState();
      }, 3000);
    }
  });

  // ── Rich property grid (m6) ──
  // Right-click an element to open an editable property panel. Values are read via /api/getProperty
  // and live-edited via /api/setProperty — the shared endpoints the canvas and VS Code shells reuse.
  // Curated editable properties per type (m6) with typed editors (N1): bool/number/text/color/enum.
  // Enum choices are stable MAUI framework enums, and the agent already converts hex colors
  // (Color.FromArgb) and enum names (Enum.Parse) on setProperty — so these apply with no protocol
  // change. A future accuracy upgrade could source these from an agent property-descriptor endpoint.
  const ENUMS = {
    LayoutOptions: ['Start', 'Center', 'End', 'Fill'],
    TextAlignment: ['Start', 'Center', 'End'],
    FontAttributes: ['None', 'Bold', 'Italic'],
    LineBreakMode: ['NoWrap', 'WordWrap', 'CharacterWrap', 'HeadTruncation', 'MiddleTruncation', 'TailTruncation'],
  };
  const COMMON_PROPS = {
    '*': [['IsVisible', 'bool'], ['IsEnabled', 'bool'], ['Opacity', 'number'], ['BackgroundColor', 'color']],
    Label: [['Text', 'text'], ['TextColor', 'color'], ['FontSize', 'number'], ['FontAttributes', 'enum', ENUMS.FontAttributes], ['HorizontalTextAlignment', 'enum', ENUMS.TextAlignment], ['LineBreakMode', 'enum', ENUMS.LineBreakMode]],
    Button: [['Text', 'text'], ['TextColor', 'color'], ['FontSize', 'number']],
    Entry: [['Text', 'text'], ['Placeholder', 'text'], ['TextColor', 'color']],
    Editor: [['Text', 'text'], ['Placeholder', 'text'], ['TextColor', 'color']],
    SearchBar: [['Text', 'text'], ['Placeholder', 'text']],
    CheckBox: [['IsChecked', 'bool'], ['Color', 'color']],
    Switch: [['IsToggled', 'bool'], ['OnColor', 'color']],
    Frame: [['BorderColor', 'color'], ['CornerRadius', 'number'], ['HasShadow', 'bool']],
    StackLayout: [['Spacing', 'number']],
  };

  function styleField(el) {
    el.className = 'df-field';
  }
  // The agent's ColorTypeConverter returns "#RRGGBBAA"; setProperty accepts Color.FromArgb
  // ("#AARRGGBB"). Split the runtime value for the RGB picker and preserve alpha separately.
  function parseHexColor(v) {
    if (v == null) return null;
    const s = String(v).trim().replace(/^#/, '');
    if (/^[0-9a-fA-F]{8}$/.test(s)) return { rgb: '#' + s.slice(0, 6), alpha: s.slice(6).toUpperCase() };
    if (/^[0-9a-fA-F]{6}$/.test(s)) return { rgb: '#' + s, alpha: 'FF' };
    return null;
  }

  const propsPaneEl = document.getElementById('df-props-pane');
  const propsBodyEl = document.getElementById('df-props');
  const propsElLabel = document.getElementById('df-props-el');
  const propsCloseBtn = document.getElementById('df-props-close');
  if (propsCloseBtn) propsCloseBtn.addEventListener('click', closePropertyGrid);
  let propsLoadToken = 0;

  function propsFor(type) {
    return [...(COMMON_PROPS[type] || []), ...COMMON_PROPS['*']];
  }

  async function apiPostDetailed(path, body) {
    try {
      const r = await fetch(`${basePath}${path}`, {
        method: 'POST',
        headers: inspectorToken
          ? { 'Content-Type': 'application/json', 'X-DevFlow-Inspector-Token': inspectorToken }
          : { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      const responseBody = await r.json().catch(() => ({}));
      return { ok: r.ok, status: r.status, body: responseBody };
    } catch (err) {
      console.error(`${path} failed:`, err);
      return { ok: false, status: 0, body: null, error: String(err) };
    }
  }

  async function apiPost(path, body) {
    const result = await apiPostDetailed(path, body);
    return result.ok ? result.body : null;
  }

  function updatePropertyGridWriterState() {
    for (const field of document.querySelectorAll('#df-props .df-field')) {
      field.disabled = !isWriter;
    }
    for (const button of document.querySelectorAll('#df-props .df-prop-source')) {
      button.disabled = !isWriter || button.dataset.busy === 'true' || button.dataset.valueValid === 'false';
    }
  }

  function closePropertyGrid() {
    if (propsPaneEl) propsPaneEl.classList.add('df-hidden');
    if (propsBodyEl) propsBodyEl.replaceChildren();
    if (propsElLabel) propsElLabel.textContent = '';
    propsLoadToken++;   // cancel any in-flight property load
    syncPaneChrome();
  }

  async function openPropertyGrid(targetEl) {
    const elementId = targetEl.getAttribute('data-id');
    if (!elementId) return;

    const type = targetEl.dataset.type || 'Element';
    if (isTransientPaneLayout()) {
      if (isTreeDrawerLayout()) setTreeVisible(false);
      closeDock();
    }
    if (propsElLabel) propsElLabel.textContent = elementLabel(targetEl);
    if (propsPaneEl) propsPaneEl.classList.remove('df-hidden');
    syncPaneChrome();
    if (!propsBodyEl) return;
    propsBodyEl.replaceChildren();
    const loadToken = ++propsLoadToken;

    for (const [name, kind, choices] of propsFor(type)) {
      const res = await apiPost('/api/getProperty', { elementId, name });
      if (loadToken !== propsLoadToken) return;   // selection changed while awaiting — abandon stale rows
      const hasValue = !!res && Object.prototype.hasOwnProperty.call(res, 'value') && res.value != null;
      const val = hasValue ? res.value : null;

      const row = document.createElement('label');
      row.className = 'df-prop-row';
      const nameEl = document.createElement('span');
      nameEl.className = 'df-prop-name';
      nameEl.textContent = name;
      nameEl.title = name;
      const fieldWrap = document.createElement('span');
      fieldWrap.className = 'df-prop-field';

      let editor, readValue;
      let valueEdited = false;
      if (kind === 'bool') {
        editor = document.createElement('input'); editor.type = 'checkbox'; editor.className = 'df-field';
        editor.checked = String(val).toLowerCase() === 'true';
        readValue = () => String(editor.checked);
      } else if (kind === 'enum') {
        editor = document.createElement('select'); editor.className = 'df-field';
        const opts = (choices || []).slice();
        if (val != null && !opts.includes(String(val))) opts.unshift(String(val));
        for (const c of opts) { const o = document.createElement('option'); o.value = c; o.textContent = c; editor.appendChild(o); }
        if (val != null) editor.value = String(val);
        readValue = () => editor.value;
      } else if (kind === 'color') {
        editor = document.createElement('input'); editor.type = 'color'; editor.className = 'df-field';
        const color = parseHexColor(val);
        if (color) {
          editor.value = color.rgb;
          editor.dataset.alpha = color.alpha;
        }
        else editor.dataset.representable = 'false';
        editor.title = val != null ? String(val) : '';
        readValue = () => {
          const alpha = editor.dataset.alpha || 'FF';
          return alpha === 'FF' ? editor.value : '#' + alpha + editor.value.slice(1);
        };
      } else {
        if (kind === 'text') {
          editor = document.createElement('textarea');
          editor.rows = String(val ?? '').includes('\n') ? 3 : 1;
          editor.className = 'df-field df-text-field';
          const originalValue = val == null ? '' : String(val);
          editor.value = originalValue;
          readValue = () => valueEdited ? editor.value : originalValue;
        } else {
          editor = document.createElement('input'); editor.type = 'number'; editor.className = 'df-field';
          editor.required = true;
          if (name === 'Opacity') {
            editor.min = '0';
            editor.max = '1';
            editor.step = '0.05';
          } else {
            editor.step = 'any';
          }
          if (val != null) editor.value = val;
          readValue = () => editor.value;
        }
      }
      if (!hasValue) {
        editor.dataset.representable = 'false';
        editor.title = 'Value unavailable. Enter a value explicitly before applying it to XAML.';
      }

      let sourceButton = null;
      const syncSourceValidity = () => {
        if (!sourceButton) return;
        sourceButton.dataset.valueValid =
          editor.checkValidity() && editor.dataset.representable !== 'false' ? 'true' : 'false';
        updatePropertyGridWriterState();
      };
      editor.addEventListener('input', () => {
        valueEdited = true;
        editor.dataset.representable = 'true';
        syncSourceValidity();
      });
      editor.addEventListener('change', async () => {
        valueEdited = true;
        editor.dataset.representable = 'true';
        if (!editor.checkValidity()) {
          editor.reportValidity();
          setStatus(`Enter a valid ${name} value.`);
          syncSourceValidity();
          return;
        }
        const value = readValue();
        const result = await apiPostDetailed('/api/setProperty', { elementId, name, value });
        if (result.ok) {
          if (recordingId) recordStep('setProperty', elById(elementId), { name, value });
          scheduleRefresh(200);
        } else {
          setStatus(`The running app rejected ${name}.`);
        }
        syncSourceValidity();
      });

      if (targetEl.getAttribute('data-hasSource') === 'true') {
        sourceButton = document.createElement('button');
        sourceButton.type = 'button';
        sourceButton.className = 'df-prop-source';
        sourceButton.title = `Apply ${name} to the direct XAML attribute`;
        sourceButton.setAttribute('aria-label', `Apply ${name} to XAML source`);
        sourceButton.innerHTML = '<svg class="df-ic"><use href="#i-source"/></svg>';
        sourceButton.addEventListener('click', async () => {
          if (!isWriter) {
            setStatus('Read-only — take control before updating XAML source.');
            return;
          }
          if (!editor.checkValidity() || editor.dataset.representable === 'false') {
            editor.reportValidity();
            setStatus(`Enter a valid ${name} value before updating XAML source.`);
            syncSourceValidity();
            return;
          }

          sourceButton.dataset.busy = 'true';
          updatePropertyGridWriterState();
          try {
            const result = await apiPostDetailed('/api/persistProperty', {
              elementId,
              name,
              value: readValue(),
            });
            if (result.ok && result.body && result.body.ok) {
              sourceButton.classList.add('df-saved');
              setTimeout(() => sourceButton.classList.remove('df-saved'), 1200);
              setStatus(`Saved ${name} to ${shortFile(result.body.file || 'XAML source')}.`);
            } else {
              const error = result.body && result.body.error
                ? result.body.error
                : 'Could not update the XAML source.';
              setStatus(error);
            }
          } finally {
            sourceButton.dataset.busy = 'false';
            updatePropertyGridWriterState();
          }
        });
        syncSourceValidity();
      }

      row.appendChild(nameEl);
      fieldWrap.appendChild(editor);
      if (sourceButton) fieldWrap.appendChild(sourceButton);
      row.appendChild(fieldWrap);
      propsBodyEl.appendChild(row);
    }
    updatePropertyGridWriterState();
  }

  viewport.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    let el = document.elementFromPoint(e.clientX, e.clientY);
    while (el && el !== viewport && !(el.getAttribute && el.getAttribute('data-id'))) el = el.parentElement;
    if (el && el.getAttribute && el.getAttribute('data-id')) openPropertyGrid(el);
  });

  // ── Inspector chrome: interaction mode, hover highlight + badge, element tree (A + B) ──
  // Everything here lives in the SHARED bundle, so the browser, canvas, and VS Code hosts
  // all inherit it. Nothing host-specific belongs in this file.
  const tb = {
    interact: document.getElementById('df-mode-interact'),
    inspect: document.getElementById('df-mode-inspect'),
    tree: document.getElementById('df-toggle-tree'),
    bounds: document.getElementById('df-toggle-bounds'),
    more: document.getElementById('df-more'),
    secondary: document.getElementById('df-toolbar-secondary'),
    status: document.getElementById('df-status'),
  };
  const treePanel = document.getElementById('df-tree');
  const paneScrim = document.getElementById('df-pane-scrim');
  let hostKind = 'browser';
  let hostLayout = 'wide';

  function classifyHostLayout() {
    const width = document.documentElement.clientWidth || window.innerWidth || 1;
    const height = document.documentElement.clientHeight || window.innerHeight || 1;
    if (height < 560) return 'short';
    if (hostKind === 'canvas' && width < 860) return 'narrow';
    if (width < 720) return 'narrow';
    if (width < 1040) return 'compact';
    return 'wide';
  }

  function isTransientPaneLayout() {
    return hostLayout === 'compact' || hostLayout === 'narrow' || hostLayout === 'short';
  }

  function isTreeDrawerLayout() {
    return hostLayout === 'narrow' || hostLayout === 'short';
  }

  function setTreeVisible(visible) {
    document.body.classList.toggle('df-tree-hidden', !visible);
    if (tb.tree) {
      tb.tree.classList.toggle('df-active', visible);
      tb.tree.setAttribute('aria-expanded', String(visible));
    }
    if (visible && isTreeDrawerLayout())
      closePropertyGrid();
    syncPaneChrome();
  }

  function setMoreOpen(open) {
    document.body.classList.toggle('df-more-open', !!open);
    if (tb.more) tb.more.setAttribute('aria-expanded', String(!!open));
  }

  function syncPaneChrome() {
    const propsOpen = !!propsPaneEl && !propsPaneEl.classList.contains('df-hidden');
    const treeOpen = !document.body.classList.contains('df-tree-hidden');
    const dockOpen = document.body.classList.contains('df-dock-open');
    const showScrim =
      (isTreeDrawerLayout() && treeOpen) ||
      (isTransientPaneLayout() && propsOpen) ||
      (hostLayout !== 'wide' && dockOpen);
    document.body.classList.toggle('df-props-open', propsOpen);
    if (paneScrim) paneScrim.classList.toggle('df-hidden', !showScrim);
  }

  function updateHostLayout() {
    const next = classifyHostLayout();
    if (next !== hostLayout || !document.body.dataset.hostLayout) {
      hostLayout = next;
      document.body.dataset.hostLayout = next;
      document.documentElement.dataset.hostLayout = next;
      setMoreOpen(false);
      setTreeVisible(next === 'wide' || next === 'compact');
    }
    syncPaneChrome();
  }

  function sanitizeFontFamily(value) {
    if (typeof value !== 'string') return null;
    const font = value.trim();
    if (!font || font.length > 160 || /[;{}<>]|url\(|expression|javascript:/i.test(font))
      return null;
    return font;
  }

  function applyHostProfile(profile) {
    if (!profile || typeof profile !== 'object') return;
    const root = document.documentElement;
    if (typeof profile.surface === 'string' && /^[a-z-]{2,32}$/.test(profile.surface))
      root.dataset.hostSurface = profile.surface;
    root.dataset.hostContrast = profile.contrast === 'high' ? 'high' : 'normal';
    root.dataset.reducedMotion = profile.reducedMotion ? 'true' : 'false';
    const fontProfile = profile.font && typeof profile.font === 'object' ? profile.font : {};
    const font = sanitizeFontFamily(profile.fontFamily || fontProfile.family);
    if (font) root.style.setProperty('--df-font', font);
    const rawFontSize = profile.fontSize != null ? profile.fontSize : fontProfile.size;
    const fontSize = typeof rawFontSize === 'string'
      ? Number(rawFontSize.replace(/px$/i, ''))
      : Number(rawFontSize);
    if (Number.isFinite(fontSize) && fontSize >= 10 && fontSize <= 16)
      root.style.setProperty('--df-font-size', fontSize + 'px');
    const weight = String(profile.fontWeight || fontProfile.weight || '').trim();
    if (/^(normal|bold|[1-9]00)$/.test(weight))
      root.style.setProperty('--df-font-weight', weight);
  }

  if (paneScrim) {
    paneScrim.addEventListener('click', () => {
      setMoreOpen(false);
      if (document.body.classList.contains('df-dock-open')) closeDock();
      if (propsPaneEl && !propsPaneEl.classList.contains('df-hidden')) closePropertyGrid();
      if (isTreeDrawerLayout()) setTreeVisible(false);
    });
  }
  if (tb.more) tb.more.addEventListener('click', (e) => {
    e.stopPropagation();
    setMoreOpen(!document.body.classList.contains('df-more-open'));
  });
  if (tb.secondary) {
    tb.secondary.addEventListener('click', (e) => {
      if (e.target.closest('button')) setMoreOpen(false);
    });
    if (window.MutationObserver && tb.more) {
      new MutationObserver(() => {
        tb.more.classList.toggle('df-active', !!tb.secondary.querySelector('.df-tool-btn.df-active'));
      }).observe(tb.secondary, { subtree: true, attributes: true, attributeFilter: ['class'] });
    }
  }
  document.addEventListener('pointerdown', (e) => {
    if (!document.body.classList.contains('df-more-open')) return;
    if (tb.secondary && tb.secondary.contains(e.target)) return;
    if (tb.more && tb.more.contains(e.target)) return;
    setMoreOpen(false);
  });

  function cssEscape(s) {
    return (window.CSS && CSS.escape) ? CSS.escape(String(s)) : String(s).replace(/["\\]/g, '\\$&');
  }
  function elById(id) {
    return id ? viewport.querySelector('.devflow-element[data-id="' + cssEscape(id) + '"]') : null;
  }
  function setStatus(text) { if (tb.status) tb.status.textContent = text || ''; }
  function elementLabel(el) {
    const type = el.getAttribute('data-type') || 'Element';
    const name = el.getAttribute('data-automationId') || el.getAttribute('data-text') || '';
    return name ? (type + ' · ' + name) : type;
  }

  function setMode(next) {
    mode = next;
    tb.interact.classList.toggle('df-active', next === 'interact');
    tb.inspect.classList.toggle('df-active', next === 'inspect');
    tb.interact.setAttribute('aria-pressed', String(next === 'interact'));
    tb.inspect.setAttribute('aria-pressed', String(next === 'inspect'));
    document.body.classList.toggle('df-mode-inspect', next === 'inspect');
    setStatus(next === 'inspect' ? 'Inspect mode — click selects an element' : (selectedId ? '' : ''));
  }

  // ── Hover highlight + size badge ──
  function ensureBadge() {
    if (badgeEl) return badgeEl;
    badgeEl = document.createElement('div');
    badgeEl.id = 'df-badge';
    viewport.appendChild(badgeEl);
    return badgeEl;
  }
  function setHover(el) {
    if (hoveredEl === el) return;
    if (hoveredEl) hoveredEl.classList.remove('df-hover');
    hoveredEl = el;
    if (!el) { if (badgeEl) badgeEl.style.display = 'none'; return; }
    el.classList.add('df-hover');
    const b = ensureBadge();
    const w = Math.round(parseFloat(el.style.width) || el.offsetWidth);
    const h = Math.round(parseFloat(el.style.height) || el.offsetHeight);
    b.textContent = (el.getAttribute('data-type') || 'Element') + ' ' + w + '×' + h;
    b.style.left = el.offsetLeft + 'px';
    b.style.top = Math.max(0, el.offsetTop - 18) + 'px';
    b.style.display = 'block';
  }
  function hitTest(clientX, clientY) {
    const node = document.elementFromPoint(clientX, clientY);
    return (node && node.closest) ? node.closest('.devflow-element') : null;
  }
  viewport.addEventListener('pointermove', (e) => {
    if (isGesturing || activeEditor) return;
    setHover(hitTest(e.clientX, e.clientY));
  });
  viewport.addEventListener('pointerleave', () => setHover(null));

  // ── Selection: screenshot ↔ tree ↔ property grid ──
  function selectElement(id) {
    viewport.querySelectorAll('.devflow-element.df-selected').forEach((el) => el.classList.remove('df-selected'));
    selectedId = id || null;
    if (!id) { closePropertyGrid(); updateTreeSelection(); setStatus(''); updateHostButtons(); updateFlowButtons(); postSelectionToHost(null); return; }
    const el = elById(id);
    if (el) el.classList.add('df-selected');
    updateTreeSelection();
    revealTreeNode(id);
    if (el) { openPropertyGrid(el); setStatus(elementLabel(el)); }
    updateHostButtons();
    updateFlowButtons();
    postSelectionToHost(el);
  }

  // ── Element tree (derived from the overlay divs → one source of truth, auto-synced by the poll) ──
  function collectElements() {
    const map = new Map();
    viewport.querySelectorAll('.devflow-element').forEach((el) => {
      const id = el.getAttribute('data-id');
      if (!id) return;
      map.set(id, {
        id,
        parentId: el.getAttribute('data-parentId') || null,
        type: el.getAttribute('data-type') || 'Element',
        name: el.getAttribute('data-automationId') || el.getAttribute('data-text') || '',
        hasSource: el.getAttribute('data-hasSource') === 'true',
        visible: el.getAttribute('data-isVisible') !== 'false',
      });
    });
    return map;
  }
  function treeSignature(map) {
    const parts = [];
    map.forEach((n) => parts.push(n.id + '>' + (n.parentId || '')));
    return parts.sort().join(',');
  }
  function buildTree() {
    const map = collectElements();
    lastTreeSig = treeSignature(map);
    const kids = new Map();
    const roots = [];
    map.forEach((n) => {
      if (n.parentId && map.has(n.parentId)) {
        if (!kids.has(n.parentId)) kids.set(n.parentId, []);
        kids.get(n.parentId).push(n.id);
      } else {
        roots.push(n.id);
      }
    });
    treePanel.textContent = '';
    const frag = document.createDocumentFragment();
    roots.forEach((id) => frag.appendChild(renderTreeNode(id, map, kids, 0)));
    treePanel.appendChild(frag);
    const cntEl = document.getElementById('df-tree-count');
    if (cntEl) cntEl.textContent = map.size ? String(map.size) : '';
    updateTreeSelection();
  }
  // Map a MAUI element type to a tree glyph. Broad substring buckets keep it robust to the long tail
  // of control/layout type names (and custom subclasses) without a giant lookup table.
  function typeIcon(type) {
    const t = (type || '').toLowerCase();
    if (/shell|page|window|tabbar|flyout/.test(t)) return 'i-window';
    if (/collectionview|listview|carousel|tableview/.test(t)) return 'i-list';
    if (/button/.test(t)) return 'i-button';
    if (/entry|editor|searchbar|picker|stepper|slider/.test(t)) return 'i-input';
    if (/checkbox|switch|radiobutton/.test(t)) return 'i-check';
    if (/image/.test(t)) return 'i-image';
    if (/grid|stack|layout|border|frame|scrollview|contentview|contentpresenter/.test(t)) return 'i-layout';
    if (/label|span|text/.test(t)) return 'i-text';
    return 'i-node';
  }
  function renderTreeNode(id, map, kids, depth) {
    const n = map.get(id);
    const childIds = kids.get(id) || [];
    const wrap = document.createElement('div');
    wrap.className = 'df-tree-item';

    const row = document.createElement('div');
    row.className = 'df-tree-node';
    row.dataset.treeId = id;
    if (!n.visible) row.classList.add('df-hidden-el');
    row.style.paddingLeft = (depth * 12 + 4) + 'px';

    const twisty = document.createElement('span');
    const hasKids = childIds.length > 0;
    twisty.className = 'df-tree-twisty' + (hasKids ? '' : ' df-leaf') + (hasKids && !collapsedIds.has(id) ? ' df-open' : '');
    if (hasKids) twisty.innerHTML = '<svg class="df-ic-xs"><use href="#i-chevron"/></svg>';

    const label = document.createElement('span');
    label.className = 'df-tree-label';
    const ic = document.createElement('span');
    ic.className = 'df-tree-icon';
    ic.innerHTML = '<svg class="df-ic-xs"><use href="#' + typeIcon(n.type) + '"/></svg>';
    label.appendChild(ic);
    const typeEl = document.createElement('span');
    typeEl.className = 'df-tree-type';
    typeEl.textContent = n.type;
    label.appendChild(typeEl);
    if (n.name) {
      const nm = document.createElement('span');
      nm.className = 'df-tree-name';
      nm.textContent = ' ' + n.name;
      label.appendChild(nm);
    }
    if (n.hasSource) {
      const src = document.createElement('span');
      src.className = 'df-tree-src';
      src.innerHTML = '<svg class="df-ic-xs"><use href="#i-source"/></svg>';
      src.title = 'XAML source available';
      label.appendChild(src);
    }

    row.appendChild(twisty);
    row.appendChild(label);
    wrap.appendChild(row);

    let childrenWrap = null;
    if (childIds.length) {
      childrenWrap = document.createElement('div');
      childrenWrap.className = 'df-tree-children' + (collapsedIds.has(id) ? ' df-collapsed' : '');
      childIds.forEach((cid) => childrenWrap.appendChild(renderTreeNode(cid, map, kids, depth + 1)));
      wrap.appendChild(childrenWrap);
    }

    twisty.addEventListener('click', (e) => {
      e.stopPropagation();
      if (!childIds.length) return;
      const wasCollapsed = collapsedIds.has(id);
      if (wasCollapsed) collapsedIds.delete(id); else collapsedIds.add(id);
      twisty.classList.toggle('df-open', wasCollapsed);
      if (childrenWrap) childrenWrap.classList.toggle('df-collapsed', !wasCollapsed);
    });
    row.addEventListener('click', () => selectElement(id));
    row.addEventListener('mouseenter', () => setHover(elById(id)));
    row.addEventListener('mouseleave', () => setHover(null));
    return wrap;
  }
  function updateTreeSelection() {
    treePanel.querySelectorAll('.df-tree-node.df-selected').forEach((r) => r.classList.remove('df-selected'));
    if (!selectedId) return;
    const row = treePanel.querySelector('.df-tree-node[data-tree-id="' + cssEscape(selectedId) + '"]');
    if (row) row.classList.add('df-selected');
  }
  function revealTreeNode(id) {
    const row = treePanel.querySelector('.df-tree-node[data-tree-id="' + cssEscape(id) + '"]');
    if (!row) return;
    let p = row.parentElement;
    while (p && p !== treePanel) {
      if (p.classList.contains('df-tree-children')) p.classList.remove('df-collapsed');
      p = p.parentElement;
    }
    row.scrollIntoView({ block: 'nearest' });
  }

  // Called after every patchElements(): rebuild the tree only on structural change,
  // and re-apply the selection highlight to the (possibly replaced) overlay div.
  function onElementsUpdated() {
    const sig = treeSignature(collectElements());
    if (sig !== lastTreeSig) buildTree();
    if (selectedId) {
      const el = elById(selectedId);
      if (el) el.classList.add('df-selected'); else selectElement(null);
    }
    if (hoveredEl && !hoveredEl.isConnected) { hoveredEl = null; if (badgeEl) badgeEl.style.display = 'none'; }
  }

  // ── Workflow recording (feature C): capture interactions into the shared Flow format, then
  // download a replayable Markdown test. Reuses the broker's /api/flows/record/* endpoints, which
  // feed the same FlowRecorder/FlowMarkdown engine as the MCP maui_flow_record_* tools. ──
  const recordBtn = document.getElementById('df-toggle-record');
  const assertBtn = document.getElementById('df-assert');

  // ── Timeline: a live strip of recorded step chips (shown while recording; each chip reselects its element) ──
  const timelineEl = document.getElementById('df-timeline');
  const timelineStepsEl = document.getElementById('df-timeline-steps');
  const timelineMetaEl = document.getElementById('df-timeline-meta');
  const timelineTitleText = document.getElementById('df-timeline-title-text');
  const timelineCloseBtn = document.getElementById('df-timeline-close');
  if (timelineCloseBtn) timelineCloseBtn.addEventListener('click', () => {
    if (timelineEl) timelineEl.classList.add('df-hidden');
    document.body.classList.remove('df-timeline-open');
  });
  function timelineStart() {
    if (!timelineEl) return;
    if (timelineStepsEl) timelineStepsEl.replaceChildren();
    if (timelineMetaEl) timelineMetaEl.textContent = '';
    if (timelineTitleText) timelineTitleText.textContent = 'Recording';
    timelineEl.classList.remove('df-hidden', 'df-tl-done');
    document.body.classList.add('df-timeline-open');
  }
  function timelineStepLabel(action, el, extra) {
    if (action === 'assert' && extra && extra.assertsJson) {
      try {
        const a = JSON.parse(extra.assertsJson)[0]; const s = a.selector || {};
        const name = s.automationId || s.text || s.id || 'element';
        return a.kind === 'propEquals' ? (name + '.' + a.name + ' = ' + String(a.expected).slice(0, 18)) : (name + ' exists');
      } catch (e) { return ''; }
    }
    const base = el ? elementLabel(el) : '';
    if (action === 'scroll') { const d = extra || {}; return (d.dx || d.dy) ? ('\u0394 ' + Math.round(d.dx || 0) + ', ' + Math.round(d.dy || 0)) : ''; }
    if (extra && extra.value != null) return base + ' = ' + String(extra.value).slice(0, 18);
    if (extra && extra.text != null) return base + ' = ' + String(extra.text).slice(0, 18);
    return base;
  }
  function timelineAdd(seq, action, el, extra) {
    if (!timelineEl || timelineEl.classList.contains('df-hidden') || !timelineStepsEl) return;
    const chip = document.createElement('button');
    chip.className = 'df-tl-step';
    const id = el && el.getAttribute && el.getAttribute('data-id');
    const seqEl = document.createElement('span'); seqEl.className = 'df-tl-seq'; seqEl.textContent = String(seq || (timelineStepsEl.children.length + 1));
    const actEl = document.createElement('span'); actEl.className = 'df-tl-act'; actEl.textContent = action;
    const lblEl = document.createElement('span'); lblEl.className = 'df-tl-label'; lblEl.textContent = timelineStepLabel(action, el, extra);
    chip.append(seqEl, actEl, lblEl);
    if (id) chip.addEventListener('click', () => selectElement(id));
    timelineStepsEl.appendChild(chip);
    chip.scrollIntoView({ inline: 'end', block: 'nearest' });
  }
  function timelineStop(steps) {
    if (!timelineEl) return;
    timelineEl.classList.add('df-tl-done');
    if (timelineTitleText) timelineTitleText.textContent = 'Recorded';
    if (timelineMetaEl) timelineMetaEl.textContent = steps ? (steps + ' step' + (steps === 1 ? '' : 's')) : '';
  }

  function updateRecordButton() {
    if (!recordBtn) return;
    recordBtn.classList.toggle('df-recording', !!recordingId);
    recordBtn.setAttribute('aria-pressed', String(!!recordingId));
    // Update only the label span so the leading icon and the responsive icon-collapse survive
    // (setting textContent here would wipe the .df-btn-label wrapper the toolbar relies on).
    const lbl = recordBtn.querySelector('.df-btn-label');
    if (lbl) lbl.textContent = recordingId ? `Rec (${recStepCount})` : 'Record';
    else recordBtn.textContent = recordingId ? `\u25CF Rec (${recStepCount})` : '\u25CF Record';
  }

  // Highest-precedence DURABLE selector for the element (automationId > text > id). We never send a
  // bare type (a type-only selector would need a real, server-ordered index to replay).
  function selectorPayload(el) {
    if (!el) return {};
    const automationId = el.getAttribute('data-automationId');
    if (automationId) return { automationId };
    const text = el.getAttribute('data-text');
    if (text) return { text };
    const id = el.getAttribute('data-id');
    return id ? { id } : {};
  }

  async function recordStep(action, el, extra) {
    if (!recordingId) return;
    const body = Object.assign({ recordingId, action }, selectorPayload(el), extra || {});
    try {
      const r = await fetch(`${basePath}/api/flows/record/step`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
      });
      const j = r.ok ? await r.json().catch(() => null) : null;
      if (j && j.ok) { recStepCount = j.stepCount; updateRecordButton(); timelineAdd(j.stepCount, action, el, extra); }
    } catch (err) { console.error('record step failed:', err); }
  }

  function recordStepById(action, elementId, extra) {
    recordStep(action, elById(elementId), extra);
  }

  async function startRecording() {
    const name = 'recording-' + new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
    try {
      const r = await fetch(`${basePath}/api/flows/record/start`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name }),
      });
      const j = r.ok ? await r.json().catch(() => null) : null;
      if (j && j.ok) {
        recordingId = j.recordingId; recStepCount = 0; recName = j.name;
        if (j.route) { checkpointRoute = j.route; checkpointLabel = 'recording start'; }
        updateFlowButtons();
        timelineStart();
        setStatus('Recording — interact with the app; each action is captured.');
      } else {
        setStatus('Could not start recording.');
      }
    } catch (err) { setStatus('Could not start recording.'); }
  }

  async function stopRecording(reason) {
    if (!recordingId) return;
    const id = recordingId;
    recordingId = null;
    const pre = reason ? reason + ' ' : '';   // optional prefix, e.g. when a lost writer lease forces the stop
    updateFlowButtons();
    try {
      const r = await fetch(`${basePath}/api/flows/record/stop`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ recordingId: id }),
      });
      const j = r.ok ? await r.json().catch(() => null) : null;
      if (j && j.ok && j.markdown) {
        lastMarkdown = j.markdown;
        timelineStop(j.steps);
        const fname = (j.name || recName || 'recording') + '.md';
        // Host-side save when a host advertises it (VS Code / canvas know workspace conventions);
        // otherwise download in the browser. Gated by the authenticated bridge (feature D).
        if (hostHas('saveRecording') && postToHost('devflow:recordingComplete', { name: j.name, steps: j.steps, markdown: j.markdown })) {
          setStatus(`${pre}Recorded ${j.steps} step(s) — handed to the host to save. Replay is now available.`);
        } else {
          downloadText(fname, j.markdown);
          setStatus(`${pre}Recorded ${j.steps} step(s) → ${fname}. Replay is now available.`);
        }
      } else {
        setStatus(pre + 'Recording stopped: ' + ((j && j.error) || 'no replayable steps'));
      }
      updateFlowButtons();
      updateHostButtons();   // a completed recording enables Send-to-Copilot even with no selection
    } catch (err) { setStatus('Stop recording failed.'); }
  }

  function downloadText(filename, text) {
    try {
      const blob = new Blob([text], { type: 'text/markdown' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url; a.download = filename;
      document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (err) { console.error('download failed:', err); }
  }

  if (recordBtn) {
    recordBtn.addEventListener('click', () => { if (recordingId) stopRecording(); else startRecording(); });
  }

  // ── Replay the recorded test + return-to-start-route (checkpoint) ──
  const replayBtn = document.getElementById('df-toggle-replay');
  const checkpointBtn = document.getElementById('df-goto-checkpoint');
  let replayPanel = null;

  function updateFlowButtons() {
    // canDrive: this session holds the writer lease AND a live app is connected. The drive-actions
    // (record / replay / assert / return-to-start-route) 409 or fail otherwise, so disable them
    // rather than let a click error out. Record stays clickable WHILE recording so you can stop.
    const canDrive = isWriter && connected;
    updateRecordButton();
    if (recordBtn) recordBtn.disabled = replaying || (!canDrive && !recordingId);
    if (assertBtn) assertBtn.disabled = !recordingId || !selectedId || replaying || !canDrive;
    if (replayBtn) replayBtn.disabled = !lastMarkdown || !!recordingId || replaying || !canDrive;
    if (checkpointBtn) checkpointBtn.disabled = !checkpointRoute || replaying || !canDrive;
  }

  // While recording, capture an assertion on the selected element so the .md test VALIDATES (not
  // just reproduces): prefer "Text == <current value>" (propEquals), else "element exists". The
  // assertion carries its own durable selector and rides on a validation-only "assert" step.
  async function recordAssert() {
    if (!recordingId || !selectedId) return;
    const el = elById(selectedId);
    const sel = selectorPayload(el);
    if (!sel.automationId && !sel.text && !sel.id) { setStatus('Cannot assert: element has no durable selector (add an AutomationId).'); return; }
    let assert;
    const res = await apiPost('/api/getProperty', { elementId: selectedId, name: 'Text' });
    const text = res && res.value;
    if (text != null && String(text).length > 0) assert = { kind: 'propEquals', selector: sel, name: 'Text', expected: String(text), verify: true };
    else assert = { kind: 'exists', selector: sel, verify: true };
    await recordStep('assert', null, { assertsJson: JSON.stringify([assert]) });
    setStatus(assert.kind === 'propEquals' ? `Asserted Text == "${assert.expected}"` : 'Asserted element is present');
  }
  if (assertBtn) assertBtn.addEventListener('click', recordAssert);

  function setReplayUi(on) {
    replaying = on;
    document.body.classList.toggle('df-replaying', on);
    updateFlowButtons();
    // Pause the 3s poll while replaying so the screenshot doesn't churn under the driven app.
    if (on && pollInterval) { clearInterval(pollInterval); pollInterval = null; }
    else if (!on && !pollInterval) { pollInterval = setInterval(() => { if (!refreshTimer) refreshState(); }, 3000); }
  }

  async function captureCheckpoint(label) {
    try {
      const r = await fetch(`${basePath}/api/checkpoint`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}',
      });
      const j = r.ok ? await r.json().catch(() => null) : null;
      if (j && j.ok && j.route) { checkpointRoute = j.route; checkpointLabel = label; updateFlowButtons(); }
    } catch (err) { /* best-effort */ }
  }

  async function gotoCheckpoint() {
    if (!checkpointRoute || replaying) return;
    try {
      const r = await fetch(`${basePath}/api/navigate`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ route: checkpointRoute }),
      });
      setStatus(r.ok
        ? `Returned to ${checkpointLabel || 'start'} (${checkpointRoute}). Navigation only — app data is not reset.`
        : 'Could not navigate to the checkpoint route.');
      scheduleRefresh(500);
    } catch (err) { setStatus('Navigate failed.'); }
  }

  // A host-agnostic confirm dialog. window.confirm() is a no-op in the VS Code webview and the
  // Copilot canvas (embedded webviews block it), which previously made Replay silently abort in
  // those hosts. This DOM modal works everywhere and adopts the inspector's (VS Code-synced) theme.
  function confirmModal(message, confirmLabel) {
    return new Promise((resolve) => {
      const backdrop = document.createElement('div');
      Object.assign(backdrop.style, {
        position: 'fixed', inset: '0', zIndex: '10002', background: 'rgba(0,0,0,0.45)',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      });
      const box = document.createElement('div');
      box.setAttribute('role', 'dialog');
      box.setAttribute('aria-modal', 'true');
      Object.assign(box.style, {
        background: 'var(--df-surface, #252526)', color: 'var(--df-fg, #d4d4d4)',
        border: '1px solid var(--df-border, #3c3c3c)', borderRadius: 'var(--df-radius, 5px)',
        padding: '16px 18px', maxWidth: '360px', width: 'calc(100% - 48px)',
        boxShadow: 'var(--df-shadow, 0 8px 30px rgba(0,0,0,0.5))',
        font: '13px var(--df-font, system-ui, sans-serif)',
      });
      const msg = document.createElement('div');
      msg.textContent = message;
      Object.assign(msg.style, { marginBottom: '14px', lineHeight: '1.4' });
      const row = document.createElement('div');
      Object.assign(row.style, { display: 'flex', gap: '8px', justifyContent: 'flex-end' });
      const mkBtn = (label, primary) => {
        const b = document.createElement('button');
        b.textContent = label;
        Object.assign(b.style, {
          padding: '6px 14px', borderRadius: 'var(--df-radius-sm, 3px)', cursor: 'pointer',
          border: '1px solid var(--df-border, #3c3c3c)',
          background: primary ? 'var(--df-accent, #0e639c)' : 'var(--df-surface-2, #2d2d2d)',
          color: primary ? 'var(--df-accent-fg, #fff)' : 'var(--df-fg, #d4d4d4)',
        });
        if (primary) b.style.borderColor = 'var(--df-accent, #0e639c)';
        return b;
      };
      const cancel = mkBtn('Cancel', false);
      const ok = mkBtn(confirmLabel || 'OK', true);
      let done = false;
      const finish = (val) => {
        if (done) return; done = true;
        document.removeEventListener('keydown', onKey, true);
        backdrop.remove();
        resolve(val);
      };
      const onKey = (e) => {
        if (e.key === 'Escape') { e.preventDefault(); e.stopImmediatePropagation(); finish(false); }
        else if (e.key === 'Enter') { e.preventDefault(); e.stopImmediatePropagation(); finish(true); }
      };
      cancel.addEventListener('click', () => finish(false));
      ok.addEventListener('click', () => finish(true));
      backdrop.addEventListener('click', (e) => { if (e.target === backdrop) finish(false); });
      document.addEventListener('keydown', onKey, true);
      row.appendChild(cancel); row.appendChild(ok);
      box.appendChild(msg); box.appendChild(row);
      backdrop.appendChild(box);
      document.body.appendChild(backdrop);
      ok.focus();
    });
  }

  async function replay() {
    if (!lastMarkdown || recordingId || replaying) return;
    if (!(await confirmModal('Replay will drive the LIVE app and may change its data. Continue?', 'Replay'))) return;
    // Auto-capture a "before replay" checkpoint so you can return to where you were.
    await captureCheckpoint('before replay');
    setReplayUi(true);
    setStatus('Replaying…');
    try {
      const r = await fetch(`${basePath}/api/flows/replay`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ markdown: lastMarkdown }),
      });
      const rep = await r.json().catch(() => null);
      if (r.ok && rep) {
        showReplayReport(rep);
      } else {
        const validation = Array.isArray(rep && rep.errors) ? rep.errors.join('; ') : null;
        const error = validation || (rep && rep.error);
        setStatus(error || (r.status === 409
          ? 'Replay is busy — try again shortly.'
          : `Replay failed (HTTP ${r.status}).`));
      }
    } catch (err) { setStatus('Replay failed.'); }
    finally { setReplayUi(false); scheduleRefresh(400); }
  }

  function closeReplayReport() { if (replayPanel) { replayPanel.remove(); replayPanel = null; } }

  function showReplayReport(rep) {
    closeReplayReport();
    const panel = document.createElement('div');
    replayPanel = panel;
    Object.assign(panel.style, {
      position: 'fixed', bottom: '12px', right: '12px', zIndex: '10001', width: '320px',
      maxHeight: '60vh', overflowY: 'auto', background: 'var(--df-surface)', color: 'var(--df-fg)',
      border: '1px solid ' + (rep.ok ? 'var(--df-success)' : 'var(--df-danger)'),
      borderRadius: 'var(--df-radius)', padding: '8px 10px',
      font: 'var(--df-font-size) var(--df-font)', boxShadow: 'var(--df-shadow)',
    });
    const head = document.createElement('div');
    Object.assign(head.style, { display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '6px' });
    const title = document.createElement('strong');
    title.textContent = rep.ok ? `Replay passed (${rep.passed}/${rep.total})` : `Replay: ${rep.passed}/${rep.total} passed`;
    title.style.color = rep.ok ? 'var(--df-success)' : 'var(--df-error)';
    const close = document.createElement('button');
    close.textContent = '×';
    Object.assign(close.style, { background: 'none', border: 'none', color: 'var(--df-fg)', cursor: 'pointer', fontSize: '16px', lineHeight: '1' });
    close.addEventListener('click', closeReplayReport);
    head.appendChild(title); head.appendChild(close);
    panel.appendChild(head);
    if (rep.error) {
      const e = document.createElement('div');
      e.textContent = rep.error; e.style.color = 'var(--df-error)'; e.style.marginBottom = '4px';
      panel.appendChild(e);
    }
    for (const s of (rep.results || [])) {
      const row = document.createElement('div');
      Object.assign(row.style, { display: 'flex', gap: '6px', padding: '2px 0', alignItems: 'baseline' });
      const dot = document.createElement('span');
      dot.textContent = s.ok ? '✓' : '✕';
      dot.style.color = s.ok ? 'var(--df-success)' : 'var(--df-error)';
      const lbl = document.createElement('span');
      lbl.textContent = `${s.seq}. ${s.label || s.action}`;
      lbl.style.flex = '1';
      if (!s.ok && s.error) row.title = s.error;
      row.appendChild(dot); row.appendChild(lbl);
      panel.appendChild(row);
    }
    document.body.appendChild(panel);
    setStatus(rep.ok ? `Replay passed ${rep.passed}/${rep.total}.` : `Replay: ${rep.failed} step(s) did not pass.`);
  }

  if (replayBtn) replayBtn.addEventListener('click', replay);
  if (checkpointBtn) checkpointBtn.addEventListener('click', gotoCheckpoint);
  updateFlowButtons();

  // ── Host bridge (feature D): send-to-Copilot + open-XAML-source over a nonce-authenticated
  // iframe->host channel. The host (VS Code webview / canvas shell) advertises capabilities; a plain
  // browser tab has no host and uses clipboard fallbacks. The bridge nonce arrives in the URL
  // fragment (never sent to the broker) and gates every message in both directions. ──
  const framed = window.parent && window.parent !== window;
  const bridgeId = (location.hash.match(/devflowBridge=([A-Za-z0-9_-]+)/) || [])[1] || null;
  let hostCaps = null;
  const copilotBtn = document.getElementById('df-send-copilot');
  const sourceBtn = document.getElementById('df-open-source');
  const attachDataBtn = document.getElementById('df-attach-data');
  let dockSnapshot = null;
  let dockActiveTab = null;
  const pendingHostRequests = new Map();
  let hostRequestSequence = 0;

  function postToHost(type, data) {
    if (!framed || !bridgeId) return false;
    window.parent.postMessage(Object.assign({ v: 1, bridgeId, type }, data || {}), '*');
    return true;
  }
  function requestHost(type, data, timeoutMs) {
    if (!framed || !bridgeId) return Promise.resolve({ ok: false, error: 'No compatible host is available.' });
    const requestId = `h${Date.now().toString(36)}-${(++hostRequestSequence).toString(36)}`;
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        pendingHostRequests.delete(requestId);
        resolve({ ok: false, error: 'The host did not confirm the context attachment.' });
      }, timeoutMs || 10000);
      pendingHostRequests.set(requestId, { resolve, timer });
      postToHost(type, Object.assign({ requestId }, data || {}));
    });
  }
  function hostHas(cap) { return !!hostCaps && hostCaps.indexOf(cap) !== -1; }

  // Compact, durable element context shared with the host (Copilot). Everything the agent needs to
  // resolve "the selected element" without a screenshot.
  function elementInfo(el) {
    if (!el) return null;
    return {
      id: el.getAttribute('data-id') || null,
      type: el.getAttribute('data-type') || 'Element',
      automationId: el.getAttribute('data-automationId') || null,
      text: el.getAttribute('data-text') || null,
      hasSource: el.getAttribute('data-hasSource') === 'true',
    };
  }
  // Tell the host which element is selected so the agent can answer about "the selected element"
  // (canvas: updates the extension's selection store; VS Code: feeds a language-model tool).
  // Debounced: rapid tree-browsing must not flood the host (the canvas relays each to /control select).
  let _selHostTimer = null, _selHostPending = null;
  function postSelectionToHost(el) {
    _selHostPending = elementInfo(el);
    if (_selHostTimer) clearTimeout(_selHostTimer);
    _selHostTimer = setTimeout(() => { _selHostTimer = null; postToHost('devflow:selectionChanged', { element: _selHostPending }); }, 120);
  }

  // Reliable handshake: announce readiness (with the bridge nonce) and retry until the host acks
  // with its capabilities. The host also re-announces on iframe load, so either order works.
  let hsTries = 0, hsTimer = null;
  function announceReady() {
    if (hostCaps || !framed || !bridgeId) return;
    postToHost('devflow:ready', { version: 1 });
    if (++hsTries < 12) hsTimer = setTimeout(announceReady, 300);
  }
  window.addEventListener('message', (e) => {
    if (e.source !== window.parent) return;              // only our embedding host
    const d = e.data;
    if (!d || d.bridgeId !== bridgeId) return;            // authenticated by the per-session bridge nonce
    if (d.type === 'devflow:hostResult') {
      const pending = typeof d.requestId === 'string' ? pendingHostRequests.get(d.requestId) : null;
      if (!pending) return;
      clearTimeout(pending.timer);
      pendingHostRequests.delete(d.requestId);
      pending.resolve({
        ok: d.ok === true,
        message: typeof d.message === 'string' ? d.message : null,
        error: typeof d.error === 'string' ? d.error : null,
      });
    } else if (d.type === 'devflow:host') {
      hostCaps = Array.isArray(d.capabilities) ? d.capabilities : [];
      if (typeof d.hostKind === 'string' && d.hostKind) {
        leaseHolderKind = d.hostKind;
        hostKind = d.hostKind.includes('canvas') ? 'canvas' : (d.hostKind.includes('vscode') ? 'vscode' : d.hostKind);
        document.body.dataset.hostKind = hostKind;
        document.documentElement.dataset.hostKind = hostKind;
      }
      if (typeof d.hostLabel === 'string' && d.hostLabel) leaseHolderLabel = d.hostLabel;
      if (d.profile) applyHostProfile(d.profile);
      updateHostLayout();
      if (hsTimer) { clearTimeout(hsTimer); hsTimer = null; }
      updateHostButtons();
      if (d.theme) applyTheme(d.theme);                  // host may bundle its theme with the capability ack
    } else if (d.type === 'devflow:theme') {
      if (d.profile) applyHostProfile(d.profile);
      applyTheme(d);                                     // host reports/updates its color scheme + palette
    }
  });

  function selectedElement() { return selectedId ? elById(selectedId) : null; }

  function updateHostButtons() {
    const el = selectedElement();
    // Source: enabled only when the selected element has a XAML source map.
    if (sourceBtn) sourceBtn.disabled = !(el && el.getAttribute('data-hasSource') === 'true');
    // Copilot: there must be something to send — a selected element or a recorded test. Disabled
    // when neither exists (matches the sendToCopilot guard, so the button can't be a no-op click).
    if (copilotBtn) copilotBtn.disabled = !(el || lastMarkdown);
    updateDataAttachButton();
  }

  async function copyText(text) {
    try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
  }
  function shortFile(f) { const p = String(f).split(/[\\/]/); return p[p.length - 1] || f; }

  async function openSource() {
    const el = selectedElement();
    if (!el || el.getAttribute('data-hasSource') !== 'true') return;
    const id = el.getAttribute('data-id');
    try {
      const r = await fetch(`${basePath}/api/source`, {
        method: 'POST',
        headers: inspectorToken
          ? { 'Content-Type': 'application/json', 'X-DevFlow-Inspector-Token': inspectorToken }
          : { 'Content-Type': 'application/json' },
        body: JSON.stringify({ elementId: id }),
      });
      const j = r.ok ? await r.json().catch(() => null) : null;
      if (!j || !j.ok || !j.file) { setStatus('No source available for this element.'); return; }
      if (hostHas('openSource') && postToHost('devflow:openSource', {
        file: j.file,
        line: j.line || 1,
        column: j.column || 1,
        sourceHash: j.sourceHash || null,
      })) {
        setStatus(`Opening ${shortFile(j.file)}:${j.line || 1}…`);
      } else {
        const loc = `${j.file}:${j.line || 1}`;
        await copyText(loc);
        setStatus(`Source: ${loc} (copied)`);
      }
    } catch (err) { setStatus('Could not resolve source.'); }
  }

  let _copilotBusy = false;
  async function sendToCopilot() {
    if (_copilotBusy) return;              // throttle rapid repeat clicks so the host isn't spammed with pills
    _copilotBusy = true;
    setTimeout(() => { _copilotBusy = false; }, 600);
    const el = selectedElement();
    const element = el ? {
      type: el.getAttribute('data-type') || 'Element',
      automationId: el.getAttribute('data-automationId') || null,
      text: el.getAttribute('data-text') || null,
      id: el.getAttribute('data-id') || null,
    } : null;
    // Keep the payload small: omit the screenshot (a localhost URL Chat can't fetch) and cap the
    // recording Markdown; the host can fall back to the saved .md for anything larger.
    let markdown = lastMarkdown || null, markdownTruncated = false;
    if (markdown && markdown.length > 6000) { markdown = markdown.slice(0, 6000); markdownTruncated = true; }
    const payload = { element, markdown, markdownTruncated, appName: document.title || null };
    if (hostHas('copilot') && postToHost('devflow:sendToCopilot', { payload })) {
      setStatus('Sent context to Copilot.');
      return;
    }
    // Browser fallback: copy a compact summary to paste into any chat.
    if (!element && !lastMarkdown) { setStatus('Select an element or record a test first, then Send to Copilot.'); return; }
    const lines = [];
    if (element) lines.push(`Element: ${element.type}${element.automationId ? ' #' + element.automationId : ''}${element.text ? ' "' + element.text + '"' : ''}`);
    if (lastMarkdown) lines.push('', 'Recorded test:', lastMarkdown);
    const ok = await copyText(lines.join('\n'));
    setStatus(ok ? 'Copied context to clipboard for Copilot.' : 'Copy failed — try selecting an element again.');
  }

  if (sourceBtn) sourceBtn.addEventListener('click', openSource);
  if (copilotBtn) copilotBtn.addEventListener('click', sendToCopilot);
  if (framed && bridgeId) announceReady();
  updateHostButtons();

  // ── N2 data dock: Logs / Network / Preferences / Device / Sensors / Files ──
  // Lazy-loaded read-only tabs over the token-gated broker proxies. All app-controlled data is
  // rendered with textContent / DOM nodes (never innerHTML) so a malicious log line, URL, header,
  // filename, or preference value can't inject markup. Inherited by every host (browser/VS Code/canvas).
  const dockEl = document.getElementById('df-dock');
  const dockTabsEl = document.getElementById('df-dock-tabs');
  const dockBodyEl = document.getElementById('df-dock-body');
  const dockMetaEl = document.getElementById('df-dock-meta');
  const dockCollapseBtn = document.getElementById('df-dock-collapse');
  const dockRefreshBtn = document.getElementById('df-dock-refresh');
  const dockCloseBtn = document.getElementById('df-dock-close');
  const toggleDockBtn = document.getElementById('df-toggle-dock');
  dockActiveTab = 'logs';
  let dockLoaded = false;
  let filesRoot = null, filesPath = '';
  let filesRoots = [];
  let filesLoadGeneration = 0;
  let cdpWebviewId = null;
  let dockViewGeneration = 0;
  let networkDetailId = null;
  let networkListSignature = '';
  let networkPollInFlight = false;
  let networkRequestEpoch = 0;
  const NETWORK_AUTO_REFRESH_MS = 2000;
  const DATA_CONTEXT_MAX_CHARS = 14000;
  const DATA_CONTEXT_MAX_ENVELOPE_CHARS = 18000;
  const DATA_CONTEXT_MAX_STRING = 2000;
  const DATA_CONTEXT_MAX_STRING_SCAN = DATA_CONTEXT_MAX_STRING * 4;
  const DATA_CONTEXT_SCOPES = new Set(['logs', 'network', 'preferences', 'device', 'sensors', 'files']);
  const DATA_CONTEXT_TOOLS = {
    logs: ['maui_logs'],
    network: ['maui_network', 'maui_network_detail'],
    preferences: ['maui_preferences_list', 'maui_preferences_get'],
    device: ['maui_device_info', 'maui_display_info', 'maui_battery_info', 'maui_connectivity'],
    sensors: ['maui_sensors_list', 'maui_sensors_start', 'maui_sensors_stop'],
    files: ['maui_storage_roots', 'maui_files_list', 'maui_files_download'],
  };

  function elh(tag, attrs, ...children) {
    const e = document.createElement(tag);
    if (attrs) for (const k in attrs) {
      if (k === 'text') e.textContent = attrs[k];
      else if (k === 'class') e.className = attrs[k];
      else if (k === 'onclick') e.addEventListener('click', attrs[k]);
      else e.setAttribute(k, attrs[k]);
    }
    for (const c of children) if (c != null) e.append(c);
    return e;
  }
  function svgIcon(id, className) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', className || 'df-ic-xs');
    svg.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', '#' + id);
    svg.append(use);
    return svg;
  }
  function dockEmpty(msg) { dockBodyEl.replaceChildren(elh('div', { class: 'df-empty', text: msg })); }
  function setDockMeta(text) { if (dockMetaEl) dockMetaEl.textContent = text || ''; }
  const SECRET_KEY = /token|secret|password|auth|apikey|api[_-]?key|cookie|connection\s*string/i;
  const CONTEXT_JWT = /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/g;
  const CONTEXT_BEARER = /(bearer\s+)[A-Za-z0-9._~+/=-]{12,}/gi;
  const CONTEXT_SENSITIVE_HEADER = /\b(authorization|proxy-authorization|cookie|set-cookie)\s*:\s*[^\r\n]*/gi;
  const CONTEXT_SECRET_ASSIGNMENT = /((?:token|secret|password|pwd|api[_-]?key|authorization|cookie|connection\s*string)\s*[=:]\s*)(?:"[^"]*"|'[^']*'|[^\s,;]+)/gi;
  const CONTEXT_URL_START = /h[\t\r\n]*t[\t\r\n]*t[\t\r\n]*p[\t\r\n]*s?[\t\r\n]*:(?:[\\/\t\r\n]+|(?=[a-z0-9%]))|https?%(?:25)*3a%(?:25)*(?:2f|5c)%(?:25)*(?:2f|5c)/gi;
  const CONTEXT_LITERAL_URL_START = /^https?:(?:[\\/]+|(?=[a-z0-9%]))/i;
  const CONTEXT_ENCODED_URL_START = /^https?%(?:25)*3a%(?:25)*(?:2f|5c)%(?:25)*(?:2f|5c)/i;
  const CONTEXT_MIXED_ENCODED_URL_START = /^https?:%(?:25)*(?:2f|5c)%(?:25)*(?:2f|5c)/i;
  const CONTEXT_URL_TRAILING_PUNCTUATION = /[\])},.;!?:]$/;
  const CONTEXT_QUERY_SECRET = /^(?:code|sig)$/i;
  const CONTEXT_QUERY_SECRET_SUFFIX = /(?:^|[-_.])(?:key|signature|credential)$/i;
  const CONTEXT_PATH_SECRET = /(;(?:j?sessionid|phpsessid)=)[^/;?#]+/gi;
  const CONTEXT_URL_MAX_DEPTH = 4;
  const CONTEXT_URL_MAX_DECODE_DEPTH = 6;
  const CONTEXT_URL_MAX_CANDIDATES = 32;

  function decodeContextQueryKey(value) {
    let decoded = String(value || '').replace(/\+/g, ' ');
    for (let attempt = 0; attempt < CONTEXT_URL_MAX_DECODE_DEPTH; attempt++) {
      const next = decoded.replace(/%([a-f0-9]{2})/gi, (_, hex) => String.fromCharCode(parseInt(hex, 16)));
      if (next === decoded) break;
      decoded = next;
    }
    return { value: decoded, unresolved: /%[a-f0-9]{2}/i.test(decoded) };
  }

  function isSensitiveContextQueryKey(key) {
    const decodedKey = decodeContextQueryKey(key);
    if (decodedKey.unresolved) return true;
    const decoded = decodedKey.value;
    const normalized = decoded.replace(/[^a-z0-9]/gi, '').toLowerCase();
    return SECRET_KEY.test(decoded)
      || CONTEXT_QUERY_SECRET.test(decoded)
      || CONTEXT_QUERY_SECRET_SUFFIX.test(decoded)
      || normalized === 'key'
      || normalized === 'code'
      || normalized === 'sig'
      || normalized === 'hmac'
      || normalized === 'hdnts'
      || normalized === 'hdnea'
      || normalized === 'ticket'
      || normalized === 'session'
      || normalized === 'sid'
      || normalized === 'oobcode'
      || normalized === 'samlart'
      || normalized === 'samlresponse'
      || normalized.endsWith('subscriptionkey')
      || normalized.endsWith('signature')
      || normalized.endsWith('credential')
      || normalized.endsWith('accesskeyid')
      || normalized.endsWith('sessionid')
      || normalized.endsWith('sessiontoken')
      || normalized === 'phpsessid'
      || normalized === 'googleaccessid';
  }

  function isContextUrlHardStop(ch) {
    return ch === '<'
      || ch === '>'
      || ch === '`'
      || (/\s/.test(ch) && ch !== '\t' && ch !== '\r' && ch !== '\n');
  }

  function scanContextUrlEnd(value, start) {
    const preceding = start > 0 ? value[start - 1] : '';
    const wrapperQuote = /['"]/.test(preceding) ? preceding : null;
    const wrapperOpen = preceding === '(' || preceding === '[' || preceding === '{' ? preceding : null;
    const wrapperClose = wrapperOpen === '(' ? ')' : wrapperOpen === '[' ? ']' : wrapperOpen === '{' ? '}' : null;
    let wrapperDepth = 0;
    let quote = null;
    for (let index = start; index < value.length; index++) {
      const ch = value[index];
      if (!quote && wrapperOpen && ch === wrapperOpen) {
        wrapperDepth++;
        continue;
      }
      if (!quote && wrapperClose && ch === wrapperClose) {
        if (wrapperDepth > 0) {
          wrapperDepth--;
          continue;
        }
        return index;
      }
      if (isContextUrlHardStop(ch) && !quote) return index;
      if (ch === '<' || ch === '>' || ch === '`') return index;
      if (quote) {
        if (ch === quote && value[index - 1] !== '\\') quote = null;
        continue;
      }
      if (wrapperQuote && ch === wrapperQuote) {
        const next = value[index + 1];
        if (next === undefined || isContextUrlHardStop(next) || /[\])},.;!?:]/.test(next)) return index;
      }
      if ((ch === '"' || ch === "'") && value[index - 1] !== '\\') quote = ch;
    }
    return value.length;
  }

  function splitContextUrlSuffix(value) {
    let end = value.length;
    while (end > 0) {
      const terminal = value[end - 1];
      if (CONTEXT_URL_TRAILING_PUNCTUATION.test(terminal)) {
        end--;
        continue;
      }
      if (terminal === '"' || terminal === "'") {
        let quoteCount = 0;
        for (let index = 0; index < end; index++) {
          if (value[index] === terminal && value[index - 1] !== '\\') quoteCount++;
        }
        if (quoteCount % 2 === 1) {
          end--;
          continue;
        }
      }
      break;
    }
    return { core: value.slice(0, end), suffix: value.slice(end) };
  }

  function normalizeContextHttpUrl(value) {
    value = value.replace(/[\t\r\n]/g, '');
    const match = /^(https?):[\\/]*/i.exec(value);
    if (!match) return null;
    const remainder = value.slice(match[0].length);
    const queryIndex = remainder.search(/[?#]/);
    const pathEnd = queryIndex >= 0 ? queryIndex : remainder.length;
    return `${match[1]}://${remainder.slice(0, pathEnd).replace(/\\/g, '/')}${remainder.slice(pathEnd)}`;
  }

  function resolveContextUrlCandidate(value) {
    let resolved = value;
    let encodingDepth = 0;
    for (let attempt = 0; attempt <= CONTEXT_URL_MAX_DECODE_DEPTH; attempt++) {
      if (CONTEXT_MIXED_ENCODED_URL_START.test(resolved)) {
        if (attempt === CONTEXT_URL_MAX_DECODE_DEPTH) return null;
        try {
          const next = decodeURIComponent(resolved);
          if (next === resolved) return null;
          resolved = next;
          continue;
        } catch {
          return null;
        }
      }
      if (CONTEXT_LITERAL_URL_START.test(resolved)) {
        const normalized = normalizeContextHttpUrl(resolved);
        return normalized ? { value: normalized, encodingDepth } : null;
      }
      if (!CONTEXT_ENCODED_URL_START.test(resolved) || attempt === CONTEXT_URL_MAX_DECODE_DEPTH) return null;
      try {
        const next = decodeURIComponent(resolved);
        if (next === resolved) return null;
        resolved = next;
        encodingDepth++;
      } catch {
        return null;
      }
    }
    return null;
  }

  function encodeContextUrlLayers(value, layers) {
    for (let layer = 0; layer < layers; layer++) value = encodeURIComponent(value);
    return value;
  }

  function redactContextQueryValue(value) {
    const leadingWhitespace = value.match(/^\s*/)?.[0] || '';
    const trailingWhitespace = value.match(/\s*$/)?.[0] || '';
    const core = value.slice(leadingWhitespace.length, value.length - trailingWhitespace.length);
    const quote = core[0] === '"' || core[0] === "'" ? core[0] : '';
    const closingQuote = quote && core.length > 1 && core.endsWith(quote) ? quote : '';
    return leadingWhitespace + quote + '<redacted>' + closingQuote + trailingWhitespace;
  }

  function redactContextQuery(query, depth, budget) {
    let result = '';
    let start = 0;
    for (let index = 0; index <= query.length; index++) {
      if (index < query.length && query[index] !== '&') continue;
      const pair = query.slice(start, index);
      const equals = pair.indexOf('=');
      if (equals < 0) {
        result += redactContextUrls(pair, depth + 1, budget);
      } else {
        const key = pair.slice(0, equals);
        const value = pair.slice(equals + 1);
        const redactedKey = redactContextUrls(key, depth + 1, budget);
        result += redactedKey + '=' + (isSensitiveContextQueryKey(key)
          ? redactContextQueryValue(value)
          : redactContextUrls(value, depth + 1, budget));
      }
      if (index < query.length) result += '&';
      start = index + 1;
    }
    return result;
  }

  function redactContextUrl(value, depth, budget) {
    const rawParts = splitContextUrlSuffix(value);
    if (!rawParts.core || depth > CONTEXT_URL_MAX_DEPTH) return '<redacted-url>' + rawParts.suffix;
    const resolvedCandidate = resolveContextUrlCandidate(rawParts.core);
    if (!resolvedCandidate) return '<redacted-url>' + rawParts.suffix;

    const resolvedParts = splitContextUrlSuffix(resolvedCandidate.value);
    const resolved = resolvedParts.core;
    const decodedSuffix = resolvedParts.suffix;
    let parsed;
    try {
      parsed = new URL(resolved);
    } catch {
      return '<redacted-url>' + decodedSuffix + rawParts.suffix;
    }
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
      return '<redacted-url>' + decodedSuffix + rawParts.suffix;
    }

    const scheme = /^(https?):\/\//i.exec(resolved);
    if (!scheme) return '<redacted-url>' + decodedSuffix + rawParts.suffix;
    const authorityStart = scheme[0].length;
    let authorityEnd = resolved.length;
    for (const delimiter of ['/', '?', '#']) {
      const index = resolved.indexOf(delimiter, authorityStart);
      if (index >= 0) authorityEnd = Math.min(authorityEnd, index);
    }

    let authority = resolved.slice(authorityStart, authorityEnd);
    const userInfoEnd = authority.lastIndexOf('@');
    if (userInfoEnd >= 0 || parsed.username || parsed.password) {
      if (userInfoEnd < 0) return '<redacted-url>' + decodedSuffix + rawParts.suffix;
      authority = 'redacted@' + authority.slice(userInfoEnd + 1);
    }

    const queryStart = resolved.indexOf('?', authorityEnd);
    const fragmentStart = resolved.indexOf('#', authorityEnd);
    const pathEnd = Math.min(
      queryStart >= 0 ? queryStart : resolved.length,
      fragmentStart >= 0 ? fragmentStart : resolved.length);
    const path = redactContextUrls(resolved.slice(authorityEnd, pathEnd), depth + 1, budget)
      .replace(CONTEXT_PATH_SECRET, '$1<redacted>');
    const queryEnd = fragmentStart >= 0 ? fragmentStart : resolved.length;
    const query = queryStart >= 0 && queryStart < queryEnd
      ? '?' + redactContextQuery(resolved.slice(queryStart + 1, queryEnd), depth, budget)
      : '';
    const fragment = fragmentStart >= 0 ? '#<redacted>' : '';
    const sanitized = scheme[0] + authority + path + query + fragment + decodedSuffix;
    return encodeContextUrlLayers(sanitized, resolvedCandidate.encodingDepth) + rawParts.suffix;
  }

  function redactContextUrls(value, depth, budget) {
    const contextBudget = budget || { remaining: CONTEXT_URL_MAX_CANDIDATES, truncated: false };
    const matcher = new RegExp(CONTEXT_URL_START.source, CONTEXT_URL_START.flags);
    let result = '';
    let cursor = 0;
    let searchFrom = 0;
    while (searchFrom < value.length) {
      matcher.lastIndex = searchFrom;
      const match = matcher.exec(value);
      if (!match) break;
      result += value.slice(cursor, match.index);
      if (contextBudget.remaining <= 0) {
        contextBudget.truncated = true;
        return result + '<redacted-context>';
      }
      const end = scanContextUrlEnd(value, match.index);
      contextBudget.remaining--;
      result += redactContextUrl(value.slice(match.index, end), depth || 0, contextBudget);
      cursor = end;
      searchFrom = end;
    }
    return result + value.slice(cursor);
  }

  function sanitizeContextValue(value, keyName, depth, state) {
    if (keyName && SECRET_KEY.test(keyName)) return '<redacted>';
    if (value === null || value === undefined || typeof value === 'boolean' || typeof value === 'number') return value;
    if (typeof value === 'string') {
      const scanValue = value.length > DATA_CONTEXT_MAX_STRING_SCAN
        ? value.slice(0, DATA_CONTEXT_MAX_STRING_SCAN)
        : value;
      if (scanValue.length !== value.length) state.truncated = true;
      const budget = { remaining: CONTEXT_URL_MAX_CANDIDATES, truncated: false };
      let text = redactContextUrls(scanValue, 0, budget);
      if (budget.truncated) state.truncated = true;
      text = text
        .replace(CONTEXT_SENSITIVE_HEADER, '$1: <redacted>')
        .replace(CONTEXT_JWT, '<jwt>')
        .replace(CONTEXT_BEARER, '$1<redacted>')
        .replace(CONTEXT_SECRET_ASSIGNMENT, '$1<redacted>');
      if (text.length > DATA_CONTEXT_MAX_STRING) {
        state.truncated = true;
        text = text.slice(0, DATA_CONTEXT_MAX_STRING) + '…';
      }
      return text;
    }
    if (depth >= 8) {
      state.truncated = true;
      return '<max-depth>';
    }
    if (Array.isArray(value)) {
      if (value.length > 200) state.truncated = true;
      return value.slice(0, 200).map((item) => sanitizeContextValue(item, null, depth + 1, state));
    }
    if (typeof value === 'object') {
      const result = {};
      const keys = Object.keys(value);
      if (keys.length > 100) state.truncated = true;
      for (const key of keys.slice(0, 100)) result[key] = sanitizeContextValue(value[key], key, depth + 1, state);
      return result;
    }
    return String(value);
  }

  function createDockSnapshot(scope, title, payload, itemCount, metadata) {
    const state = { truncated: false };
    const safeTitle = String(sanitizeContextValue(String(title || scope), 'title', 0, state)).slice(0, 512);
    const sanitized = sanitizeContextValue(payload, null, 0, state);
    const serialized = JSON.stringify(sanitized);
    let data = sanitized;
    let dataFormat = 'json';
    if (serialized.length > DATA_CONTEXT_MAX_CHARS) {
      state.truncated = true;
      data = serialized.slice(0, DATA_CONTEXT_MAX_CHARS);
      dataFormat = 'json-prefix';
    }
    const snapshot = {
      kind: 'dataSnapshot',
      scope,
      title: safeTitle,
      appName: inspectorAgent.appName,
      agent: sanitizeContextValue(inspectorAgent, null, 0, state),
      capturedAt: new Date().toISOString(),
      itemCount: Number.isFinite(itemCount) ? itemCount : null,
      truncated: state.truncated,
      redacted: true,
      dataFormat,
      data,
      metadata: sanitizeContextValue(metadata || {}, null, 0, state),
      followUpTools: DATA_CONTEXT_TOOLS[scope] || [],
    };
    snapshot.truncated = state.truncated;

    if (JSON.stringify(snapshot).length > DATA_CONTEXT_MAX_ENVELOPE_CHARS) {
      snapshot.truncated = true;
      snapshot.dataFormat = 'json-prefix';
      const dataText = typeof snapshot.data === 'string' ? snapshot.data : JSON.stringify(snapshot.data);
      snapshot.data = '';
      let available = DATA_CONTEXT_MAX_ENVELOPE_CHARS - JSON.stringify(snapshot).length - 32;
      if (available < 0) {
        snapshot.metadata = {};
        snapshot.title = snapshot.title.slice(0, 256);
        available = DATA_CONTEXT_MAX_ENVELOPE_CHARS - JSON.stringify(snapshot).length - 32;
      }
      snapshot.data = dataText.slice(0, Math.max(0, available));
      while (JSON.stringify(snapshot).length > DATA_CONTEXT_MAX_ENVELOPE_CHARS && snapshot.data.length > 0) {
        const excess = JSON.stringify(snapshot).length - DATA_CONTEXT_MAX_ENVELOPE_CHARS;
        snapshot.data = snapshot.data.slice(0, Math.max(0, snapshot.data.length - excess - 64));
      }
      if (JSON.stringify(snapshot).length > DATA_CONTEXT_MAX_ENVELOPE_CHARS) {
        snapshot.data = '';
        snapshot.metadata = {};
        snapshot.title = snapshot.title.slice(0, 128);
      }
    }
    return snapshot;
  }

  function clearDockSnapshot() {
    dockSnapshot = null;
    updateDataAttachButton();
  }

  function recordDockSnapshot(scope, title, payload, itemCount, metadata) {
    if (!DATA_CONTEXT_SCOPES.has(scope)) { clearDockSnapshot(); return; }
    dockSnapshot = createDockSnapshot(scope, title, payload, itemCount, metadata);
    updateDataAttachButton();
  }

  function updateDataAttachButton() {
    if (!attachDataBtn) return;
    if (!dockSnapshot) {
      attachDataBtn.disabled = true;
      attachDataBtn.title = 'Load a supported Data tab before adding it to Copilot';
      return;
    }
    attachDataBtn.disabled = false;
    attachDataBtn.title = `Add ${dockSnapshot.title} to Copilot`;
  }

  async function attachDockDataToCopilot() {
    if (!dockSnapshot) {
      setStatus('Load a supported Data tab before adding it to Copilot.');
      return;
    }
    if (hostHas('attachData')) {
      setStatus(`Adding ${dockSnapshot.title} to Copilot…`);
      const result = await requestHost('devflow:attachData', { snapshot: dockSnapshot }, 12000);
      setStatus(result.ok
        ? (result.message || `Added ${dockSnapshot.title} to Copilot.`)
        : (result.error || `Could not add ${dockSnapshot.title} to Copilot.`));
      return;
    }
    const text = [
      'MAUI DevFlow Data snapshot:',
      JSON.stringify(dockSnapshot, null, 2),
    ].join('\n');
    const ok = await copyText(text);
    setStatus(ok ? 'Copied Data context for Copilot.' : 'Could not copy Data context.');
  }

  // Safe generic JSON renderer (objects -> key/value tables, arrays -> lists, primitives -> text),
  // masking values whose key looks secret. Used for Preferences/Device and as a fallback.
  function jsonView(value, keyName) {
    if (value === null || value === undefined) return elh('span', { text: 'null' });
    if (typeof value !== 'object') {
      const s = String(value);
      if (keyName && SECRET_KEY.test(keyName) && s.length > 0) {
        const span = elh('span', { class: 'df-masked', text: '••••• (reveal)' });
        span.addEventListener('click', () => { span.className = ''; span.textContent = s; });
        return span;
      }
      return elh('span', { text: s });
    }
    if (Array.isArray(value)) {
      if (!value.length) return elh('span', { text: '[]' });
      const tbl = elh('table');
      value.forEach((item, i) => tbl.append(elh('tr', null, elh('td', { class: 'df-kv-key', text: String(i) }), elh('td', null, jsonView(item)))));
      return tbl;
    }
    const keys = Object.keys(value);
    if (!keys.length) return elh('span', { text: '{}' });
    const tbl = elh('table');
    for (const k of keys) tbl.append(elh('tr', null, elh('td', { class: 'df-kv-key', text: k }), elh('td', null, jsonView(value[k], k))));
    return tbl;
  }

  function renderLogs(j) {
    const logs = j && j.logs;
    if (!Array.isArray(logs) || !logs.length) {
      clearDockSnapshot();
      dockEmpty(j && j.error ? j.error : 'No logs.');
      return;
    }
    const frag = document.createDocumentFragment();
    for (const e of logs) {
      const level = e.l || e.level || 'Info';
      const row = elh('div', { class: 'df-log-row df-log-' + level });
      row.append(elh('span', { class: 'df-log-time', text: (e.t || '').replace('T', ' ').replace('Z', '') + ' ' }));
      row.append(elh('span', { text: '[' + level + '] ' }));
      if (e.c || e.s) row.append(elh('span', { class: 'df-log-cat', text: (e.c || e.s) + ' ' }));
      row.append(elh('span', { text: e.m || '' }));
      if (e.e) row.append(elh('div', { class: 'df-log-ERROR', text: String(e.e) }));
      frag.append(row);
    }
    dockBodyEl.replaceChildren(frag);
    // The logs API returns entries newest first.
    const captured = logs.slice(0, 100);
    recordDockSnapshot(
      'logs',
      `Logs · ${captured.length === logs.length ? logs.length : `newest ${captured.length} of ${logs.length}`}`,
      captured,
      logs.length,
      { capturedCount: captured.length });
  }

  function networkSignature(reqs) {
    return JSON.stringify(reqs.map((r) => [
      r.id || null,
      r.method || null,
      r.url || r.path || null,
      r.statusCode ?? null,
      r.durationMs ?? null,
      r.error || null,
    ]));
  }

  function renderNetwork(j, options) {
    const opts = options || {};
    const reqs = j && j.requests;
    if (!Array.isArray(reqs)) {
      if (opts.nonDestructive) return false;
      clearDockSnapshot();
      networkListSignature = '';
      dockEmpty(j && j.error ? j.error : 'Network data unavailable.');
      return true;
    }
    const signature = networkSignature(reqs);
    if (!opts.force && signature === networkListSignature) return false;
    const scrollTop = opts.preserveScroll ? dockBodyEl.scrollTop : 0;
    networkListSignature = signature;
    if (!reqs.length) {
      clearDockSnapshot();
      dockEmpty('No requests captured.');
      return true;
    }
    const tbl = elh('table');
    tbl.append(elh('tr', null, elh('th', { text: 'Method' }), elh('th', { text: 'URL' }), elh('th', { text: 'Status' }), elh('th', { text: 'ms' })));
    for (const r of reqs) {
      const tr = elh('tr', { class: 'df-row-click' },
        elh('td', { text: r.method || '' }),
        elh('td', { text: r.url || r.path || '' }),
        elh('td', { text: r.statusCode != null ? String(r.statusCode) : (r.error ? 'ERR' : '') }),
        elh('td', { text: r.durationMs != null ? String(r.durationMs) : '' }));
      if (r.id) tr.addEventListener('click', () => loadNetworkDetail(r.id));
      tbl.append(tr);
    }
    dockBodyEl.replaceChildren(tbl);
    if (opts.preserveScroll) dockBodyEl.scrollTop = scrollTop;
    recordDockSnapshot(
      'network',
      `Network · ${reqs.length} request${reqs.length === 1 ? '' : 's'}`,
      reqs.map((r) => ({
        id: r.id || null,
        method: r.method || null,
        url: r.url || r.path || null,
        statusCode: r.statusCode ?? null,
        durationMs: r.durationMs ?? null,
        error: r.error || null,
      })),
      reqs.length,
      { view: 'list' });
    return true;
  }

  function networkListIsCurrent(generation) {
    return generation === dockViewGeneration && dockActiveTab === 'network' && networkDetailId === null;
  }

  function networkPollIsActive() {
    return networkListIsCurrent(dockViewGeneration)
      && !dockEl.classList.contains('df-hidden')
      && !document.body.classList.contains('df-dock-collapsed')
      && !document.hidden
      && !replaying;
  }

  async function loadNetworkList(options) {
    const opts = options || {};
    if (opts.automatic && networkPollInFlight) return false;
    if (opts.automatic) networkPollInFlight = true;
    const generation = opts.generation ?? dockViewGeneration;
    const requestEpoch = ++networkRequestEpoch;
    try {
      const j = await apiPost('/api/network', { limit: 100 });
      if (requestEpoch !== networkRequestEpoch) return false;
      if (!networkListIsCurrent(generation)) return false;
      if (opts.automatic && !networkPollIsActive()) return false;
      const changed = renderNetwork(j, {
        force: !opts.automatic,
        nonDestructive: !!opts.automatic,
        preserveScroll: !!opts.automatic,
      });
      if (opts.automatic && changed) setDockMeta('live · updated ' + new Date().toLocaleTimeString());
      return changed;
    } finally {
      if (opts.automatic) networkPollInFlight = false;
    }
  }

  async function loadNetworkDetail(id) {
    if (!id) return;
    const generation = ++dockViewGeneration;
    networkDetailId = id;
    clearDockSnapshot();
    setDockMeta('loading…');
    const j = await apiPost('/api/network/detail', { id });
    if (generation !== dockViewGeneration || dockActiveTab !== 'network' || networkDetailId !== id) return;
    setDockMeta('live paused · captured ' + new Date().toLocaleTimeString());
    const r = j && j.request;
    if (!r) { dockEmpty('Request detail unavailable.'); return; }
    const frag = document.createDocumentFragment();
    frag.append(elh('button', { class: 'df-dock-btn', type: 'button', text: '‹ Back to requests', onclick: () => loadTab('network') }));
    frag.append(elh('div', { class: 'df-section-title', text: (r.method || '') + ' ' + (r.url || '') }));
    frag.append(jsonView({
      status: r.statusCode, statusText: r.statusText, durationMs: r.durationMs,
      requestContentType: r.requestContentType, responseContentType: r.responseContentType,
      requestHeaders: r.requestHeaders, responseHeaders: r.responseHeaders,
      requestBody: r.requestBody, responseBody: r.responseBody, error: r.error,
    }));
    dockBodyEl.replaceChildren(frag);
    recordDockSnapshot(
      'network',
      `Network request · ${r.method || ''} ${r.url || ''}`.trim(),
      {
        id: r.id || id,
        method: r.method || null,
        url: r.url || null,
        statusCode: r.statusCode ?? null,
        statusText: r.statusText || null,
        durationMs: r.durationMs ?? null,
        requestContentType: r.requestContentType || null,
        responseContentType: r.responseContentType || null,
        requestHeaders: r.requestHeaders || null,
        responseHeaders: r.responseHeaders || null,
        error: r.error || null,
      },
      1,
      { view: 'detail', requestId: r.id || id });
  }

  function renderPreferences(j) {
    if (!j || j.ok === false) {
      clearDockSnapshot();
      dockEmpty((j && j.error) || 'Preferences unavailable.');
      return;
    }
    const frag = document.createDocumentFragment();
    frag.append(elh('div', { class: 'df-section-title', text: 'Known preferences (values with secret-looking keys are masked)' }));
    frag.append(jsonView(j.preferences));
    dockBodyEl.replaceChildren(frag);
    const count = j.preferences && typeof j.preferences === 'object' ? Object.keys(j.preferences).length : 0;
    recordDockSnapshot('preferences', `Preferences · ${count} entr${count === 1 ? 'y' : 'ies'}`, j.preferences, count);
  }

  function renderDevice(j) {
    const dev = j && j.device;
    if (!dev || typeof dev !== 'object' || !Object.keys(dev).length) {
      clearDockSnapshot();
      dockEmpty('Device info unavailable.');
      return;
    }
    const frag = document.createDocumentFragment();
    const labels = { 'device-info': 'Device', 'device-display': 'Display', battery: 'Battery', connectivity: 'Connectivity' };
    for (const k of Object.keys(dev)) {
      frag.append(elh('div', { class: 'df-section-title', text: labels[k] || k }));
      frag.append(jsonView(dev[k]));
    }
    dockBodyEl.replaceChildren(frag);
    recordDockSnapshot('device', 'Device snapshot', dev, Object.keys(dev).length);
  }

  function renderSensors(j) {
    const frag = document.createDocumentFragment();
    const geoBtn = elh('button', { class: 'df-dock-btn df-icon-label', type: 'button' },
      svgIcon('i-location'), elh('span', { text: 'Read geolocation' }));
    geoBtn.addEventListener('click', readGeolocation);
    frag.append(geoBtn);
    const geoOut = elh('div', { id: 'df-geo-out' });
    frag.append(geoOut);
    const sensors = j && j.sensors;
    if (Array.isArray(sensors) && sensors.length) {
      const tbl = elh('table');
      tbl.append(elh('tr', null, elh('th', { text: 'Sensor' }), elh('th', { text: 'Supported' }), elh('th', { text: 'Active' }), elh('th', { text: 'Subscribers' })));
      for (const s of sensors) tbl.append(elh('tr', null,
        elh('td', { text: s.sensor || '' }), elh('td', { text: String(s.supported) }),
        elh('td', { text: String(s.active) }), elh('td', { text: String(s.subscribers != null ? s.subscribers : '') })));
      frag.append(tbl);
    } else {
      frag.append(elh('div', { class: 'df-empty', text: 'No sensors reported.' }));
    }
    dockBodyEl.replaceChildren(frag);
    if (Array.isArray(sensors) && sensors.length) {
      recordDockSnapshot('sensors', `Sensors · ${sensors.length}`, sensors, sensors.length, { geolocationIncluded: false });
    } else {
      clearDockSnapshot();
    }
  }

  async function readGeolocation() {
    if (replaying) { setStatus('Geolocation is disabled during replay.'); return; }
    const out = document.getElementById('df-geo-out');
    if (out) out.textContent = 'Reading location…';
    const j = await apiPost('/api/geolocation', {});
    if (out) out.replaceChildren(j && j.ok ? jsonView(j.location) : elh('span', { class: 'df-empty', text: (j && j.error) || 'Geolocation unavailable.' }));
  }

  async function renderFiles(j) {
    const roots = j && j.roots;
    const frag = document.createDocumentFragment();
    const rootsArr = extractRoots(roots);
    if (!rootsArr.length) {
      clearDockSnapshot();
      dockEmpty((j && j.error) || 'No storage roots advertised by this app.');
      return;
    }
    filesRoots = rootsArr;
    if (!filesRoot || !rootsArr.some((r) => r.id === filesRoot)) filesRoot = rootsArr[0].id;
    const bar = elh('div', { class: 'df-files-toolbar' });
    bar.append(elh('label', { class: 'df-kv-key', for: 'df-files-root', text: 'Root' }));
    const sel = elh('select', { class: 'df-dock-btn', id: 'df-files-root' });
    for (const r of rootsArr) sel.append(elh('option', { value: r.id, text: r.label }));
    sel.value = filesRoot;
    sel.addEventListener('change', () => {
      filesRoot = sel.value;
      filesPath = '';
      updateFilesRootInfo();
      loadFiles();
    });
    bar.append(sel);
    bar.append(elh('span', {
      class: 'df-files-mode',
      title: 'The inspector browses files without changing them. DevFlow tools can download, upload, or delete files when explicitly requested.',
      text: 'Browse only',
    }));
    frag.append(bar);
    frag.append(elh('div', { id: 'df-files-root-info', class: 'df-files-root-info' }));
    frag.append(elh('div', { id: 'df-files-list' }));
    dockBodyEl.replaceChildren(frag);
    updateFilesRootInfo();
    await loadFiles();
  }

  function extractRoots(roots) {
    let arr = [];
    if (Array.isArray(roots)) arr = roots;
    else if (roots && Array.isArray(roots.roots)) arr = roots.roots;
    return arr.map((r) => (typeof r === 'string'
      ? { id: r, label: r }
      : {
        id: r.id || r.name || r.root || r.path || '',
        label: r.displayName || r.name || r.id || r.path || '(root)',
        kind: r.kind || null,
        isReadOnly: r.isReadOnly === true,
        isPersistent: r.isPersistent === true,
        isUserVisible: r.isUserVisible !== false,
      })).filter((r) => r.id);
  }

  function currentFilesRoot() {
    return filesRoots.find((r) => r.id === filesRoot) || null;
  }

  function updateFilesRootInfo() {
    const info = document.getElementById('df-files-root-info');
    if (!info) return;
    const root = currentFilesRoot();
    if (!root) { info.textContent = ''; return; }
    const parts = [];
    if (root.kind === 'appData') parts.push('Private app storage');
    else if (root.kind) parts.push(root.kind);
    if (root.isPersistent) parts.push('persistent');
    if (root.isReadOnly) parts.push('root is read-only');
    info.textContent = parts.join(' · ');
  }

  async function loadFiles() {
    const list = document.getElementById('df-files-list');
    if (!list) return;
    const loadGeneration = ++filesLoadGeneration;
    const dockGeneration = dockViewGeneration;
    const requestedRoot = filesRoot;
    const requestedPath = filesPath;
    clearDockSnapshot();
    list.textContent = 'Loading…';
    const j = await apiPost('/api/files/list', { root: requestedRoot, path: requestedPath });
    if (loadGeneration !== filesLoadGeneration || dockGeneration !== dockViewGeneration
        || dockActiveTab !== 'files' || requestedRoot !== filesRoot || requestedPath !== filesPath) return;
    const frag = document.createDocumentFragment();
    // Breadcrumb.
    const crumb = elh('div', null);
    crumb.append(elh('span', { class: 'df-crumb', text: '/', onclick: () => { filesPath = ''; loadFiles(); } }));
    if (filesPath) {
      const parts = filesPath.split('/').filter(Boolean);
      let acc = '';
      for (const p of parts) { acc += (acc ? '/' : '') + p; const here = acc; crumb.append(elh('span', { text: ' ' }), elh('span', { class: 'df-crumb', text: p, onclick: () => { filesPath = here; loadFiles(); } })); }
    }
    frag.append(crumb);
    const entries = extractEntries(j && j.files);
    if (j && j.error) {
      clearDockSnapshot();
      frag.append(elh('div', { class: 'df-empty', text: j.error }));
    } else if (!entries.length) {
      const empty = elh('div', { class: 'df-empty df-files-empty' });
      const root = currentFilesRoot();
      empty.append(elh('strong', {
        text: filesPath ? 'This folder is empty.' : ((root && root.label) || 'This storage root') + ' is empty.',
      }));
      if (!filesPath) {
        empty.append(elh('div', {
          text: 'Files appear here after the app writes to this location. In-memory data and Preferences are not files.',
        }));
      }
      frag.append(empty);
    }
    else {
      const tbl = elh('table');
      tbl.append(elh('tr', null, elh('th', { text: 'Name' }), elh('th', { text: 'Size' })));
      // Directories first.
      for (const e of entries.filter((x) => x.dir)) {
        const openFolder = () => { filesPath = (filesPath ? filesPath + '/' : '') + e.name; loadFiles(); };
        const nameCell = elh('td', null,
          elh('button', { class: 'df-file-link', type: 'button', title: 'Open ' + e.name, onclick: openFolder },
            svgIcon('i-folder'), elh('span', { text: e.name })));
        tbl.append(elh('tr', null, nameCell, elh('td', { text: '' })));
      }
      for (const e of entries.filter((x) => !x.dir)) {
        tbl.append(elh('tr', null,
          elh('td', null, elh('span', { class: 'df-file-name' }, svgIcon('i-file'), elh('span', { text: e.name }))),
          elh('td', { text: e.size != null ? String(e.size) : '' })));
      }
      frag.append(tbl);
    }
    list.replaceChildren(frag);
    if (!(j && j.error)) {
      const root = currentFilesRoot();
      recordDockSnapshot(
        'files',
        `Files · ${(root && root.label) || filesRoot || 'storage'}`,
        {
          root: root ? {
            id: root.id,
            displayName: root.label,
            kind: root.kind,
            isReadOnly: root.isReadOnly,
            isPersistent: root.isPersistent,
          } : { id: filesRoot },
          path: filesPath,
          entries: entries.map((entry) => ({
            name: entry.name,
            type: entry.dir ? 'directory' : 'file',
            size: entry.size,
            lastModified: entry.lastModified || null,
          })),
        },
        entries.length,
        { contentsIncluded: false });
    }
  }

  function extractEntries(files) {
    let arr = [];
    if (Array.isArray(files)) arr = files;
    else if (files && Array.isArray(files.entries)) arr = files.entries;
    else if (files && Array.isArray(files.files)) arr = files.files;
    return arr.map((e) => (typeof e === 'string'
      ? { name: e, dir: false, size: null }
      : {
        name: e.name || e.path || '',
        dir: !!(e.isDirectory || e.directory || e.dir || e.type === 'directory'),
        size: e.size != null ? e.size : e.length,
        lastModified: e.lastModified || e.modified || null,
      })).filter((e) => e.name);
  }

  function dockLoadIsCurrent(name, generation) {
    return dockActiveTab === name && dockViewGeneration === generation;
  }

  const tabLoaders = {
    logs: async (generation) => {
      const j = await apiPost('/api/logs', { limit: 200 });
      if (dockLoadIsCurrent('logs', generation)) renderLogs(j);
    },
    network: async (generation) => loadNetworkList({ generation }),
    preferences: async (generation) => {
      const j = await apiPost('/api/preferences', {});
      if (dockLoadIsCurrent('preferences', generation)) renderPreferences(j);
    },
    device: async (generation) => {
      const j = await apiPost('/api/device', {});
      if (dockLoadIsCurrent('device', generation)) renderDevice(j);
    },
    sensors: async (generation) => {
      const j = await apiPost('/api/sensors', {});
      if (dockLoadIsCurrent('sensors', generation)) renderSensors(j);
    },
    files: async (generation) => {
      const j = await apiPost('/api/files/roots', {});
      if (dockLoadIsCurrent('files', generation)) await renderFiles(j);
    },
    webview: async (generation) => {
      const j = await apiPost('/api/cdp/webviews', {});
      if (dockLoadIsCurrent('webview', generation)) renderWebView(j);
    },
  };

  // ── N3 (scoped): Blazor WebView CDP tab — list WebViews, view source, evaluate JS ──
  function extractWebviews(v) {
    let arr = [];
    if (Array.isArray(v)) arr = v;
    else if (v && Array.isArray(v.webViews)) arr = v.webViews;
    else if (v && Array.isArray(v.webviews)) arr = v.webviews;
    else if (v && Array.isArray(v.targets)) arr = v.targets;
    return arr.map((w) => (typeof w === 'string'
      ? { id: w, label: w }
      : { id: w.id || w.targetId || w.webviewId || '', label: w.title || w.url || w.id || 'webview' })).filter((w) => w.id);
  }

  async function renderWebView(j) {
    const wvs = extractWebviews(j && j.webviews);
    if (!wvs.length) { dockEmpty((j && j.error) || 'No Blazor WebViews in this app.'); return; }
    const frag = document.createDocumentFragment();
    const bar = elh('div', null, elh('span', { class: 'df-kv-key', text: 'WebView: ' }));
    const sel = elh('select', { class: 'df-dock-btn' });
    for (const w of wvs) sel.append(elh('option', { value: w.id, text: w.label }));
    if (!cdpWebviewId || !wvs.some((w) => w.id === cdpWebviewId)) cdpWebviewId = wvs[0].id;
    sel.value = cdpWebviewId;
    sel.addEventListener('change', () => { cdpWebviewId = sel.value; });
    bar.append(sel);
    bar.append(document.createTextNode(' '));
    bar.append(elh('button', { class: 'df-dock-btn', text: 'View source', onclick: cdpViewSource }));
    frag.append(bar);
    const evalRow = elh('div', null);
    const inp = elh('input', { class: 'df-dock-btn', id: 'df-cdp-expr', placeholder: 'JS expression, e.g. document.title' });
    inp.style.width = '360px';
    inp.addEventListener('keydown', (e) => { if (e.key === 'Enter') cdpEval(); });
    evalRow.append(inp, document.createTextNode(' '), elh('button', { class: 'df-dock-btn', text: 'Run', onclick: cdpEval }));
    frag.append(evalRow);
    frag.append(elh('div', { id: 'df-cdp-out' }));
    dockBodyEl.replaceChildren(frag);
  }

  async function cdpViewSource() {
    const out = document.getElementById('df-cdp-out');
    if (out) out.textContent = 'Loading…';
    const j = await apiPost('/api/cdp/source', { webviewId: cdpWebviewId });
    if (out) out.replaceChildren(elh('pre', { class: 'df-log-row', text: (j && j.ok && j.source != null) ? String(j.source) : ((j && j.error) || 'No source.') }));
  }

  async function cdpEval() {
    const inp = document.getElementById('df-cdp-expr');
    const out = document.getElementById('df-cdp-out');
    const expr = inp ? inp.value : '';
    if (!expr) return;
    if (out) out.textContent = 'Running…';
    const j = await apiPost('/api/cdp/eval', { expression: expr, webviewId: cdpWebviewId });
    if (out) out.replaceChildren(jsonView(j && j.ok ? j.result : ((j && j.error) || 'evaluate failed')));
  }

  async function loadTab(name) {
    const generation = ++dockViewGeneration;
    dockActiveTab = name;
    networkDetailId = null;
    clearDockSnapshot();
    for (const b of dockTabsEl.querySelectorAll('.df-dock-tab')) {
      const active = b.getAttribute('data-tab') === name;
      b.classList.toggle('df-active', active);
      b.setAttribute('aria-selected', String(active));
      b.tabIndex = active ? 0 : -1;
    }
    dockEmpty('Loading…');
    setDockMeta('loading…');
    try {
      await tabLoaders[name](generation);
      if (!dockLoadIsCurrent(name, generation)) return;
      setDockMeta((name === 'network' ? 'live · updated ' : 'captured ') + new Date().toLocaleTimeString());
    }
    catch (e) {
      if (!dockLoadIsCurrent(name, generation)) return;
      dockEmpty('Failed to load.');
      setDockMeta('');
    }
  }

  function openDock() {
    if (isTransientPaneLayout()) {
      closePropertyGrid();
      if (isTreeDrawerLayout()) setTreeVisible(false);
    }
    dockEl.classList.remove('df-hidden');
    document.body.classList.add('df-dock-open');
    document.body.classList.remove('df-dock-collapsed');
    if (dockCollapseBtn) {
      dockCollapseBtn.setAttribute('aria-expanded', 'true');
      dockCollapseBtn.title = 'Collapse data panel';
    }
    if (toggleDockBtn) {
      toggleDockBtn.classList.add('df-active');
      toggleDockBtn.setAttribute('aria-pressed', 'true');
    }
    if (!dockLoaded) { dockLoaded = true; loadTab(dockActiveTab); }
    syncPaneChrome();
  }
  function closeDock() {
    dockEl.classList.add('df-hidden');
    document.body.classList.remove('df-dock-open', 'df-dock-collapsed');
    if (toggleDockBtn) {
      toggleDockBtn.classList.remove('df-active');
      toggleDockBtn.setAttribute('aria-pressed', 'false');
    }
    syncPaneChrome();
  }
  function toggleDockCollapsed() {
    const collapsed = document.body.classList.toggle('df-dock-collapsed');
    if (dockCollapseBtn) {
      dockCollapseBtn.setAttribute('aria-expanded', String(!collapsed));
      dockCollapseBtn.title = collapsed ? 'Expand data panel' : 'Collapse data panel';
    }
  }
  if (toggleDockBtn) toggleDockBtn.addEventListener('click', () => (dockEl.classList.contains('df-hidden') ? openDock() : closeDock()));
  if (dockCloseBtn) dockCloseBtn.addEventListener('click', closeDock);
  if (dockCollapseBtn) dockCollapseBtn.addEventListener('click', toggleDockCollapsed);
  if (dockRefreshBtn) dockRefreshBtn.addEventListener('click', () => {
    if (dockActiveTab === 'network' && networkDetailId) loadNetworkDetail(networkDetailId);
    else loadTab(dockActiveTab);
  });
  if (attachDataBtn) attachDataBtn.addEventListener('click', attachDockDataToCopilot);
  for (const b of dockTabsEl.querySelectorAll('.df-dock-tab')) b.addEventListener('click', () => loadTab(b.getAttribute('data-tab')));
  setInterval(() => {
    if (networkPollIsActive()) loadNetworkList({ automatic: true, generation: dockViewGeneration });
  }, NETWORK_AUTO_REFRESH_MS);

  // ── N4 presence / single-writer coordination ──
  function setWriterUi(writer, heldByOther) {
    const lostLease = isWriter && !writer;   // we were driving; another session just took over
    isWriter = !!writer;
    const p = document.getElementById('df-presence');
    const t = document.getElementById('df-take-control');
    if (p) {
      p.innerHTML = writer ? '<svg class="df-ic"><use href="#i-edit"/></svg> Driving' : (heldByOther ? '<svg class="df-ic"><use href="#i-lock"/></svg> Read-only' : '');
      p.className = 'df-presence' + (writer ? ' df-writer' : (heldByOther ? ' df-readonly' : ''));
    }
    if (t) t.classList.toggle('df-hidden', !heldByOther);
    // Recording is app-scoped: another valid lease holder can continue it after handoff. This tab
    // becomes a passive observer and can resume the existing recording if it takes control again.
    if (lostLease && recordingId) {
      recordingId = null;
      setStatus('Read-only — recording continues under the session that took control.');
    }
    updateFlowButtons();   // read-only / disconnected re-evaluates the drive-actions
    updatePropertyGridWriterState();
  }
  async function control(action, force) {
    const j = await apiPost('/api/control', force ? { action, force: true } : { action });
    if (j) setWriterUi(j.youAreWriter, j.heldByOther);
    return j;
  }
  const takeControlEl = document.getElementById('df-take-control');
  if (takeControlEl) takeControlEl.addEventListener('click', () => control('claim', true));
  control('claim');   // optimistically claim the writer lease on load
  setInterval(() => { if (!document.hidden) control(isWriter ? 'heartbeat' : 'status'); }, 4000);
  // On refocus, immediately reconcile writer presence instead of waiting up to 4s for the next tick —
  // a backgrounded tab pauses its heartbeat, so its lease may have expired or been claimed elsewhere.
  document.addEventListener('visibilitychange', () => { if (!document.hidden) control(isWriter ? 'heartbeat' : 'status'); });

  // ── Toolbar wiring + init ──
  tb.interact.addEventListener('click', () => setMode('interact'));
  tb.inspect.addEventListener('click', () => setMode('inspect'));
  tb.tree.addEventListener('click', () => {
    setTreeVisible(document.body.classList.contains('df-tree-hidden'));
  });
  tb.bounds.addEventListener('click', () => {
    const on = document.body.classList.toggle('df-show-all');
    tb.bounds.classList.toggle('df-active', on);
    tb.bounds.setAttribute('aria-pressed', String(on));
  });

  // ── Fit-to-container scaling (responsive) ──────────────────────────────────
  // In hosts smaller than the app's logical size (a Copilot canvas side panel, a short VS Code
  // panel) scale #app-viewport down so the whole app stays visible; #df-stage is sized to the
  // scaled box so centering + scrollbars behave. "Fit" (default) never upscales past 1:1 (keeps the
  // screenshot crisp); toggling it off shows 1:1 actual pixels and lets #df-viewport-wrap scroll.
  function applyScale() {
    const dw = parseFloat(viewport.dataset.width) || viewport.offsetWidth || 1;
    const dh = parseFloat(viewport.dataset.height) || viewport.offsetHeight || 1;
    if (!stage || !vpWrap) return;
    let s = 1;
    if (fitMode) {
      const availW = vpWrap.clientWidth, availH = vpWrap.clientHeight;
      if (availW > 0 && availH > 0) s = Math.min(availW / dw, availH / dh, 1);
      if (!(s > 0) || !isFinite(s)) s = 1;
    }
    viewport.style.transform = s === 1 ? 'none' : ('scale(' + s + ')');
    stage.style.width = (dw * s) + 'px';
    stage.style.height = (dh * s) + 'px';
  }
  function scheduleScale() {
    if (scaleRaf) cancelAnimationFrame(scaleRaf);
    scaleRaf = requestAnimationFrame(() => { scaleRaf = 0; applyScale(); });
  }
  const fitBtn = document.getElementById('df-toggle-fit');
  if (fitBtn) {
    fitBtn.addEventListener('click', () => {
      fitMode = !fitMode;
      fitBtn.classList.toggle('df-active', fitMode);
      fitBtn.setAttribute('aria-pressed', String(fitMode));
      const lbl = fitBtn.querySelector('.df-btn-label'); if (lbl) lbl.textContent = fitMode ? 'Fit' : '1:1';
      applyScale();
    });
  }
  if (window.ResizeObserver && vpWrap) { try { new ResizeObserver(scheduleScale).observe(vpWrap); } catch (_) {} }
  window.addEventListener('resize', () => { updateHostLayout(); scheduleScale(); });

  // ── Host theme sync (devflow:theme) ────────────────────────────────────────
  // A cross-origin iframe can't read the host's theme and prefers-color-scheme is unreliable across
  // the VS Code / Canvas boundary, so the host tells us over the authenticated bridge. Two knobs:
  //   mode:    'light' | 'dark' | 'system' -> pins <html data-theme> (or clears it to follow the OS)
  //   palette: { '--df-*': '<color>' }      -> overrides specific semantic tokens (e.g. VS Code colors)
  // Every palette value is whitelisted by key and strictly validated as a CSS color before it touches
  // the DOM, so a hostile/compromised host frame can't inject url()/expressions/rule-breaking values.
  const THEME_VARS = new Set(['--df-bg','--df-surface','--df-surface-2','--df-fg','--df-muted','--df-border','--df-border-subtle','--df-hover','--df-hover-row','--df-accent','--df-accent-fg','--df-selected','--df-selected-fg','--df-danger','--df-focus','--df-type','--df-name','--df-source','--df-success','--df-warn','--df-error','--df-outline-hover','--df-outline-select']);
  const HEX_RE = /^#(?:[0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/i;
  const FUNC_RE = /^(?:rgb|rgba|hsl|hsla)\(\s*[0-9.,%\/\s]+\)$/i;
  function sanitizeColor(v) {
    if (typeof v !== 'string') return null;
    const s = v.trim();
    if (!s || s.length > 48) return null;
    if (/[;{}@]|url\(|var\(|expression|javascript:|<|>/i.test(s)) return null;
    return (HEX_RE.test(s) || FUNC_RE.test(s)) ? s : null;
  }
  function applyTheme(t) {
    if (!t || typeof t !== 'object') return;
    const root = document.documentElement;
    if (t.mode === 'light' || t.mode === 'dark') root.dataset.theme = t.mode;
    else if (t.mode === 'system' || t.mode === 'auto' || t.mode === null) delete root.dataset.theme;
    if (t.palette && typeof t.palette === 'object') {
      for (const k of Object.keys(t.palette)) {
        const key = k.charAt(0) === '-' ? k : ('--df-' + k);
        if (!THEME_VARS.has(key)) continue;
        const col = sanitizeColor(t.palette[k]);
        if (col) root.style.setProperty(key, col); else root.style.removeProperty(key);
      }
    }
  }

  // Escape closes the topmost transient host chrome. The inline editor owns Escape while active.
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape' || activeEditor) return;
    if (document.body.classList.contains('df-more-open')) { setMoreOpen(false); return; }
    if (document.body.classList.contains('df-dock-open')) { closeDock(); return; }
    if (propsPaneEl && !propsPaneEl.classList.contains('df-hidden')) { closePropertyGrid(); return; }
    if (isTreeDrawerLayout() && !document.body.classList.contains('df-tree-hidden')) setTreeVisible(false);
  });

  document.body.dataset.hostKind = hostKind;
  document.documentElement.dataset.hostKind = hostKind;
  updateHostLayout();
  buildTree();
  applyScale();

})();
