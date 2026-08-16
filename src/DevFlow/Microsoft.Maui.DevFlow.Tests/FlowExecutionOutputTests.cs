using System.Xml.Linq;
using Microsoft.Maui.Cli.DevFlow.Execution;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class FlowExecutionOutputTests
{
    [Fact]
    public void JUnitWriter_UnverifiedRun_IsSkippedNotFailedAndCarriesExactCategory()
    {
        // Regression: an unverified run was written as a JUnit <failure>. Every shipped sample has
        // `independentBusinessOracles: []`, so every shipped flow was red in CI while passing --
        // a signal teams would have had to mute wholesale. `unverified` is a legitimate third
        // state; <skipped> plus the devflow.* properties says so without claiming the app broke.
        var report = new MauiFlowRunReport
        {
            RunId = "run-output-contract",
            LegacyFlowIdentity = "checkout.md",
            StartedAt = DateTimeOffset.UnixEpoch,
            EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
            Outcome = new MauiFlowRunOutcome
            {
                Status = MauiFlowRunOutcomes.Passed,
                Terminal = true,
                Verified = false,
            },
        };

        var output = new JUnitFlowExecutionWriter().Create(
            report,
            FlowExecutionExitCategories.Unverified);
        var document = XDocument.Parse(System.Text.Encoding.UTF8.GetString(output.Content));

        Assert.Empty(document.Descendants("failure"));
        Assert.Single(document.Descendants("skipped"));
        Assert.Equal(
            FlowExecutionExitCategories.Unverified,
            document.Descendants("property")
                .Single(element => (string?)element.Attribute("name") == "devflow.exitCategory")
                .Attribute("value")?.Value);
        Assert.Equal(
            "false",
            document.Descendants("property")
                .Single(element => (string?)element.Attribute("name") == "devflow.verified")
                .Attribute("value")?.Value);
        Assert.Equal("0", document.Root?.Attribute("failures")?.Value);
        Assert.Equal("0", document.Root?.Attribute("errors")?.Value);
        Assert.Equal("1", document.Root?.Attribute("skipped")?.Value);
    }

    [Fact]
    public void JUnitWriter_TestFailure_IsStillAFailure()
    {
        // The disclosure above is scoped to `unverified`: a real assertion failure must stay red.
        var report = new MauiFlowRunReport
        {
            RunId = "run-output-contract",
            LegacyFlowIdentity = "checkout.md",
            StartedAt = DateTimeOffset.UnixEpoch,
            EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
            Outcome = new MauiFlowRunOutcome
            {
                Status = MauiFlowRunOutcomes.Failed,
                Terminal = true,
                Verified = false,
            },
            Failure = new MauiFlowFailure
            {
                Class = MauiFlowFailureClasses.LocatorNotFound,
                Code = MauiFlowFailureClasses.LocatorNotFound,
            },
        };

        var output = new JUnitFlowExecutionWriter().Create(
            report,
            FlowExecutionExitCategories.TestFailure);
        var document = XDocument.Parse(System.Text.Encoding.UTF8.GetString(output.Content));

        Assert.Single(document.Descendants("failure"));
        Assert.Empty(document.Descendants("skipped"));
        Assert.Equal("1", document.Root?.Attribute("failures")?.Value);
        Assert.Equal("0", document.Root?.Attribute("skipped")?.Value);
    }

    [Fact]
    public async Task ImmutableWriter_ExistingFirstAttemptFile_IsRejectedWithoutOverwrite()
    {
        var root = CreateArtifactRoot();
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, MauiFlowRunReportSerializer.FileName);
        await File.WriteAllTextAsync(path, "original");
        try
        {
            var exception = await Assert.ThrowsAsync<FlowExecutionException>(() =>
                new ImmutableExecutionOutputWriter().WriteAsync(
                    root,
                    [new ExecutionOutputFile(MauiFlowRunReportSerializer.FileName, "replacement"u8.ToArray())]));

            Assert.Equal("execution-output-exists", exception.Code);
            Assert.Equal("original", await File.ReadAllTextAsync(path));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string CreateArtifactRoot()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "MauiLabs.slnx")))
            current = current.Parent;
        if (current is null)
            throw new InvalidOperationException("Repository root not found.");
        return Path.Combine(
            current.FullName,
            "artifacts",
            "TestResults",
            "flow-execution-output-tests",
            Guid.NewGuid().ToString("N"));
    }
}
