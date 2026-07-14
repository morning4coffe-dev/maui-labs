// shell.mjs — m2b + feature D: the canvas panel embeds the SHARED DevFlow broker inspector instead
// of the hand-rendered ui.mjs, so the canvas shows the exact same inspector as a browser or the VS
// Code host (visual tree, screenshot, tap/fill/scroll, the m6 rich property grid, record/replay).
// The broker hosts it per-agent at http://localhost:{brokerPort}/inspector/{agentId}/. ui.mjs stays
// as the fallback when no broker/agent is resolved.
//
// The nonce'd relay <script> is the canvas end of the authenticated host bridge. The canvas can't
// open VS Code Chat or an editor, so it advertises only `saveRecording`: when the inspector finishes
// a recording it hands the Markdown here and we POST it to the canvas server (/recording), which
// writes it into the project's maui-tests/ folder. Send-to-Copilot and open-source fall back to the
// inspector's own clipboard behavior. The bridge nonce rides in the iframe URL *fragment*, so it
// never reaches the broker over HTTP; every message in both directions is gated by it + event.source.

export function renderShell(inspectorUrl, appName, bridgeId) {
  const title = escapeHtml(appName || "MAUI app");
  const nonce = String(bridgeId || "").replace(/[^A-Za-z0-9_-]/g, "");
  const frameSrc = jsString(`${inspectorUrl}#devflowBridge=${nonce}`);
  const bridgeLiteral = jsString(nonce);
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>${title}</title>
  <meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; frame-src http://127.0.0.1:* http://localhost:* https://*.vscode-webview.net; style-src 'unsafe-inline'; connect-src 'self'; script-src 'nonce-${nonce}';" />
  <style>
    /* Fill the whole canvas panel: no host chrome (the panel tab already names it), no default padding. */
    :root { color-scheme: light dark; }
    html, body { margin: 0; padding: 0; height: 100%; overflow: hidden; background: light-dark(#ffffff, #1e1e1e); }
    #frame { position: fixed; inset: 0; width: 100%; height: 100%; border: 0; display: block; }
  </style>
</head>
<body>
  <iframe id="frame" sandbox="allow-scripts allow-forms allow-same-origin"></iframe>
  <script nonce="${nonce}">
    (function () {
      const frame = document.getElementById('frame');
      const bridgeId = ${bridgeLiteral};
      // Capabilities the canvas contributes: save recordings, receive the human's selection (so the
      // agent can answer about "the selected element"), and push that selection to Copilot as context.
      const capabilities = bridgeId ? ['saveRecording', 'selection', 'copilot'] : [];
      // Relay a control action to the canvas server (which updates the agent-facing selection store).
      function postControl(payload, cb) {
        try {
          fetch('/control', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(Object.assign({ bridgeId: bridgeId }, payload)) })
            .then(function (r) { return r && r.ok ? r.json().catch(function () { return null; }) : null; })
            .then(function (j) { if (cb) cb(j); })
            .catch(function () { /* best effort */ });
        } catch (e) { /* best effort */ }
      }
      // The Copilot canvas is a Chromium surface that follows the host app's light/dark setting via
      // prefers-color-scheme. Forward that as an explicit mode so the inspector themes correctly even
      // though prefers-color-scheme doesn't reliably cross the iframe's origin boundary.
      function themeMode() {
        try { return (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) ? 'dark' : 'light'; }
        catch (e) { return 'dark'; }
      }
      function announce() {
        try {
          if (frame.contentWindow && bridgeId) {
            frame.contentWindow.postMessage({ type: 'devflow:host', v: 1, bridgeId: bridgeId, capabilities: capabilities, hostKind: 'copilot-canvas-ui', hostLabel: 'GitHub Copilot Canvas', theme: { mode: themeMode() } }, '*');
          }
        } catch (e) { /* cross-origin during teardown */ }
      }
      function sendTheme() {
        try {
          if (frame.contentWindow && bridgeId) {
            frame.contentWindow.postMessage({ type: 'devflow:theme', v: 1, bridgeId: bridgeId, mode: themeMode() }, '*');
          }
        } catch (e) { /* cross-origin during teardown */ }
      }
      // Announce capabilities ONLY in response to the inspector's nonce-authenticated
      // 'devflow:ready' — never unconditionally on iframe 'load', so the bridge secret can't leak to
      // a page the iframe later navigates to.
      window.addEventListener('message', function (e) {
        if (e.source !== frame.contentWindow) return;           // only our embedded inspector
        const d = e.data;
        if (!d || !bridgeId || d.bridgeId !== bridgeId) return;  // nonce-authenticated
        if (d.type === 'devflow:ready') { announce(); return; }
        if (d.type === 'devflow:selectionChanged') {
          // Mirror the human's inspector selection into the extension's store so get_selection /
          // get_canvas (and "fix the selected element") resolve to the right control.
          const el = d.element;
          postControl(el && el.id ? { action: 'select', id: el.id } : { action: 'select', id: null });
          return;
        }
        if (d.type === 'devflow:sendToCopilot') {
          // Ensure the store selection matches, then push it into the composer as a context pill.
          // Pass the element itself so the server can attach it even if the store select missed.
          const pel = d.payload && d.payload.element;
          if (pel && pel.id) postControl({ action: 'select', id: pel.id }, function () { postControl({ action: 'attachSelection', element: pel }); });
          else postControl({ action: 'attachSelection' });
          return;
        }
        if (d.type === 'devflow:recordingComplete') {
          fetch('/recording', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            // Carry the bridge nonce so the server can reject cross-site localhost POSTs.
            body: JSON.stringify({ bridgeId: bridgeId, name: d.name, markdown: d.markdown }),
          }).catch(function () { /* best effort */ });
        }
      });
      // Attach the listener before navigating so no early 'ready' is lost.
      // Re-push the mode when the host app toggles light/dark.
      try {
        const mq = window.matchMedia('(prefers-color-scheme: dark)');
        if (mq && mq.addEventListener) mq.addEventListener('change', sendTheme);
        else if (mq && mq.addListener) mq.addListener(sendTheme);
      } catch (e) { /* matchMedia is available in the canvas webview */ }
      frame.src = ${frameSrc};
    })();
  </script>
</body>
</html>`;
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function jsString(s) {
  // Safe string literal for inlining into the <script>: JSON-encode, then neutralize `<`.
  return JSON.stringify(String(s)).replace(/</g, "\\u003c");
}
