using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public class MauiArtifactTrustEvaluatorTests
{
    [Fact]
    public void EvaluateImport_InternalHashesAndEmbeddedIds_DoNotUpgradeDefaultTrust()
    {
        var integrity = new MauiArtifactIntegrityVerification
        {
            ArtifactDigest = "a" + new string('1', 63),
            Verified = true,
            InternalHashesPresent = true,
            InternalHashesVerified = true,
        };

        var result = MauiArtifactTrustEvaluator.EvaluateImport(
            policy: null,
            integrity,
            new MauiArtifactVerifiedProvenanceFacts
            {
                Verified = true,
                VerifiedBy = "embedded-claim",
                VerifiedAt = DateTimeOffset.UtcNow,
                Subject = new MauiArtifactProvenanceSubject
                {
                    Repository = "owner/repo",
                    Workflow = "build",
                    Commit = "deadbeef",
                    Issuer = "untrusted-content",
                    ArtifactDigest = integrity.ArtifactDigest,
                },
            });

        Assert.Equal(MauiArtifactTrustStates.Untrusted, result.State);
        Assert.Contains(result.Reasons, reason => reason.Code == "internal-hashes-integrity-only");
        Assert.Contains(result.Reasons, reason => reason.Code == "attestation-not-configured");
    }

    [Fact]
    public void EvaluateImport_VerifiedFactsMatchingConfiguredPolicy_Attests()
    {
        var integrity = new MauiArtifactIntegrityVerification
        {
            ArtifactDigest = new string('a', 64),
            Verified = true,
        };
        var expected = CreateSubject(integrity.ArtifactDigest);
        var policy = new MauiArtifactTrustPolicy
        {
            AllowAttestation = true,
            PolicyVersion = "trust-v1",
            ExpectedProvenance = expected,
        };
        var facts = new MauiArtifactVerifiedProvenanceFacts
        {
            Verified = true,
            VerifiedBy = "local-attestation-verifier",
            VerifiedAt = DateTimeOffset.UtcNow,
            Subject = CreateSubject(integrity.ArtifactDigest),
        };

        var result = MauiArtifactTrustEvaluator.EvaluateImport(policy, integrity, facts);

        Assert.Equal(MauiArtifactTrustStates.Attested, result.State);
        Assert.Contains(result.Reasons, reason => reason.Code == "attested");
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("workflow")]
    [InlineData("commit")]
    [InlineData("artifactDigest")]
    public void EvaluateImport_MismatchedRequiredProvenance_RemainsUntrusted(string mismatch)
    {
        var integrity = new MauiArtifactIntegrityVerification
        {
            ArtifactDigest = new string('b', 64),
            Verified = true,
        };
        var subject = CreateSubject(integrity.ArtifactDigest);
        switch (mismatch)
        {
            case "repository":
                subject.Repository = "other/repo";
                break;
            case "workflow":
                subject.Workflow = "release";
                break;
            case "commit":
                subject.Commit = "different";
                break;
            case "artifactDigest":
                subject.ArtifactDigest = new string('c', 64);
                break;
        }

        var result = MauiArtifactTrustEvaluator.EvaluateImport(
            new MauiArtifactTrustPolicy
            {
                AllowAttestation = true,
                ExpectedProvenance = CreateSubject(integrity.ArtifactDigest),
            },
            integrity,
            new MauiArtifactVerifiedProvenanceFacts
            {
                Verified = true,
                VerifiedBy = "verifier",
                VerifiedAt = DateTimeOffset.UtcNow,
                Subject = subject,
            });

        Assert.Equal(MauiArtifactTrustStates.Untrusted, result.State);
        Assert.Contains(result.Reasons, reason => reason.Code == mismatch + "-mismatch");
    }

    [Fact]
    public void EvaluateLocalReproduction_MatchingNewLocalRun_TransitionsToLocallyReproduced()
    {
        var imported = CreateImportedFailure();
        var local = CreateMatchingLocalRun(imported.ImportedAt!.Value);
        var expected = CreateExpectation();

        var evaluation = MauiArtifactTrustEvaluator.EvaluateLocalReproduction(imported, local, expected);

        Assert.Equal(MauiArtifactTrustStates.LocallyReproduced, evaluation.Verification.State);
        Assert.True(evaluation.Binding.Matched);
        Assert.Equal(imported.Identity!.Id, evaluation.Binding.ImportedArtifact!.Id);
        Assert.Equal(local.LocalRunId, evaluation.Binding.LocalRunId);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("step")]
    [InlineData("checkpoint")]
    public void EvaluateLocalReproduction_FailureMismatch_DoesNotUpgradeTrust(string mismatch)
    {
        var imported = CreateImportedFailure();
        var local = CreateMatchingLocalRun(imported.ImportedAt!.Value);
        switch (mismatch)
        {
            case "code":
                local.Failure!.Code = MauiFlowFailureClasses.AssertionFailed;
                break;
            case "step":
                local.Failure!.StepId = "different-step";
                break;
            case "checkpoint":
                local.Failure!.ObservedCheckpoint!.Route = "/different";
                break;
        }

        var evaluation = MauiArtifactTrustEvaluator.EvaluateLocalReproduction(
            imported,
            local,
            CreateExpectation());

        Assert.Equal(MauiArtifactTrustStates.Untrusted, evaluation.Verification.State);
        Assert.False(evaluation.Binding.Matched);
        Assert.Contains(
            evaluation.Verification.Reasons,
            reason => reason.Code is "failureCode-mismatch" or "failureStep-mismatch" or "observedCheckpoint-mismatch");
    }

    [Fact]
    public void CanCreateProposal_RequiresLocalReproduction_NotEmbeddedLocalLookingIds()
    {
        var identity = MauiImportedArtifactIdentity.Create();
        var artifact = new MauiArtifactTrustRecord
        {
            Identity = identity,
            Verification = new MauiArtifactTrustVerificationResult
            {
                State = MauiArtifactTrustStates.Untrusted,
            },
            Projection = new MauiImportedArtifactSafeProjection
            {
                EmbeddedIdentifierDigests =
                [
                    MauiArtifactTrustRedactor.Fingerprint("run_looks_local")!,
                    MauiArtifactTrustRedactor.Fingerprint("flow_looks_local")!,
                    MauiArtifactTrustRedactor.Fingerprint("proposal_looks_local")!,
                ],
            },
            LocalReproduction = new MauiLocalReproductionBinding
            {
                ImportedArtifact = identity,
                LocalRunId = "run_looks_local",
                Matched = true,
                Verification = new MauiArtifactTrustVerificationResult
                {
                    State = MauiArtifactTrustStates.LocallyReproduced,
                },
            },
        };

        Assert.False(MauiArtifactProposalPolicy.CanCreateProposal(artifact).Allowed);
        Assert.False(MauiFlowRepairProposalTrustPolicy.CanCreateProposal(artifact).Allowed);
        Assert.False(MauiSourceProposalTrustPolicy.CanCreateProposal(artifact).Allowed);

        artifact.Verification.State = MauiArtifactTrustStates.Attested;

        Assert.False(MauiArtifactProposalPolicy.CanCreateProposal(artifact).Allowed);

        artifact.Verification.State = MauiArtifactTrustStates.LocallyReproduced;

        Assert.True(MauiArtifactProposalPolicy.CanCreateProposal(artifact).Allowed);
    }

    private static MauiArtifactProvenanceSubject CreateSubject(string? artifactDigest)
        => new()
        {
            Repository = "dotnet/maui-labs",
            Workflow = ".github/workflows/ci-devflow.yml",
            Commit = "0123456789abcdef",
            Issuer = "https://issuer.example.test",
            ArtifactDigest = artifactDigest,
            AppDigest = "app-digest",
            PackageDigest = "package-digest",
            FlowDigest = "flow-digest",
            Platform = "android",
            DeviceProfile = "pixel",
            PolicyVersion = "trust-v1",
        };

    private static MauiArtifactTrustRecord CreateImportedFailure()
    {
        var expectedCheckpoint = CreateCheckpoint();
        var observedCheckpoint = CreateCheckpoint();
        var projection = new MauiImportedArtifactSafeProjection
        {
            Kind = "flow-run",
            FlowFingerprint = MauiArtifactTrustRedactor.Fingerprint("flow-current"),
            AppBuildFingerprint = MauiArtifactTrustRedactor.Fingerprint("build-current"),
            AppSourceFingerprint = MauiArtifactTrustRedactor.Fingerprint("source-current"),
            PackageFingerprint = MauiArtifactTrustRedactor.Fingerprint("package-current"),
            PlatformFingerprint = MauiArtifactTrustRedactor.Fingerprint("android"),
            DeviceProfileFingerprint = MauiArtifactTrustRedactor.Fingerprint("pixel"),
            Failure = new MauiImportedFailureProjection
            {
                FailureKey = "if_test",
                Code = MauiFlowFailureClasses.LocatorNotFound,
                Class = MauiFlowFailureClasses.LocatorNotFound,
                StepFingerprint = MauiArtifactTrustRedactor.Fingerprint("tap-save"),
                ExpectedCheckpointFingerprint = MauiArtifactTrustRedactor.CheckpointFingerprint(expectedCheckpoint),
                ObservedCheckpointFingerprint = MauiArtifactTrustRedactor.CheckpointFingerprint(observedCheckpoint),
            },
        };

        return new MauiArtifactTrustRecord
        {
            Identity = MauiImportedArtifactIdentity.Create(),
            ArtifactKind = "flow-run",
            ImportedAt = DateTimeOffset.UtcNow,
            Projection = projection,
            Verification = new MauiArtifactTrustVerificationResult
            {
                State = MauiArtifactTrustStates.Untrusted,
            },
        };
    }

    private static MauiLocalReproductionFacts CreateMatchingLocalRun(DateTimeOffset importedAt)
        => new()
        {
            LocalRunId = "run_local_reproduction",
            IsNewLocalRun = true,
            StartedAt = importedAt.AddSeconds(1),
            FlowDigest = "flow-current",
            AppBuildFingerprint = "build-current",
            AppSourceFingerprint = "source-current",
            PackageDigest = "package-current",
            Platform = "android",
            DeviceProfile = "pixel",
            Failure = new MauiLocalFailureFacts
            {
                Code = MauiFlowFailureClasses.LocatorNotFound,
                Class = MauiFlowFailureClasses.LocatorNotFound,
                StepId = "tap-save",
                ExpectedCheckpoint = CreateCheckpoint(),
                ObservedCheckpoint = CreateCheckpoint(),
            },
        };

    private static MauiLocalReproductionExpectation CreateExpectation()
        => new()
        {
            FlowDigest = "flow-current",
            AppBuildFingerprint = "build-current",
            AppSourceFingerprint = "source-current",
            PackageDigest = "package-current",
            Platform = "android",
            DeviceProfile = "pixel",
        };

    private static MauiFlowCheckpoint CreateCheckpoint()
        => new()
        {
            AppBuildFingerprint = "build-current",
            AgentInstanceId = "agent-instance",
            SeedFingerprint = "seed",
            BackendStateFingerprint = "backend",
            Route = "/todos",
            Window = "main",
            Modal = "none",
            Locale = "en-US",
            Theme = "light",
            Orientation = "portrait",
            DisplayProfile = "pixel",
            CollectionItemKey = "todo-1",
        };
}
