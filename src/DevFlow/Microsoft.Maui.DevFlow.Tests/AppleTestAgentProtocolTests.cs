using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.TestAgent.Host;
using Microsoft.Maui.DevFlow.TestAgent.Protocol;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class AppleTestAgentProtocolTests
{
    [Fact]
    public void Protocol_IsOperationLevelAndContainsNoSemanticEngine()
    {
        Assert.Contains(AppleTestAgentOperations.Query, AppleTestAgentOperations.All);
        Assert.Contains(AppleTestAgentOperations.Tree, AppleTestAgentOperations.All);
        Assert.Contains(AppleTestAgentOperations.Tap, AppleTestAgentOperations.All);
        Assert.Contains(AppleTestAgentOperations.Screenshot, AppleTestAgentOperations.All);
        Assert.DoesNotContain("run", AppleTestAgentOperations.All);

        var root = FindRepositoryRoot();
        var coreSources = Directory.GetFiles(
            Path.Combine(root, "src", "DevFlow", "Microsoft.Maui.DevFlow.TestAgent.Core"),
            "*.cs",
            SearchOption.AllDirectories);
        var nativeSources = Directory.GetFiles(
            Path.Combine(root, "src", "DevFlow", "Microsoft.Maui.DevFlow.TestAgent", "AppleXCTestAgent"),
            "*.swift",
            SearchOption.AllDirectories);
        var nativeProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DevFlow",
            "Microsoft.Maui.DevFlow.TestAgent",
            "AppleXCTestAgent",
            "DevFlowAppleTestAgent.xcodeproj",
            "project.pbxproj"));
        var agentSource = string.Join(
            "\n",
            coreSources.Concat(nativeSources).Select(File.ReadAllText));

        Assert.Contains("com.apple.product-type.bundle.ui-testing", nativeProject, StringComparison.Ordinal);
        Assert.Contains("XCUIApplication", agentSource, StringComparison.Ordinal);
        Assert.Contains("platform == \"macos\"", agentSource, StringComparison.Ordinal);
        Assert.DoesNotContain("MauiFlowRunner", agentSource, StringComparison.Ordinal);
        Assert.DoesNotContain("MauiTestPlan", agentSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FlowSelector", agentSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RepairProposal", agentSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceProposal", agentSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Authentication_RejectsReplayAndExpiredProof()
    {
        var secret = Enumerable.Repeat((byte)0x42, 32).ToArray();
        var now = DateTimeOffset.Parse("2026-08-02T00:00:00Z");
        const string bodyDigest = "sha256:body";
        var authentication = new AppleTestAgentAuthentication
        {
            SessionId = "apple-session",
            TimestampUnixSeconds = now.ToUnixTimeSeconds(),
            Nonce = "nonce-one",
            Signature = AppleTestAgentAuthenticator.CreateSignature(
                secret,
                "POST",
                "/v1/session/apple-session/hello",
                "apple-session",
                null,
                0,
                now.ToUnixTimeSeconds(),
                "nonce-one",
                bodyDigest),
        };

        var replay = new AppleTestAgentReplayProtector();
        Assert.True(AppleTestAgentAuthenticator.Verify(
            secret,
            authentication,
            "POST",
            "/v1/session/apple-session/hello",
            null,
            0,
            bodyDigest,
            now,
            TimeSpan.FromMinutes(2),
            replay));
        Assert.False(AppleTestAgentAuthenticator.Verify(
            secret,
            authentication,
            "POST",
            "/v1/session/apple-session/hello",
            null,
            0,
            bodyDigest,
            now,
            TimeSpan.FromMinutes(2),
            replay));

        var freshReplay = new AppleTestAgentReplayProtector();
        Assert.False(AppleTestAgentAuthenticator.Verify(
            secret,
            authentication,
            "POST",
            "/v1/session/apple-session/hello",
            null,
            0,
            bodyDigest,
            now.AddMinutes(3),
            TimeSpan.FromMinutes(2),
            freshReplay));
    }

    [Fact]
    public void CommandLedger_PreservesReceiptRejectsReplayAndCancelsWithoutRetry()
    {
        var session = Session();
        var ledger = new AppleTestAgentCommandLedger(session);
        var command = Command(session, sequence: 1, commandId: "command-one");

        var prepared = ledger.Prepare(command);
        Assert.True(prepared.Accepted);
        Assert.True(prepared.ShouldDispatch);
        Assert.Equal("prepared", prepared.Receipt!.AcknowledgementState);

        Assert.True(ledger.MarkDispatched(command.CommandId).Accepted);
        var completed = ledger.Complete(new AppleTestAgentOperationCompletion
        {
            Receipt = prepared.Receipt!,
            Ok = true,
            CompletionCertainty = "certain",
        });
        Assert.True(completed.Accepted);
        Assert.Equal("completed", completed.Receipt!.AcknowledgementState);

        var duplicate = ledger.Prepare(command);
        Assert.True(duplicate.Accepted);
        Assert.True(duplicate.IsDuplicate);
        Assert.False(duplicate.ShouldDispatch);

        var gap = ledger.Prepare(Command(session, sequence: 3, commandId: "command-three"));
        Assert.False(gap.Accepted);
        Assert.Equal(AppleTestAgentErrorCodes.SequenceRejected, gap.Error!.Code);

        var second = Command(session, sequence: 2, commandId: "command-two");
        Assert.True(ledger.Prepare(second).Accepted);
        var cancelled = ledger.Cancel(second.CommandId);
        Assert.True(cancelled.Accepted);
        Assert.Equal("cancelled", cancelled.Receipt!.AcknowledgementState);
        var afterCancellation = ledger.Prepare(second);
        Assert.True(afterCancellation.Accepted);
        Assert.True(afterCancellation.IsDuplicate);
        Assert.False(afterCancellation.ShouldDispatch);
    }

    [Fact]
    public void ArtifactChunks_RequireDigestsOrderingAndBoundedFinalization()
    {
        var bytes = Encoding.UTF8.GetBytes("bounded apple artifact");
        var first = bytes[..8];
        var second = bytes[8..];
        var assembler = new AppleTestAgentArtifactChunkAssembler();

        Assert.Null(assembler.Add(Chunk("artifact", 0, 2, first, isFinal: false)));
        Assert.Null(assembler.Add(Chunk("artifact", 1, 2, second, isFinal: true)));
        Assert.True(assembler.TryGetCompleted("artifact", out var artifact));
        Assert.Equal(bytes, artifact!.Content);
        Assert.Equal(AppleTestAgentAuthenticator.ComputeDigest(bytes), artifact.Reference.Sha256);

        var duplicate = assembler.Add(Chunk("artifact", 1, 2, second, isFinal: true));
        Assert.Equal(AppleTestAgentErrorCodes.ArtifactRejected, duplicate!.Code);
    }

    [Fact]
    public async Task HostDriver_MapsOperationResponsesAndExposesWorkflowReceipt()
    {
        var transport = new FakeTransport();
        var driver = new AppleTestAgentMauiFlowDriver(transport);

        var matches = await driver.QueryAsync(automationId: "SafeButton");
        Assert.Single(matches);
        Assert.Equal("SafeButton", matches[0].AutomationId);

        Assert.True(await driver.TapAsync(matches[0].Id));
        Assert.NotNull(driver.LastWorkflowCommandReceipt);
        Assert.Equal(2, driver.LastWorkflowCommandReceipt!.Sequence);
        Assert.Equal("command-2", driver.LastWorkflowCommandReceipt.CommandId);
    }

    [Fact]
    public async Task HostDriver_StatusProjectsValueFreeSeedCheckpoint()
    {
        var driver = new AppleTestAgentMauiFlowDriver(new FakeTransport());

        var status = await driver.GetStatusAsync();

        Assert.True(status?.Running);
        Assert.Equal("//native", driver.LastCheckpointFacts?.Route);
        Assert.Equal("sha256:seed", driver.LastCheckpointFacts?.SeedFingerprint);
        Assert.Equal("sha256:backend", driver.LastCheckpointFacts?.BackendStateFingerprint);
        Assert.Equal("target-process", driver.LastCheckpointFacts?.ProcessInstanceId);
    }

    [Fact]
    public async Task HostTransport_CancellationReturnsCertainReceiptAndUnauthenticatedRequestsFail()
    {
        var session = Session();
        var secret = Enumerable.Repeat((byte)0x73, 32).ToArray();
        await using var host = new AppleTestAgentHttpHost(session, secret);
        await host.StartAsync();

        using var client = new HttpClient();
        using var response = await client.PostAsync(
            new Uri(host.Endpoint, $"v1/session/{session.SessionId}/hello"),
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var pending = host.SendAsync(
            AppleTestAgentOperations.Wait,
            new Dictionary<string, string> { ["durationMs"] = "10000" });
        var commandId = host.LastQueuedCommandId;
        Assert.NotNull(commandId);
        var receipt = await host.CancelAsync(commandId!, "test");
        var completion = await pending;

        Assert.Equal("cancelled", receipt!.AcknowledgementState);
        Assert.Equal(AppleTestAgentErrorCodes.Cancelled, completion.Error!.Code);
        Assert.Equal("certain", completion.CompletionCertainty);
    }

    [Fact]
    public async Task HostTransport_OrphanedSessionReturnsUnknownCompletionWithoutRetry()
    {
        var session = Session();
        var secret = Enumerable.Repeat((byte)0x61, 32).ToArray();
        var host = new AppleTestAgentHttpHost(session, secret);
        await host.StartAsync();
        var pending = host.SendAsync(AppleTestAgentOperations.Wait);

        await host.DisposeAsync();

        var completion = await pending;
        Assert.False(completion.Ok);
        Assert.Equal("unknown", completion.CompletionCertainty);
        Assert.Equal(AppleTestAgentErrorCodes.AgentOrphaned, completion.Error?.Code);
        Assert.Equal("unknown-completion", completion.Receipt.AcknowledgementState);
    }

    [Fact]
    public async Task HostTransport_AuthenticatedRoundtripReturnsOperationReceipt()
    {
        var session = Session();
        var secret = Enumerable.Repeat((byte)0x21, 32).ToArray();
        await using var host = new AppleTestAgentHttpHost(session, secret);
        await host.StartAsync();
        using var client = new HttpClient();

        var hello = new AppleTestAgentHello
        {
            SessionId = session.SessionId,
            Target = session.Target,
            AgentInstanceId = "native-xctest",
            AttachedAt = DateTimeOffset.UtcNow,
            Capabilities = new AppleTestAgentCapabilities
            {
                AuthenticatedTransport = true,
                TargetForegroundOwned = true,
                Operations = [AppleTestAgentOperations.Query],
                MaxArtifactChunkBytes = AppleTestAgentProtocolVersions.MaximumArtifactChunkBytes,
            },
        };
        using var helloRequest = AuthenticatedRequest(
            host,
            secret,
            HttpMethod.Post,
            $"/v1/session/{session.SessionId}/hello",
            JsonSerializer.SerializeToUtf8Bytes(hello));
        using var helloResponse = await client.SendAsync(helloRequest);
        Assert.Equal(HttpStatusCode.OK, helloResponse.StatusCode);

        var hostCall = host.SendAsync(AppleTestAgentOperations.Query);
        using var nextRequest = AuthenticatedRequest(
            host,
            secret,
            HttpMethod.Get,
            $"/v1/session/{session.SessionId}/next",
            Array.Empty<byte>());
        using var nextResponse = await client.SendAsync(nextRequest);
        Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
        var command = JsonSerializer.Deserialize<AppleTestAgentOperationCommand>(
            await nextResponse.Content.ReadAsByteArrayAsync());
        Assert.NotNull(command);

        var completion = new AppleTestAgentOperationCompletion
        {
            Receipt = new AppleTestAgentCommandReceipt
            {
                SessionId = command!.SessionId,
                CommandId = command.CommandId,
                Sequence = command.Sequence,
                ActionDigest = command.ActionDigest,
                AuthorityEpoch = command.AuthorityEpoch,
                ApprovalDigest = command.ApprovalDigest,
                AcknowledgementState = "completed",
                CompletionCertainty = "certain",
                At = DateTimeOffset.UtcNow,
            },
            Ok = true,
            CompletionCertainty = "certain",
            ResultBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("[]")),
        };
        var completionBytes = JsonSerializer.SerializeToUtf8Bytes(completion);
        using var completionRequest = AuthenticatedRequest(
            host,
            secret,
            HttpMethod.Post,
            $"/v1/session/{session.SessionId}/commands/{command.CommandId}/complete",
            completionBytes,
            command.CommandId,
            command.Sequence);
        using var completionResponse = await client.SendAsync(completionRequest);
        Assert.Equal(HttpStatusCode.OK, completionResponse.StatusCode);

        var result = await hostCall;
        Assert.True(result.Ok);
        Assert.Equal(command.CommandId, result.Receipt.CommandId);
        Assert.Equal("completed", host.LastReceipt!.AcknowledgementState);
    }

    [Fact]
    public async Task HostTransport_DeliveredCancellationWaitsForAgentAcknowledgement()
    {
        var session = Session();
        var secret = Enumerable.Repeat((byte)0x41, 32).ToArray();
        await using var host = new AppleTestAgentHttpHost(session, secret);
        await host.StartAsync();
        using var client = new HttpClient();
        var hello = new AppleTestAgentHello
        {
            SessionId = session.SessionId,
            Target = session.Target,
            AgentInstanceId = "native-xctest",
            AttachedAt = DateTimeOffset.UtcNow,
            Capabilities = new AppleTestAgentCapabilities
            {
                MaxArtifactChunkBytes = AppleTestAgentProtocolVersions.MaximumArtifactChunkBytes,
            },
        };
        var helloBytes = JsonSerializer.SerializeToUtf8Bytes(hello);
        using (var helloRequest = AuthenticatedRequest(
            host,
            secret,
            HttpMethod.Post,
            $"/v1/session/{session.SessionId}/hello",
            helloBytes))
        using (var helloResponse = await client.SendAsync(helloRequest))
            Assert.Equal(HttpStatusCode.OK, helloResponse.StatusCode);

        var pending = host.SendAsync(AppleTestAgentOperations.Wait, new Dictionary<string, string>
        {
            ["durationMs"] = "10000",
        });
        var commandId = host.LastQueuedCommandId!;
        using var nextRequest = AuthenticatedRequest(
            host,
            secret,
            HttpMethod.Get,
            $"/v1/session/{session.SessionId}/next",
            Array.Empty<byte>());
        using var nextResponse = await client.SendAsync(nextRequest);
        var command = JsonSerializer.Deserialize<AppleTestAgentOperationCommand>(
            await nextResponse.Content.ReadAsByteArrayAsync());
        Assert.NotNull(command);
        await host.WaitForCommandDeliveryAsync(commandId);

        // A delivered command remains pending until the agent attests that it saw cancellation.
        var cancellation = await host.CancelAsync(commandId, "test");
        Assert.NotNull(cancellation);
        Assert.False(pending.IsCompleted);

        var completion = new AppleTestAgentOperationCompletion
        {
            Receipt = new AppleTestAgentCommandReceipt
            {
                SessionId = command!.SessionId,
                CommandId = command.CommandId,
                Sequence = command.Sequence,
                ActionDigest = command.ActionDigest,
                AuthorityEpoch = command.AuthorityEpoch,
                ApprovalDigest = command.ApprovalDigest,
                AcknowledgementState = "cancelled",
                CompletionCertainty = "certain",
                At = DateTimeOffset.UtcNow,
            },
            Ok = false,
            CompletionCertainty = "certain",
            Error = new AppleTestAgentError { Code = AppleTestAgentErrorCodes.Cancelled, Category = "cancelled" },
        };
        var completionBytes = JsonSerializer.SerializeToUtf8Bytes(completion);
        using var completionRequest = AuthenticatedRequest(
            host,
            secret,
            HttpMethod.Post,
            $"/v1/session/{session.SessionId}/commands/{command.CommandId}/complete",
            completionBytes,
            command.CommandId,
            command.Sequence);
        using var completionResponse = await client.SendAsync(completionRequest);
        Assert.Equal(HttpStatusCode.OK, completionResponse.StatusCode);
        var result = await pending;
        Assert.Equal(AppleTestAgentErrorCodes.Cancelled, result.Error?.Code);
        Assert.Equal("cancelled", result.Receipt.AcknowledgementState);
    }

    [Fact]
    public async Task HostTransport_RejectsSecondAgentInstanceAndWrongArtifactSession()
    {
        var session = Session();
        var secret = Enumerable.Repeat((byte)0x51, 32).ToArray();
        await using var host = new AppleTestAgentHttpHost(session, secret);
        await host.StartAsync();
        using var client = new HttpClient();

        var hello = new AppleTestAgentHello
        {
            SessionId = session.SessionId,
            Target = session.Target,
            AgentInstanceId = "native-xctest-one",
            AttachedAt = DateTimeOffset.UtcNow,
            Capabilities = new AppleTestAgentCapabilities
            {
                MaxArtifactChunkBytes = AppleTestAgentProtocolVersions.MaximumArtifactChunkBytes,
            },
        };
        var helloBytes = JsonSerializer.SerializeToUtf8Bytes(hello);
        using (var request = AuthenticatedRequest(host, secret, HttpMethod.Post, $"/v1/session/{session.SessionId}/hello", helloBytes))
        using (var response = await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        hello.AgentInstanceId = "native-xctest-two";
        helloBytes = JsonSerializer.SerializeToUtf8Bytes(hello);
        using (var request = AuthenticatedRequest(host, secret, HttpMethod.Post, $"/v1/session/{session.SessionId}/hello", helloBytes))
        using (var response = await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var artifact = Chunk("artifact", 0, 1, Encoding.UTF8.GetBytes("x"), isFinal: true);
        artifact.SessionId = "wrong-session";
        var artifactBytes = JsonSerializer.SerializeToUtf8Bytes(artifact);
        using var artifactRequest = AuthenticatedRequest(
            host,
            secret,
            HttpMethod.Post,
            $"/v1/session/{session.SessionId}/artifacts",
            artifactBytes,
            artifact.ArtifactId,
            artifact.ChunkIndex);
        using var artifactResponse = await client.SendAsync(artifactRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, artifactResponse.StatusCode);
    }

    [Fact]
    public void ParityNormalization_IgnoresTransportVolatilityButRetainsFlowOutcome()
    {
        var first = Report("run-one", DateTimeOffset.Parse("2026-08-02T00:00:00Z"));
        var second = Report("run-two", DateTimeOffset.Parse("2026-08-02T00:01:00Z"));

        Assert.Equal(MauiFlowReportParity.ComputeDigest(first), MauiFlowReportParity.ComputeDigest(second));

        second.Steps[0].FailureClass = MauiFlowFailureClasses.AssertionFailed;
        Assert.NotEqual(MauiFlowReportParity.ComputeDigest(first), MauiFlowReportParity.ComputeDigest(second));
    }

    [Fact]
    public void ParityNormalization_MatchesSharedAndroidWindowsAppleGoldenFixture()
    {
        var root = FindRepositoryRoot();
        var golden = File.ReadAllText(Path.Combine(
            root,
            "tests",
            "DevFlow",
            "FlowParity",
            "android-windows-apple-passed.json"));
        using var expected = JsonDocument.Parse(golden);
        using var actual = JsonDocument.Parse(MauiFlowReportParity.CreateNormalizedJson(
            Report("apple-golden", DateTimeOffset.Parse("2026-08-02T00:00:00Z"))));

        Assert.Equal(JsonSerializer.Serialize(expected.RootElement), actual.RootElement.GetRawText());
    }

    [Fact]
    public void ExperimentalAppKitTarget_UsesTheSameReportParityWithoutMacCatalystEquivalence()
    {
        var appKit = new AppleTestAgentTarget
        {
            Platform = "macos",
            TargetBundleId = "com.companyname.mauitodo.appkit",
            Experimental = true,
        };
        var catalyst = new AppleTestAgentTarget
        {
            Platform = "maccatalyst",
            TargetBundleId = "com.companyname.mauitodo",
            Experimental = false,
        };

        Assert.True(appKit.Experimental);
        Assert.False(catalyst.Experimental);
        Assert.NotEqual(appKit.TargetBundleId, catalyst.TargetBundleId);
        Assert.Equal(
            MauiFlowReportParity.ComputeDigest(Report("appkit", DateTimeOffset.Parse("2026-08-02T00:00:00Z"))),
            MauiFlowReportParity.ComputeDigest(Report("maccatalyst", DateTimeOffset.Parse("2026-08-02T00:01:00Z"))));
    }

    [Fact]
    public void ShellHarness_ConstructsGuardedAppleSpikeWithoutSecretArgument()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "eng",
            "devflow",
            "Run-DevFlowFlowQa.sh"));

        Assert.Contains("--apple-spike", source, StringComparison.Ordinal);
        Assert.Contains("--target-app", source, StringComparison.Ordinal);
        Assert.Contains("--target-bundle-id", source, StringComparison.Ordinal);
        Assert.Contains("--safe-action-id", source, StringComparison.Ordinal);
        Assert.Contains("apple-xctest-spike.json", source, StringComparison.Ordinal);
        Assert.Contains("DEVFLOW_APPLE_AGENT_SESSION_SECRET", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--session-secret", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppleQaScript_RequiresCapabilityProofThenRunsTierOneAndContractCategory()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "eng",
            "devflow",
            "Run-DevFlowFlowQa.sh"));

        Assert.Contains("run_apple_spike", source, StringComparison.Ordinal);
        Assert.Contains("run_apple_flow_qa", source, StringComparison.Ordinal);
        Assert.Contains("Category=AppleTestAgent", source, StringComparison.Ordinal);
        Assert.Contains("apple-flow-qa.json", source, StringComparison.Ordinal);
        Assert.Contains("\"foregroundProof\"", source, StringComparison.Ordinal);
        Assert.Contains("\"authenticatedTransport\"", source, StringComparison.Ordinal);
        Assert.Contains("\"testingPackageVersion\"", source, StringComparison.Ordinal);
        Assert.Contains("\"signing\"", source, StringComparison.Ordinal);
        Assert.Contains("\"xcodeVersion\"", source, StringComparison.Ordinal);
        Assert.Contains("\"simulatorRuntime\"", source, StringComparison.Ordinal);
        Assert.Contains("\"simulatorDeviceFingerprint\"", source, StringComparison.Ordinal);
        Assert.Contains("\"artifactTrust\"", source, StringComparison.Ordinal);
        Assert.Contains("\"reportParity\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppleQaWorkflow_IsAdvisoryAndKeepsExperimentalAppKitSeparateFromMacCatalyst()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            ".github",
            "workflows",
            "devflow-integration.yml"));

        Assert.Contains("ios-flow-qa:", source, StringComparison.Ordinal);
        Assert.Contains("maccatalyst-flow-qa:", source, StringComparison.Ordinal);
        Assert.Contains("macos-appkit-flow-qa:", source, StringComparison.Ordinal);
        Assert.Contains("continue-on-error: true", source, StringComparison.Ordinal);
        Assert.Contains("apple-flow-qa", source, StringComparison.Ordinal);
        Assert.Contains("appkit-flow-qa", source, StringComparison.Ordinal);
        Assert.Contains("--platform ios", source, StringComparison.Ordinal);
        Assert.Contains("--platform maccatalyst", source, StringComparison.Ordinal);
        Assert.Contains("--platform macos", source, StringComparison.Ordinal);
        Assert.Contains("--experimental", source, StringComparison.Ordinal);
        Assert.Contains("never participates in the official Mac Catalyst", source, StringComparison.Ordinal);
    }

    static AppleTestAgentSession Session() => new()
    {
        SessionId = "apple-session",
        HostInstanceId = "host",
        Target = new AppleTestAgentTarget { Platform = "ios", TargetBundleId = "com.example.target" },
        AuthorityEpoch = 1,
        ApprovalDigest = "sha256:approval",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
    };

    static AppleTestAgentOperationCommand Command(AppleTestAgentSession session, long sequence, string commandId) => new()
    {
        SessionId = session.SessionId,
        Target = session.Target,
        AuthorityEpoch = session.AuthorityEpoch,
        CommandId = commandId,
        Sequence = sequence,
        ActionDigest = $"sha256:action-{sequence}",
        ApprovalDigest = session.ApprovalDigest,
        Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
        Operation = AppleTestAgentOperations.Tap,
    };

    static AppleTestAgentArtifactChunk Chunk(string artifactId, int index, int total, byte[] content, bool isFinal) => new()
    {
        SessionId = "apple-session",
        ArtifactId = artifactId,
        Kind = "screenshot-png",
        ChunkIndex = index,
        TotalChunks = total,
        ContentBase64 = Convert.ToBase64String(content),
        ContentDigest = AppleTestAgentAuthenticator.ComputeDigest(content),
        IsFinal = isFinal,
    };

    static MauiFlowRunReport Report(string runId, DateTimeOffset at) => new()
    {
        RunId = runId,
        StartedAt = at,
        EndedAt = at.AddSeconds(1),
        Outcome = new MauiFlowRunOutcome { Status = MauiFlowRunOutcomes.Passed, Terminal = true },
        Steps =
        [
            new MauiFlowStepAttempt
            {
                Sequence = 1,
                Action = FlowActions.Assert,
                Assertions =
                [
                    new MauiFlowAssertionResult { Kind = "exists", Passed = true },
                ],
            },
        ],
    };

    static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MauiLabs.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    static HttpRequestMessage AuthenticatedRequest(
        AppleTestAgentHttpHost host,
        byte[] secret,
        HttpMethod method,
        string path,
        byte[] body,
        string? commandId = null,
        long sequence = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var nonce = Guid.NewGuid().ToString("N");
        var request = new HttpRequestMessage(method, new Uri(host.Endpoint, path.TrimStart('/')));
        if (body.Length > 0)
            request.Content = new ByteArrayContent(body);
        request.Headers.Add("X-Maui-Apple-Session", host.Session.SessionId);
        request.Headers.Add("X-Maui-Apple-Timestamp", now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Maui-Apple-Nonce", nonce);
        request.Headers.Add(
            "X-Maui-Apple-Signature",
            AppleTestAgentAuthenticator.CreateSignature(
                secret,
                method.Method,
                path,
                host.Session.SessionId,
                commandId,
                sequence,
                now.ToUnixTimeSeconds(),
                nonce,
                AppleTestAgentAuthenticator.ComputeDigest(body)));
        return request;
    }

    sealed class FakeTransport : IAppleTestAgentTransport
    {
        long _sequence;

        public AppleTestAgentSession Session { get; } = AppleTestAgentProtocolTests.Session();
        public AppleTestAgentCommandReceipt? LastReceipt { get; private set; }
        public IReadOnlyList<AppleTestAgentArtifactReference> CompletedArtifacts => [];

        public Task<AppleTestAgentOperationCompletion> SendAsync(
            string operation,
            IReadOnlyDictionary<string, string>? arguments = null,
            CancellationToken cancellationToken = default)
        {
            var sequence = ++_sequence;
            LastReceipt = new AppleTestAgentCommandReceipt
            {
                SessionId = Session.SessionId,
                CommandId = $"command-{sequence}",
                Sequence = sequence,
                ActionDigest = $"sha256:{sequence}",
                AuthorityEpoch = Session.AuthorityEpoch,
                AcknowledgementState = "completed",
                CompletionCertainty = "certain",
                At = DateTimeOffset.UtcNow,
            };
            object body = operation switch
            {
                AppleTestAgentOperations.Query or AppleTestAgentOperations.Tree => new[]
                {
                    new ElementInfo
                    {
                        Id = "safe",
                        AutomationId = "SafeButton",
                        Type = "Button",
                        IsVisible = true,
                        IsEnabled = true,
                        Opacity = 1,
                    },
                },
                AppleTestAgentOperations.Status => new
                {
                    running = true,
                    route = "//native",
                    app = new { packageId = "com.example.target", processId = 42 },
                    testState = new
                    {
                        seedFingerprint = "sha256:seed",
                        backendStateFingerprint = "sha256:backend",
                        stateFingerprint = "sha256:state",
                        processInstanceId = "target-process",
                    },
                },
                _ => new { success = true },
            };
            return Task.FromResult(new AppleTestAgentOperationCompletion
            {
                Receipt = LastReceipt,
                Ok = true,
                CompletionCertainty = "certain",
                ResultBase64 = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(body)),
            });
        }

        public Task<AppleTestAgentCommandReceipt?> CancelAsync(string commandId, string? reason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(LastReceipt);
    }
}
