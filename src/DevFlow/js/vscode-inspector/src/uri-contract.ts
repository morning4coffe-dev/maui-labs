export const DEVFLOW_EXTENSION_ID = "maui-labs.maui-devflow-inspector";
export const DEVFLOW_URI_VERSION = "1";

export type DevFlowView = "inspector" | "problems" | "layout" | "tests" | "trace" | "evidence";

export interface DevFlowUriTarget {
  version: "1";
  agent?: string;
  instance?: string;
  element?: string;
  problem?: string;
  run?: string;
  view: DevFlowView;
}

const VIEWS = new Set<DevFlowView>([
  "inspector",
  "problems",
  "layout",
  "tests",
  "trace",
  "evidence",
]);
const IDENTIFIER = /^[A-Za-z0-9._:@-]{1,256}$/;

export function parseDevFlowUri(path: string, query: string): DevFlowUriTarget | null {
  if (path !== "/open" && path !== "open") return null;
  const parameters = new URLSearchParams(query);
  if (parameters.get("v") !== DEVFLOW_URI_VERSION) return null;
  const view = (parameters.get("view") || "inspector") as DevFlowView;
  if (!VIEWS.has(view)) return null;

  const result: DevFlowUriTarget = { version: "1", view };
  for (const key of ["agent", "instance", "element", "problem", "run"] as const) {
    const value = parameters.get(key);
    if (value == null) continue;
    if (!IDENTIFIER.test(value)) return null;
    result[key] = value;
  }
  return result;
}

export function createDevFlowUriQuery(target: Omit<DevFlowUriTarget, "version">): string {
  const parameters = new URLSearchParams({ v: DEVFLOW_URI_VERSION, view: target.view });
  for (const key of ["agent", "instance", "element", "problem", "run"] as const) {
    const value = target[key];
    if (value) {
      if (!IDENTIFIER.test(value)) throw new Error(`Invalid DevFlow URI ${key}.`);
      parameters.set(key, value);
    }
  }
  return parameters.toString();
}
