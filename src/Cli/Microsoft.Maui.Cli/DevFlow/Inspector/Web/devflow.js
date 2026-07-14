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
  // Normalize a MAUI color (Color.ToArgbHex() = "#AARRGGBB", or "#RRGGBB") to the "#RRGGBB" an
  // <input type=color> requires. Alpha is dropped in the picker (v1).
  function toHexColor(v) {
    if (v == null) return null;
    const s = String(v).trim().replace(/^#/, '');
    if (/^[0-9a-fA-F]{8}$/.test(s)) return '#' + s.slice(2);
    if (/^[0-9a-fA-F]{6}$/.test(s)) return '#' + s;
    return null;
  }

  const propsPaneEl = document.getElementById('df-props-pane');
  const propsBodyEl = document.getElementById('df-props');
  const propsElLabel = document.getElementById('df-props-el');
  const propsCloseBtn = document.getElementById('df-props-close');
  if (propsCloseBtn) propsCloseBtn.addEventListener('click', () => selectElement(null));
  let propsLoadToken = 0;

  function propsFor(type) {
    return [...(COMMON_PROPS[type] || []), ...COMMON_PROPS['*']];
  }

  async function apiPost(path, body) {
    try {
      const r = await fetch(`${basePath}${path}`, {
        method: 'POST',
        headers: inspectorToken
          ? { 'Content-Type': 'application/json', 'X-DevFlow-Inspector-Token': inspectorToken }
          : { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      return r.ok ? await r.json().catch(() => ({})) : null;
    } catch (err) {
      console.error(`${path} failed:`, err);
      return null;
    }
  }

  function closePropertyGrid() {
    if (propsPaneEl) propsPaneEl.classList.add('df-hidden');
    if (propsBodyEl) propsBodyEl.replaceChildren();
    if (propsElLabel) propsElLabel.textContent = '';
    propsLoadToken++;   // cancel any in-flight property load
  }

  async function openPropertyGrid(targetEl) {
    const elementId = targetEl.getAttribute('data-id');
    if (!elementId) return;

    const type = targetEl.dataset.type || 'Element';
    if (propsElLabel) propsElLabel.textContent = elementLabel(targetEl);
    if (propsPaneEl) propsPaneEl.classList.remove('df-hidden');
    if (!propsBodyEl) return;
    propsBodyEl.replaceChildren();
    const loadToken = ++propsLoadToken;

    for (const [name, kind, choices] of propsFor(type)) {
      const res = await apiPost('/api/getProperty', { elementId, name });
      if (loadToken !== propsLoadToken) return;   // selection changed while awaiting — abandon stale rows
      const val = res && res.value;

      const row = document.createElement('label');
      row.className = 'df-prop-row';
      const nameEl = document.createElement('span');
      nameEl.className = 'df-prop-name';
      nameEl.textContent = name;
      nameEl.title = name;
      const fieldWrap = document.createElement('span');
      fieldWrap.className = 'df-prop-field';

      let editor, readValue;
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
        const hex = toHexColor(val); if (hex) editor.value = hex;
        editor.title = val != null ? String(val) : '';
        readValue = () => editor.value;
      } else {
        editor = document.createElement('input'); editor.type = kind === 'number' ? 'number' : 'text'; editor.className = 'df-field';
        if (val != null) editor.value = val;
        readValue = () => editor.value;
      }

      editor.addEventListener('change', async () => {
        const value = readValue();
        const r = await apiPost('/api/setProperty', { elementId, name, value });
        if (recordingId && r) recordStep('setProperty', elById(elementId), { name, value });
        scheduleRefresh(200);
      });

      row.appendChild(nameEl);
      fieldWrap.appendChild(editor);
      row.appendChild(fieldWrap);
      propsBodyEl.appendChild(row);
    }
  }

  viewport.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    let el = document.elementFromPoint(e.clientX, e.clientY);
    while (el && el !== viewport && !(el.getAttribute && el.getAttribute('data-id'))) el = el.parentElement;
    if (el && el.getAttribute && el.getAttribute('data-id')) openPropertyGrid(el);
  });

  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closePropertyGrid(); });

  // ── Inspector chrome: interaction mode, hover highlight + badge, element tree (A + B) ──
  // Everything here lives in the SHARED bundle, so the browser, canvas, and VS Code hosts
  // all inherit it. Nothing host-specific belongs in this file.
  const tb = {
    interact: document.getElementById('df-mode-interact'),
    inspect: document.getElementById('df-mode-inspect'),
    tree: document.getElementById('df-toggle-tree'),
    bounds: document.getElementById('df-toggle-bounds'),
    status: document.getElementById('df-status'),
  };
  const treePanel = document.getElementById('df-tree');

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
  if (timelineCloseBtn) timelineCloseBtn.addEventListener('click', () => { if (timelineEl) timelineEl.classList.add('df-hidden'); });
  function timelineStart() {
    if (!timelineEl) return;
    if (timelineStepsEl) timelineStepsEl.replaceChildren();
    if (timelineMetaEl) timelineMetaEl.textContent = '';
    if (timelineTitleText) timelineTitleText.textContent = 'Recording';
    timelineEl.classList.remove('df-hidden', 'df-tl-done');
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
        boxShadow: '0 8px 30px rgba(0,0,0,0.5)', font: '13px system-ui, sans-serif',
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
      maxHeight: '60vh', overflowY: 'auto', background: '#1e1e1e', color: '#d4d4d4',
      border: '1px solid ' + (rep.ok ? '#4ec9b0' : '#a1260d'), borderRadius: '6px', padding: '8px 10px',
      font: '12px system-ui, sans-serif', boxShadow: '0 4px 16px rgba(0,0,0,0.5)',
    });
    const head = document.createElement('div');
    Object.assign(head.style, { display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '6px' });
    const title = document.createElement('strong');
    title.textContent = rep.ok ? `Replay passed (${rep.passed}/${rep.total})` : `Replay: ${rep.passed}/${rep.total} passed`;
    title.style.color = rep.ok ? '#4ec9b0' : '#f48771';
    const close = document.createElement('button');
    close.textContent = '×';
    Object.assign(close.style, { background: 'none', border: 'none', color: '#d4d4d4', cursor: 'pointer', fontSize: '16px', lineHeight: '1' });
    close.addEventListener('click', closeReplayReport);
    head.appendChild(title); head.appendChild(close);
    panel.appendChild(head);
    if (rep.error) {
      const e = document.createElement('div');
      e.textContent = rep.error; e.style.color = '#f48771'; e.style.marginBottom = '4px';
      panel.appendChild(e);
    }
    for (const s of (rep.results || [])) {
      const row = document.createElement('div');
      Object.assign(row.style, { display: 'flex', gap: '6px', padding: '2px 0', alignItems: 'baseline' });
      const dot = document.createElement('span');
      dot.textContent = s.ok ? '✓' : '✕';
      dot.style.color = s.ok ? '#4ec9b0' : '#f48771';
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

  function postToHost(type, data) {
    if (!framed || !bridgeId) return false;
    window.parent.postMessage(Object.assign({ v: 1, bridgeId, type }, data || {}), '*');
    return true;
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
    if (d.type === 'devflow:host') {
      hostCaps = Array.isArray(d.capabilities) ? d.capabilities : [];
      if (typeof d.hostKind === 'string' && d.hostKind) leaseHolderKind = d.hostKind;
      if (typeof d.hostLabel === 'string' && d.hostLabel) leaseHolderLabel = d.hostLabel;
      if (hsTimer) { clearTimeout(hsTimer); hsTimer = null; }
      updateHostButtons();
      if (d.theme) applyTheme(d.theme);                  // host may bundle its theme with the capability ack
    } else if (d.type === 'devflow:theme') {
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
  const dockRefreshBtn = document.getElementById('df-dock-refresh');
  const dockCloseBtn = document.getElementById('df-dock-close');
  const toggleDockBtn = document.getElementById('df-toggle-dock');
  let dockActiveTab = 'logs';
  let dockLoaded = false;
  let filesRoot = null, filesPath = '';
  let cdpWebviewId = null;

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
  function dockEmpty(msg) { dockBodyEl.replaceChildren(elh('div', { class: 'df-empty', text: msg })); }
  function setDockMeta(text) { if (dockMetaEl) dockMetaEl.textContent = text || ''; }
  const SECRET_KEY = /token|secret|password|auth|apikey|api[_-]?key|cookie|connectionstring/i;

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
    if (!Array.isArray(logs) || !logs.length) { dockEmpty(j && j.error ? j.error : 'No logs.'); return; }
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
  }

  function renderNetwork(j) {
    const reqs = j && j.requests;
    if (!Array.isArray(reqs) || !reqs.length) { dockEmpty(j && j.error ? j.error : 'No requests captured.'); return; }
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
  }

  async function loadNetworkDetail(id) {
    setDockMeta('loading…');
    const j = await apiPost('/api/network/detail', { id });
    setDockMeta('captured ' + new Date().toLocaleTimeString());
    const r = j && j.request;
    if (!r) { dockEmpty('Request detail unavailable.'); return; }
    const frag = document.createDocumentFragment();
    frag.append(elh('div', { class: 'df-dock-btn', text: '‹ Back to requests', onclick: () => loadTab('network') }));
    frag.append(elh('div', { class: 'df-section-title', text: (r.method || '') + ' ' + (r.url || '') }));
    frag.append(jsonView({
      status: r.statusCode, statusText: r.statusText, durationMs: r.durationMs,
      requestContentType: r.requestContentType, responseContentType: r.responseContentType,
      requestHeaders: r.requestHeaders, responseHeaders: r.responseHeaders,
      requestBody: r.requestBody, responseBody: r.responseBody, error: r.error,
    }));
    dockBodyEl.replaceChildren(frag);
  }

  function renderPreferences(j) {
    if (!j || j.ok === false) { dockEmpty((j && j.error) || 'Preferences unavailable.'); return; }
    const frag = document.createDocumentFragment();
    frag.append(elh('div', { class: 'df-section-title', text: 'Known preferences (values with secret-looking keys are masked)' }));
    frag.append(jsonView(j.preferences));
    dockBodyEl.replaceChildren(frag);
  }

  function renderDevice(j) {
    const dev = j && j.device;
    if (!dev || typeof dev !== 'object' || !Object.keys(dev).length) { dockEmpty('Device info unavailable.'); return; }
    const frag = document.createDocumentFragment();
    const labels = { 'device-info': 'Device', 'device-display': 'Display', battery: 'Battery', connectivity: 'Connectivity' };
    for (const k of Object.keys(dev)) {
      frag.append(elh('div', { class: 'df-section-title', text: labels[k] || k }));
      frag.append(jsonView(dev[k]));
    }
    dockBodyEl.replaceChildren(frag);
  }

  function renderSensors(j) {
    const frag = document.createDocumentFragment();
    const geoBtn = elh('button', { class: 'df-dock-btn', text: '📍 Read geolocation' });
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
    if (!rootsArr.length) { dockEmpty((j && j.error) || 'No storage roots advertised by this app.'); return; }
    const bar = elh('div', null);
    bar.append(elh('span', { class: 'df-kv-key', text: 'Root: ' }));
    const sel = elh('select', { class: 'df-dock-btn' });
    for (const r of rootsArr) sel.append(elh('option', { value: r.id, text: r.label }));
    if (filesRoot) sel.value = filesRoot;
    sel.addEventListener('change', () => { filesRoot = sel.value; filesPath = ''; loadFiles(); });
    bar.append(sel);
    frag.append(bar);
    frag.append(elh('div', { id: 'df-files-list' }));
    dockBodyEl.replaceChildren(frag);
    if (!filesRoot) filesRoot = rootsArr[0].id;
    sel.value = filesRoot;
    await loadFiles();
  }

  function extractRoots(roots) {
    let arr = [];
    if (Array.isArray(roots)) arr = roots;
    else if (roots && Array.isArray(roots.roots)) arr = roots.roots;
    return arr.map((r) => (typeof r === 'string'
      ? { id: r, label: r }
      : { id: r.id || r.name || r.root || r.path || '', label: r.name || r.id || r.path || '(root)' })).filter((r) => r.id);
  }

  async function loadFiles() {
    const list = document.getElementById('df-files-list');
    if (!list) return;
    list.textContent = 'Loading…';
    const j = await apiPost('/api/files/list', { root: filesRoot, path: filesPath });
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
    if (!entries.length) frag.append(elh('div', { class: 'df-empty', text: (j && j.error) || 'Empty.' }));
    else {
      const tbl = elh('table');
      tbl.append(elh('tr', null, elh('th', { text: 'Name' }), elh('th', { text: 'Size' })));
      // Directories first.
      for (const e of entries.filter((x) => x.dir)) {
        const nameCell = elh('td', null, elh('span', { class: 'df-dir', text: '📁 ' + e.name, onclick: () => { filesPath = (filesPath ? filesPath + '/' : '') + e.name; loadFiles(); } }));
        tbl.append(elh('tr', null, nameCell, elh('td', { text: '' })));
      }
      for (const e of entries.filter((x) => !x.dir)) tbl.append(elh('tr', null, elh('td', { text: '📄 ' + e.name }), elh('td', { text: e.size != null ? String(e.size) : '' })));
      frag.append(tbl);
    }
    list.replaceChildren(frag);
  }

  function extractEntries(files) {
    let arr = [];
    if (Array.isArray(files)) arr = files;
    else if (files && Array.isArray(files.entries)) arr = files.entries;
    else if (files && Array.isArray(files.files)) arr = files.files;
    return arr.map((e) => (typeof e === 'string'
      ? { name: e, dir: false, size: null }
      : { name: e.name || e.path || '', dir: !!(e.isDirectory || e.directory || e.dir || e.type === 'directory'), size: e.size != null ? e.size : e.length })).filter((e) => e.name);
  }

  const tabLoaders = {
    logs: async () => renderLogs(await apiPost('/api/logs', { limit: 200 })),
    network: async () => renderNetwork(await apiPost('/api/network', { limit: 100 })),
    preferences: async () => renderPreferences(await apiPost('/api/preferences', {})),
    device: async () => renderDevice(await apiPost('/api/device', {})),
    sensors: async () => renderSensors(await apiPost('/api/sensors', {})),
    files: async () => renderFiles(await apiPost('/api/files/roots', {})),
    webview: async () => renderWebView(await apiPost('/api/cdp/webviews', {})),
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
    dockActiveTab = name;
    for (const b of dockTabsEl.querySelectorAll('.df-dock-tab')) b.classList.toggle('df-active', b.getAttribute('data-tab') === name);
    dockEmpty('Loading…');
    setDockMeta('loading…');
    try { await tabLoaders[name](); setDockMeta('captured ' + new Date().toLocaleTimeString()); }
    catch (e) { dockEmpty('Failed to load.'); setDockMeta(''); }
  }

  function openDock() {
    dockEl.classList.remove('df-hidden');
    if (toggleDockBtn) toggleDockBtn.classList.add('df-active');
    if (!dockLoaded) { dockLoaded = true; loadTab(dockActiveTab); }
  }
  function closeDock() { dockEl.classList.add('df-hidden'); if (toggleDockBtn) toggleDockBtn.classList.remove('df-active'); }
  if (toggleDockBtn) toggleDockBtn.addEventListener('click', () => (dockEl.classList.contains('df-hidden') ? openDock() : closeDock()));
  if (dockCloseBtn) dockCloseBtn.addEventListener('click', closeDock);
  if (dockRefreshBtn) dockRefreshBtn.addEventListener('click', () => loadTab(dockActiveTab));
  for (const b of dockTabsEl.querySelectorAll('.df-dock-tab')) b.addEventListener('click', () => loadTab(b.getAttribute('data-tab')));

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
    const hidden = document.body.classList.toggle('df-tree-hidden');
    tb.tree.classList.toggle('df-active', !hidden);
    tb.tree.setAttribute('aria-expanded', String(!hidden));
  });
  tb.bounds.addEventListener('click', () => {
    const on = document.body.classList.toggle('df-show-all');
    tb.bounds.classList.toggle('df-active', on);
  });

  // ── Fit-to-container scaling (responsive) ──────────────────────────────────
  // In hosts smaller than the app's logical size (a Copilot canvas side panel, a short VS Code
  // panel) scale #app-viewport down so the whole app stays visible; #df-stage is sized to the
  // scaled box so centering + scrollbars behave. "Fit" (default) never upscales past 1:1 (keeps the
  // screenshot crisp); toggling it off shows 1:1 actual pixels and lets #df-viewport-wrap scroll.
  function applyScale() {
    const dw = parseFloat(viewport.dataset.width) || viewport.offsetWidth || 1;
    const dh = parseFloat(viewport.dataset.height) || viewport.offsetHeight || 1;
    // Publish the app's orientation so the layout can adapt when the inspector is width-constrained:
    // a portrait app is narrow (keep the panels beside it, a row); a landscape app is wide (drop the
    // panels below the screenshot, a column). See the "Smart layout" media query in devflow.css.
    document.body.dataset.appOrient = dh > dw ? 'portrait' : 'landscape';
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
  window.addEventListener('resize', scheduleScale);

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

  // Escape hides the tree on a narrow host to free space for the screenshot — whether the tree is
  // a portrait overlay drawer, a landscape bottom strip, or an inline column. (The inline editor,
  // when open, handles Escape itself, so defer to it.)
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape' || activeEditor) return;
    if (document.documentElement.clientWidth <= 640 && !document.body.classList.contains('df-tree-hidden')) {
      document.body.classList.add('df-tree-hidden');
      tb.tree.classList.remove('df-active');
      tb.tree.setAttribute('aria-expanded', 'false');
    }
  });

  // Collapse the tree by default in very narrow hosts (e.g. a VS Code side panel).
  if (document.documentElement.clientWidth < 480) {
    document.body.classList.add('df-tree-hidden');
    tb.tree.classList.remove('df-active');
    tb.tree.setAttribute('aria-expanded', 'false');
  }
  buildTree();
  applyScale();

})();
