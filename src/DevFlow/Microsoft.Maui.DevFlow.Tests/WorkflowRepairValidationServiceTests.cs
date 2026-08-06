using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class WorkflowRepairValidationServiceTests
{
    [Fact]
    public async Task ValidateAsync_MatchingTransientOverride_RecordsRunWithoutPersistingFlow()
    {
        var fingerprint = Fingerprint();
        var checkpoint = Checkpoint();
        var proposal = Proposal(fingerprint);
        var host = new FakeHost(checkpoint, fingerprint)
        {
            Replay = new WorkflowRepairReplayValidation
            {
                ReachedFailedStep = true,
                Passed = true,
                RunId = "validation-run",
                CandidateMatchCount = 1,
                ObservedFingerprint = fingerprint,
                SemanticFingerprintMatches = true,
                HardAssertionsUnchanged = true,
                IndependentOracleSucceeded = true,
            },
        };

        var result = await new WorkflowRepairValidationService(host).ValidateAsync(
            new WorkflowRepairTransientValidationRequest
            {
                Proposal = proposal,
                InMemorySelectorOverrideOnly = true,
                ReplaySafety = new MauiFlowReplayEligibilityDecision
                {
                    DownstreamContinuationAllowed = false,
                },
            },
            CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal("validation-run", Assert.Single(result.RunIds));
        Assert.Equal(0, host.PersistCalls);
    }

    [Fact]
    public async Task ValidateAsync_UnsafeDownstreamContinuation_FailsClosed()
    {
        var fingerprint = Fingerprint();
        var checkpoint = Checkpoint();
        var host = new FakeHost(checkpoint, fingerprint)
        {
            Replay = new WorkflowRepairReplayValidation
            {
                ReachedFailedStep = true,
                Passed = true,
                CandidateMatchCount = 1,
                ObservedFingerprint = fingerprint,
                SemanticFingerprintMatches = true,
                HardAssertionsUnchanged = true,
                IndependentOracleSucceeded = true,
                ContinuedDownstream = true,
            },
        };

        var result = await new WorkflowRepairValidationService(host).ValidateAsync(
            new WorkflowRepairTransientValidationRequest
            {
                Proposal = Proposal(fingerprint),
                InMemorySelectorOverrideOnly = true,
                ReplaySafety = new MauiFlowReplayEligibilityDecision
                {
                    DownstreamContinuationAllowed = false,
                },
            },
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("downstream-continuation-prohibited", result.FailureFacts);
    }

    private static MauiFlowRepairProposal Proposal(MauiElementFingerprint fingerprint) => new()
    {
        ProposalId = "repair-1",
        ProposedSelector = new FlowSelector { AutomationId = "new-save" },
        Candidate = new MauiSelectorCandidate { Fingerprint = fingerprint },
        UnchangedAssertionsProof = new MauiRepairAssertionProof
        {
            Unchanged = true,
            ActionsUnchanged = true,
            ValuesUnchanged = true,
            OrderUnchanged = true,
        },
    };

    private static MauiFlowCheckpoint Checkpoint() => new()
    {
        AppBuildFingerprint = "build",
        AgentInstanceId = "agent",
        SeedFingerprint = "seed",
        BackendStateFingerprint = "backend",
        Route = "/checkout",
        Window = "main",
        Modal = "none",
        Locale = "en-US",
        Theme = "light",
        Orientation = "portrait",
        DisplayProfile = "320x640",
        CollectionItemKey = "order-1",
    };

    private static MauiElementFingerprint Fingerprint() => new()
    {
        Context = new MauiElementFingerprintContext
        {
            AppId = "app",
            AppBuild = "build",
            Platform = "android",
            Route = "/checkout",
            Window = "main",
            Modal = "none",
            Locale = "en-US",
            Theme = "light",
            Orientation = "portrait",
            DisplayProfile = "320x640",
        },
        Managed = new MauiManagedElementIdentity
        {
            Type = "Button",
            FullType = "Button",
            Role = "button",
        },
        Topology = new MauiTopologySignature { AncestorHash = "a", SiblingHash = "s" },
    };

    private sealed class FakeHost : IWorkflowRepairValidationHost
    {
        private readonly MauiFlowCheckpoint _checkpoint;
        private readonly MauiElementFingerprint _fingerprint;

        public FakeHost(MauiFlowCheckpoint checkpoint, MauiElementFingerprint fingerprint)
        {
            _checkpoint = checkpoint;
            _fingerprint = fingerprint;
        }

        public WorkflowRepairReplayValidation Replay { get; set; } = new();
        public int PersistCalls { get; private set; }

        public Task<WorkflowRepairLifecycleValidation> HardResetAsync(
            WorkflowRepairTransientValidationRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(new WorkflowRepairLifecycleValidation
            {
                Succeeded = true,
                ExpectedCheckpoint = _checkpoint,
                ObservedCheckpoint = _checkpoint,
                ObservedFingerprint = _fingerprint,
            });

        public Task<WorkflowRepairReplayValidation> ReplayWithInMemorySelectorOverrideAsync(
            WorkflowRepairTransientValidationRequest request,
            CancellationToken cancellationToken)
        {
            Assert.True(request.InMemorySelectorOverrideOnly);
            return Task.FromResult(Replay);
        }
    }
}
