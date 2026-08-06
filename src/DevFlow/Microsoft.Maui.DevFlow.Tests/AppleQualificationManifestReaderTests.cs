using System.Text.Json.Nodes;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class AppleQualificationManifestReaderTests
{
    [Theory]
    [InlineData("ios", false, null, true, null, "simulator")]
    [InlineData("maccatalyst", false, null, true, null, "desktop-host")]
    [InlineData("macos", true, "appkit", false, false, "desktop-host")]
    public void ParseJson_ValidApplePlatforms_MapSourceFactsWithoutChangingCoverageLabels(
        string platform,
        bool experimental,
        string? backend,
        bool officialCoverage,
        bool? macCatalystEquivalent,
        string deviceEvidenceKind)
    {
        var parsed = MauiAppleFlowQaManifestReader.ParseJson(
            CreateManifest(platform, experimental, backend, officialCoverage, macCatalystEquivalent, deviceEvidenceKind));

        Assert.True(parsed.Ok, string.Join(", ", parsed.Errors));
        var apple = Assert.IsType<MauiQualificationAppleQaEvidence>(parsed.Input.AppleQa);
        Assert.Equal(MauiAppleFlowQaManifestReader.AdapterContractVersion, apple.ContractVersion);
        Assert.Equal(platform, apple.Platform);
        Assert.Equal(experimental, apple.Experimental);
        Assert.Equal(backend, apple.Backend);
        Assert.Equal(officialCoverage, apple.OfficialCoverage);
        Assert.Equal(macCatalystEquivalent, apple.MacCatalystEquivalent);
        Assert.Equal("proved", apple.SpikeStatus);
        Assert.True(apple.ForegroundProof);
        Assert.True(apple.AuthenticatedTransport);
        Assert.True(apple.Receipt);
        Assert.True(apple.Cancellation);
        Assert.True(apple.Parity);
        Assert.Equal(1, apple.FirstAttemptCount);
        Assert.Equal(2, apple.CleanAttemptCount);
        Assert.Equal(1, apple.ArtifactCount);
        Assert.Equal(1, apple.OmissionCount);
        Assert.Equal(deviceEvidenceKind, Assert.Single(parsed.Input.Profiles).DeviceEvidenceKind);
        Assert.False(Assert.Single(parsed.Input.Profiles).RealDevice);
        Assert.Single(apple.FlowDigests);
        Assert.Contains(parsed.Input.ArtifactRefs, artifact => artifact.Kind == "flow-digest");
        Assert.Contains(parsed.Input.Exclusions, exclusion => exclusion.Kind == "diagnostic-rerun");
    }

    [Fact]
    public void ParseJson_MissingAppleSection_IsInvalidAndDoesNotInventEvidence()
    {
        var root = JsonNode.Parse(CreateManifest())!.AsObject();
        root["appleQa"] = null;

        var parsed = MauiAppleFlowQaManifestReader.ParseJson(root.ToJsonString());

        Assert.False(parsed.Ok);
        Assert.Contains("apple-qa-section-missing", parsed.Errors);
        Assert.False(parsed.Input.Evidence!.ArtifactManifestValid);
        Assert.False(parsed.Input.Evidence.FirstAttemptEvidencePresent);
    }

    [Fact]
    public void ParseJson_MismatchedNestedPlatformAndOfficialLabels_FailsClosed()
    {
        var root = JsonNode.Parse(CreateManifest())!.AsObject();
        root["appleQa"]!["platform"] = "maccatalyst";
        root["experimental"] = true;

        var parsed = MauiAppleFlowQaManifestReader.ParseJson(root.ToJsonString());

        Assert.False(parsed.Ok);
        Assert.Contains("apple-qa-section-platform-mismatch", parsed.Errors);
        Assert.Contains("apple-qa-official-platform-labels-invalid", parsed.Errors);
    }

    [Fact]
    public void ParseJson_FirstAttemptAccounting_UsesFirstCleanAttemptAndKeepsRepeatsSeparate()
    {
        var parsed = MauiPreviewQualificationArtifactManifestReader.ParseJson(CreateManifest());

        Assert.True(parsed.Ok, string.Join(", ", parsed.Errors));
        Assert.Equal(2, parsed.Input.Samples.Count);
        Assert.Equal(1, parsed.Input.Samples.Count(sample => sample.FirstAttempt == true));
        Assert.Equal(1, parsed.Input.Samples.Count(sample => sample.FirstAttempt == false));
        Assert.All(parsed.Input.Samples, sample => Assert.True(sample.CleanState));
        Assert.All(parsed.Input.Samples, sample => Assert.Equal(MauiQualificationSampleSources.DeviceBacked, sample.Source));
        Assert.All(parsed.Input.Samples, sample => Assert.Equal(MauiFlowRunOutcomes.Passed, sample.Outcome));
    }

    [Fact]
    public void ParseJson_FirstAttemptMismatch_IsNotCountedAsFirstAttemptEvidence()
    {
        var root = JsonNode.Parse(CreateManifest())!.AsObject();
        root["appleQa"]!["flows"]![0]!["cleanAttempts"]![0]!["status"] = "failed";

        var parsed = MauiAppleFlowQaManifestReader.ParseJson(root.ToJsonString());

        Assert.False(parsed.Ok);
        Assert.Contains("apple-qa-first-attempt-mismatch", parsed.Errors);
        Assert.Equal(0, parsed.Input.AppleQa!.FirstAttemptCount);
        Assert.False(parsed.Input.Evidence!.FirstAttemptEvidencePresent);
        Assert.All(parsed.Input.Samples, sample => Assert.False(sample.FirstAttempt == true));
    }

    [Fact]
    public void QualificationMapping_PreservesAppleEvidenceAndDoesNotCertifySimulatorAsPhysical()
    {
        var parsed = MauiPreviewQualificationArtifactManifestReader.ParseJson(CreateManifest());

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(parsed.Input);

        var apple = Assert.IsType<MauiQualificationAppleQaEvidence>(report.AppleQa);
        Assert.Equal("ios", apple.Platform);
        Assert.True(apple.ForegroundProof);
        Assert.True(apple.AuthenticatedTransport);
        Assert.True(apple.Receipt);
        Assert.True(apple.Cancellation);
        Assert.True(apple.Parity);
        Assert.Equal(1, apple.FirstAttemptCount);
        Assert.Equal(2, apple.CleanAttemptCount);
        Assert.Equal("simulator", Assert.Single(report.Profiles).DeviceEvidenceKind);
        Assert.False(Assert.Single(report.Profiles).RealDevice);
        Assert.NotEqual(MauiPreviewQualificationStates.Pass, report.Status);
    }

    private static string CreateManifest(
        string platform = "ios",
        bool experimental = false,
        string? backend = null,
        bool officialCoverage = true,
        bool? macCatalystEquivalent = null,
        string deviceEvidenceKind = "simulator")
    {
        var hashA = Hash('a');
        var hashB = Hash('b');
        var hashC = Hash('c');
        var hashD = Hash('d');
        var backendJson = backend is null ? "null" : $"\"{backend}\"";
        var equivalentJson = macCatalystEquivalent is null
            ? "null"
            : macCatalystEquivalent.Value ? "true" : "false";

        return $$"""
            {
              "schema": 1,
              "kind": "devflow-flow-qa",
              "repository": { "commit": "commit" },
              "workflow": { "runId": "run-1" },
              "experimental": {{experimental.ToString().ToLowerInvariant()}},
              "backend": {{backendJson}},
              "officialCoverage": {{officialCoverage.ToString().ToLowerInvariant()}},
              "macCatalystEquivalent": {{equivalentJson}},
              "testing": { "packageVersion": "testing" },
              "platform": {
                "name": "{{platform}}",
                "host": {
                  "xcode": "Xcode 26.3",
                  "runtime": "iOS 18.0",
                  "deviceEvidence": {
                    "kind": "{{deviceEvidenceKind}}",
                    "realDevice": false,
                    "deviceIdFingerprint": "{{hashA}}",
                    "profile": "iPhone"
                  }
                }
              },
              "app": {
                "project": "samples/DevFlow.Sample/DevFlow.Sample.csproj",
                "sourceDigest": "{{hashB}}",
                "buildFingerprint": "{{hashC}}",
                "packageDigest": "{{hashD}}"
              },
              "flows": [
                {
                  "path": "samples/DevFlow.Sample/maui-tests/tier-one.md",
                  "sha256": "{{hashA}}"
                }
              ],
              "appleQa": {
                "schema": 1,
                "kind": "devflow-apple-flow-qa",
                "platform": "{{platform}}",
                "experimental": {{experimental.ToString().ToLowerInvariant()}},
                "backend": {{backendJson}},
                "officialCoverage": {{officialCoverage.ToString().ToLowerInvariant()}},
                "macCatalystEquivalent": {{equivalentJson}},
                "spike": {
                  "status": "proved",
                  "foregroundProof": true,
                  "authenticatedTransport": true,
                  "receipt": true,
                  "cancellation": true,
                  "parity": true
                },
                "checkpoint": {
                  "resetFingerprint": "{{hashB}}",
                  "seedFingerprint": "{{hashC}}",
                  "backendStateFingerprint": "{{hashD}}"
                },
                "apple": {
                  "xcodeVersion": "Xcode 26.3",
                  "simulatorRuntime": "iOS 18.0",
                  "simulatorDeviceFingerprint": "{{hashA}}",
                  "simulatorDeviceProfile": "iPhone"
                },
                "flows": [
                  {
                    "name": "tier-one",
                    "firstAttempt": {
                      "repetition": 1,
                      "status": "passed",
                      "report": "artifacts/devflow/run-1/{{platform}}/apple-flow-runs/tier-one/flow-run.json",
                      "reportDigest": "{{hashB}}"
                    },
                    "cleanAttempts": [
                      {
                        "repetition": 1,
                        "status": "passed",
                        "report": "artifacts/devflow/run-1/{{platform}}/apple-flow-runs/tier-one/flow-run.json",
                        "reportDigest": "{{hashB}}"
                      },
                      {
                        "repetition": 2,
                        "status": "passed",
                        "report": "artifacts/devflow/run-1/{{platform}}/apple-flow-runs/tier-one-2/flow-run.json",
                        "reportDigest": "{{hashC}}"
                      }
                    ]
                  }
                ]
              },
              "artifacts": [
                {
                  "kind": "flow-run-report",
                  "path": "artifacts/devflow/run-1/{{platform}}/apple-flow-runs/tier-one/flow-run.json",
                  "sha256": "{{hashB}}",
                  "sizeBytes": 12,
                  "redacted": true
                }
              ],
              "omissions": [
                {
                  "kind": "diagnostic-rerun",
                  "reason": "No automatic diagnostic rerun was performed."
                }
              ]
            }
            """;
    }

    private static string Hash(char character) => "sha256:" + new string(character, 64);
}
