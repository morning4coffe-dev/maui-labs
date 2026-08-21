import type { AgentRegistration } from "@maui-devflow/client";
import type {
  DataSnapshot,
  InspectorPanelContext,
  SelectedElement,
} from "./context-store";
import type { DevFlowUriTarget } from "./uri-contract";

export interface DevFlowProblem {
  id: string;
  kind?: string;
  severity?: string;
  code?: string | null;
  message: string;
  count?: number;
  elementId?: string | null;
  elementType?: string | null;
  property?: string | null;
  bindingPath?: string | null;
  sourceFile?: string | null;
  sourceLine?: number | null;
  sourceColumn?: number | null;
}

export interface DevFlowProblemBatch {
  enabled: boolean;
  revision: number;
  count: number;
  evicted: number;
  problems: DevFlowProblem[];
}

export interface DevFlowLayoutFinding {
  id?: string;
  ruleId?: string;
  outcome?: string;
  confidence?: string;
  severity?: string;
  actionability?: string;
  message?: string;
  explanation?: string;
  element?: {
    id: string;
    type?: string;
    automationId?: string | null;
    sourceFile?: string | null;
    sourceLine?: number | null;
    sourceColumn?: number | null;
  } | null;
  limitations?: string[];
}

export interface DevFlowLayoutReport {
  capturedUtc?: string;
  platform?: string;
  scope?: { window?: number | null; truncated?: boolean };
  coverage?: { overall?: string; limitations?: string[] };
  findings?: DevFlowLayoutFinding[];
}

export interface DevFlowEvidenceContext {
  kind: "dataSnapshot" | "evidencePreview" | "flowRun";
  value: DataSnapshot | unknown;
}

export interface DevFlowActiveApp {
  agent: AgentRegistration;
  brokerPort: number;
}

export interface InspectorOpenHints {
  test?: unknown;
  trace?: unknown;
  agentRequest?: unknown;
  agent?: unknown;
  instance?: unknown;
  element?: unknown;
  problem?: unknown;
  run?: unknown;
  view?: unknown;
}

export interface DevFlowHostServices {
  getPanelContext(): InspectorPanelContext | null;
  getDataSnapshot(): DataSnapshot | null;
  openInspector(hints?: InspectorOpenHints): Promise<void>;
  openUriTarget(target: DevFlowUriTarget): Promise<void>;
  resolveActiveApp(): Promise<DevFlowActiveApp | null>;
  getProblems(elementId?: string): Promise<DevFlowProblemBatch | null>;
  getLayoutDiagnostics(): Promise<DevFlowLayoutReport | null>;
  getCurrentEvidence(): Promise<DevFlowEvidenceContext | null>;
  getSelectedElement(): SelectedElement | null;
}
