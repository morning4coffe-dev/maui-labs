// ui.mjs — the human-facing surface rendered inside the Copilot canvas side panel.
//
// Layout: a phone frame showing the app's LIVE screenshot, with the visual-tree bounds drawn
// as an overlay. Click the screenshot to hit-test + select. A sidebar shows the tree, an
// EDITABLE property grid for the selected element, a theme toggle, and an action timeline.
//
// It talks to the extension's loopback server:
//   GET  /            → this HTML
//   GET  /events      → SSE stream of store snapshots
//   GET  /shot?seq=N  → current screenshot PNG
//   POST /control     → { action, ... } → JSON result
//
// All element fields are camelCase (id, type, automationId, text, bounds:{x,y,width,height}),
// matching what the DevFlow agent serializes.

export function renderHtml(bridgeId = "") {
  const bridgeLiteral = JSON.stringify(String(bridgeId));
  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>MAUI Live Canvas</title>
<style>
  :root {
    /* LIGHT palette (default; also used for prefers-color-scheme: light) — GitHub Primer light */
    --bg: #ffffff; --panel: #f6f8fa; --panel2: #eaeef2; --border: #d0d7de;
    --text: #1f2328; --muted: #656d76;
    --accent: #0969da; --accent2: #1a7f37; --warn: #9a6700; --err: #cf222e;
    --btn-bg: #f6f8fa; --btn-bg-hover: #eaeef2; --input-bg: #ffffff;
    --node-hover: #eaeef2; --sel-bg: rgba(9,105,218,.10);
    --surface: #ffffff; --shadow: rgba(31,35,40,.10);
    --hover-outline: rgba(9,105,218,.55); --hover-fill: rgba(9,105,218,.06);
    --allbounds-outline: rgba(9,105,218,.26);
    --sel-outline: #0969da; --sel-fill: rgba(9,105,218,.12);
    --ok-border: rgba(26,127,55,.35); --ok-bg: rgba(26,127,55,.08); --err-border: rgba(207,34,46,.35);
  }
  /* DARK palette — GitHub Primer dark. Applied when the host prefers dark (Auto) or the
     panel appearance override is set to Dark. Duplicated across the two selectors because
     CSS custom properties can't be shared between a media query and an attribute rule. */
  @media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
      --bg: #0d1117; --panel: #161b22; --panel2: #12161d; --border: #30363d;
      --text: #e6edf3; --muted: #8b949e;
      --accent: #2f81f7; --accent2: #3fb950; --warn: #d29922; --err: #f85149;
      --btn-bg: #21262d; --btn-bg-hover: #2a3038; --input-bg: #0d1117;
      --node-hover: #1c2230; --sel-bg: rgba(47,129,247,.18);
      --surface: #0b0f14; --shadow: rgba(0,0,0,.5);
      --hover-outline: rgba(88,166,255,.6); --hover-fill: rgba(88,166,255,.08);
      --allbounds-outline: rgba(88,166,255,.30);
      --sel-outline: #3fb950; --sel-fill: rgba(63,185,80,.14);
      --ok-border: rgba(63,185,80,.30); --ok-bg: rgba(63,185,80,.08); --err-border: rgba(248,81,73,.35);
    }
  }
  :root[data-theme="dark"] {
    --bg: #0d1117; --panel: #161b22; --panel2: #12161d; --border: #30363d;
    --text: #e6edf3; --muted: #8b949e;
    --accent: #2f81f7; --accent2: #3fb950; --warn: #d29922; --err: #f85149;
    --btn-bg: #21262d; --btn-bg-hover: #2a3038; --input-bg: #0d1117;
    --node-hover: #1c2230; --sel-bg: rgba(47,129,247,.18);
    --surface: #0b0f14; --shadow: rgba(0,0,0,.5);
    --hover-outline: rgba(88,166,255,.6); --hover-fill: rgba(88,166,255,.08);
    --allbounds-outline: rgba(88,166,255,.30);
    --sel-outline: #3fb950; --sel-fill: rgba(63,185,80,.14);
    --ok-border: rgba(63,185,80,.30); --ok-bg: rgba(63,185,80,.08); --err-border: rgba(248,81,73,.35);
  }
  * { box-sizing: border-box; }
  body { margin: 0; font: 13px/1.45 -apple-system, "Segoe UI", Roboto, sans-serif;
         background: var(--bg); color: var(--text); transition: background .15s ease, color .15s ease; }
  header { display: flex; align-items: center; gap: 8px; padding: 8px 12px;
           border-bottom: 1px solid var(--border); background: var(--panel); }
  header .title { font-weight: 600; }
  header .pill { font-size: 11px; padding: 2px 8px; border-radius: 999px;
                 border: 1px solid var(--border); color: var(--muted); }
  header .pill.ok { color: var(--accent2); border-color: var(--ok-border); background: var(--ok-bg); }
  header .pill.bad { color: var(--err); border-color: var(--err-border); }
  header .spacer { flex: 1; }
  header select.agentpick, header select.uipick { font: inherit; color: var(--text); background: var(--btn-bg);
           border: 1px solid var(--border); border-radius: 6px; padding: 4px 8px; cursor: pointer; }
  header select.agentpick { max-width: 190px; }
  header select.uipick { font-size: 12px; }
  header select.agentpick:hover, header select.uipick:hover { border-color: var(--accent); }
  header select.agentpick:disabled { opacity: .6; cursor: default; }
  button { font: inherit; color: var(--text); background: var(--btn-bg); border: 1px solid var(--border);
           border-radius: 6px; padding: 4px 10px; cursor: pointer; transition: background .12s ease, border-color .12s ease; }
  button:hover { border-color: var(--accent); background: var(--btn-bg-hover); }
  button.primary { background: var(--accent); border-color: var(--accent); color: #fff; }
  button.toggled { background: var(--accent); border-color: var(--accent); color: #fff; }
  button.toggled:hover { background: var(--accent); filter: brightness(1.05); }
  main { display: grid; grid-template-columns: minmax(280px, 380px) 1fr; height: calc(100vh - 45px); }
  .stage { display: flex; align-items: flex-start; justify-content: center; padding: 18px; overflow: auto; }
  /* Flat, theme-aware surface (no skeuomorphic phone bezel — correct for desktop and mobile). */
  .frame { position: relative; background: var(--surface); border: 1px solid var(--border);
           border-radius: 10px; box-shadow: 0 6px 24px var(--shadow); overflow: hidden; }
  .frame img { display: block; max-width: 100%; border-radius: 9px; }
  .overlay { position: absolute; inset: 0; pointer-events: none; }
  /* Hit-targets are invisible by default — they light up only on hover or when selected,
     so the screenshot stays clean (PR #295 model). "Show all bounds" x-rays every box. */
  .hit { position: absolute; pointer-events: auto; border: 1px solid transparent; border-radius: 2px;
         transition: border-color .08s ease, background .08s ease; }
  .hit:hover { border-color: var(--hover-outline); background: var(--hover-fill); }
  .overlay.showall .hit { border-color: var(--allbounds-outline); }
  .hit.sel { border: 2px solid var(--sel-outline); background: var(--sel-fill); pointer-events: none; }
  .side { border-left: 1px solid var(--border); background: var(--panel); overflow: auto; }
  section { border-bottom: 1px solid var(--border); }
  section h3 { margin: 0; padding: 8px 12px; font-size: 11px; letter-spacing: .04em;
               text-transform: uppercase; color: var(--muted); background: var(--panel2);
               display: flex; align-items: center; gap: 8px; }
  section h3 .spacer { flex: 1; }
  .minitog { font-size: 10px; font-weight: 500; text-transform: none; letter-spacing: 0;
             color: var(--muted); display: inline-flex; align-items: center; gap: 4px; cursor: pointer; }
  .minitog input { cursor: pointer; margin: 0; }
  .tree { max-height: 34vh; overflow: auto; padding: 4px 0; }
  .node { padding: 2px 12px; cursor: pointer; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .node:hover { background: var(--node-hover); }
  .node.sel { background: var(--sel-bg); }
  .node .type { color: var(--accent); }
  .node .aid { color: var(--warn); }
  .node .txt { color: var(--muted); }
  .grid { padding: 8px 12px; }
  .grid .row { display: grid; grid-template-columns: 96px 1fr; gap: 8px; align-items: center; margin: 4px 0; }
  .grid label { color: var(--muted); font-size: 12px; }
  .grid input, .grid select { width: 100%; background: var(--input-bg); color: var(--text);
           border: 1px solid var(--border); border-radius: 5px; padding: 4px 6px; }
  .grid .editable { display: grid; grid-template-columns: 1fr auto; gap: 6px; }
  .muted { color: var(--muted); }
  .timeline { max-height: 24vh; overflow: auto; padding: 4px 12px; font-family: ui-monospace, monospace; font-size: 11px; }
  .timeline .ev { padding: 2px 0; border-bottom: 1px dashed var(--border); }
  .timeline .ev.bad { color: var(--err); }
  .timeline .ev .k { color: var(--accent); }
  .empty { padding: 14px 12px; color: var(--muted); }
  .err { color: var(--err); padding: 6px 12px; }
  .agentbadge { display: inline-flex; align-items: center; gap: 6px; margin: 2px 12px 6px;
                padding: 3px 9px; font-size: 11px; color: var(--accent2); border: 1px solid var(--ok-border);
                border-radius: 999px; background: var(--ok-bg); }
  .agentbadge .dot { width: 6px; height: 6px; border-radius: 50%; background: var(--accent2);
                     box-shadow: 0 0 0 0 rgba(63,185,80,.6); animation: pulse 2s infinite; }
  @keyframes pulse { 0% { box-shadow: 0 0 0 0 rgba(63,185,80,.5);} 70% { box-shadow: 0 0 0 5px rgba(63,185,80,0);} 100% { box-shadow: 0 0 0 0 rgba(63,185,80,0);} }
  .selactions { display: flex; align-items: center; gap: 10px; padding: 8px 12px; border-bottom: 1px solid var(--border); }
  .selactions #btnAttach { background: var(--accent); border-color: var(--accent); color: #fff; }
  .selactions #btnAttach:hover:not(:disabled) { filter: brightness(1.08); }
  .selactions #btnAttach:disabled { background: var(--btn-bg); border-color: var(--border); color: var(--muted); cursor: default; }
  .autotog { font-size: 12px; color: var(--muted); display: inline-flex; align-items: center; gap: 5px; cursor: pointer; user-select: none; }
  .autotog input { cursor: pointer; }
  /* ── Workflow Recorder ── */
  .recdot { display: inline-block; width: 9px; height: 9px; border-radius: 50%; background: var(--err, #e5534b);
            margin-left: 6px; vertical-align: middle; animation: recpulse 1.2s ease-in-out infinite; }
  @keyframes recpulse { 0%,100% { opacity: 1; } 50% { opacity: .35; } }
  .recbar { display: flex; align-items: center; gap: 6px; margin-bottom: 6px; }
  .recbar2 { margin-bottom: 8px; }
  .recname { flex: 1; min-width: 0; background: var(--input-bg, var(--panel)); color: var(--text);
             border: 1px solid var(--border); border-radius: 6px; padding: 5px 8px; font-size: 12px; }
  .testpick { max-width: 46%; background: var(--input-bg, var(--panel)); color: var(--text);
              border: 1px solid var(--border); border-radius: 6px; padding: 4px 6px; font-size: 12px; }
  .steps { display: flex; flex-direction: column; gap: 3px; max-height: 200px; overflow: auto; }
  .steps .step { display: flex; align-items: baseline; gap: 7px; padding: 4px 6px; border: 1px solid var(--border);
                 border-radius: 6px; background: var(--panel); font-size: 12px; }
  .steps .step .seq { color: var(--muted); font-variant-numeric: tabular-nums; min-width: 16px; text-align: right; }
  .steps .step .lbl { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .steps .step .badge { font-size: 10px; padding: 1px 5px; border-radius: 999px; border: 1px solid var(--border); color: var(--muted); }
  .steps .step .badge.warn { color: var(--err); border-color: var(--err-border, var(--border)); }
  .report { margin-top: 8px; border: 1px solid var(--border); border-radius: 8px; padding: 8px; font-size: 12px; background: var(--panel); }
  .report .rhead { font-weight: 600; margin-bottom: 5px; }
  .report .rrow { display: flex; align-items: baseline; gap: 6px; padding: 2px 0; }
  .report .rrow .mk { min-width: 14px; }
  .report .rrow.pass .mk { color: var(--ok, #57ab5a); }
  .report .rrow.fail .mk { color: var(--err, #e5534b); }
  .report .rrow .rlbl { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .report .rsum.pass { color: var(--ok, #57ab5a); }
  .report .rsum.fail { color: var(--err, #e5534b); }
  .toast { position: fixed; bottom: 14px; left: 50%; transform: translateX(-50%);
           background: var(--panel); border: 1px solid var(--border); color: var(--text);
           padding: 8px 14px; border-radius: 8px; box-shadow: 0 6px 24px var(--shadow);
           font-size: 12px; max-width: 80vw; opacity: 0; pointer-events: none;
           transition: opacity .18s ease; z-index: 50; }
  .toast.show { opacity: 1; }
  .toast.bad { border-color: var(--err-border); color: var(--err); }
</style>
</head>
<body>
<header>
  <span class="title">MAUI Live Canvas</span>
  <span id="conn" class="pill">connecting…</span>
  <span id="app" class="pill"></span>
  <span class="spacer"></span>
  <select id="agentPick" class="agentpick" title="Attached running app / platform"><option value="">…</option></select>
  <button id="btnRefresh">Refresh</button>
  <button id="btnBack">Back</button>
  <button id="btnTheme" title="Toggle the running app's light/dark theme">Theme</button>
  <button id="btnShot">Screenshot</button>
  <button id="btnBounds" title="X-ray: outline every element's bounds on the screenshot" aria-pressed="false">⧉ Bounds</button>
  <select id="uiTheme" class="uipick" title="Panel appearance (follows the Copilot app by default)">
    <option value="auto">Auto</option>
    <option value="light">Light</option>
    <option value="dark">Dark</option>
  </select>
</header>
<main>
  <div class="stage">
    <div id="frame" class="frame">
      <img id="shot" alt="app screenshot" />
      <div id="overlay" class="overlay"></div>
    </div>
  </div>
  <div class="side">
    <section>
      <h3>Visual Tree</h3>
      <div id="tree" class="tree"><div class="empty">Waiting for app…</div></div>
    </section>
    <section>
      <h3>Selected Element</h3>
      <div id="selActions" class="selactions">
        <button id="btnAttach" title="Push this selection into the Copilot composer as context" disabled>📎 Attach to Copilot</button>
        <label class="autotog" title="Automatically attach each element you select"><input type="checkbox" id="autoAttach" /> Auto-attach</label>
      </div>
      <div id="props" class="grid"><div class="empty">Nothing selected. Click the screenshot or a tree node.</div></div>
    </section>
    <section>
      <h3>Workflow Recorder <span id="recDot" class="recdot" title="recording" hidden></span></h3>
      <div class="recbar">
        <input id="recName" class="recname" placeholder="scenario name (e.g. add a subscription)" />
        <button id="btnRec" title="Start recording the actions you perform on the app">● Record</button>
        <button id="btnRecSave" title="Stop recording and save the test into the project's maui-tests folder" disabled>⏹ Save</button>
      </div>
      <div class="recbar recbar2">
        <button id="btnRecDel" title="Delete the last recorded step" disabled>⌫ Last</button>
        <button id="btnRecClear" title="Discard the current recording" disabled>Clear</button>
        <span class="spacer"></span>
        <select id="testPick" class="testpick" title="Saved workflow tests"><option value="">saved tests…</option></select>
        <button id="btnReplay" title="Replay the selected test and verify it" disabled>▶ Replay</button>
      </div>
      <div id="steps" class="steps"><div class="empty">Not recording. Enter a name and press Record, then act on the app.</div></div>
      <div id="report" class="report" hidden></div>
    </section>
    <section>
      <h3>Timeline</h3>
      <div id="timeline" class="timeline"></div>
    </section>
  </div>
</main>
<script type="module">
  const $ = (s) => document.querySelector(s);
  let snap = null;
  const shot = $("#shot"), overlay = $("#overlay");

  async function control(action, payload = {}) {
    const res = await fetch("/control", {
      method: "POST", headers: { "content-type": "application/json" },
      body: JSON.stringify({ bridgeId: ${bridgeLiteral}, action, ...payload }),
    });
    try { return await res.json(); } catch { return { ok: false }; }
  }

  // ── Screenshot scaling ──────────────────────────────────────────────────────
  function scale() {
    const sx = shot.naturalWidth ? shot.clientWidth / shot.naturalWidth : 1;
    const sy = shot.naturalHeight ? shot.clientHeight / shot.naturalHeight : 1;
    return { sx, sy };
  }

  function flatten(roots, out = []) {
    for (const el of roots || []) { out.push(el); if (el.children) flatten(el.children, out); }
    return out;
  }

  let showAllBounds = false;

  function drawOverlay() {
    overlay.innerHTML = "";
    overlay.classList.toggle("showall", showAllBounds);
    if (!snap) return;
    const { sx, sy } = scale();
    if (!(sx > 0) || !(sy > 0)) return;                // image not laid out yet
    const selWin = snap.selectedElement?.windowBounds; // authoritative for the selected node
    let selBox = null;
    for (const el of flatten(snap.roots)) {
      if (el.isVisible === false) continue;            // skip collapsed/offscreen nodes
      const isSel = String(el.id) === String(snap.selectedId);
      // Selected → prefer authoritative windowBounds (from hit-test); others → approx absBounds.
      const b = (isSel && selWin) ? selWin : (el.absBounds || el.bounds);
      if (!b) continue;
      if (!(b.width > 0) || !(b.height > 0)) continue; // skip zero-size nodes
      const d = document.createElement("div");
      d.className = "hit" + (isSel ? " sel" : "");
      d.dataset.id = el.id;
      d.title = (el.type || "?") + (el.automationId ? " #" + el.automationId : "");
      d.style.left = (b.x * sx) + "px";
      d.style.top = (b.y * sy) + "px";
      d.style.width = (b.width * sx) + "px";
      d.style.height = (b.height * sy) + "px";
      if (isSel) selBox = d; else overlay.appendChild(d);
    }
    if (selBox) overlay.appendChild(selBox);           // draw selection on top
  }

  // Keep the overlay aligned as the image loads or the panel resizes (naturalWidth-based).
  shot.addEventListener("load", drawOverlay);
  window.addEventListener("resize", drawOverlay);
  if (window.ResizeObserver) {
    const ro = new ResizeObserver(() => drawOverlay());
    ro.observe($("#frame"));
  }

  // Click the screenshot → authoritative agent hit-test in DEVICE coords. The invisible
  // hit-target divs are for HOVER highlight only; clicks bubble up to the frame so the
  // real MAUI hit-test (z-order / InputTransparent / clipping aware) decides selection.
  $("#frame").addEventListener("click", async (e) => {
    const r = shot.getBoundingClientRect();
    if (!r.width || !r.height) return;
    const { sx, sy } = scale();
    const x = (e.clientX - r.left) / (sx || 1);
    const y = (e.clientY - r.top) / (sy || 1);
    if (x < 0 || y < 0) return;
    await control("hitTest", { x, y });
  });

  // "⧉ Bounds" → x-ray every element's bounds; default OFF (clean screenshot).
  $("#btnBounds").addEventListener("click", () => {
    showAllBounds = !showAllBounds;
    const b = $("#btnBounds");
    b.classList.toggle("toggled", showAllBounds);
    b.setAttribute("aria-pressed", showAllBounds ? "true" : "false");
    drawOverlay();
  });

  // ── Tree ────────────────────────────────────────────────────────────────────
  function renderTree() {
    const host = $("#tree");
    const rows = [];
    const walk = (el, depth) => {
      const sel = String(el.id) === String(snap.selectedId);
      const aid = el.automationId ? ' <span class="aid">#' + esc(el.automationId) + "</span>" : "";
      const txt = el.text ? ' <span class="txt">"' + esc(String(el.text).slice(0, 24)) + '"</span>' : "";
      rows.push(
        '<div class="node' + (sel ? " sel" : "") + '" data-id="' + esc(el.id) + '" style="padding-left:' +
        (12 + depth * 14) + 'px"><span class="type">' + esc(el.type || "?") + "</span>" + aid + txt + "</div>"
      );
      for (const c of el.children || []) walk(c, depth + 1);
    };
    for (const r of snap.roots || []) walk(r, 0);
    host.innerHTML = rows.length ? rows.join("") : '<div class="empty">Empty tree.</div>';
    host.querySelectorAll(".node").forEach((n) =>
      n.addEventListener("click", () => control("select", { id: n.dataset.id }))
    );
  }

  // ── Property grid (editable) ─────────────────────────────────────────────────
  function renderProps() {
    const host = $("#props");
    const el = snap.selectedElement;
    if (!el) { host.innerHTML = '<div class="empty">Nothing selected. Click the screenshot or a tree node.</div>'; return; }
    const b = el.windowBounds || el.absBounds || el.bounds || {};
    const ro = (k, v) => '<div class="row"><label>' + k + '</label><span class="muted">' + esc(v ?? "") + "</span></div>";
    const editable = (k, v) =>
      '<div class="row"><label>' + k + '</label><div class="editable">' +
      '<input id="edit-' + k + '" value="' + esc(v ?? "") + '" />' +
      '<button data-prop="' + k + '" class="applyBtn">Apply</button></div></div>';
    const parts = [
      '<div class="agentbadge"><span class="dot"></span>Selected — press &ldquo;Attach to Copilot&rdquo; above, or ask about &ldquo;the selected element&rdquo;</div>',
      ro("id", el.id), ro("type", el.type),
      el.automationId != null ? ro("automationId", el.automationId) : "",
      editable("Text", el.text ?? ""),
      el.value != null ? editable("Value", el.value) : "",
      ro("visible", el.isVisible), ro("enabled", el.isEnabled),
      ro("bounds", b.x + ", " + b.y + "  " + b.width + "×" + b.height),
      '<div class="row"><label></label><div><button id="btnTap" class="primary">Tap</button> ' +
      '<button id="btnGet">Read Text</button></div></div>',
    ];
    host.innerHTML = parts.join("");
    host.querySelectorAll(".applyBtn").forEach((btn) =>
      btn.addEventListener("click", async () => {
        const prop = btn.dataset.prop;
        const val = $("#edit-" + prop).value;
        const r = await control("applyVerify", { id: el.id, name: prop, value: val });
        btn.textContent = r.verified ? "✓ verified" : (r.ok ? "set (unverified)" : "failed");
        setTimeout(() => (btn.textContent = "Apply"), 1600);
      })
    );
    const tap = $("#btnTap"); if (tap) tap.addEventListener("click", () => control("tap", { id: el.id }));
    const get = $("#btnGet"); if (get) get.addEventListener("click", async () => {
      const r = await control("getProperty", { id: el.id, name: "Text" });
      get.textContent = r.ok ? ('Text = "' + String(r.value ?? "") + '"') : "read failed";
      setTimeout(() => (get.textContent = "Read Text"), 2000);
    });
  }

  function renderTimeline() {
    const host = $("#timeline");
    const evs = (snap.timeline || []).slice().reverse();
    host.innerHTML = evs.map((e) =>
      '<div class="ev' + (e.ok ? "" : " bad") + '"><span class="k">' + esc(e.kind) + "</span> " +
      esc(JSON.stringify(e.detail)) + "</div>").join("") || '<div class="empty">No actions yet.</div>';
  }

  // ── Workflow Recorder panel ──────────────────────────────────────────────────
  function renderRecorder() {
    const rec = (snap && snap.recorder) || null;
    const recording = !!(snap && snap.recording);
    const count = rec ? rec.count : 0;
    const dot = $("#recDot"); if (dot) dot.hidden = !recording;

    const nameEl = $("#recName");
    if (nameEl && document.activeElement !== nameEl && rec && rec.name) nameEl.value = rec.name;

    // Button states.
    const set = (id, dis) => { const b = $(id); if (b) b.disabled = dis; };
    set("#btnRec", recording);
    $("#btnRec") && ($("#btnRec").textContent = recording ? "● Recording…" : "● Record");
    set("#btnRecSave", !recording && count === 0);
    set("#btnRecDel", count === 0);
    set("#btnRecClear", count === 0 && !recording);

    const host = $("#steps");
    if (host) {
      if (rec && rec.steps && rec.steps.length) {
        host.innerHTML = rec.steps.map((s) => {
          const badges = [];
          if (s.fragile) badges.push('<span class="badge warn" title="No AutomationId — selector may be fragile">fragile</span>');
          if (s.asserts) badges.push('<span class="badge" title="auto-assertions">' + s.asserts + ' chk</span>');
          return '<div class="step"><span class="seq">' + s.seq + '</span>' +
                 '<span class="lbl" title="' + esc(s.label) + '">' + esc(s.label) + '</span>' +
                 badges.join(" ") + '</div>';
        }).join("");
        host.scrollTop = host.scrollHeight;
      } else {
        host.innerHTML = recording
          ? '<div class="empty">Recording… act on the app (tap, type, navigate) to capture steps.</div>'
          : '<div class="empty">Not recording. Enter a name and press Record, then act on the app.</div>';
      }
    }
  }

  function renderReport(rep) {
    const host = $("#report");
    if (!host) return;
    if (!rep) { host.hidden = true; host.innerHTML = ""; return; }
    if (rep.error && !Array.isArray(rep.results)) {
      host.hidden = false;
      host.innerHTML = '<div class="rhead rsum fail">Replay failed</div><div class="rrow">' + esc(rep.error) + '</div>';
      return;
    }
    const rows = (rep.results || []).map((r) => {
      const cls = r.ok ? "pass" : "fail";
      const mk = r.ok ? "✓" : "✗";
      const extra = r.error ? " — " + r.error
        : (r.asserts || []).filter((a) => !a.ok).map((a) => " — " + a.kind + (a.expected != null ? ' "' + a.expected + '"' : "") + " failed").join("");
      return '<div class="rrow ' + cls + '"><span class="mk">' + mk + '</span>' +
             '<span class="rlbl" title="' + esc(r.label || r.action) + '">' + r.seq + ". " + esc(r.label || r.action) + esc(extra) + '</span></div>';
    }).join("");
    const sumCls = rep.ok ? "pass" : "fail";
    host.hidden = false;
    host.innerHTML =
      '<div class="rhead rsum ' + sumCls + '">' + (rep.ok ? "PASS" : "FAIL") + " · " + (rep.passed || 0) + "/" + (rep.total || 0) +
      " steps" + (rep.name ? " · " + esc(rep.name) : "") + "</div>" + rows;
  }

  let lastTestsSig = "";
  function renderTests(list) {
    const sel = $("#testPick");
    if (!sel) return;
    const tests = (list && list.tests) || [];
    const sig = tests.map((t) => t.name).join("|");
    if (sig === lastTestsSig) return;
    lastTestsSig = sig;
    const keep = sel.value;
    sel.innerHTML = '<option value="">saved tests…</option>' +
      tests.map((t) => '<option value="' + esc(t.name) + '">' + esc(t.name) + "</option>").join("");
    if (keep) sel.value = keep;
    $("#btnReplay") && ($("#btnReplay").disabled = !sel.value);
  }

  async function refreshTests() {
    try { renderTests(await control("record.list")); } catch {}
  }

  function renderHeader() {
    const c = $("#conn");
    c.textContent = snap.connected ? "connected" : "no agent";
    c.className = "pill " + (snap.connected ? "ok" : "bad");
    const a = $("#app");
    const i = snap.info || {};
    a.textContent = (i.appName || "app") + " · " + (i.platform || "?");
    const bt = $("#btnTheme");
    if (bt) bt.textContent = (i.theme === "dark") ? "\u263e Dark" : "\u2600 Light";
  }

  function platLabel(p) {
    const s = String(p || "").toLowerCase();
    if (s.includes("android")) return "Android";
    if (s.includes("maccatalyst")) return "Mac Catalyst";
    if (s.includes("ios")) return "iOS";
    if (s.includes("macos")) return "macOS";
    if (s.includes("windows") || s.includes("winui")) return "Windows";
    if (s.includes("tizen")) return "Tizen";
    return p || "device";
  }

  // Rebuild the platform picker only when the set of agents (or the active one) changes,
  // so we never clobber the dropdown while the user is interacting with it.
  let lastAgentsSig = "";
  function renderAgents() {
    const sel = $("#agentPick");
    if (!sel) return;
    const agents = (snap && snap.agents) || [];
    const active = (snap && snap.activePort) || null;
    const sig = agents.map((a) => a.port + ":" + a.platform).join("|") + "@" + active;
    if (sig === lastAgentsSig) return;
    lastAgentsSig = sig;
    if (!agents.length) {
      sel.innerHTML = '<option value="">no running apps</option>';
      sel.disabled = true;
      return;
    }
    sel.disabled = false;
    const counts = {};
    for (const a of agents) { const b = platLabel(a.platform); counts[b] = (counts[b] || 0) + 1; }
    sel.innerHTML = agents.map((a) => {
      const base = platLabel(a.platform);
      const label = counts[base] > 1 ? base + " (:" + a.port + ")" : base;
      const on = a.port === active ? " selected" : "";
      return '<option value="' + a.port + '" data-platform="' + esc(a.platform) + '"' + on + ">" + esc(label) + "</option>";
    }).join("");
  }

  let lastShotSeq = -1;
  let lastRev = -1;
  function apply(next) {
    // Drop out-of-order snapshots so overlapping refresh/sync/settle can't flicker the UI.
    if (next && typeof next.rev === "number") {
      if (next.rev <= lastRev) return;
      lastRev = next.rev;
    }
    snap = next;
    renderHeader(); renderAgents(); renderTree(); renderProps(); renderTimeline(); renderRecorder();
    const btnA = $("#btnAttach");
    if (btnA) btnA.disabled = !(snap && snap.selectedId);
    maybeAutoAttach();
    if (snap.shotSeq !== lastShotSeq) {
      lastShotSeq = snap.shotSeq;
      shot.src = "/shot?seq=" + snap.shotSeq;   // cache-bust
    } else {
      drawOverlay();
    }
  }

  function esc(s) { return String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c])); }

  $("#btnRefresh").addEventListener("click", () => control("refresh"));
  $("#btnShot").addEventListener("click", () => control("screenshot"));
  $("#btnBack").addEventListener("click", () => control("back"));
  $("#agentPick").addEventListener("change", async (e) => {
    const opt = e.target.selectedOptions && e.target.selectedOptions[0];
    if (!opt || !opt.value) return;
    const port = Number(opt.value);
    const platform = opt.getAttribute("data-platform") || undefined;
    e.target.disabled = true;
    try { await control("selectAgent", { port, platform }); }
    finally { e.target.disabled = false; }
  });
  $("#btnTheme").addEventListener("click", async () => {
    const cur = (snap && snap.info && snap.info.theme) || "light";
    await control("setTheme", { theme: cur === "dark" ? "light" : "dark" });
  });

  // ── Panel appearance (chrome theme) ──────────────────────────────────────────
  // The panel follows the Copilot host via prefers-color-scheme (Auto). If the host
  // webview doesn't propagate that media query, the user can force Light/Dark here;
  // the choice persists in localStorage. This is the chrome theme — independent of the
  // running app's theme shown in the screenshot (host and app themes may differ).
  const uiThemeSel = $("#uiTheme");
  function applyUiTheme(mode) {
    const root = document.documentElement;
    if (mode === "light" || mode === "dark") root.dataset.theme = mode;
    else { delete root.dataset.theme; mode = "auto"; }
    try { localStorage.setItem("mlc.uiTheme", mode); } catch {}
    if (uiThemeSel) uiThemeSel.value = mode;
  }
  let savedUiTheme = "auto";
  try { savedUiTheme = localStorage.getItem("mlc.uiTheme") || "auto"; } catch {}
  applyUiTheme(savedUiTheme);
  if (uiThemeSel) uiThemeSel.addEventListener("change", (e) => applyUiTheme(e.target.value));

  // ── Selection → Copilot context ──────────────────────────────────────────────
  // "Attach to Copilot" drops the current selection into the composer as a context
  // pill (server calls session.extensions.sendAttachmentsToMessage). Auto-attach
  // does the same automatically on each NEW selection, deduped by id. Default OFF.
  let autoAttachOn = false;
  let lastPushedId = null;
  let autoAttachTimer = null;
  const toast = $("#toast");

  function showToast(msg, ok = true) {
    toast.textContent = msg;
    toast.className = "toast show" + (ok ? "" : " bad");
    clearTimeout(showToast._t);
    showToast._t = setTimeout(() => { toast.className = "toast"; }, 2600);
  }

  async function attachSelection(auto = false) {
    const r = await control("attachSelection");
    if (r && r.ok) {
      showToast(r.status || "Attached to Copilot");
    } else if (r && r.code === "unsupported_runtime") {
      showToast(r.error || "This Copilot build can't receive canvas context.", false);
    } else if (!auto) {
      showToast((r && r.error) || "Nothing to attach.", false);
    }
    return r;
  }

  function maybeAutoAttach() {
    if (!autoAttachOn) return;
    const id = snap && snap.selectedId;
    if (!id || String(id) === String(lastPushedId)) return;
    clearTimeout(autoAttachTimer);
    autoAttachTimer = setTimeout(async () => {
      const cur = snap && snap.selectedId;                 // re-check after debounce
      if (!autoAttachOn || !cur || String(cur) === String(lastPushedId)) return;
      lastPushedId = cur;
      await attachSelection(true);
    }, 250);
  }

  $("#btnAttach").addEventListener("click", () => attachSelection(false));
  $("#autoAttach").addEventListener("change", (e) => {
    autoAttachOn = !!e.target.checked;
    if (autoAttachOn) { lastPushedId = null; maybeAutoAttach(); }  // attach current selection now
  });

  // ── Workflow Recorder controls ───────────────────────────────────────────────
  $("#btnRec").addEventListener("click", async () => {
    const name = ($("#recName").value || "").trim();
    const r = await control("record.start", { name });
    showToast(r && r.recording ? "Recording — act on the app to capture steps" : "Recording started");
    renderReport(null);
  });
  $("#btnRecSave").addEventListener("click", async () => {
    const name = ($("#recName").value || "").trim();
    const save = await control("record.save", name ? { name } : {});
    if (save && save.ok) {
      showToast("Saved " + (save.file ? save.file.replace(/^.*[\\\/]/, "") : "test"));
      refreshTests();
    } else {
      showToast((save && save.error) || "Save failed", false);
    }
  });
  $("#btnRecDel").addEventListener("click", () => control("record.deleteLast"));
  $("#btnRecClear").addEventListener("click", async () => { await control("record.clear"); renderReport(null); });
  $("#recName").addEventListener("keydown", (e) => { if (e.key === "Enter") $("#btnRec").click(); });
  $("#testPick").addEventListener("change", (e) => { $("#btnReplay").disabled = !e.target.value; });
  $("#btnReplay").addEventListener("click", async () => {
    const name = $("#testPick").value;
    if (!name) return;
    const btn = $("#btnReplay"); btn.disabled = true; btn.textContent = "▶ Replaying…";
    try {
      const rep = await control("replay", { name });
      renderReport(rep);
      showToast(rep && rep.ok ? "Replay PASSED " + rep.passed + "/" + rep.total : "Replay FAILED", !!(rep && rep.ok));
    } finally {
      btn.disabled = false; btn.textContent = "▶ Replay";
    }
  });
  refreshTests();

  // Live updates.
  const es = new EventSource("/events");
  es.onmessage = (m) => { try { apply(JSON.parse(m.data)); } catch {} };
  es.onerror = () => { $("#conn").textContent = "reconnecting…"; $("#conn").className = "pill"; };

  // Self-heal: this legacy UI is only a FALLBACK shown when the shared inspector wasn't reachable
  // at panel-load (typically the agent was still (re)connecting after a broker/app restart). Poll
  // for it and, once the broker + a running app resolve, reload into the shared inspector so the
  // canvas always converges to the SAME tool the browser + VS Code use. The 5s guard stops a
  // flapping broker from hot-looping reloads; the shared shell has no poller, so it never loops.
  (function () {
    async function heal() {
      try {
        const r = await fetch("/inspector-ready", { cache: "no-store" });
        const j = await r.json();
        if (j && j.ready) {
          const last = +sessionStorage.getItem("df_healAt") || 0;
          if (Date.now() - last > 5000) { sessionStorage.setItem("df_healAt", String(Date.now())); location.reload(); }
        }
      } catch {}
    }
    setInterval(heal, 2500);
  })();
</script>
  <div id="toast" class="toast"></div>
</body>
</html>`;
}
