const REQUEST_BRIDGE_TYPES = new Set([
  "devflow:sendToCopilot",
  "devflow:attachCopilot",
  "devflow:requestTestProposal",
  "devflow:pickWorkflow",
  "devflow:attachData",
  "devflow:openSource",
  "devflow:recordingComplete",
  "devflow:saveTestBundle",
  "devflow:loadTestBundle",
  "devflow:pickTrace",
  "devflow:openSourceDiff",
  "devflow:applySourceProposal",
  "devflow:applyCSharpSourceProposal",
  "devflow:getCSharpSourceSelection",
]);

export function requiresBridgeRequestId(type: string | undefined): boolean {
  return typeof type === "string" && REQUEST_BRIDGE_TYPES.has(type);
}

export function isBridgeResultMessage(type: string | undefined): boolean {
  return requiresBridgeRequestId(type);
}
