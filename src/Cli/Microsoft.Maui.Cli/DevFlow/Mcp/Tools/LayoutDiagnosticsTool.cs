using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Microsoft.Maui.Cli.DevFlow.Mcp.Tools;

[McpServerToolType]
public sealed class LayoutDiagnosticsTool
{
    [McpServerTool(Name = "maui_layout_diagnostics"),
     Description(
         "Run a single, explicit, read-only layout scan of the running MAUI app and return typed findings. " +
         "Reports only what managed MAUI layout state can prove: elements that are visible but were arranged " +
         "with no area, arranged sizes that break a declared min/max request, elements arranged entirely " +
         "outside the window, measure/arrange size pressure (observation), and children overflowing a parent " +
         "(low-confidence observation). It never claims clipping, occlusion, text truncation, or accessibility " +
         "mismatches, and geometry it could not read is returned as 'incomplete' — never as a pass. " +
         "Read the 'coverage' and 'limitations' fields before drawing conclusions. There is no watch mode: " +
         "call this again after you change the UI.")]
    public static async Task<string> GetLayoutDiagnostics(
        McpAgentSession session,
        [Description("Agent HTTP port (optional if only one agent connected)")] int? agentPort = null,
        [Description("Restrict the scan to this element id and its descendants")] string? elementId = null,
        [Description("0-based window index (optional; defaults to every window)")] int? window = null,
        [Description("Element budget for the scan (default: 2000, maximum: 5000)")] int? maxElements = null)
    {
        using var agent = await session.GetAgentClientAsync(agentPort);
        var report = await agent.GetLayoutDiagnosticsAsync(elementId, window, maxElements);
        return report is null
            ? "Layout diagnostics are unavailable on the connected agent, or the requested element does not exist."
            : CliJson.SerializeUntyped(report, indented: false);
    }
}
