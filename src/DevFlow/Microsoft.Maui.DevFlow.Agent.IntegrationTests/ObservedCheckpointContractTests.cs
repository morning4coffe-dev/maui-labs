using Microsoft.Maui.DevFlow.Agent.IntegrationTests.Fixtures;

namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests;

/// <summary>
/// Pins the observed clean-state checkpoint a platform fixture must report.
/// </summary>
/// <remarks>
/// Admission compares a plan's declared checkpoint against what the host observed. A field the
/// host never reports is not a mismatch the author can fix by correcting the plan: it is
/// permanently unsatisfiable, so every plan declaring that field is rejected before mutation with
/// no way to satisfy it. This was observed on Android, where the three committed plans declaring
/// <c>window</c> were the only three that could never run, which also meant no independently
/// verified flow ever executed in the pilot lane. The source scan is deliberate: the defect is the
/// absence of an assignment, so only reading the construction site can catch it, and it costs no
/// device to run.
/// </remarks>
public sealed class ObservedCheckpointContractTests
{
    static readonly string[] RequiredAssignments = ["Window =", "Modal ="];

    [Fact]
    public void EveryPlatformFixture_ObservesWindowAndModal()
    {
        var fixtures = Directory.GetFiles(FixturesDirectory(), "*Fixture.cs");
        Assert.NotEmpty(fixtures);

        var missing = new List<string>();
        var scanned = 0;
        foreach (var path in fixtures)
        {
            var source = File.ReadAllText(path);
            if (!source.Contains("new MauiFlowCheckpoint", StringComparison.Ordinal))
                continue;

            scanned++;
            foreach (var assignment in RequiredAssignments)
            {
                if (!source.Contains(assignment, StringComparison.Ordinal))
                    missing.Add($"{Path.GetFileName(path)} does not observe '{assignment.TrimEnd(' ', '=')}'.");
            }
        }

        Assert.True(scanned > 0, "No platform fixture builds an observed checkpoint.");
        Assert.Empty(missing);
    }

    static string FixturesDirectory()
    {
        var directory = Path.Combine(
            AppFixtureBase.FindRepoRoot(),
            "src",
            "DevFlow",
            "Microsoft.Maui.DevFlow.Agent.IntegrationTests",
            "Fixtures");
        Assert.True(Directory.Exists(directory), $"Fixtures directory not found at '{directory}'.");
        return directory;
    }
}
