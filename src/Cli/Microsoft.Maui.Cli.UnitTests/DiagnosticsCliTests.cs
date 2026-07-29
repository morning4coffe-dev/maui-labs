using System.Text.Json;
using Microsoft.Maui.Cli.UnitTests.Fixtures;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

/// <summary>
/// Exercises <c>maui devflow diagnostics</c> end to end against the mock agent: the one-shot layout
/// scan and the bounded performance triage window, in both JSON and human output.
/// </summary>
[Collection("CLI")]
public class DiagnosticsCliTests
{
    private static async Task<(MockAgentServer server, CliTestHarness cli)> CreateFixturesAsync()
    {
        var server = new MockAgentServer();
        await server.StartAsync();
        return (server, new CliTestHarness(server.Port));
    }

    [Fact]
    public async Task DiagnosticsLayout_ReturnsTheTypedReportAsJson()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;

        var result = await cli.InvokeAsync("devflow", "diagnostics", "layout", "--json");

        Assert.Equal(0, result.ExitCode);
        var json = result.ParseJsonOutput();
        Assert.Equal("1.0", json.GetProperty("schemaVersion").GetString());
        Assert.Equal(1, json.GetProperty("summary").GetProperty("violations").GetInt32());
        Assert.Equal(1, json.GetProperty("summary").GetProperty("incomplete").GetInt32());
        Assert.Equal("partial", json.GetProperty("coverage").GetProperty("overall").GetString());
        Assert.NotEmpty(json.GetProperty("coverage").GetProperty("neverCaptured").EnumerateArray());
        Assert.Contains(server.RecordedRequests, r => r.Path == "/api/v1/ui/diagnostics/layout");
    }

    [Fact]
    public async Task DiagnosticsLayout_ForwardsScopeOptionsToTheAgent()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;

        var result = await cli.InvokeAsync(
            "devflow", "diagnostics", "layout", "--element", "Title", "--max-elements", "25", "--json");

        Assert.Equal(0, result.ExitCode);
        var request = Assert.Single(server.RecordedRequests, r => r.Path == "/api/v1/ui/diagnostics/layout");
        Assert.Contains("elementId=Title", request.QueryString, StringComparison.Ordinal);
        Assert.Contains("maxElements=25", request.QueryString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticsLayout_HumanOutputExplainsCoverageAndLimitations()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;

        var result = await cli.InvokeAsync("devflow", "diagnostics", "layout", "--no-json");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Layout diagnostics", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("1 violation(s)", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Coverage: partial", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("VIOLATION", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("INCOMPLETE", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Limitations:", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Never captured:", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticsLayout_CanHideObservationsFromHumanOutput()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;

        var result = await cli.InvokeAsync(
            "devflow", "diagnostics", "layout", "--include-observations", "false", "--no-json");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("VIOLATION", result.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("OBSERVATION", result.StdOut, StringComparison.Ordinal);
        // Coverage is still reported, so a hidden observation never reads as a clean pass.
        Assert.Contains("Coverage: partial", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticsPerformance_RecordsAWindowAndSummarizesIt()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;

        var result = await cli.InvokeAsync(
            "devflow", "diagnostics", "performance", "--duration", "1", "--sample-interval", "250", "--json");

        Assert.Equal(0, result.ExitCode);
        var json = result.ParseJsonOutput();
        Assert.Equal(2, json.GetProperty("session").GetProperty("sampleCount").GetInt32());
        Assert.Equal(500000, json.GetProperty("memory").GetProperty("managedDeltaBytes").GetInt64());
        Assert.Equal(2, json.GetProperty("gc").GetProperty("gen0Delta").GetInt32());
        Assert.True(json.GetProperty("cpu").GetProperty("peakPercent").GetDouble() > 0);
        // Estimated frame timings are never surfaced as a frame rate.
        Assert.False(json.GetProperty("frames").GetProperty("supported").GetBoolean());
        Assert.True(json.GetProperty("loss").GetProperty("anyLoss").GetBoolean());

        Assert.Contains(server.RecordedRequests, r => r.Path == "/api/v1/profiler/sessions" && r.Method == "POST");
        Assert.Contains(server.RecordedRequests, r => r.Method == "DELETE" && r.Path.StartsWith("/api/v1/profiler/sessions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnosticsPerformance_HumanOutputWarnsAboutPerturbationAndLoss()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;

        var result = await cli.InvokeAsync(
            "devflow", "diagnostics", "performance", "--duration", "1", "--no-json");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Performance triage", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Buffer loss", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Frames: unavailable", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("native profiler", result.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Limitations:", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticsPerformance_AttachDoesNotStartOrStopASession()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;

        var result = await cli.InvokeAsync("devflow", "diagnostics", "performance", "--attach", "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(server.RecordedRequests, r => r.Path == "/api/v1/profiler/sessions" && r.Method == "POST");
        Assert.DoesNotContain(server.RecordedRequests, r => r.Method == "DELETE" && r.Path.StartsWith("/api/v1/profiler/sessions", StringComparison.Ordinal));
        Assert.Contains(server.RecordedRequests, r => r.Path.Contains("/samples", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnosticsPerformance_RejectsAnOutOfRangeSampleInterval()
    {
        var (server, cli) = await CreateFixturesAsync();
        await using var _ = server;

        var result = await cli.InvokeAsync(
            "devflow", "diagnostics", "performance", "--sample-interval", "5", "--json");

        Assert.Equal(1, result.ExitCode);
        using var error = JsonDocument.Parse(result.StdErr);
        Assert.Contains("sample-interval", error.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
