using Microsoft.Maui.Cli.UnitTests.Fixtures;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

[Collection("CLI")]
public sealed class FlowQualificationCliTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "flow-qualification-cli-tests",
        Guid.NewGuid().ToString("N"));

    public FlowQualificationCliTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task FlowQualify_StaticCorpusWritesAdvisoryNotQualifiedJsonWithoutConnectingToAnAgent()
    {
        var output = Path.Combine(_root, "qualification.json");
        var corpus = Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus");
        var cli = new CliTestHarness(mockAgentPort: 1);

        var result = await cli.InvokeRawAsync(
            "devflow",
            "flow",
            "qualify",
            "--platform",
            "android",
            "--corpus",
            corpus,
            "--output",
            output,
            "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(output));
        var json = result.ParseJsonOutput();
        Assert.Equal("maui-preview-qualification", json.GetProperty("kind").GetString());
        Assert.Equal("not-qualified", json.GetProperty("status").GetString());
        Assert.Equal(316, json.GetProperty("metrics").GetProperty("falseHeals").GetProperty("denominator").GetInt32());
        Assert.Equal(0, json.GetProperty("metrics").GetProperty("falseHeals").GetProperty("numerator").GetInt32());
        Assert.Equal(
            "missing",
            json.GetProperty("metrics").GetProperty("runtimeOverhead").GetProperty("deviceOverhead").GetProperty("state").GetString());
    }

    [Fact]
    public async Task FlowQualify_AppleQaManifestUsesTheAppleAdapterAndPreservesSimulatorEvidence()
    {
        var output = Path.Combine(_root, "apple-qualification.json");
        var manifest = Path.Combine(_root, "apple-manifest.json");
        var corpus = Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus");
        await File.WriteAllTextAsync(manifest, CreateAppleManifest());
        var cli = new CliTestHarness(mockAgentPort: 1);

        var result = await cli.InvokeRawAsync(
            "devflow",
            "flow",
            "qualify",
            "--platform",
            "ios",
            "--corpus",
            corpus,
            "--artifact-manifest",
            manifest,
            "--output",
            output,
            "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(output));
        var json = result.ParseJsonOutput();
        var apple = json.GetProperty("appleQa");
        Assert.Equal("ios", apple.GetProperty("platform").GetString());
        Assert.True(apple.GetProperty("foregroundProof").GetBoolean());
        Assert.True(apple.GetProperty("authenticatedTransport").GetBoolean());
        Assert.Equal(1, apple.GetProperty("firstAttemptCount").GetInt32());
        var profile = Assert.Single(
            json.GetProperty("profiles").EnumerateArray(),
            value => value.GetProperty("deviceEvidenceKind").GetString() == "simulator");
        Assert.False(profile.GetProperty("realDevice").GetBoolean());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static string CreateAppleManifest()
    {
        var hash = "sha256:" + new string('a', 64);
        return $$"""
            {
              "schema": 1,
              "kind": "devflow-flow-qa",
              "repository": { "commit": "commit" },
              "workflow": { "runId": "run-1" },
              "experimental": false,
              "backend": null,
              "officialCoverage": true,
              "macCatalystEquivalent": null,
              "testing": { "packageVersion": "testing" },
              "platform": {
                "name": "ios",
                "host": {
                  "xcode": "Xcode 26.3",
                  "runtime": "iOS 18.0",
                  "deviceEvidence": {
                    "kind": "simulator",
                    "realDevice": false,
                    "deviceIdFingerprint": "{{hash}}",
                    "profile": "iPhone"
                  }
                }
              },
              "app": {
                "project": "samples/DevFlow.Sample/DevFlow.Sample.csproj",
                "sourceDigest": "{{hash}}",
                "buildFingerprint": "{{hash}}",
                "packageDigest": "{{hash}}"
              },
              "flows": [
                { "path": "samples/DevFlow.Sample/maui-tests/tier-one.md", "sha256": "{{hash}}" }
              ],
              "appleQa": {
                "schema": 1,
                "kind": "devflow-apple-flow-qa",
                "platform": "ios",
                "experimental": false,
                "backend": null,
                "officialCoverage": true,
                "macCatalystEquivalent": null,
                "spike": {
                  "status": "proved",
                  "foregroundProof": true,
                  "authenticatedTransport": true,
                  "receipt": true,
                  "cancellation": true,
                  "parity": true
                },
                "checkpoint": {
                  "resetFingerprint": "{{hash}}",
                  "seedFingerprint": "{{hash}}",
                  "backendStateFingerprint": "{{hash}}"
                },
                "apple": {
                  "xcodeVersion": "Xcode 26.3",
                  "simulatorRuntime": "iOS 18.0",
                  "simulatorDeviceFingerprint": "{{hash}}",
                  "simulatorDeviceProfile": "iPhone"
                },
                "flows": [
                  {
                    "name": "tier-one",
                    "firstAttempt": {
                      "status": "passed",
                      "report": "artifacts/devflow/run-1/ios/apple-flow-runs/tier-one/flow-run.json",
                      "reportDigest": "{{hash}}"
                    },
                    "cleanAttempts": [
                      {
                        "status": "passed",
                        "report": "artifacts/devflow/run-1/ios/apple-flow-runs/tier-one/flow-run.json",
                        "reportDigest": "{{hash}}"
                      }
                    ]
                  }
                ]
              },
              "artifacts": [
                {
                  "kind": "flow-run-report",
                  "path": "artifacts/devflow/run-1/ios/apple-flow-runs/tier-one/flow-run.json",
                  "sha256": "{{hash}}",
                  "sizeBytes": 1,
                  "redacted": true
                }
              ],
              "omissions": []
            }
            """;
    }
}
