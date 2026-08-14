using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Execution;
using Microsoft.Maui.Cli.DevFlow.Inspector;
using Microsoft.Maui.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;
using ElementInfo = Microsoft.Maui.DevFlow.Driver.ElementInfo;
using BoundsInfo = Microsoft.Maui.DevFlow.Driver.BoundsInfo;
using MutationLeaseStatus = Microsoft.Maui.DevFlow.Agent.Core.MutationLeaseStatus;

namespace Microsoft.Maui.DevFlow.Tests;

/// <summary>
/// Drives repair validation through the production stack rather than a stubbed replay delegate: a
/// real <see cref="BrokerServer"/>, the real <c>WorkflowRunCoordinator</c>, the real
/// <see cref="MauiFlowRunner"/>, the real driver, and a loopback agent that enforces mutation
/// leases with the agent's own <c>MutationLeaseCoordinator</c> and fences workflow-run commands
/// with the agent's own <c>WorkflowCommandLedger</c>.
/// </summary>
public sealed class BrokerRepairValidationIntegrationTests
{
    private const string StableStepId = "stable-save-step";
    private const string DriftedSelector = "old-save";
    private const string RepairedSelector = "new-save";
    private const string StableAssertSelector = "page-title";
    private const string Route = "//checkout";

    [Fact]
    public async Task RepairValidation_DrivesTheRealWorkflowRunPath_LeasesTheAgentAndNeverPersistsTheProposal()
    {
        using var agent = new LeaseEnforcingAgent();
        var brokerPort = FreePort();
        var owner = new RecordingLifecycleResetOwner();
        var attester = new WorkflowRepairLifecycleResetAttester(
            owner,
            // The lifecycle owner also answers the independent oracle, because an oracle that is
            // independent of the UI is by definition not observable through the agent.
            (_, _) => Task.FromResult<IReadOnlyList<MauiIndependentBusinessOracleResult>>(
            [
                new MauiIndependentBusinessOracleResult
                {
                    OracleId = "checkout-order-persisted",
                    Independent = true,
                    Succeeded = true,
                },
            ]));

        using var broker = new BrokerServer(
            brokerPort,
            TimeSpan.FromMinutes(1),
            log: null,
            checkpointStore: null,
            recordingStorageRoot: null,
            clock: null,
            previewFlags: PreviewTestFeatures.AllEnabled(),
            trustedHostApprovalVerifier: null,
            nativeApprovalToken: null,
            requireWorkflowRunAuthorization: false,
            repairResetAttesterResolver: _ => attester);
        using var cancellation = new CancellationTokenSource();
        var brokerTask = broker.RunAsync(cancellation.Token);
        await WaitForBrokerAsync(brokerPort);

        agent.BrokerPort = brokerPort;
        agent.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{brokerPort}/ws/agent"), CancellationToken.None);
        await SendAsync(socket, $$"""
            {"type":"register","project":"repair-integration","tfm":"net10.0","platform":"android","appName":"Repair Integration","currentPort":{{agent.Port}}}
            """);
        await ReceiveAsync(socket);

        try
        {
            using var http = new HttpClient();
            using var agents = JsonDocument.Parse(
                await http.GetStringAsync($"http://127.0.0.1:{brokerPort}/api/agents"));
            var agentId = agents.RootElement[0].GetProperty("id").GetString()!;
            agent.AgentId = agentId;
            var instanceId = agents.RootElement[0].GetProperty("instanceId").GetString()!;
            agent.AgentInstanceId = instanceId;

            // Force the Inspector (and with it the repair validation host) into existence.
            _ = await http.GetAsync(
                $"http://127.0.0.1:{brokerPort}/inspector/{Uri.EscapeDataString(agentId)}/");
            var inspector = await GetInspectorAsync(broker, agentId);
            var host = GetRepairValidationHost(inspector);
            Assert.NotNull(host);

            // The candidate fingerprint is derived from the live app exactly as the run path derives
            // it, so the semantic comparison is a real comparison rather than a hand-written match.
            var fingerprint = await ObserveFingerprintAsync(agent.Port, RepairedSelector, instanceId);
            var sourceFlow = DriftedFlow();
            var sourceDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(sourceFlow);
            var proposal = Proposal(sourceFlow, sourceDigest, fingerprint);
            var plan = Plan(sourceDigest);
            var classified = Classified(instanceId, owner);
            var request = new WorkflowRepairTransientValidationRequest
            {
                Proposal = proposal,
                InMemorySelectorOverrideOnly = true,
                Eligibility = new MauiFlowRepairEligibilityDecision
                {
                    Eligible = true,
                    CurrentCheckpoint = classified,
                },
                ClassifiedCheckpoint = classified,
                ReplaySafety = new MauiFlowReplayEligibilityDecision
                {
                    SideEffectPolicy = MauiFlowSideEffectPolicies.None,
                    RepairValidationAllowed = true,
                    RepairEligibility = true,
                    RunVerificationAllowed = true,
                    DownstreamContinuationAllowed = false,
                },
                SourceFlow = sourceFlow,
                SourcePlan = plan,
            };

            var record = await new WorkflowRepairValidationService(host!)
                .ValidateAsync(request, CancellationToken.None);

            Assert.True(
                record.Passed,
                $"code={record.FailureCode} facts={string.Join(",", record.FailureFacts)} " +
                $"taps={string.Join(",", agent.Taps)} rejections={string.Join(",", agent.LeaseRejections)} " +
                $"navigations={string.Join(",", agent.Navigations)}");

            // The lifecycle owner performed the reset; the broker did not invent one.
            Assert.Equal(1, owner.ResetCount);

            // The route restore must claim the agent lease explicitly. A caller that only sets the
            // lease identity header without claiming is rejected by the agent, so a successful
            // navigation proves the claim happened.
            Assert.Contains(Route, agent.Navigations);
            var restoreClaim = Assert.Single(
                agent.LeaseActions.Where(action =>
                    action.Action == "claim" && action.HolderKind == "repair-validation"));
            Assert.Contains(
                agent.LeaseActions,
                action => action.Action == "release" && action.LeaseId == restoreClaim.LeaseId);

            // ...and it must release. A stale repair lease would block every later claim for the
            // whole lease duration, which is precisely what an unreleased auto-acquired lease does.
            Assert.True(
                agent.TryClaimFreshLease("post-validation-probe"),
                "The repair route restore did not release the agent mutation lease.");

            // The replay really executed the patched selector against the live app, through the
            // agent's own workflow-command fence rather than around it.
            Assert.Equal(RepairedSelector, Assert.Single(agent.Taps));
            Assert.Contains("begin", agent.WorkflowRunControls);
            Assert.Contains("end", agent.WorkflowRunControls);
            Assert.Empty(agent.LeaseRejections);
            Assert.Single(record.RunIds);

            // The proposed selector is evidence, never a stored flow: the trusted source flow is
            // byte-identical afterwards and still carries the drifted selector.
            Assert.Equal(sourceDigest, MauiFlowRunReportSerializer.ComputeFlowDigest(sourceFlow));
            Assert.Equal(
                DriftedSelector,
                sourceFlow.Steps[0].Args?.Selector?.AutomationId);
            Assert.Equal(sourceDigest, plan.Flow!.Digest);
        }
        finally
        {
            cancellation.Cancel();
            broker.Dispose();
            await brokerTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task RepairValidation_WithoutARegisteredLifecycleOwner_StaysUnavailable()
    {
        using var agent = new LeaseEnforcingAgent();
        var brokerPort = FreePort();
        using var broker = new BrokerServer(
            brokerPort,
            TimeSpan.FromMinutes(1),
            previewFlags: PreviewTestFeatures.AllEnabled());
        using var cancellation = new CancellationTokenSource();
        var brokerTask = broker.RunAsync(cancellation.Token);
        await WaitForBrokerAsync(brokerPort);

        agent.BrokerPort = brokerPort;
        agent.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{brokerPort}/ws/agent"), CancellationToken.None);
        await SendAsync(socket, $$"""
            {"type":"register","project":"repair-unavailable","tfm":"net10.0","platform":"android","appName":"Repair Integration","currentPort":{{agent.Port}}}
            """);
        await ReceiveAsync(socket);

        try
        {
            using var http = new HttpClient();
            using var agents = JsonDocument.Parse(
                await http.GetStringAsync($"http://127.0.0.1:{brokerPort}/api/agents"));
            var agentId = agents.RootElement[0].GetProperty("id").GetString()!;
            agent.AgentId = agentId;

            _ = await http.GetAsync(
                $"http://127.0.0.1:{brokerPort}/inspector/{Uri.EscapeDataString(agentId)}/");
            var inspector = await GetInspectorAsync(broker, agentId);

            // No lifecycle owner registered, so the broker builds no host and the workbench keeps
            // reporting the capability as unavailable instead of promising an unattestable reset.
            Assert.Null(GetRepairValidationHost(inspector));
            Assert.Empty(agent.Navigations);
        }
        finally
        {
            cancellation.Cancel();
            broker.Dispose();
            await brokerTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Reproduces the run path's own fingerprint derivation against the live agent, so the proposal
    /// carries a fingerprint the replay can actually match.
    /// </summary>
    private static async Task<MauiElementFingerprint> ObserveFingerprintAsync(
        int agentPort,
        string automationId,
        string agentInstanceId)
    {
        using var client = new AgentClient("localhost", agentPort) { AutoAcquireMutationLease = false };
        var status = await client.GetStatusAsync();
        var matches = await client.QueryAsync(automationId: automationId);
        var target = Assert.Single(matches);
        var tree = await client.GetTreeAsync();
        var observation = MauiSelectorObservationFactory.Create(
            target,
            tree,
            new MauiSelectorObservationContext
            {
                AppId = status?.App?.PackageId,
                AppBuild = status?.App?.Build,
                Platform = status?.Device?.Platform,
                Route = status?.Route,
                Window = status?.Window,
                Modal = status?.Modal,
                Locale = status?.Locale,
                Theme = status?.Theme,
                Orientation = status?.Orientation,
                DisplayProfile = status?.DisplayProfile,
                CapabilityVersion = "flow-run-selector-evidence-v1",
            },
            truncated: tree.Count == 0);
        Assert.False(string.IsNullOrWhiteSpace(agentInstanceId));
        var generated = MauiSelectorCandidateGenerator.Generate(observation);
        var fingerprint = Assert.IsType<MauiElementFingerprint>(generated.Fingerprint);

        // A proposal's candidate fingerprint always comes out of a run report, and the runner
        // applies report limits — including redaction — before anyone sees it. Deriving an
        // unredacted fingerprint here would compare evidence the product can never produce, so the
        // same production redaction is applied to the derived fingerprint.
        var redacted = new MauiFlowRunReport
        {
            RunId = "fingerprint-derivation",
            FlowId = "fingerprint-derivation",
            FlowDigest = new string('0', 64),
            Steps = [new MauiFlowStepAttempt { StepId = "probe", Sequence = 1, Fingerprint = fingerprint }],
        };
        MauiFlowRunReportSerializer.ApplyLimits(redacted, new MauiFlowRunReportLimits());
        return Assert.IsType<MauiElementFingerprint>(redacted.Steps[0].Fingerprint);
    }

    private static MauiFlow DriftedFlow() => new()
    {
        Name = "repair-integration",
        Steps =
        [
            new FlowStep
            {
                Seq = 1,
                StepId = StableStepId,
                Action = FlowActions.Tap,
                Args = new FlowStepArgs { Selector = new FlowSelector { AutomationId = DriftedSelector } },
                Asserts =
                [
                    // The assertion selector is untouched by a selector-only repair, so it must
                    // point at an element the drift did not move.
                    new FlowAssert
                    {
                        Kind = "exists",
                        Verify = true,
                        Selector = new FlowSelector { AutomationId = StableAssertSelector },
                    },
                ],
            },
        ],
    };

    private static MauiTestPlan Plan(string sourceFlowDigest) => new()
    {
        PlanId = "repair-integration-plan",
        Revision = 3,
        Goal = "Prove the checkout save button still persists an order after selector drift.",
        SideEffectPolicy = MauiFlowSideEffectPolicies.None,
        Reset = new MauiTestResetRequirement
        {
            Required = true,
            Strategy = "android-clear-app-data",
        },
        Provenance = new MauiActorProvenance
        {
            ActorKind = "human",
            Channel = "cli",
        },
        Flow = new MauiFlowReference
        {
            Path = "repair-integration.md",
            FlowId = "flow-repair-integration",
            Digest = sourceFlowDigest,
            Revision = 3,
        },
        IndependentBusinessOracles =
        [
            new MauiIndependentBusinessOracleDeclaration
            {
                OracleId = "checkout-order-persisted",
                Description = "The order row exists in the test tenant after the tap.",
                Required = true,
                Independent = true,
                EvidenceKind = "backend-query",
            },
        ],
    };

    private static MauiFlowCheckpoint Classified(string agentInstanceId, RecordingLifecycleResetOwner owner)
    {
        var applied = owner.Applied;
        return new MauiFlowCheckpoint
        {
            AppBuildFingerprint = LeaseEnforcingAgent.AppBuild,
            AgentInstanceId = agentInstanceId,
            SeedFingerprint = applied.SeedFingerprint,
            BackendStateFingerprint = applied.BackendStateFingerprint,
            Route = Route,
            Window = LeaseEnforcingAgent.Window,
            Modal = LeaseEnforcingAgent.Modal,
            Locale = LeaseEnforcingAgent.Locale,
            Theme = LeaseEnforcingAgent.Theme,
            Orientation = LeaseEnforcingAgent.Orientation,
            DisplayProfile = LeaseEnforcingAgent.DisplayProfile,
            CollectionItemKey = applied.CollectionItemKey,
        };
    }

    private static MauiFlowRepairProposal Proposal(
        MauiFlow flow,
        string flowDigest,
        MauiElementFingerprint fingerprint)
    {
        var generated = MauiFlowRepairProposalGenerator.Generate(new MauiFlowRepairProposalGenerationInput
        {
            Eligibility = new MauiFlowRepairEligibilityDecision
            {
                Eligible = true,
                FailureCode = MauiFlowFailureClasses.LocatorNotFound,
            },
            Flow = flow,
            BaseFlow = new MauiFlowReference
            {
                Path = "repair-integration.md",
                FlowId = "flow-repair-integration",
                Digest = flowDigest,
                Revision = 3,
            },
            SourceRunId = "run-drifted",
            SourceStepId = StableStepId,
            SourceFailureId = "failure-1",
            SourceFailureCode = MauiFlowFailureClasses.LocatorNotFound,
            PriorFingerprint = fingerprint,
            PriorActiveSelectorResolution = new MauiRepairPriorSelectorResolution
            {
                RunId = "prior-run",
                TrustedRun = true,
                WasUniquelyResolved = true,
                ActiveSelector = new FlowSelector { AutomationId = DriftedSelector },
                Fingerprint = fingerprint,
            },
            SelectorHealthCandidates =
            [
                new MauiSelectorCandidate
                {
                    CandidateId = "candidate-new-save",
                    Rank = 1,
                    Priority = 1,
                    Selector = new FlowSelector { AutomationId = RepairedSelector },
                    SelectorDescriptor = new MauiSelectorCandidateSelector
                    {
                        Kind = "automation-id",
                        AutomationId = RepairedSelector,
                    },
                    Score = .9,
                    Scores = new MauiSelectorCandidateScores { DeterministicRankScore = .9 },
                    Unique = true,
                    Validation = new MauiSelectorCandidateValidation
                    {
                        Unique = true,
                        MatchCount = 1,
                        Accepted = true,
                        PlatformState = "validated",
                    },
                    Fingerprint = fingerprint,
                },
            ],
            CurrentResolutions =
            [
                new MauiRepairCandidateResolution
                {
                    CandidateId = "candidate-new-save",
                    MatchCount = 1,
                    SemanticFingerprintMatches = true,
                    CurrentFingerprint = fingerprint,
                },
            ],
            Trust = "current-local-run",
        });
        Assert.True(
            generated.Proposals.Count == 1,
            "abstentions=" + string.Join(
                ";",
                generated.Abstentions.Select(a => $"{a.Code}:{a.Message}")));
        return generated.Proposals[0];
    }

    private static IWorkflowRepairValidationHost? GetRepairValidationHost(InspectorServer inspector)
        => (IWorkflowRepairValidationHost?)typeof(InspectorServer)
            .GetField("_repairValidationHost", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(inspector);

    private static async Task<InspectorServer> GetInspectorAsync(BrokerServer broker, string agentId)
    {
        var field = typeof(BrokerServer).GetField("_inspectors", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var inspectors = (IReadOnlyDictionary<string, InspectorServer>)field.GetValue(broker)!;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (inspectors.TryGetValue(agentId, out var inspector))
                return inspector;
            await Task.Delay(25);
        }

        throw new InvalidOperationException("Broker did not create the Inspector.");
    }

    private static async Task WaitForBrokerAsync(int port)
    {
        using var http = new HttpClient();
        for (var attempt = 0; attempt < 80; attempt++)
        {
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/api/health");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(25);
        }

        throw new InvalidOperationException("Broker did not start.");
    }

    private static async Task SendAsync(ClientWebSocket socket, string text)
        => await socket.SendAsync(
            Encoding.UTF8.GetBytes(text),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

    private static async Task<string> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// A lifecycle owner that records what it applied. It stands in for the platform owner without
    /// weakening the contract: the fingerprints still come from the shared deterministic derivation
    /// and never from anything the app reported.
    /// </summary>
    private sealed class RecordingLifecycleResetOwner : IFlowLifecycleResetOwner
    {
        public int ResetCount { get; private set; }

        public string OwnerId => "test-lifecycle-owner";

        public FlowLifecycleAppliedState Applied { get; } = BuildState();

        public Task<FlowLifecycleAppliedState?> GetAppliedStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<FlowLifecycleAppliedState?>(Applied);

        public Task<FlowLifecycleResetOutcome> ResetAsync(
            FlowLifecycleResetRequest request,
            CancellationToken cancellationToken = default)
        {
            ResetCount++;
            return Task.FromResult(new FlowLifecycleResetOutcome
            {
                Succeeded = true,
                Applied = Applied,
                EvidenceIds = ["test-lifecycle-reset"],
            });
        }

        private static FlowLifecycleAppliedState BuildState()
        {
            var resetIdentity = FlowLifecycleResetFingerprints.ResetIdentity(
                "test-lifecycle-owner",
                "test-reset",
                "com.example.repair",
                "emulator-5554");
            return new FlowLifecycleAppliedState
            {
                Strategy = "test-reset",
                ResetIdentity = resetIdentity,
                SeedIdentity = null,
                SeedFingerprint = FlowLifecycleResetFingerprints.SeedFingerprint(
                    resetIdentity,
                    LeaseEnforcingAgent.AppBuild,
                    seedIdentity: null),
                BackendStateFingerprint = FlowLifecycleResetFingerprints.NoBackendApplied,
                CollectionItemKey = FlowLifecycleResetFingerprints.NoCollectionItem,
                AppStateSucceeded = true,
                BackendTestDataSucceeded = false,
            };
        }
    }

    /// <summary>
    /// A loopback app that serves the endpoints the run path actually calls and enforces mutation
    /// leases with the agent's own coordinator, so an unclaimed or unreleased lease produces the
    /// same HTTP 409 a real agent produces.
    /// </summary>
    private sealed class LeaseEnforcingAgent : IDisposable
    {
        internal const string AppBuild = "build-repair-integration";
        internal const string Window = "main";
        internal const string Modal = "none";
        internal const string Locale = "en-US";
        internal const string Theme = "light";
        internal const string Orientation = "portrait";
        internal const string DisplayProfile = "phone";

        private readonly AgentHttpServer _server;
        private readonly MutationLeaseCoordinator _leases = new(() => null, leaseDurationMs: 120_000);
        private readonly WorkflowCommandLedger _workflowCommands;
        private readonly List<ElementInfo> _elements =
        [
            new ElementInfo
            {
                Id = "root",
                Type = "ContentPage",
                FullType = "Microsoft.Maui.Controls.ContentPage",
                AutomationId = "checkout-page",
                Text = string.Empty,
                IsVisible = true,
                IsEnabled = true,
                Bounds = new BoundsInfo { X = 0, Y = 0, Width = 400, Height = 800 },
            },
            new ElementInfo
            {
                Id = "title",
                ParentId = "root",
                Type = "Label",
                FullType = "Microsoft.Maui.Controls.Label",
                AutomationId = StableAssertSelector,
                Text = "Checkout",
                IsVisible = true,
                IsEnabled = true,
                Bounds = new BoundsInfo { X = 0, Y = 0, Width = 400, Height = 40 },
            },
            // The drifted AutomationId is genuinely absent; only the repaired one resolves.
            new ElementInfo
            {
                Id = "save",
                ParentId = "root",
                Type = "Button",
                FullType = "Microsoft.Maui.Controls.Button",
                AutomationId = RepairedSelector,
                Text = "Save",
                IsVisible = true,
                IsEnabled = true,
                Bounds = new BoundsInfo { X = 0, Y = 100, Width = 200, Height = 48 },
            },
        ];

        private readonly Lock _gate = new();
        private string _route = Route;

        public LeaseEnforcingAgent()
        {
            Port = FreePort();
            _server = new AgentHttpServer(Port);
            _server.MutationLeaseValidator = ValidateAsync;
            // A real agent fences workflow-run commands with its own ledger. Without it every
            // fenced command is refused, so the run path is only genuinely exercised with it.
            _workflowCommands = new WorkflowCommandLedger(
                () => AgentInstanceId,
                () => BrokerPort != 0 && AgentId.Length != 0,
                new WorkflowCommandLedgerOptions());
            _server.WorkflowCommandLedger = _workflowCommands;
            MapRoutes();
        }

        public int Port { get; }
        public int BrokerPort { get; set; }
        public string AgentId { get; set; } = string.Empty;
        public string AgentInstanceId { get; set; } = string.Empty;
        public List<string> Taps { get; } = [];
        public List<string> Navigations { get; } = [];
        public List<string> LeaseRejections { get; } = [];
        public List<string> WorkflowRunControls { get; } = [];
        public List<LeaseAction> LeaseActions { get; } = [];

        public void Start() => _server.Start();

        /// <summary>Proves a later holder can take the lease, which only a released lease allows.</summary>
        public bool TryClaimFreshLease(string label)
        {
            var leaseId = $"probe-{Guid.NewGuid():N}";
            var status = _leases.ControlAsync(new MutationLeaseRequest
            {
                Action = "claim",
                LeaseId = leaseId,
                HolderKind = "probe",
                Label = label,
            }).GetAwaiter().GetResult();
            return status.YouHold;
        }

        private void MapRoutes()
        {
            _server.MapGet("/api/v1/agent/status", _ => Task.FromResult(HttpResponse.Json(Status())));
            _server.MapGet("/api/v1/agent/capabilities", _ => Task.FromResult(HttpResponse.Json(new
            {
                ui = true,
                logs = true,
                mutations = true,
            })));
            _server.MapPost("/api/v1/agent/lease", HandleLease, requiresMutationLease: false);
            _server.MapPost("/api/v1/agent/workflow-runs", HandleWorkflowRunControl, requiresMutationLease: false);
            _server.MapGet("/api/v1/ui/tree", _ => Task.FromResult(HttpResponse.Json(Snapshot())));
            _server.MapGet("/api/v1/ui/elements", HandleQuery);
            _server.MapGet("/api/v1/ui/elements/{id}", HandleElement);
            _server.MapPost("/api/v1/ui/actions/tap", HandleTap);
            _server.MapPost("/api/v1/ui/actions/navigate", HandleNavigate);
        }

        private Task<MutationLeaseStatus> ValidateAsync(HttpRequest request)
        {
            request.Headers.TryGetValue("X-DevFlow-Lease", out var leaseId);
            if (request.Headers.ContainsKey("X-DevFlow-Workflow-Run"))
                return ValidateWorkflowAuthorityAsync(request.Path, leaseId);
            return RecordAsync(request.Path, leaseId, _leases.ValidateAsync(leaseId));
        }

        /// <summary>
        /// Mirrors a real agent: a workflow-run command is validated by the broker that issued it,
        /// not by the agent's local lease state.
        /// </summary>
        private async Task<MutationLeaseStatus> ValidateWorkflowAuthorityAsync(string path, string? leaseId)
        {
            if (string.IsNullOrWhiteSpace(leaseId) || BrokerPort == 0 || AgentId.Length == 0)
            {
                lock (_gate)
                    LeaseRejections.Add($"{path}:no-broker-lease");
                return MutationLeaseStatus.Failure("The workflow ledger requires a broker mutation lease.");
            }

            MutationLeaseStatus? status;
            try
            {
                using var http = new HttpClient();
                using var response = await http.PostAsync(
                    $"http://127.0.0.1:{BrokerPort}/api/leases/{Uri.EscapeDataString(AgentId)}",
                    new StringContent(
                        JsonSerializer.Serialize(new { action = "validate", leaseId }),
                        Encoding.UTF8,
                        "application/json"));
                var body = await response.Content.ReadAsStringAsync();
                status = JsonSerializer.Deserialize<MutationLeaseStatus>(
                    body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                lock (_gate)
                    LeaseRejections.Add($"{path}:broker-validate-threw:{ex.GetType().Name}");
                return MutationLeaseStatus.Failure("The broker lease check failed.");
            }

            if (status is null || !status.Allowed)
            {
                lock (_gate)
                    LeaseRejections.Add($"{path}:workflow:{leaseId}");
            }

            return status ?? MutationLeaseStatus.Failure("The broker did not validate the lease.");
        }

        private async Task<MutationLeaseStatus> RecordAsync(
            string path,
            string? leaseId,
            Task<MutationLeaseStatus> pending)
        {
            var status = await pending.ConfigureAwait(false);
            if (!status.Allowed)
            {
                lock (_gate)
                    LeaseRejections.Add($"{path}:{leaseId}");
            }

            return status;
        }

        private Task<HttpResponse> HandleWorkflowRunControl(HttpRequest request)
        {
            var body = request.BodyAs<WorkflowRunControlRequest>() ?? new WorkflowRunControlRequest();
            body.Action = body.Action?.Trim().ToLowerInvariant();
            lock (_gate)
                WorkflowRunControls.Add(body.Action ?? string.Empty);
            var result = _workflowCommands.Control(body);
            var response = HttpResponse.Json(result);
            response.StatusCode = result.StatusCode;
            response.StatusText = result.StatusCode == 200 ? "OK" : "Conflict";
            return Task.FromResult(response);
        }

        private async Task<HttpResponse> HandleLease(HttpRequest request)
        {
            var body = request.BodyAs<MutationLeaseRequest>() ?? new MutationLeaseRequest();
            lock (_gate)
                LeaseActions.Add(new LeaseAction(body.Action, body.LeaseId, body.HolderKind));
            return HttpResponse.Json(await _leases.ControlAsync(body).ConfigureAwait(false));
        }

        private Task<HttpResponse> HandleQuery(HttpRequest request)
        {
            IEnumerable<ElementInfo> matches = Snapshot();
            if (request.QueryParams.TryGetValue("automationId", out var automationId))
                matches = matches.Where(element => element.AutomationId == automationId);
            else if (request.QueryParams.TryGetValue("text", out var text))
                matches = matches.Where(element => element.Text == text);
            else if (request.QueryParams.TryGetValue("type", out var type))
                matches = matches.Where(element => element.Type == type);
            return Task.FromResult(HttpResponse.Json(matches.ToList()));
        }

        private Task<HttpResponse> HandleElement(HttpRequest request)
        {
            var id = request.RouteParams.TryGetValue("id", out var value) ? value : string.Empty;
            var element = Snapshot().FirstOrDefault(candidate => candidate.Id == id);
            return Task.FromResult(element is null
                ? HttpResponse.NotFound("No such element")
                : HttpResponse.Json(element));
        }

        private Task<HttpResponse> HandleTap(HttpRequest request)
        {
            var id = ReadString(request.Body, "elementId");
            var element = Snapshot().FirstOrDefault(candidate => candidate.Id == id);
            lock (_gate)
                Taps.Add(element?.AutomationId ?? id ?? string.Empty);
            return Task.FromResult(HttpResponse.Json(new { success = true }));
        }

        private Task<HttpResponse> HandleNavigate(HttpRequest request)
        {
            var route = ReadString(request.Body, "route");
            lock (_gate)
            {
                Navigations.Add(route ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(route))
                    _route = route;
            }

            return Task.FromResult(HttpResponse.Json(new { success = true }));
        }

        private object Status()
        {
            string route;
            lock (_gate)
                route = _route;
            return new
            {
                running = true,
                agent = new { name = "DevFlow", version = "1" },
                app = new
                {
                    name = "Repair Integration",
                    build = AppBuild,
                    packageId = "com.example.repair",
                    version = "1.0",
                },
                device = new { platform = "android", deviceType = "emulator", idiom = "phone" },
                capabilities = new { ui = true, logs = true, mutations = true },
                route,
                window = Window,
                modal = Modal,
                locale = Locale,
                theme = Theme,
                orientation = Orientation,
                displayProfile = DisplayProfile,
            };
        }

        private List<ElementInfo> Snapshot()
        {
            lock (_gate)
                return [.. _elements];
        }

        private static string? ReadString(string? body, string property)
        {
            if (string.IsNullOrWhiteSpace(body))
                return null;
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty(property, out var value) &&
                value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        public void Dispose() => _server.Dispose();

        internal sealed record LeaseAction(string Action, string? LeaseId, string? HolderKind);
    }
}
