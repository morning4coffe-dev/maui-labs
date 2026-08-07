using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.Maui.Cli.DevFlow.Mcp;
using Microsoft.Maui.Cli.DevFlow.Inspector;
using Microsoft.Maui.DevFlow.Driver;

namespace Microsoft.Maui.Cli.DevFlow.Mcp.Tools;

[McpServerToolType]
public sealed class TreeTool
{
	[McpServerTool(Name = "maui_tree"), Description("Inspect the visual tree of the running MAUI app. Returns structured JSON element hierarchy with IDs, types, bounds, visibility, and properties. Use element IDs from this tree for tap, fill, scroll, and other interaction commands.")]
	public static async Task<string> Tree(
		McpAgentSession session,
		[Description("Agent HTTP port (optional if only one agent connected)")] int? agentPort = null,
		[Description("Window index for multi-window apps (default: 0)")] int? window = null,
		[Description("Max tree depth to return (default: 50)")] int depth = 50,
		[Description("Filter to a specific element type, e.g. 'Label', 'Button', 'Entry'")] string? filter = null,
		[Description("Return only the subtree rooted at this element ID")] string? elementId = null,
		[Description("Tree projection: activeVisual (default, same tree as Inspector hosts) or raw (low-level agent tree)")] string projection = "activeVisual")
	{
		List<ElementInfo>? tree;
		if (string.Equals(projection, "activeVisual", StringComparison.OrdinalIgnoreCase))
		{
			if (window is not null)
				return "The activeVisual Inspector projection does not accept a window index. Use projection='raw' for a window-specific agent tree.";
			tree = await session.GetInspectorTreeAsync(agentPort);
			if (tree is not null)
				InspectorSnapshotService.TrimDepth(tree, depth);
		}
		else if (string.Equals(projection, "raw", StringComparison.OrdinalIgnoreCase))
		{
			using var agent = await session.GetAgentClientAsync(agentPort);
			tree = await agent.GetTreeAsync(depth, window);
		}
		else
		{
			return "projection must be 'activeVisual' or 'raw'.";
		}

		if (tree == null || tree.Count == 0)
			return projection.Equals("activeVisual", StringComparison.OrdinalIgnoreCase)
				? "No canonical Inspector tree is available. Is the broker running and the app connected?"
				: "No visual tree available. Is the agent connected and the app running?";

		IEnumerable<ElementInfo> result = tree;

		if (elementId != null)
		{
			var subtree = FindElement(tree, elementId);
			if (subtree == null)
				return $"Element '{elementId}' not found in the visual tree.";
			result = [subtree];
		}

		if (filter != null)
		{
			result = FilterByType(result.ToList(), filter);
			if (!result.Any())
				return $"No elements of type '{filter}' found in the visual tree.";
		}

		return CliJson.SerializeUntyped(result, indented: false);
	}

	private static ElementInfo? FindElement(IEnumerable<ElementInfo> elements, string id)
	{
		foreach (var el in elements)
		{
			if (el.Id == id) return el;
			if (el.Children != null)
			{
				var found = FindElement(el.Children, id);
				if (found != null) return found;
			}
		}
		return null;
	}

	private static List<ElementInfo> FilterByType(List<ElementInfo> elements, string type)
	{
		var result = new List<ElementInfo>();
		foreach (var el in elements)
		{
			if (el.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
				result.Add(el);
			else if (el.Children != null)
			{
				var filtered = FilterByType(el.Children, type);
				if (filtered.Count > 0)
					result.AddRange(filtered);
			}
		}
		return result;
	}
}
