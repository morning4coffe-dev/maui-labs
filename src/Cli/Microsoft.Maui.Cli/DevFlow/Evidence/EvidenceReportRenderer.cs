using System.Globalization;
using System.Net;
using System.Text;

namespace Microsoft.Maui.Cli.DevFlow.Evidence;

/// <summary>
/// Renders a validated bundle into a fresh, self-contained, static HTML report.
///
/// The report is REGENERATED from parsed data — no markup, script, or style from the bundle is
/// ever forwarded. Every interpolated value is HTML-encoded and the document declares a
/// restrictive CSP (no script, no network, images limited to embedded data URIs), so opening a
/// report produced from an untrusted bundle cannot execute anything or phone home.
/// </summary>
internal static class EvidenceReportRenderer
{
    internal const string ContentSecurityPolicy =
        "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src 'none'; " +
        "connect-src 'none'; script-src 'none'; object-src 'none'; base-uri 'none'; " +
        "form-action 'none'; frame-ancestors 'none'";

    private const int MaxRenderedTreeNodes = 2_000;
    private const int MaxRenderedRows = 500;

    public static string Render(EvidenceReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var manifest = result.Manifest;
        var appName = manifest?.App?.Name ?? "MAUI app";
        var title = $"DevFlow evidence — {appName}";

        var html = new StringBuilder(64 * 1024);
        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
        html.Append("<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(ContentSecurityPolicy).Append("\">\n");
        html.Append("<meta name=\"referrer\" content=\"no-referrer\">\n");
        html.Append("<title>").Append(E(title)).Append("</title>\n");
        html.Append("<style>").Append(Css).Append("</style>\n");
        html.Append("</head>\n<body>\n<main>\n");

        html.Append("<h1>").Append(E(title)).Append("</h1>\n");
        RenderOverview(html, result);
        RenderContents(html, manifest);
        RenderWarnings(html, result);
        RenderScreenshot(html, result);
        RenderProblems(html, result);
        RenderNetwork(html, result);
        RenderLogs(html, result);
        RenderTree(html, result);
        RenderWorkflow(html, result);

        html.Append("<footer><p>Generated locally by the MAUI DevFlow CLI. This report is static: it contains no scripts and makes no network requests.</p></footer>\n");
        html.Append("</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    private static void RenderOverview(StringBuilder html, EvidenceReadResult result)
    {
        var manifest = result.Manifest;
        html.Append("<section><h2>Overview</h2>\n<dl class=\"grid\">\n");
        Definition(html, "Captured (UTC)", manifest?.CapturedUtc);
        Definition(html, "Captured by", manifest?.Source);
        Definition(html, "Tool version", manifest?.Tool?.Version);
        Definition(html, "Format version", manifest?.FormatVersion.ToString(CultureInfo.InvariantCulture));
        Definition(html, "Redaction ruleset", manifest?.RedactionVersion.ToString(CultureInfo.InvariantCulture));
        Definition(html, "App", manifest?.App?.Name);
        Definition(html, "App version", Join(manifest?.App?.Version, manifest?.App?.Build));
        Definition(html, "Package", manifest?.App?.PackageId);
        Definition(html, "Platform", manifest?.Platform?.Name);
        Definition(html, "Device type", manifest?.Platform?.DeviceType);
        Definition(html, "Agent", manifest?.Platform?.AgentVersion);
        Definition(html, "Framework", Join(manifest?.Platform?.Framework, manifest?.Platform?.FrameworkVersion));
        Definition(html, "Selected element", manifest?.SelectedElementId);

        var device = result.Environment?.Device;
        if (device is not null)
        {
            Definition(html, "Device", Join(device.Manufacturer, device.Model));
            Definition(html, "OS version", device.OsVersion);
            Definition(html, "Architecture", device.Architecture);
        }

        var display = result.Environment?.Display;
        if (display is not null)
        {
            Definition(html, "Display",
                display.Width is not null && display.Height is not null
                    ? $"{Num(display.Width)} × {Num(display.Height)}"
                    : null);
            Definition(html, "Density", Num(display.Density));
            Definition(html, "Orientation", display.Orientation);
        }

        html.Append("</dl>\n");

        var counts = manifest?.Counts;
        if (counts is not null)
        {
            html.Append("<ul class=\"stats\">\n");
            Stat(html, "Elements", counts.TreeElements);
            Stat(html, "Problems", counts.Problems);
            Stat(html, "Log entries", counts.Logs);
            Stat(html, "Network requests", counts.NetworkRequests);
            html.Append("</ul>\n");
        }

        var capabilities = result.Environment?.Capabilities ?? manifest?.Capabilities;
        if (capabilities is { Count: > 0 })
        {
            html.Append("<p class=\"muted\"><strong>Agent capabilities:</strong> ")
                .Append(E(string.Join(", ", capabilities)))
                .Append("</p>\n");
        }

        html.Append("</section>\n");
    }

    private static void RenderContents(StringBuilder html, EvidenceManifest? manifest)
    {
        html.Append("<section><h2>Contents</h2>\n");

        if (manifest?.Entries is { Count: > 0 })
        {
            html.Append("<table><caption>Included</caption><thead><tr><th scope=\"col\">Entry</th><th scope=\"col\">What it holds</th><th scope=\"col\">Items</th><th scope=\"col\">Size</th></tr></thead><tbody>\n");
            foreach (var entry in manifest.Entries)
            {
                html.Append("<tr><td><code>").Append(E(entry.Name)).Append("</code></td><td>")
                    .Append(E(entry.Description)).Append("</td><td>")
                    .Append(entry.Count is null ? "—" : E(entry.Count.Value.ToString(CultureInfo.InvariantCulture)))
                    .Append("</td><td>").Append(E(FormatBytes(entry.Bytes))).Append("</td></tr>\n");
            }
            html.Append("</tbody></table>\n");
        }

        if (manifest?.Excluded is { Count: > 0 })
        {
            html.Append("<table><caption>Excluded from this capture</caption><thead><tr><th scope=\"col\">Entry</th><th scope=\"col\">Reason</th></tr></thead><tbody>\n");
            foreach (var exclusion in manifest.Excluded)
            {
                html.Append("<tr><td><code>").Append(E(exclusion.Name)).Append("</code></td><td>")
                    .Append(E(exclusion.Reason)).Append("</td></tr>\n");
            }
            html.Append("</tbody></table>\n");
        }

        var never = manifest?.NeverIncluded is { Count: > 0 } ? manifest.NeverIncluded : EvidenceFormat.NeverIncluded;
        html.Append("<h3>Never captured</h3>\n<ul>\n");
        foreach (var item in never)
            html.Append("<li>").Append(E(item)).Append("</li>\n");
        html.Append("</ul>\n");

        var screenshot = manifest?.Screenshot;
        if (screenshot is not null)
        {
            var state = screenshot.Included
                ? "Included at explicit request."
                : screenshot.OmittedReason ?? "Not included.";
            html.Append("<p class=\"muted\"><strong>Screenshot:</strong> ").Append(E(state)).Append("</p>\n");
        }

        html.Append("</section>\n");
    }

    private static void RenderWarnings(StringBuilder html, EvidenceReadResult result)
    {
        var warnings = new List<string>();
        if (result.Manifest?.Warnings is { Count: > 0 }) warnings.AddRange(result.Manifest.Warnings);
        if (result.Warnings.Count > 0) warnings.AddRange(result.Warnings);
        if (warnings.Count == 0) return;

        html.Append("<section class=\"warn\"><h2>Warnings</h2>\n<ul>\n");
        foreach (var warning in warnings)
            html.Append("<li>").Append(E(warning)).Append("</li>\n");
        html.Append("</ul>\n</section>\n");
    }

    private static void RenderScreenshot(StringBuilder html, EvidenceReadResult result)
    {
        if (result.Screenshot is not { Length: > 0 }) return;
        html.Append("<section><h2>Screenshot</h2>\n<img alt=\"App screenshot captured with this bundle\" src=\"data:image/png;base64,")
            .Append(Convert.ToBase64String(result.Screenshot))
            .Append("\">\n</section>\n");
    }

    private static void RenderProblems(StringBuilder html, EvidenceReadResult result)
    {
        var problems = result.Problems;
        if (problems is null) return;

        html.Append("<section><h2>Problems</h2>\n");
        if (problems.Problems.Count == 0)
        {
            html.Append("<p class=\"muted\">No problems were recorded.</p>\n</section>\n");
            return;
        }

        html.Append("<table><thead><tr><th scope=\"col\">Severity</th><th scope=\"col\">Kind</th><th scope=\"col\">Message</th><th scope=\"col\">Element</th><th scope=\"col\">Binding</th><th scope=\"col\">Source</th><th scope=\"col\">Count</th></tr></thead><tbody>\n");
        foreach (var problem in problems.Problems.Take(MaxRenderedRows))
        {
            html.Append("<tr><td>").Append(E(problem.Severity)).Append("</td><td>")
                .Append(E(problem.Kind)).Append("</td><td>")
                .Append(E(problem.Message)).Append("</td><td>")
                .Append(E(Join(problem.ElementType, problem.Property))).Append("</td><td>")
                .Append(E(problem.BindingPath)).Append("</td><td>")
                .Append(E(FormatSource(problem.SourceFile, problem.SourceLine, problem.SourceColumn))).Append("</td><td>")
                .Append(problem.Count.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>\n");
        }
        html.Append("</tbody></table>\n");
        AppendTruncationNote(html, problems.Problems.Count, MaxRenderedRows);
        html.Append("</section>\n");
    }

    private static void RenderNetwork(StringBuilder html, EvidenceReadResult result)
    {
        var network = result.Network;
        if (network is null) return;

        html.Append("<section><h2>Network</h2>\n<p class=\"muted\">Summaries only — no headers, bodies, or query-string values were captured.</p>\n");
        if (network.Requests.Count == 0)
        {
            html.Append("<p class=\"muted\">No requests were recorded.</p>\n</section>\n");
            return;
        }

        html.Append("<table><thead><tr><th scope=\"col\">#</th><th scope=\"col\">Method</th><th scope=\"col\">Host</th><th scope=\"col\">Path</th><th scope=\"col\">Query keys</th><th scope=\"col\">Status</th><th scope=\"col\">Duration</th><th scope=\"col\">Size</th></tr></thead><tbody>\n");
        foreach (var request in network.Requests.Take(MaxRenderedRows))
        {
            var status = request.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? request.Error ?? "—";
            var size = request.ResponseBytes is null ? "—" : FormatBytes(request.ResponseBytes.Value);
            html.Append("<tr><td>").Append(request.Sequence.ToString(CultureInfo.InvariantCulture)).Append("</td><td>")
                .Append(E(request.Method)).Append("</td><td>")
                .Append(E(request.Host)).Append("</td><td>")
                .Append(E(request.Path)).Append("</td><td>")
                .Append(E(request.QueryKeys is null ? null : string.Join(", ", request.QueryKeys))).Append("</td><td>")
                .Append(E(status)).Append("</td><td>")
                .Append(E(request.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms")).Append("</td><td>")
                .Append(E(size)).Append("</td></tr>\n");
        }
        html.Append("</tbody></table>\n");
        AppendTruncationNote(html, network.Requests.Count, MaxRenderedRows);
        html.Append("</section>\n");
    }

    private static void RenderLogs(StringBuilder html, EvidenceReadResult result)
    {
        var logs = result.Logs;
        if (logs is null) return;

        html.Append("<section><h2>Logs</h2>\n");
        if (logs.Entries.Count == 0)
        {
            html.Append("<p class=\"muted\">No log entries were captured.</p>\n</section>\n");
            return;
        }

        html.Append("<table><thead><tr><th scope=\"col\">Time</th><th scope=\"col\">Level</th><th scope=\"col\">Category</th><th scope=\"col\">Message</th></tr></thead><tbody>\n");
        foreach (var entry in logs.Entries.Take(MaxRenderedRows))
        {
            var message = entry.Exception is { Length: > 0 }
                ? entry.Message + "\n" + entry.Exception
                : entry.Message;
            html.Append("<tr><td>").Append(E(entry.Timestamp)).Append("</td><td>")
                .Append(E(entry.Level)).Append("</td><td>")
                .Append(E(entry.Category)).Append("</td><td><pre>")
                .Append(E(message)).Append("</pre></td></tr>\n");
        }
        html.Append("</tbody></table>\n");
        AppendTruncationNote(html, logs.Entries.Count, MaxRenderedRows);
        html.Append("</section>\n");
    }

    private static void RenderTree(StringBuilder html, EvidenceReadResult result)
    {
        var tree = result.Tree;
        if (tree is null) return;

        html.Append("<section><h2>Visual tree</h2>\n<p class=\"muted\">Structure only — element text and property values were not captured.</p>\n");
        if (tree.Roots.Count == 0)
        {
            html.Append("<p class=\"muted\">No elements were captured.</p>\n</section>\n");
            return;
        }

        var selected = result.Manifest?.SelectedElementId;
        var budget = MaxRenderedTreeNodes;
        AppendTreeNodes(html, tree.Roots, selected, ref budget);
        if (budget <= 0)
            html.Append("<p class=\"muted\">Tree display truncated.</p>\n");
        html.Append("</section>\n");
    }

    private static void AppendTreeNodes(StringBuilder html, List<EvidenceTreeNode> nodes, string? selectedId, ref int budget)
    {
        html.Append("<ul class=\"tree\">\n");
        foreach (var node in nodes)
        {
            if (budget <= 0) break;
            budget--;

            var isSelected = selectedId is not null && string.Equals(node.Id, selectedId, StringComparison.Ordinal);
            html.Append(isSelected ? "<li class=\"sel\">" : "<li>");
            html.Append("<code>").Append(E(node.Type)).Append("</code>");
            if (node.AutomationId is { Length: > 0 })
                html.Append(" <span class=\"aid\">#").Append(E(node.AutomationId)).Append("</span>");
            if (isSelected)
                html.Append(" <span class=\"badge\">selected</span>");
            if (node.Bounds is not null)
            {
                html.Append(" <span class=\"muted\">")
                    .Append(E($"{Num(node.Bounds.X)},{Num(node.Bounds.Y)} {Num(node.Bounds.Width)}×{Num(node.Bounds.Height)}"))
                    .Append("</span>");
            }
            if (!node.Visible) html.Append(" <span class=\"muted\">hidden</span>");
            if (!node.Enabled) html.Append(" <span class=\"muted\">disabled</span>");
            var source = FormatSource(node.SourceFile, node.SourceLine, node.SourceColumn);
            if (source is not null)
                html.Append(" <span class=\"src\">").Append(E(source)).Append("</span>");

            if (node.Children is { Count: > 0 } && budget > 0)
                AppendTreeNodes(html, node.Children, selectedId, ref budget);

            html.Append("</li>\n");
        }
        html.Append("</ul>\n");
    }

    private static void RenderWorkflow(StringBuilder html, EvidenceReadResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Workflow)) return;
        // Rendered as inert, encoded text — bundled markdown is never interpreted as markup.
        html.Append("<section><h2>Workflow</h2>\n<pre class=\"doc\">")
            .Append(E(result.Workflow))
            .Append("</pre>\n</section>\n");
    }

    private static void AppendTruncationNote(StringBuilder html, int total, int rendered)
    {
        if (total <= rendered) return;
        html.Append("<p class=\"muted\">Showing the first ")
            .Append(rendered.ToString(CultureInfo.InvariantCulture))
            .Append(" of ")
            .Append(total.ToString(CultureInfo.InvariantCulture))
            .Append(" rows.</p>\n");
    }

    private static void Definition(StringBuilder html, string term, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        html.Append("<dt>").Append(E(term)).Append("</dt><dd>").Append(E(value)).Append("</dd>\n");
    }

    private static void Stat(StringBuilder html, string label, int value)
        => html.Append("<li><span class=\"n\">").Append(value.ToString(CultureInfo.InvariantCulture))
               .Append("</span><span>").Append(E(label)).Append("</span></li>\n");

    private static string? Join(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return string.IsNullOrWhiteSpace(second) ? null : second;
        return string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}";
    }

    private static string? FormatSource(string? file, int? line, int? column)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        if (line is null) return file;
        return column is null
            ? $"{file}:{line.Value.ToString(CultureInfo.InvariantCulture)}"
            : $"{file}:{line.Value.ToString(CultureInfo.InvariantCulture)}:{column.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string? Num(double? value)
        => value is null ? null : Math.Round(value.Value, 2).ToString(CultureInfo.InvariantCulture);

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes.ToString(CultureInfo.InvariantCulture)} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} KB";
        return $"{(bytes / (1024.0 * 1024.0)).ToString("0.##", CultureInfo.InvariantCulture)} MB";
    }

    /// <summary>HTML-encodes every interpolated value. Null becomes an empty cell.</summary>
    private static string E(string? value) => value is null ? "" : WebUtility.HtmlEncode(value);

    private const string Css = """
        :root { color-scheme: light dark; }
        body { margin: 0; font: 14px/1.5 system-ui, -apple-system, Segoe UI, sans-serif; background: #f6f7f9; color: #1b1b1f; }
        main { max-width: 1100px; margin: 0 auto; padding: 24px 20px 64px; }
        h1 { font-size: 22px; margin: 0 0 16px; }
        h2 { font-size: 17px; margin: 0 0 10px; }
        h3 { font-size: 15px; margin: 18px 0 6px; }
        section { background: #fff; border: 1px solid #dfe1e6; border-radius: 8px; padding: 16px 18px; margin: 0 0 16px; }
        section.warn { border-color: #e0b400; background: #fffbe6; }
        dl.grid { display: grid; grid-template-columns: max-content 1fr; gap: 4px 16px; margin: 0; }
        dt { font-weight: 600; }
        dd { margin: 0; }
        ul.stats { display: flex; flex-wrap: wrap; gap: 18px; list-style: none; padding: 0; margin: 14px 0 0; }
        ul.stats li { display: flex; flex-direction: column; min-width: 110px; }
        ul.stats .n { font-size: 20px; font-weight: 600; }
        table { width: 100%; border-collapse: collapse; margin: 8px 0; font-size: 13px; }
        caption { text-align: left; font-weight: 600; padding: 6px 0; }
        th, td { border-bottom: 1px solid #e6e8ec; padding: 6px 8px; text-align: left; vertical-align: top; }
        th { background: #f0f1f4; }
        pre { margin: 0; white-space: pre-wrap; word-break: break-word; font: 12px/1.45 ui-monospace, Consolas, monospace; }
        pre.doc { background: #f0f1f4; padding: 12px; border-radius: 6px; }
        code { font: 12px/1.45 ui-monospace, Consolas, monospace; }
        img { max-width: 100%; height: auto; border: 1px solid #dfe1e6; border-radius: 6px; }
        ul.tree { list-style: none; margin: 4px 0 0; padding-left: 18px; border-left: 1px solid #e6e8ec; }
        ul.tree li { padding: 1px 0; }
        li.sel { background: #fff3cd; }
        .aid { color: #0b6bcb; }
        .src { color: #6a737d; }
        .muted { color: #6a737d; }
        .badge { background: #0b6bcb; color: #fff; border-radius: 4px; padding: 0 6px; font-size: 11px; }
        footer { color: #6a737d; font-size: 12px; }
        @media (prefers-color-scheme: dark) {
          body { background: #16181d; color: #e6e6e6; }
          section { background: #1f2229; border-color: #333842; }
          section.warn { background: #2b2718; border-color: #7a6410; }
          th { background: #262a33; }
          th, td { border-color: #333842; }
          pre.doc { background: #262a33; }
          li.sel { background: #3a3418; }
          img { border-color: #333842; }
        }
        """;
}
