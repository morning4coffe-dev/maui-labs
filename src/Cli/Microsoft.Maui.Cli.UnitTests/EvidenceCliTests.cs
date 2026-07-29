using System.Text.Json;
using Microsoft.Maui.Cli.UnitTests.Fixtures;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

/// <summary>
/// Exercises <c>maui devflow evidence</c> end to end against the mock agent: the preview plan, the
/// atomic capture (including the explicit-overwrite rule), and the regenerated offline report.
/// </summary>
[Collection("CLI")]
public class EvidenceCliTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory, "evidence-cli-tests", Guid.NewGuid().ToString("N"));

    public EvidenceCliTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    private static async Task<(MockAgentServer server, CliTestHarness cli)> CreateFixturesAsync()
    {
        var server = new MockAgentServer();
        await server.StartAsync();
        return (server, new CliTestHarness(server.Port));
    }

    [Fact]
    public async Task EvidencePreview_ReportsInclusionsExclusionsAndLimits()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;

        var result = await cli.InvokeAsync("devflow", "evidence", "preview", "--json");

        Assert.Equal(0, result.ExitCode);
        var json = result.ParseJsonOutput();
        Assert.Equal("cli", json.GetProperty("source").GetString());
        Assert.True(json.GetProperty("redactionVersion").GetInt32() >= 1);
        Assert.False(json.GetProperty("screenshot").GetProperty("requested").GetBoolean());
        Assert.True(json.GetProperty("neverIncluded").GetArrayLength() > 0);

        var included = json.GetProperty("included").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("manifest.json", included);
        Assert.Contains("tree.json", included);
        Assert.Contains("environment.json", included);
        Assert.DoesNotContain("screenshot.png", included);

        var excluded = json.GetProperty("excluded").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("screenshot.png", excluded);
        Assert.EndsWith(".mauitrace", json.GetProperty("outputPath").GetString()!, StringComparison.Ordinal);
        Assert.Single(server.RecordedRequests, r => r.Path == "/api/v1/ui/tree");
        // A preview must never take a screenshot.
        Assert.DoesNotContain(server.RecordedRequests, r => r.Path == "/api/v1/ui/screenshot");
    }

    [Fact]
    public async Task EvidenceCapture_WritesABundleAndReportsItsManifest()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;
        var output = Path.Combine(_root, "capture.mauitrace");

        var result = await cli.InvokeAsync("devflow", "evidence", "capture", "--output", output, "--json");

        Assert.Equal(0, result.ExitCode);
        var json = result.ParseJsonOutput();
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.True(File.Exists(output));
        Assert.Equal(Path.GetFullPath(output), json.GetProperty("path").GetString());

        var manifest = json.GetProperty("manifest");
        Assert.Equal("maui-devflow-evidence", manifest.GetProperty("schema").GetString());
        Assert.Equal("cli", manifest.GetProperty("source").GetString());
        Assert.False(manifest.GetProperty("screenshot").GetProperty("included").GetBoolean());
        Assert.True(manifest.GetProperty("entries").GetArrayLength() > 0);
    }

    [Fact]
    public async Task EvidenceCapture_RefusesToOverwriteWithoutTheFlag()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;
        var output = Path.Combine(_root, "existing.mauitrace");
        File.WriteAllText(output, "original");

        var refused = await cli.InvokeAsync("devflow", "evidence", "capture", "--output", output, "--json");

        Assert.Equal(1, refused.ExitCode);
        using var error = JsonDocument.Parse(refused.StdErr);
        Assert.Equal("InvocationError", error.RootElement.GetProperty("type").GetString());
        Assert.Contains("already exists", error.RootElement.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", File.ReadAllText(output));

        var overwritten = await cli.InvokeAsync("devflow", "evidence", "capture", "--output", output, "--overwrite", "--json");

        Assert.Equal(0, overwritten.ExitCode);
        Assert.NotEqual("original", File.ReadAllText(output));
    }

    [Fact]
    public async Task EvidenceCapture_RejectsANonMauitraceExtension()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;

        var result = await cli.InvokeAsync(
            "devflow", "evidence", "capture", "--output", Path.Combine(_root, "bundle.zip"), "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(".mauitrace", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvidenceCapture_RejectsAMissingWorkflowFile()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;

        var result = await cli.InvokeAsync(
            "devflow", "evidence", "capture",
            "--output", Path.Combine(_root, "workflow.mauitrace"),
            "--workflow", Path.Combine(_root, "missing.md"),
            "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Workflow file not found", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvidenceView_GeneratesAStaticReportFromTheBundle()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;
        var bundle = Path.Combine(_root, "view.mauitrace");
        var report = Path.Combine(_root, "report.html");
        Assert.Equal(0, (await cli.InvokeAsync("devflow", "evidence", "capture", "--output", bundle, "--json")).ExitCode);

        var result = await cli.InvokeRawAsync(
            "devflow", "evidence", "view", bundle, "--no-open", "--output-report", report, "--json");

        Assert.Equal(0, result.ExitCode);
        var json = result.ParseJsonOutput();
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.False(json.GetProperty("opened").GetBoolean());
        Assert.True(File.Exists(report));

        var html = File.ReadAllText(report);
        Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
        Assert.Contains("script-src 'none'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvidenceView_RejectsAMissingBundle()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;

        var result = await cli.InvokeRawAsync(
            "devflow", "evidence", "view", Path.Combine(_root, "nope.mauitrace"), "--no-open", "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Bundle not found", result.StdErr, StringComparison.Ordinal);
    }
}
