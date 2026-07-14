// clean-open-canvases.mjs — maintenance tool for the "Canvas is not registered" error.
//
// WHY THIS EXISTS
// ---------------
// The GitHub Copilot app spawns a canvas extension as a short-lived CHILD PROCESS, bound to one
// SESSION_ID, and SIGTERMs it after ~10 min. The canvas "provider" registration lives only in that
// process. But the app also PERSISTS, per session, a row in data.db → session_open_canvases marking
// the canvas as status:"ready" (with a now-dead loopback URL). When you re-open an EXISTING session,
// the app tries to auto-restore that persisted canvas by calling the native
// canvasRegistryTryAcquireOpenSlot BEFORE the just-respawned extension has finished registering its
// provider — so it throws:  Canvas "user:maui-live-canvas/maui-live-canvas" is not registered.
// A FRESH session works because the open happens AFTER registration.
//
// Stale rows from older extension versions (mismatched instanceIds like "maui-fluent", "maui-live-1")
// make this worse: they seed instances that can never rehydrate. Clearing them converts the hard
// error into a clean "canvas simply not auto-open" state — you then open the canvas on demand, which
// spawns + registers the provider and works.
//
// This tool ONLY touches session_open_canvases rows for THIS extension. It never deletes sessions,
// conversation history, or any other canvas's rows. Conversation history lives in a different DB
// (session-store.db → turns) and is untouched.
//
// USAGE  (Node 22.12+ / 24; uses the built-in node:sqlite)
//   node clean-open-canvases.mjs            # DRY RUN — list stale rows for this extension
//   node clean-open-canvases.mjs --clean    # delete those rows
//   node clean-open-canvases.mjs --clean --all-extensions   # also clear hello/mock canvases
//
// IMPORTANT: CLOSE the GitHub Copilot app first, so the SQLite DB isn't locked (WAL).

import { DatabaseSync } from "node:sqlite";
import { homedir } from "node:os";
import { join } from "node:path";
import { existsSync } from "node:fs";

const args = new Set(process.argv.slice(2));
const DO_CLEAN = args.has("--clean");
const ALL_EXT = args.has("--all-extensions");

// The extensionId the Copilot app records for this canvas (user scope → "user:<folder-name>").
const OUR_EXTENSION_ID = "user:maui-live-canvas";

const dbPath = join(homedir(), ".copilot", "data.db");
if (!existsSync(dbPath)) {
  console.error(`data.db not found at ${dbPath} — is the GitHub Copilot app installed?`);
  process.exit(1);
}

function fmt(row) {
  let canvasId = "?", extensionId = "?", status = "?", url = "?";
  try {
    const p = JSON.parse(row.payload);
    canvasId = p.canvasId ?? "?";
    extensionId = p.extensionId ?? "?";
    status = p.status ?? "?";
    url = p.url ?? "?";
  } catch { /* payload not JSON */ }
  return { session: row.session_id, instance: row.instance_id, extensionId, canvasId, status, url, updated: row.updated_at };
}

let db;
try {
  // Open read-write. busyTimeout gives the app a moment to release locks; if it's still open
  // we fail cleanly rather than risk a half-write.
  db = new DatabaseSync(dbPath, { readOnly: !DO_CLEAN });
  try { db.exec("PRAGMA busy_timeout = 3000"); } catch { /* ignore */ }

  const all = db.prepare("SELECT session_id, instance_id, payload, updated_at FROM session_open_canvases ORDER BY updated_at").all();
  const mine = all.filter((r) => {
    if (ALL_EXT) return true;
    try { return JSON.parse(r.payload).extensionId === OUR_EXTENSION_ID; }
    catch { return String(r.payload).includes(OUR_EXTENSION_ID); }
  });

  console.log(`\nsession_open_canvases: ${all.length} total row(s); ${mine.length} match ${ALL_EXT ? "ALL extensions" : OUR_EXTENSION_ID}\n`);
  for (const r of mine) {
    const f = fmt(r);
    console.log(`  session=${f.session}`);
    console.log(`    instance=${f.instance}  canvas=${f.extensionId}/${f.canvasId}  status=${f.status}`);
    console.log(`    stale url=${f.url}  (updated ${f.updated})`);
  }

  if (!DO_CLEAN) {
    console.log(`\nDRY RUN. Nothing changed. Re-run with --clean to delete the ${mine.length} row(s) above.`);
    console.log(`(Close the Copilot app first so the database isn't locked.)\n`);
    process.exit(0);
  }

  if (mine.length === 0) {
    console.log("Nothing to clean.\n");
    process.exit(0);
  }

  // Delete EXACTLY the rows previewed above, addressed by primary key (session_id, instance_id).
  // This guarantees --clean removes precisely what the dry run listed — never a broader substring
  // match. In particular a "%user:maui-live-canvas%" LIKE would also hit "user:maui-live-canvas-mock";
  // deleting by PK from the already-filtered `mine` set avoids that entirely.
  const del = db.prepare("DELETE FROM session_open_canvases WHERE session_id = ? AND instance_id = ?");
  db.exec("BEGIN");
  let removed = 0;
  try {
    for (const r of mine) {
      removed += del.run(r.session_id, r.instance_id).changes;
    }
    db.exec("COMMIT");
  } catch (e) {
    try { db.exec("ROLLBACK"); } catch { /* ignore */ }
    throw e;
  }
  console.log(`\nRemoved ${removed} stale open-canvas row(s).`);
  console.log(`Existing sessions will no longer auto-fail with "Canvas is not registered".`);
  console.log(`Open the canvas on demand (ask the agent, or the /-picker) and it will register + open cleanly.\n`);
} catch (e) {
  const msg = String(e?.message || e);
  if (/lock|busy/i.test(msg)) {
    console.error(`\nDatabase is locked — CLOSE the GitHub Copilot app, then run this again.\n(${msg})\n`);
  } else {
    console.error(`\nFailed: ${msg}\n`);
  }
  process.exit(1);
} finally {
  try { db?.close(); } catch { /* ignore */ }
}
