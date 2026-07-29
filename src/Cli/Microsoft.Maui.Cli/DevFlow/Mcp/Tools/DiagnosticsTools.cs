using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.Maui.Cli.DevFlow.Mcp;

namespace Microsoft.Maui.Cli.DevFlow.Mcp.Tools;

[McpServerToolType]
public sealed class DiagnosticsTools
{
    [McpServerTool(Name = "maui_problems"),
     Description("List deduplicated runtime UI problems such as MAUI binding failures, including affected element/property and source metadata when available.")]
    public static async Task<string> GetProblems(
        McpAgentSession session,
        [Description("Agent HTTP port (optional if only one agent connected)")] int? agentPort = null,
        [Description("Maximum number of problems to return (default: 100)")] int limit = 100,
        [Description("Optional element ID to return only problems correlated with that element")] string? elementId = null)
    {
        using var agent = await session.GetAgentClientAsync(agentPort);
        var problems = await agent.GetDiagnosticProblemsAsync(limit, elementId);
        return CliJson.SerializeUntyped(problems, indented: false);
    }

    [McpServerTool(Name = "maui_problems_clear"),
     Description("Clear the running agent's bounded diagnostic Problems list.")]
    public static async Task<string> ClearProblems(
        McpAgentSession session,
        [Description("Agent HTTP port (optional if only one agent connected)")] int? agentPort = null)
    {
        using var agent = await session.GetAgentClientAsync(agentPort);
        return await agent.ClearDiagnosticProblemsAsync()
            ? "Diagnostic Problems cleared."
            : "Failed to clear diagnostic Problems.";
    }
}

