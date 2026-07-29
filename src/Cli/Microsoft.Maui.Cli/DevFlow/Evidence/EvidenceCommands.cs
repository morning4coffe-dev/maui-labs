using System.CommandLine;
using System.Text;
using Microsoft.Maui.DevFlow.Driver;

namespace Microsoft.Maui.Cli.DevFlow.Evidence;

/// <summary>
/// <c>maui devflow evidence</c>: preview, capture, and view <c>.mauitrace</c> evidence bundles.
///
/// Every command follows the DevFlow CLI conventions — the shared <c>--json</c>/<c>--no-json</c>
/// options, <see cref="IDevFlowOutputWriter"/> for output, and structured errors on stderr.
/// </summary>
internal static class EvidenceCommands
{
    public static Command Create(
        Option<bool> jsonOption,
        Option<bool> noJsonOption,
        Option<string> agentHostOption,
        Option<int> agentPortOption,
        IDevFlowOutputWriter output,
        Func<string, int, Task<AgentClient>> clientFactory,
        Action onError)
    {
        var command = new Command("evidence",
            "Capture a shareable, redacted .mauitrace evidence bundle from the running app");

        command.Add(CreatePreviewCommand(jsonOption, noJsonOption, agentHostOption, agentPortOption, output, clientFactory, onError));
        command.Add(CreateCaptureCommand(jsonOption, noJsonOption, agentHostOption, agentPortOption, output, clientFactory, onError));
        command.Add(CreateViewCommand(jsonOption, noJsonOption, output, onError));
        return command;
    }

    private static Command CreatePreviewCommand(
        Option<bool> jsonOption,
        Option<bool> noJsonOption,
        Option<string> agentHostOption,
        Option<int> agentPortOption,
        IDevFlowOutputWriter output,
        Func<string, int, Task<AgentClient>> clientFactory,
        Action onError)
    {
        var screenshotOption = CreateScreenshotOption();
        var elementOption = CreateElementOption();
        var logLimitOption = CreateLogLimitOption();
        var networkLimitOption = CreateNetworkLimitOption();
        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = $"Destination that 'capture' would use, echoed back in the plan (default: ./{EvidenceFormat.DefaultFolderName}/<app>-<timestamp>{EvidenceFormat.FileExtension})"
        };

        var command = new Command("preview",
            "Show exactly what an evidence capture would include and exclude, without writing anything")
        {
            screenshotOption,
            elementOption,
            logLimitOption,
            networkLimitOption,
            outputOption,
        };

        command.SetAction(async (ctx, ct) =>
        {
            var isJson = output.ResolveJsonMode(ctx.GetValue(jsonOption), ctx.GetValue(noJsonOption));
            try
            {
                using var client = await clientFactory(ctx.GetValue(agentHostOption)!, ctx.GetValue(agentPortOption));
                var plan = await EvidenceCapture.PreviewAsync(client, new EvidenceRequest
                {
                    IncludeScreenshot = ctx.GetValue(screenshotOption),
                    SelectedElementId = ctx.GetValue(elementOption),
                    LogLimit = ctx.GetValue(logLimitOption),
                    NetworkLimit = ctx.GetValue(networkLimitOption),
                    OutputPath = ctx.GetValue(outputOption),
                    Source = "cli",
                }, ct);

                if (isJson) output.WriteRawJson(EvidenceJson.Serialize(plan, indented: true));
                else Console.WriteLine(FormatPlan(plan));
            }
            catch (Exception ex)
            {
                output.WriteError(ex.Message, isJson, suggestions: ["Run 'maui devflow status' to confirm an app is connected"]);
                onError();
            }
        });

        return command;
    }

    private static Command CreateCaptureCommand(
        Option<bool> jsonOption,
        Option<bool> noJsonOption,
        Option<string> agentHostOption,
        Option<int> agentPortOption,
        IDevFlowOutputWriter output,
        Func<string, int, Task<AgentClient>> clientFactory,
        Action onError)
    {
        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = $"Bundle path (default: ./{EvidenceFormat.DefaultFolderName}/<app>-<timestamp>{EvidenceFormat.FileExtension} under the project root)"
        };
        var overwriteOption = new Option<bool>("--overwrite")
        {
            Description = "Overwrite an existing bundle (default: fail if the file exists)",
            DefaultValueFactory = _ => false,
        };
        var screenshotOption = CreateScreenshotOption();
        var workflowOption = new Option<string?>("--workflow")
        {
            Description = "Markdown file with the reproduction steps to include (max 1 MB; secrets and absolute paths are scrubbed)"
        };
        var elementOption = CreateElementOption();
        var logLimitOption = CreateLogLimitOption();
        var networkLimitOption = CreateNetworkLimitOption();

        var command = new Command("capture", "Write a redacted evidence bundle for the connected app")
        {
            outputOption,
            overwriteOption,
            screenshotOption,
            workflowOption,
            elementOption,
            logLimitOption,
            networkLimitOption,
        };

        command.SetAction(async (ctx, ct) =>
        {
            var isJson = output.ResolveJsonMode(ctx.GetValue(jsonOption), ctx.GetValue(noJsonOption));
            try
            {
                var workflowPath = ctx.GetValue(workflowOption);
                string? workflow = null;
                if (!string.IsNullOrWhiteSpace(workflowPath))
                {
                    var read = ReadWorkflowFile(workflowPath!);
                    if (read.Error is not null)
                    {
                        output.WriteError(read.Error, isJson, "InvocationError");
                        onError();
                        return;
                    }
                    workflow = read.Text;
                }

                using var client = await clientFactory(ctx.GetValue(agentHostOption)!, ctx.GetValue(agentPortOption));
                var result = await EvidenceCapture.CaptureAsync(client, new EvidenceRequest
                {
                    IncludeScreenshot = ctx.GetValue(screenshotOption),
                    Overwrite = ctx.GetValue(overwriteOption),
                    OutputPath = ctx.GetValue(outputOption),
                    WorkflowMarkdown = workflow,
                    SelectedElementId = ctx.GetValue(elementOption),
                    LogLimit = ctx.GetValue(logLimitOption),
                    NetworkLimit = ctx.GetValue(networkLimitOption),
                    Source = "cli",
                }, ct);

                if (!result.Ok)
                {
                    output.WriteError(result.Error ?? "Could not capture evidence.", isJson, "InvocationError");
                    onError();
                    return;
                }

                if (isJson) output.WriteRawJson(EvidenceJson.Serialize(result, indented: true));
                else Console.WriteLine(FormatCapture(result));
            }
            catch (Exception ex)
            {
                output.WriteError(ex.Message, isJson, suggestions: ["Run 'maui devflow status' to confirm an app is connected"]);
                onError();
            }
        });

        return command;
    }

    private static Command CreateViewCommand(
        Option<bool> jsonOption,
        Option<bool> noJsonOption,
        IDevFlowOutputWriter output,
        Action onError)
    {
        var bundleArgument = new Argument<string>("bundle")
        {
            Description = $"Path to a {EvidenceFormat.FileExtension} bundle"
        };
        var noOpenOption = new Option<bool>("--no-open")
        {
            Description = "Generate the report without opening it in a browser",
            DefaultValueFactory = _ => false,
        };
        var reportOption = new Option<string?>("--output-report")
        {
            Description = "Write the generated HTML report to this path instead of a temporary file"
        };

        var command = new Command("view",
            "Validate a bundle and generate a static, self-contained HTML report from its contents")
        {
            bundleArgument,
            noOpenOption,
            reportOption,
        };

        command.SetAction((ctx, ct) =>
        {
            var isJson = output.ResolveJsonMode(ctx.GetValue(jsonOption), ctx.GetValue(noJsonOption));
            try
            {
                var result = EvidenceCapture.View(
                    ctx.GetValue(bundleArgument)!,
                    ctx.GetValue(reportOption),
                    open: !ctx.GetValue(noOpenOption));

                if (!result.Ok)
                {
                    output.WriteError(result.Error ?? "Could not read the bundle.", isJson, "InvocationError");
                    onError();
                    return Task.CompletedTask;
                }

                if (isJson) output.WriteRawJson(EvidenceJson.Serialize(result, indented: true));
                else Console.WriteLine(FormatView(result));
            }
            catch (Exception ex)
            {
                output.WriteError(ex.Message, isJson);
                onError();
            }
            return Task.CompletedTask;
        });

        return command;
    }

    private static Option<bool> CreateScreenshotOption() => new("--include-screenshot")
    {
        Description = "Include a screenshot (off by default — a screenshot may show on-screen data)",
        DefaultValueFactory = _ => false,
    };

    private static Option<string?> CreateElementOption() => new("--element")
    {
        Description = "Element id to highlight in the bundle (metadata only; no text or values are captured)"
    };

    private static Option<int> CreateLogLimitOption() => new("--log-limit")
    {
        Description = $"Maximum log entries to include (1-{EvidenceFormat.MaxLogLimit})",
        DefaultValueFactory = _ => EvidenceFormat.DefaultLogLimit,
    };

    private static Option<int> CreateNetworkLimitOption() => new("--network-limit")
    {
        Description = $"Maximum network request summaries to include (1-{EvidenceFormat.MaxNetworkLimit})",
        DefaultValueFactory = _ => EvidenceFormat.DefaultNetworkLimit,
    };

    internal static (string? Text, string? Error) ReadWorkflowFile(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "Workflow path is not a valid file path.");
        }

        if (!full.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return (null, "Workflow files must be Markdown (.md).");
        if (!File.Exists(full))
            return (null, $"Workflow file not found: {full}");

        try
        {
            var info = new FileInfo(full);
            if (info.Length > EvidenceFormat.MaxWorkflowBytes)
                return (null, $"Workflow file is larger than {EvidenceFormat.MaxWorkflowBytes / 1024} KB.");
            return (File.ReadAllText(full), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"Could not read the workflow file: {ex.Message}");
        }
    }

    // ── human-readable formatting ────────────────────────────────────────────────────────────

    internal static string FormatPlan(EvidencePlan plan)
    {
        var text = new StringBuilder();
        text.AppendLine($"Evidence preview for {plan.App?.Name ?? "the connected app"} ({plan.Platform?.Name ?? "unknown platform"})");
        text.AppendLine($"  Format v{plan.FormatVersion} · redaction ruleset v{plan.RedactionVersion} · source {plan.Source}");
        if (plan.OutputPath is not null)
            text.AppendLine($"  Would write: {plan.OutputPath} (~{EvidenceReportRenderer.FormatBytes(plan.EstimatedBytes)})");

        text.AppendLine("  Includes:");
        foreach (var entry in plan.Included)
        {
            var count = entry.Count is null ? "" : $" ({entry.Count} items)";
            text.AppendLine($"    - {entry.Name}{count}: {entry.Description}");
        }

        if (plan.Excluded.Count > 0)
        {
            text.AppendLine("  Excluded:");
            foreach (var exclusion in plan.Excluded)
                text.AppendLine($"    - {exclusion.Name}: {exclusion.Reason}");
        }

        text.AppendLine("  Never captured:");
        foreach (var item in plan.NeverIncluded)
            text.AppendLine($"    - {item}");

        text.AppendLine($"  Screenshot: {(plan.Screenshot.Included ? "included (opt-in)" : plan.Screenshot.OmittedReason ?? "not included")}");
        text.AppendLine($"  Limits: {plan.Limits.Logs} logs · {plan.Limits.Network} requests · {plan.Limits.TreeElements} elements");

        if (plan.Warnings.Count > 0)
        {
            text.AppendLine("  Warnings:");
            foreach (var warning in plan.Warnings)
                text.AppendLine($"    ! {warning}");
        }

        return text.ToString().TrimEnd();
    }

    internal static string FormatCapture(EvidenceCaptureResult result)
    {
        var text = new StringBuilder();
        text.AppendLine($"Evidence bundle written: {result.Path} ({EvidenceReportRenderer.FormatBytes(result.Bytes)})");
        var counts = result.Manifest?.Counts;
        if (counts is not null)
        {
            text.AppendLine($"  {counts.TreeElements} elements · {counts.Problems} problems · {counts.Logs} log entries · {counts.NetworkRequests} requests");
        }
        text.AppendLine($"  Screenshot: {(result.Manifest?.Screenshot.Included == true ? "included" : "omitted")}");
        text.AppendLine($"  Redaction ruleset v{result.Manifest?.RedactionVersion}");
        if (result.Manifest?.Warnings is { Count: > 0 })
        {
            foreach (var warning in result.Manifest.Warnings)
                text.AppendLine($"  ! {warning}");
        }
        text.Append($"  View it with: maui devflow evidence view \"{result.Path}\"");
        return text.ToString();
    }

    internal static string FormatView(EvidenceViewResult result)
    {
        var text = new StringBuilder();
        text.AppendLine($"Report generated: {result.Report}");
        text.AppendLine($"  Bundle: {result.Bundle}");
        text.AppendLine($"  Entries: {string.Join(", ", result.Entries)}");
        if (result.Manifest is not null)
        {
            text.AppendLine($"  Captured {result.Manifest.CapturedUtc} by {result.Manifest.Source} · redaction ruleset v{result.Manifest.RedactionVersion}");
        }
        foreach (var warning in result.Warnings)
            text.AppendLine($"  ! {warning}");
        text.Append(result.Opened ? "  Opened in your default browser." : "  Open it in a browser to read the report.");
        return text.ToString();
    }
}
