using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Execution;

internal sealed record ExecutionOutputFile(string FileName, byte[] Content)
{
    public long SizeBytes => Content.LongLength;
    public string Digest => "sha256:" + Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();
}

internal sealed class FlowRunReportWriter
{
    public ExecutionOutputFile Create(MauiFlowRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var limits = new MauiFlowRunReportLimits();
        report.ReportPath = MauiFlowRunReportSerializer.FileName;
        report.ReportDigest = null;
        MauiFlowRunReportSerializer.ApplyLimits(report, limits);
        report.ReportDigest = MauiFlowRunReportSerializer.ComputeDigest(report);
        if (!report.Artifacts.Any(static artifact =>
                string.Equals(artifact.Kind, "flow-run-report", StringComparison.Ordinal)))
        {
            report.Artifacts.Add(new MauiFlowArtifactReference
            {
                ArtifactId = "flow-run-" + (report.RunId ?? "run"),
                Kind = "flow-run-report",
                Path = MauiFlowRunReportSerializer.FileName,
                Digest = report.ReportDigest,
                MediaType = "application/json",
                Redacted = true,
                CreatedAt = report.EndedAt,
            });
        }
        var bytes = MauiFlowRunReportSerializer.SerializeToUtf8Bytes(report);
        if (bytes.Length > limits.MaxJsonBytes)
        {
            throw FlowExecutionException.Infrastructure(
                "flow-report-size-limit",
                "The bounded flow report still exceeded the supported size limit.");
        }
        return new ExecutionOutputFile(MauiFlowRunReportSerializer.FileName, bytes);
    }
}

internal sealed class JUnitFlowExecutionWriter
{
    public const string FileName = "report.junit.xml";

    public ExecutionOutputFile Create(MauiFlowRunReport report, string exitCategory)
    {
        ArgumentNullException.ThrowIfNull(report);
        var elapsed = report.StartedAt is not null && report.EndedAt is not null
            ? Math.Max(0, (report.EndedAt.Value - report.StartedAt.Value).TotalSeconds)
            : 0;
        var testCase = new XElement(
            "testcase",
            new XAttribute("classname", "maui.devflow"),
            new XAttribute("name", "flow"),
            new XAttribute("time", elapsed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)),
            new XElement(
                "properties",
                new XElement("property", new XAttribute("name", "devflow.exitCategory"), new XAttribute("value", exitCategory)),
                new XElement("property", new XAttribute("name", "devflow.runId"), new XAttribute("value", SafeXml(report.RunId ?? "run"))),
                new XElement("property", new XAttribute("name", "devflow.verified"), new XAttribute("value", report.Outcome?.Verified == true ? "true" : "false"))));

        var failures = 0;
        var errors = 0;
        var skipped = 0;
        switch (exitCategory)
        {
            case FlowExecutionExitCategories.Pass:
                break;
            case FlowExecutionExitCategories.Unverified:
                failures = 1;
                testCase.Add(new XElement(
                    "failure",
                    new XAttribute("type", FlowExecutionExitCategories.Unverified),
                    new XAttribute("message", "The flow completed but lacks required independent verification evidence.")));
                break;
            case FlowExecutionExitCategories.TestFailure:
                failures = 1;
                testCase.Add(new XElement(
                    "failure",
                    new XAttribute("type", SafeXml(report.Failure?.Code ?? "test-failure")),
                    new XAttribute("message", "The flow assertion or interaction failed.")));
                break;
            default:
                errors = 1;
                testCase.Add(new XElement(
                    "error",
                    new XAttribute("type", exitCategory),
                    new XAttribute("message", "The flow could not complete.")));
                break;
        }

        var suite = new XElement(
            "testsuite",
            new XAttribute("name", "MAUI DevFlow"),
            new XAttribute("tests", "1"),
            new XAttribute("failures", failures),
            new XAttribute("errors", errors),
            new XAttribute("skipped", skipped),
            new XAttribute("time", elapsed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)),
            testCase);
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), suite);
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            OmitXmlDeclaration = false,
        }))
        {
            document.Save(writer);
        }
        return new ExecutionOutputFile(FileName, stream.ToArray());
    }

    private static string SafeXml(string value)
    {
        var bounded = value.Length > 512 ? value[..512] : value;
        return new string(bounded.Where(XmlConvertIsLegal).ToArray());
    }

    private static bool XmlConvertIsLegal(char value)
        => value is '\t' or '\n' or '\r' || value >= ' ';
}

internal sealed class ExecutionManifestWriter
{
    public const string FileName = "execution-manifest.json";

    public ExecutionOutputFile Create(MauiTestExecutionManifest manifest)
        => new(FileName, MauiTestExecutionManifestSerializer.SerializeToUtf8Bytes(manifest));
}

internal sealed class ImmutableExecutionOutputWriter
{
    public async Task WriteAsync(
        string outputDirectory,
        IReadOnlyCollection<ExecutionOutputFile> files,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
            return;
        var root = Path.GetFullPath(outputDirectory);
        if (!ExecutionPathSafety.EntryExists(root))
            root = ExecutionPathSafety.PrepareNewOrEmptyDirectory(root);
        else
            ExecutionPathSafety.ValidateOutputDirectory(root);
        var names = files.Select(static file => file.FileName).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
            names.Any(static name =>
                string.IsNullOrWhiteSpace(name) ||
                !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)))
        {
            throw FlowExecutionException.Infrastructure(
                "execution-output-name-invalid",
                "An execution output filename was invalid.");
        }

        var staged = new List<(string Temporary, string Target)>(files.Count);
        var committed = new List<string>(files.Count);
        try
        {
            foreach (var file in files)
            {
                ExecutionPathSafety.ValidateOutputDirectory(root);
                var target = Path.Combine(root, file.FileName);
                if (ExecutionPathSafety.EntryExists(target))
                {
                    throw FlowExecutionException.Invalid(
                        "execution-output-exists",
                        "The execution output already exists. First-attempt artifacts are immutable.");
                }
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await using (var stream = new FileStream(
                    temporary,
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    }))
                {
                    await stream.WriteAsync(file.Content, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                ExecutionPathSafety.ValidateOutputDirectory(root);
                ExecutionPathSafety.RejectReparsePoints(
                    temporary,
                    "execution-output-reparse-point",
                    "The execution output path and its existing ancestors cannot be symbolic links or reparse points.");
                staged.Add((temporary, target));
            }

            foreach (var file in staged)
            {
                ExecutionPathSafety.ValidateOutputDirectory(root);
                if (ExecutionPathSafety.EntryExists(file.Target))
                {
                    throw FlowExecutionException.Invalid(
                        "execution-output-exists",
                        "The execution output already exists. First-attempt artifacts are immutable.");
                }
                File.Move(file.Temporary, file.Target);
                committed.Add(file.Target);
            }
        }
        catch
        {
            foreach (var file in staged)
            {
                try { File.Delete(file.Temporary); } catch { }
            }
            foreach (var path in committed)
            {
                try { File.Delete(path); } catch { }
            }
            throw;
        }
    }
}
