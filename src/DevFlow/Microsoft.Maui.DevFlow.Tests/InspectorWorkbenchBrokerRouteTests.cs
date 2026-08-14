using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Inspector;
using Microsoft.Maui.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class InspectorWorkbenchBrokerRouteTests
{
    [Fact]
    public async Task WorkbenchImproveRoute_AnalyzesLoadedFlowPlanAndLiveTreeWithoutMutation()
    {
        var brokerPort = FreePort();
        var agentPort = FreePort();
        using var broker = new BrokerServer(
            brokerPort,
            TimeSpan.FromMinutes(1),
            previewFlags: PreviewTestFeatures.AllEnabled());
        using var cancellation = new CancellationTokenSource();
        var brokerTask = broker.RunAsync(cancellation.Token);
        await WaitForBrokerAsync(brokerPort);

        using var agent = new AgentHttpServer(agentPort);
        agent.MapGet("/api/v1/agent/status", _ => Task.FromResult(HttpResponse.Json(new
        {
            running = true,
            agent = new { name = "DevFlow", instanceId = "improve-instance", version = "1" },
            app = new { name = "Improve Test", build = "build-1", packageId = "com.example.improve", version = "1.0" },
            device = new { platform = "android", deviceType = "emulator", idiom = "phone" },
            capabilities = new { ui = true, mutations = true },
            route = "/orders",
            locale = "en-US",
            theme = "light",
            orientation = "portrait",
            displayProfile = "320x640"
        })));
        agent.MapGet("/api/v1/ui/tree", _ => Task.FromResult(HttpResponse.Json(new[]
        {
            new
            {
                id = "root",
                type = "ContentPage",
                automationId = "orders-page",
                isVisible = true,
                isEnabled = true,
                children = new[]
                {
                    new { id = "save-1", parentId = "root", type = "Button", role = "button", automationId = "save", isVisible = true, isEnabled = true },
                    new { id = "save-2", parentId = "root", type = "Button", role = "button", automationId = "save", isVisible = true, isEnabled = true }
                }
            }
        })));
        agent.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{brokerPort}/ws/agent"), CancellationToken.None);
        await SendAsync(socket, $$"""
            {"type":"register","project":"workbench-improve-test","tfm":"net10.0","platform":"android","appName":"Improve Test","currentPort":{{agentPort}}}
            """);
        await ReceiveAsync(socket);

        try
        {
            using var http = new HttpClient();
            var embedToken = (string)typeof(BrokerServer)
                .GetField("_embedToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(broker)!;
            Assert.Null(typeof(BrokerServer).GetField(
                "_hostApprovalToken",
                BindingFlags.Instance | BindingFlags.NonPublic));

            async Task<HttpResponseMessage> ProbeGrantIssueAsync(string token)
            {
                using var message = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"http://127.0.0.1:{brokerPort}/api/test-agent/grants/issue");
                message.Headers.TryAddWithoutValidation("X-DevFlow-Host-Approval-Token", token);
                message.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                return await http.SendAsync(message);
            }

            using (var embedProbe = await ProbeGrantIssueAsync(embedToken))
                Assert.Equal(HttpStatusCode.NotImplemented, embedProbe.StatusCode);

            using var agents = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{brokerPort}/api/agents"));
            var agentId = agents.RootElement[0].GetProperty("id").GetString()!;
            var inspectorBase = $"http://127.0.0.1:{brokerPort}/inspector/{Uri.EscapeDataString(agentId)}";
            _ = await http.GetAsync($"{inspectorBase}/");
            _ = await http.GetAsync($"{inspectorBase}/");
            var inspector = await GetInspectorAsync(broker, agentId);
            var token = (string)typeof(InspectorServer)
                .GetField("_readToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(inspector)!;
            http.DefaultRequestHeaders.Add("X-DevFlow-Inspector-Token", token);

            var response = await PostJsonAsync(http, $"{inspectorBase}/api/workbench/improve/analyze", new
            {
                flow = new
                {
                    schema = 2,
                    name = "improve",
                    steps = new[]
                    {
                        new
                        {
                            seq = 1,
                            action = "tap",
                            args = new { selector = new { automationId = "save" } }
                        }
                    }
                },
                plan = new
                {
                    schema = 1,
                    requiredPlatforms = new[] { "android", "windows" },
                    acceptanceCriteria = new[] { new { criterionId = "order-saved", required = true } }
                },
                includeLiveTree = true
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
            var findingIds = body.RootElement.GetProperty("analysis").GetProperty("findings")
                .EnumerateArray()
                .Select(finding => finding.GetProperty("diagnosticId").GetString())
                .ToArray();
            Assert.Contains(MauiSelectorHealthDiagnosticIds.DuplicateAutomationId, findingIds);
            Assert.Contains(MauiSelectorHealthDiagnosticIds.RequiredPlatform, findingIds);
            Assert.True(body.RootElement.GetProperty("liveTree").GetProperty("available").GetBoolean());
        }
        finally
        {
            cancellation.Cancel();
            broker.Dispose();
            await brokerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task WorkbenchRoutes_UseBrokerRunAndTrustServicesWithInspectorToken()
    {
        var brokerPort = FreePort();
        var agentPort = FreePort();
        using var broker = new BrokerServer(
            brokerPort,
            TimeSpan.FromMinutes(1),
            previewFlags: PreviewTestFeatures.AllEnabled());
        using var cancellation = new CancellationTokenSource();
        var brokerTask = broker.RunAsync(cancellation.Token);
        await WaitForBrokerAsync(brokerPort);

        using var agent = new AgentHttpServer(agentPort);
        agent.MapGet("/api/v1/agent/status", _ => Task.FromResult(HttpResponse.Json(new
        {
            running = true,
            agent = new { name = "DevFlow", instanceId = "runtime-instance", version = "1" },
            app = new { name = "Workbench Test", build = "build-1", packageId = "com.example.workbench", version = "1.0" },
            device = new { platform = "test", deviceType = "emulator", idiom = "phone" },
            capabilities = new { ui = true, mutations = true, workflowCommandLedger = true },
            route = "/home",
        })));
        agent.MapPost("/api/v1/agent/workflow-runs", _ => Task.FromResult(HttpResponse.Json(new
        {
            ok = true,
            state = "active",
        })), requiresMutationLease: false);
        agent.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{brokerPort}/ws/agent"), CancellationToken.None);
        await SendAsync(socket, $$"""
            {"type":"register","project":"workbench-route-test","tfm":"net10.0","platform":"test","appName":"Workbench Test","currentPort":{{agentPort}}}
            """);
        await ReceiveAsync(socket);

        try
        {
            using var http = new HttpClient();
            using var agents = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{brokerPort}/api/agents"));
            var registration = agents.RootElement[0];
            var agentId = registration.GetProperty("id").GetString()!;
            var instanceId = registration.GetProperty("instanceId").GetString()!;
            var inspectorBase = $"http://127.0.0.1:{brokerPort}/inspector/{Uri.EscapeDataString(agentId)}";
            _ = await http.GetAsync($"{inspectorBase}/");

            // This first request constructs the per-agent Inspector, but cannot read the
            // token-gated workbench target facts.
            var denied = await http.GetAsync($"{inspectorBase}/api/workbench/target");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            var inspector = await GetInspectorAsync(broker, agentId);
            var inspectorToken = (string)typeof(InspectorServer)
                .GetField("_readToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(inspector)!;
            var inspectorLeaseId = (string)typeof(InspectorServer)
                .GetField("_fallbackMutationLeaseId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(inspector)!;
            http.DefaultRequestHeaders.Add("X-DevFlow-Inspector-Token", inspectorToken);

            using var leaseClaim = await PostJsonAsync(
                http,
                $"http://127.0.0.1:{brokerPort}/api/leases/{Uri.EscapeDataString(agentId)}",
                new
                {
                    action = "claim",
                    leaseId = inspectorLeaseId,
                    holderKind = "web-inspector",
                    label = "DevFlow Web Inspector",
                });
            Assert.Equal(HttpStatusCode.OK, leaseClaim.StatusCode);

            using var target = JsonDocument.Parse(await http.GetStringAsync($"{inspectorBase}/api/workbench/target"));
            Assert.True(target.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(instanceId, target.RootElement.GetProperty("target").GetProperty("agentInstanceId").GetString());
            Assert.Equal("build-1", target.RootElement.GetProperty("target").GetProperty("app").GetProperty("build").GetString());

            var flow = FailingTapFlow();
            var markdown = FlowMarkdown.Serialize(flow);
            using var preflight = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/run/preflight",
                new
                {
                    run = new
                    {
                        agentId,
                        agentInstanceId = instanceId,
                        idempotencyKey = "inspector-workbench-key",
                        markdown,
                    },
                    evidence = new { includeScreenshot = false, includeWorkflow = false },
                });
            Assert.Equal(HttpStatusCode.OK, preflight.StatusCode);
            using var preflightBody = JsonDocument.Parse(await preflight.Content.ReadAsStringAsync());
            Assert.True(preflightBody.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(preflightBody.RootElement.TryGetProperty("capabilityToken", out _));

            using var started = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/run/start",
                new
                {
                    run = new
                    {
                        agentId,
                        agentInstanceId = instanceId,
                        idempotencyKey = "inspector-workbench-key",
                        markdown,
                    },
                    evidence = new { includeScreenshot = false, includeWorkflow = false },
                });
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            using var startBody = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
            var run = startBody.RootElement.GetProperty("run");
            var runId = run.GetProperty("runId").GetString()!;
            var runToken = startBody.RootElement.GetProperty("capabilityToken").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(runToken));

            // A reload/host handoff gets the server-held journal snapshot without requiring that
            // the browser echo the token again.
            using var journal = JsonDocument.Parse(await http.GetStringAsync($"{inspectorBase}/api/workbench/run/journal"));
            Assert.True(journal.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(runId, journal.RootElement.GetProperty("run").GetProperty("runId").GetString());
            Assert.False(journal.RootElement.GetProperty("run").TryGetProperty("capabilityToken", out _));

            JsonDocument? terminal = null;
            for (var attempt = 0; attempt < 240; attempt++)
            {
                using var status = await PostJsonAsync(
                    http,
                    $"{inspectorBase}/api/workbench/run/{Uri.EscapeDataString(runId)}/status",
                    new { capabilityToken = runToken });
                terminal?.Dispose();
                terminal = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
                if (terminal.RootElement.GetProperty("run").GetProperty("terminal").GetBoolean())
                    break;
                await Task.Delay(50);
            }
            using (terminal)
            {
                Assert.NotNull(terminal);
                Assert.True(terminal!.RootElement.GetProperty("run").GetProperty("terminal").GetBoolean());
                Assert.Equal("failed", terminal.RootElement.GetProperty("run").GetProperty("state").GetString());

                using var forgedTrust = await PostJsonAsync(
                    http,
                    $"{inspectorBase}/api/workbench/repair/classify",
                    new
                    {
                        runId,
                        runCapabilityToken = runToken,
                        isCurrentLocalRun = true,
                        artifactTrust = MauiArtifactTrustStates.LocallyReproduced,
                    });
                Assert.Equal(HttpStatusCode.BadRequest, forgedTrust.StatusCode);
                Assert.Contains(
                    "not trusted",
                    await forgedTrust.Content.ReadAsStringAsync(),
                    StringComparison.OrdinalIgnoreCase);

                using var wrongRunCapability = await PostJsonAsync(
                    http,
                    $"{inspectorBase}/api/workbench/repair/classify",
                    new
                    {
                        runId,
                        runCapabilityToken = "forged-run-capability",
                    });
                Assert.Equal(HttpStatusCode.Forbidden, wrongRunCapability.StatusCode);

                using var brokerClassified = await PostJsonAsync(
                    http,
                    $"{inspectorBase}/api/workbench/repair/classify",
                    new
                    {
                        runId,
                        runCapabilityToken = runToken,
                    });
                Assert.Equal(HttpStatusCode.OK, brokerClassified.StatusCode);
                using var brokerClassification = JsonDocument.Parse(
                    await brokerClassified.Content.ReadAsStringAsync());
                Assert.Equal(
                    "broker-current-local-run",
                    brokerClassification.RootElement.GetProperty("evidenceSource").GetString());
            }

            using var handoffResponse = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-handoff",
                new
                {
                    runId,
                    capabilityToken = runToken,
                    flowName = "inspector-workbench.md",
                    markdown,
                    flow,
                });
            Assert.Equal(HttpStatusCode.OK, handoffResponse.StatusCode);
            using var handoff = JsonDocument.Parse(await handoffResponse.Content.ReadAsStringAsync());
            var context = handoff.RootElement.GetProperty("context");
            var failureRequest = context.GetProperty("failureRequest");
            Assert.Equal(runId, failureRequest.GetProperty("runId").GetString());
            Assert.Equal(runToken, failureRequest.GetProperty("runCapabilityToken").GetString());
            var failureEnvelope = failureRequest.GetProperty("envelope");
            var handoffSessionId = failureEnvelope
                .GetProperty("correlation")
                .GetProperty("authoringSessionId")
                .GetString()!;
            var handoffReadCapability = failureEnvelope.GetProperty("readCapabilityId").GetString()!;
            Assert.Equal(runId, failureEnvelope.GetProperty("correlation").GetProperty("runId").GetString());
            Assert.Equal(instanceId, failureEnvelope.GetProperty("target").GetProperty("agentInstanceId").GetString());

            var sessionService = (TestAgentSessionService)typeof(BrokerServer)
                .GetField("_testAgentSessions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(broker)!;
            var binding = sessionService.GetRunBinding(new MauiTestAgentRunBindingRequest
            {
                SessionId = handoffSessionId,
                ReadCapabilityId = handoffReadCapability,
                Envelope = failureEnvelope.Deserialize(
                    MauiTestingJsonContext.Default.MauiTestAgentRequestEnvelope),
                RunId = runId,
            });
            Assert.True(binding.Ok, binding.Error?.Message);

            using var repeatedHandoffResponse = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-handoff",
                new
                {
                    runId,
                    flowName = "inspector-workbench.md",
                    markdown,
                    flow,
                });
            Assert.Equal(HttpStatusCode.OK, repeatedHandoffResponse.StatusCode);
            using var repeatedHandoff = JsonDocument.Parse(await repeatedHandoffResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                handoffSessionId,
                repeatedHandoff.RootElement
                    .GetProperty("context")
                    .GetProperty("failureRequest")
                    .GetProperty("envelope")
                    .GetProperty("correlation")
                    .GetProperty("authoringSessionId")
                    .GetString());

            const string hostile = "CorrectHorseBatteryStaple";
            using var import = new HttpRequestMessage(
                HttpMethod.Post,
                $"{inspectorBase}/api/workbench/artifacts/import?kind=flow-run")
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(FlowRunJson(hostile))),
            };
            import.Content.Headers.ContentType = new("application/json");
            using var imported = await http.SendAsync(import);
            Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
            var importedText = await imported.Content.ReadAsStringAsync();
            Assert.DoesNotContain(hostile, importedText, StringComparison.Ordinal);
            using var importedBody = JsonDocument.Parse(importedText);
            var artifactId = importedBody.RootElement
                .GetProperty("status")
                .GetProperty("identity")
                .GetProperty("id")
                .GetString()!;
            var artifactCapability = importedBody.RootElement
                .GetProperty("capabilityToken")
                .GetString()!;
            Assert.Equal(
                "imported-artifact",
                importedBody.RootElement.GetProperty("status").GetProperty("identity").GetProperty("namespace").GetString());

            using var untrustedImportedRepair = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/repair/classify",
                new
                {
                    runId,
                    runCapabilityToken = runToken,
                    artifactId,
                    artifactCapabilityToken = artifactCapability,
                });
            Assert.Equal(HttpStatusCode.Conflict, untrustedImportedRepair.StatusCode);
            Assert.Contains(
                "not locally reproduced",
                await untrustedImportedRepair.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            cancellation.Cancel();
            broker.Dispose();
            await brokerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task WorkbenchPreflight_UsesBrokerObservedCheckpointFactsAndRequirementCapabilities()
    {
        var brokerPort = FreePort();
        var agentPort = FreePort();
        using var broker = new BrokerServer(
            brokerPort,
            TimeSpan.FromMinutes(1),
            previewFlags: PreviewTestFeatures.AllEnabled());
        using var cancellation = new CancellationTokenSource();
        var brokerTask = broker.RunAsync(cancellation.Token);
        await WaitForBrokerAsync(brokerPort);

        using var agent = new AgentHttpServer(agentPort);
        agent.MapGet("/api/v1/agent/status", _ => Task.FromResult(HttpResponse.Json(new
        {
            running = true,
            agent = new { name = "DevFlow", instanceId = "preflight-instance", version = "1" },
            app = new { name = "Preflight Test", build = "build-2", packageId = "com.example.preflight", version = "1.0" },
            device = new { platform = "android", deviceType = "emulator", idiom = "phone" },
            capabilities = new { ui = true, logs = true, mutations = true },
            route = "/checkout",
            window = "main",
            modal = "sheet",
            locale = "fr-FR",
            theme = "dark",
            orientation = "landscape",
            displayProfile = "tablet",
        })));
        agent.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{brokerPort}/ws/agent"), CancellationToken.None);
        await SendAsync(socket, $$"""
            {"type":"register","project":"workbench-preflight-test","tfm":"net10.0","platform":"android","appName":"Preflight Test","currentPort":{{agentPort}}}
            """);
        await ReceiveAsync(socket);

        try
        {
            using var http = new HttpClient();
            using var agents = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{brokerPort}/api/agents"));
            var registration = agents.RootElement[0];
            var agentId = registration.GetProperty("id").GetString()!;
            var instanceId = registration.GetProperty("instanceId").GetString()!;
            var inspectorBase = $"http://127.0.0.1:{brokerPort}/inspector/{Uri.EscapeDataString(agentId)}";

            _ = await http.GetAsync($"{inspectorBase}/");
            var inspector = await GetInspectorAsync(broker, agentId);
            var inspectorToken = (string)typeof(InspectorServer)
                .GetField("_readToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(inspector)!;
            http.DefaultRequestHeaders.Add("X-DevFlow-Inspector-Token", inspectorToken);

            using var target = JsonDocument.Parse(await http.GetStringAsync($"{inspectorBase}/api/workbench/target"));
            var brokerCapabilities = target.RootElement.GetProperty("broker");
            Assert.False(brokerCapabilities.GetProperty("repairValidationAvailable").GetBoolean());
            var observed = target.RootElement.GetProperty("target").GetProperty("observedCheckpoint");
            Assert.Equal("build-2", observed.GetProperty("appBuildFingerprint").GetString());
            Assert.Equal("/checkout", observed.GetProperty("route").GetString());
            Assert.Equal("main", observed.GetProperty("window").GetString());
            Assert.Equal("sheet", observed.GetProperty("modal").GetString());
            Assert.Equal("fr-FR", observed.GetProperty("locale").GetString());
            Assert.Equal("dark", observed.GetProperty("theme").GetString());
            Assert.Equal("landscape", observed.GetProperty("orientation").GetString());
            Assert.Equal("tablet", observed.GetProperty("displayProfile").GetString());

            var preflightFlow = AssertOnlyFlow();
            var preflightFlowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(preflightFlow);
            using var preflight = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/run/preflight",
                new
                {
                    run = new
                    {
                        agentId,
                        agentInstanceId = instanceId,
                        idempotencyKey = "workbench-preflight-checkpoint-key",
                        markdown = FlowMarkdown.Serialize(preflightFlow),
                        plan = new
                        {
                            schema = 1,
                            planId = "plan-workbench-checkpoint",
                            revision = 1,
                            flow = new { path = "checkpoint.md", revision = 1, digest = preflightFlowDigest },
                            goal = "Validate observed checkpoint and target requirements",
                            reset = new { required = false, strategy = "host-owned" },
                            provenance = new
                            {
                                actorKind = "human",
                                actorId = "test",
                                channel = "unit-test",
                                provider = "xunit",
                            },
                            sideEffectPolicy = "none",
                            requiredPlatforms = new[] { "android" },
                            checkpoint = new
                            {
                                appBuildFingerprint = "build-2",
                                route = "/checkout",
                                window = "main",
                                modal = "sheet",
                                locale = "fr-FR",
                                theme = "dark",
                                orientation = "landscape",
                                displayProfile = "tablet",
                            },
                            requirements = new
                            {
                                requiredCapabilities = new[]
                                {
                                    new { name = "logs", required = true },
                                },
                                requiredSemantics = Array.Empty<object>(),
                            },
                        },
                    },
                    evidence = new { includeScreenshot = false, includeWorkflow = false },
                });
            Assert.Equal(HttpStatusCode.OK, preflight.StatusCode);
            using var preflightBody = JsonDocument.Parse(await preflight.Content.ReadAsStringAsync());
            Assert.True(preflightBody.RootElement.GetProperty("ok").GetBoolean());
            if (preflightBody.RootElement.TryGetProperty("errors", out var errors))
            {
                Assert.True(
                    errors.ValueKind == JsonValueKind.Null ||
                    !errors.EnumerateArray().Any());
            }
        }
        finally
        {
            cancellation.Cancel();
            broker.Dispose();
            await brokerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task WorkbenchPreflight_RecognizesCanonicalWorkflowCommandLedgerCapability()
    {
        var brokerPort = FreePort();
        var agentPort = FreePort();
        using var broker = new BrokerServer(
            brokerPort,
            TimeSpan.FromMinutes(1),
            previewFlags: PreviewTestFeatures.AllEnabled());
        using var cancellation = new CancellationTokenSource();
        var brokerTask = broker.RunAsync(cancellation.Token);
        await WaitForBrokerAsync(brokerPort);

        using var agent = new AgentHttpServer(agentPort);
        agent.MapGet("/api/v1/agent/status", _ => Task.FromResult(HttpResponse.Json(new
        {
            running = true,
            agent = new { name = "DevFlow", instanceId = "ledger-instance", version = "1" },
            app = new { name = "Ledger Test", build = "build-ledger", packageId = "com.example.ledger", version = "1.0" },
            device = new { platform = "android", deviceType = "emulator", idiom = "phone" },
            capabilities = new { ui = true, mutations = true, workflowCommandLedger = true },
            route = "/ledger",
        })));
        agent.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{brokerPort}/ws/agent"), CancellationToken.None);
        await SendAsync(socket, $$"""
            {"type":"register","project":"workbench-ledger-capability-test","tfm":"net10.0","platform":"android","appName":"Ledger Test","currentPort":{{agentPort}}}
            """);
        await ReceiveAsync(socket);

        try
        {
            using var http = new HttpClient();
            using var agents = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{brokerPort}/api/agents"));
            var registration = agents.RootElement[0];
            var agentId = registration.GetProperty("id").GetString()!;
            var instanceId = registration.GetProperty("instanceId").GetString()!;
            var inspectorBase = $"http://127.0.0.1:{brokerPort}/inspector/{Uri.EscapeDataString(agentId)}";

            _ = await http.GetAsync($"{inspectorBase}/");
            var inspector = await GetInspectorAsync(broker, agentId);
            var inspectorToken = (string)typeof(InspectorServer)
                .GetField("_readToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(inspector)!;
            http.DefaultRequestHeaders.Add("X-DevFlow-Inspector-Token", inspectorToken);

            var preflightFlow = AssertOnlyFlow();
            var preflightFlowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(preflightFlow);
            using var preflight = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/run/preflight",
                new
                {
                    run = new
                    {
                        agentId,
                        agentInstanceId = instanceId,
                        idempotencyKey = "workbench-preflight-ledger-key",
                        markdown = FlowMarkdown.Serialize(preflightFlow),
                        plan = new
                        {
                            schema = 1,
                            planId = "plan-workbench-ledger",
                            revision = 1,
                            flow = new { path = "ledger.md", revision = 1, digest = preflightFlowDigest },
                            goal = "Validate workflow command ledger capability",
                            reset = new { required = false, strategy = "host-owned" },
                            provenance = new
                            {
                                actorKind = "human",
                                actorId = "test",
                                channel = "unit-test",
                                provider = "xunit",
                            },
                            sideEffectPolicy = "none",
                            checkpoint = new
                            {
                                route = "/ledger",
                            },
                            requirements = new
                            {
                                requiredCapabilities = new[]
                                {
                                    new { name = "agent.workflowCommandLedger", required = true },
                                },
                                requiredSemantics = Array.Empty<object>(),
                            },
                        },
                    },
                    evidence = new { includeScreenshot = false, includeWorkflow = false },
                });
            Assert.Equal(HttpStatusCode.OK, preflight.StatusCode);
            using var preflightBody = JsonDocument.Parse(await preflight.Content.ReadAsStringAsync());
            Assert.True(preflightBody.RootElement.GetProperty("ok").GetBoolean());
            if (preflightBody.RootElement.TryGetProperty("errors", out var errors))
                Assert.True(errors.ValueKind == JsonValueKind.Null || !errors.EnumerateArray().Any());
        }
        finally
        {
            cancellation.Cancel();
            broker.Dispose();
            await brokerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task WorkbenchRunJournal_StatusAndCancel_UseServerHeldCapabilityWithoutBrowserEcho()
    {
        var brokerPort = FreePort();
        var agentPort = FreePort();
        using var broker = new BrokerServer(
            brokerPort,
            TimeSpan.FromMinutes(1),
            previewFlags: PreviewTestFeatures.AllEnabled());
        using var cancellation = new CancellationTokenSource();
        var brokerTask = broker.RunAsync(cancellation.Token);
        await WaitForBrokerAsync(brokerPort);

        using var agent = new AgentHttpServer(agentPort);
        agent.MapGet("/api/v1/agent/status", _ => Task.FromResult(HttpResponse.Json(new
        {
            running = true,
            agent = new { name = "DevFlow", instanceId = "recovery-instance", version = "1" },
            app = new { name = "Recovery Test", build = "build-recovery", packageId = "com.example.recovery", version = "1.0" },
            device = new { platform = "test", deviceType = "emulator", idiom = "phone" },
            capabilities = new { ui = true, mutations = true, workflowCommandLedger = true },
            route = "/home",
        })));
        agent.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{brokerPort}/ws/agent"), CancellationToken.None);
        await SendAsync(socket, $$"""
            {"type":"register","project":"workbench-recovery-test","tfm":"net10.0","platform":"test","appName":"Recovery Test","currentPort":{{agentPort}}}
            """);
        await ReceiveAsync(socket);

        try
        {
            using var http = new HttpClient();
            using var agents = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{brokerPort}/api/agents"));
            var registration = agents.RootElement[0];
            var agentId = registration.GetProperty("id").GetString()!;
            var instanceId = registration.GetProperty("instanceId").GetString()!;
            var inspectorBase = $"http://127.0.0.1:{brokerPort}/inspector/{Uri.EscapeDataString(agentId)}";
            _ = await http.GetAsync($"{inspectorBase}/");

            var inspector = await GetInspectorAsync(broker, agentId);
            var inspectorToken = (string)typeof(InspectorServer)
                .GetField("_readToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(inspector)!;
            var inspectorLeaseId = (string)typeof(InspectorServer)
                .GetField("_fallbackMutationLeaseId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(inspector)!;
            http.DefaultRequestHeaders.Add("X-DevFlow-Inspector-Token", inspectorToken);

            using var leaseClaim = await PostJsonAsync(
                http,
                $"http://127.0.0.1:{brokerPort}/api/leases/{Uri.EscapeDataString(agentId)}",
                new
                {
                    action = "claim",
                    leaseId = inspectorLeaseId,
                    holderKind = "web-inspector",
                    label = "DevFlow Web Inspector",
                });
            Assert.Equal(HttpStatusCode.OK, leaseClaim.StatusCode);

            using var start = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/run/start",
                new
                {
                    run = new
                    {
                        agentId,
                        agentInstanceId = instanceId,
                        idempotencyKey = "recovery-key",
                        markdown = FlowMarkdown.Serialize(FailingTapFlow()),
                    },
                    evidence = new { includeScreenshot = false, includeWorkflow = false },
                });
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
            using var startBody = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
            var runId = startBody.RootElement.GetProperty("run").GetProperty("runId").GetString()!;
            var runToken = startBody.RootElement.GetProperty("capabilityToken").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(runToken));

            using var journal = JsonDocument.Parse(await http.GetStringAsync($"{inspectorBase}/api/workbench/run/journal"));
            Assert.True(journal.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(runId, journal.RootElement.GetProperty("run").GetProperty("runId").GetString());
            Assert.False(journal.RootElement.GetProperty("run").TryGetProperty("capabilityToken", out _));

            using var status = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/run/{Uri.EscapeDataString(runId)}/status",
                new { });
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            using var statusBody = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            Assert.Equal(runId, statusBody.RootElement.GetProperty("run").GetProperty("runId").GetString());
            Assert.False(statusBody.RootElement.GetProperty("run").TryGetProperty("capabilityToken", out _));

            using var cancel = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/run/{Uri.EscapeDataString(runId)}/cancel",
                new { });
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            using var cancelBody = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync());
            Assert.Equal(runId, cancelBody.RootElement.GetProperty("run").GetProperty("runId").GetString());
            Assert.False(cancelBody.RootElement.GetProperty("run").TryGetProperty("capabilityToken", out _));
        }
        finally
        {
            cancellation.Cancel();
            broker.Dispose();
            await brokerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AgentRequest_WorkbenchApprovalDeliversGrantThroughRestrictedSessionStatus()
    {
        var brokerPort = FreePort();
        var agentPort = FreePort();
        var previewFlags = PreviewTestFeatures.AllEnabled();
        var hostApprovalToken = Guid.NewGuid().ToString("N");
        using var broker = new BrokerServer(
            brokerPort,
            TimeSpan.FromMinutes(1),
            previewFlags,
            trustedHostApprovalVerifier: supplied =>
                string.Equals(supplied, hostApprovalToken, StringComparison.Ordinal));
        using var cancellation = new CancellationTokenSource();
        var brokerTask = broker.RunAsync(cancellation.Token);
        await WaitForBrokerAsync(brokerPort);

        using var agent = new AgentHttpServer(agentPort);
        agent.MapGet("/api/v1/agent/status", _ => Task.FromResult(HttpResponse.Json(new
        {
            running = true,
            agent = new { name = "DevFlow", instanceId = "approval-instance", version = "1" },
            app = new { name = "Approval Test", build = "build-approval", packageId = "com.example.approval", version = "1.0" },
            device = new { platform = "android", deviceType = "emulator", idiom = "phone" },
            capabilities = new { ui = true, mutations = true },
            route = "/todos",
        })));
        agent.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{brokerPort}/ws/agent"), CancellationToken.None);
        await SendAsync(socket, $$"""
            {"type":"register","project":"workbench-approval-test","tfm":"net10.0-android","platform":"android","appName":"Approval Test","currentPort":{{agentPort}}}
            """);
        await ReceiveAsync(socket);

        try
        {
            using var http = new HttpClient();
            using var agents = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{brokerPort}/api/agents"));
            var registration = agents.RootElement[0];
            var agentId = registration.GetProperty("id").GetString()!;
            var instanceId = registration.GetProperty("instanceId").GetString()!;
            var target = new MauiTestAgentTarget
            {
                AgentId = agentId,
                AgentInstanceId = instanceId,
            };
            var provenance = new
            {
                actorKind = "agent",
                actorId = "approval-route-agent",
                channel = "mcp",
                provider = "host-owned",
            };

            using var beginResponse = await PostJsonAsync(
                http,
                $"http://127.0.0.1:{brokerPort}/api/test-agent/sessions/begin",
                new
                {
                    envelope = new
                    {
                        schema = 1,
                        requestId = "begin-approval-route",
                        idempotencyKey = "begin-approval-route",
                        target,
                        correlation = new { },
                        provenance,
                        intent = "Add one disposable todo",
                        deadlineMs = 30_000,
                        policyVersion = MauiTestAgentProtocolVersions.PolicyVersion,
                    },
                    targetState = new { agentId, agentInstanceId = instanceId, seedFingerprint = "seed-a" },
                });
            Assert.Equal(HttpStatusCode.OK, beginResponse.StatusCode);
            using var begin = JsonDocument.Parse(await beginResponse.Content.ReadAsStringAsync());
            var snapshot = begin.RootElement.GetProperty("snapshot");
            target.AppBuildFingerprint = snapshot.GetProperty("target")
                .GetProperty("appBuildFingerprint")
                .GetString();
            Assert.True(
                !snapshot.GetProperty("target").TryGetProperty("seedFingerprint", out var echoedTargetSeed) ||
                echoedTargetSeed.ValueKind == JsonValueKind.Null);
            Assert.True(
                !snapshot.GetProperty("targetState").TryGetProperty("seedFingerprint", out var echoedStateSeed) ||
                echoedStateSeed.ValueKind == JsonValueKind.Null);
            var sessionId = snapshot.GetProperty("sessionId").GetString()!;
            var readCapabilityId = snapshot.GetProperty("readCapabilityId").GetString()!;
            var plan = snapshot.GetProperty("plan");
            var correlation = new
            {
                authoringSessionId = sessionId,
                planId = plan.GetProperty("planId").GetString(),
                planRevision = plan.GetProperty("revision").GetInt32(),
                planDigest = snapshot.GetProperty("planDigest").GetString(),
                flowId = plan.GetProperty("flow").GetProperty("flowId").GetString(),
                flowRevision = snapshot.GetProperty("flowRevision").GetInt32(),
                flowDigest = snapshot.GetProperty("flowDigest").GetString(),
            };

            using var requestResponse = await PostJsonAsync(
                http,
                $"http://127.0.0.1:{brokerPort}/api/test-agent/approvals/request",
                new
                {
                    envelope = new
                    {
                        schema = 1,
                        requestId = "request-approval-route",
                        idempotencyKey = "request-approval-route",
                        target,
                        correlation,
                        provenance,
                        intent = "Fill NewTodoEntry, then tap AddButton",
                        readCapabilityId,
                        deadlineMs = 30_000,
                        policyVersion = MauiTestAgentProtocolVersions.PolicyVersion,
                    },
                    kind = MauiTestAgentApprovalKinds.DraftChange,
                    scope = new
                    {
                        allowedActions = new[] { MauiTestAgentActions.Fill, MauiTestAgentActions.Tap },
                        allowedSelectors = new[] { "automationId:NewTodoEntry", "automationId:AddButton" },
                        allowedSideEffectClasses = new[] { "ui" },
                        maxActionCount = 2,
                        maxValueBytes = 64,
                    },
                });
            Assert.Equal(HttpStatusCode.OK, requestResponse.StatusCode);
            using var submitted = JsonDocument.Parse(await requestResponse.Content.ReadAsStringAsync());
            var approvalRequestId = submitted.RootElement.GetProperty("request").GetProperty("approvalRequestId").GetString()!;

            var inspectorBase = $"http://127.0.0.1:{brokerPort}/inspector/{Uri.EscapeDataString(agentId)}";
            var denied = await http.GetAsync($"{inspectorBase}/api/workbench/agent-requests");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            var inspector = await GetInspectorAsync(broker, agentId);
            var inspectorToken = (string)typeof(InspectorServer)
                .GetField("_readToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(inspector)!;
            http.DefaultRequestHeaders.Add("X-DevFlow-Inspector-Token", inspectorToken);

            using var inbox = JsonDocument.Parse(await http.GetStringAsync($"{inspectorBase}/api/workbench/agent-requests"));
            var inboxRequest = Assert.Single(inbox.RootElement.GetProperty("requests").EnumerateArray());
            Assert.Equal(MauiTestAgentApprovalStates.Pending, inboxRequest.GetProperty("state").GetString());
            Assert.True(
                !inboxRequest.TryGetProperty("grantId", out var hiddenGrant) ||
                hiddenGrant.ValueKind == JsonValueKind.Null);

            using var approveResponse = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-requests/{Uri.EscapeDataString(approvalRequestId)}/approve",
                new
                {
                    humanConfirmed = true,
                    approvedScope = new
                    {
                        allowedActions = new[] { MauiTestAgentActions.Tap },
                        allowedSelectors = new[] { "automationId:AddButton" },
                        allowedSideEffectClasses = new[] { "ui" },
                        maxActionCount = 1,
                        maxValueBytes = 0,
                    },
                    grantDurationSeconds = 60,
                });
            Assert.Equal(HttpStatusCode.Forbidden, approveResponse.StatusCode);

            var approvedScope = new
            {
                allowedActions = new[] { MauiTestAgentActions.Tap },
                allowedSelectors = new[] { "automationId:AddButton" },
                allowedSideEffectClasses = new[] { "ui" },
                maxActionCount = 1,
                maxValueBytes = 0,
            };
            using var untrustedConfirmation = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/approval-confirmations/issue",
                new
                {
                    action = "agent-request-approve",
                    subjectId = approvalRequestId,
                    approvedScope,
                    grantDurationSeconds = 60,
                });
            Assert.Equal(HttpStatusCode.Forbidden, untrustedConfirmation.StatusCode);

            using var confirmationResponse = await PostTrustedJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/approval-confirmations/issue",
                hostApprovalToken,
                new
                {
                    action = "agent-request-approve",
                    subjectId = approvalRequestId,
                    approvedScope,
                    grantDurationSeconds = 60,
                });
            Assert.Equal(HttpStatusCode.Created, confirmationResponse.StatusCode);
            using var confirmation = JsonDocument.Parse(await confirmationResponse.Content.ReadAsStringAsync());
            var mismatchedConfirmationCapability = confirmation.RootElement
                .GetProperty("confirmationCapability")
                .GetString()!;

            using var mismatchedApproval = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-requests/{Uri.EscapeDataString(approvalRequestId)}/approve",
                new
                {
                    confirmationCapability = mismatchedConfirmationCapability,
                    approvedScope = new
                    {
                        allowedActions = new[] { MauiTestAgentActions.Tap },
                        allowedSelectors = new[] { "automationId:AddButton" },
                        allowedSideEffectClasses = new[] { "ui" },
                        maxActionCount = 1,
                        maxValueBytes = 1,
                    },
                    grantDurationSeconds = 60,
                });
            Assert.Equal(HttpStatusCode.Forbidden, mismatchedApproval.StatusCode);

            using var exactConfirmationResponse = await PostTrustedJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/approval-confirmations/issue",
                hostApprovalToken,
                new
                {
                    action = "agent-request-approve",
                    subjectId = approvalRequestId,
                    approvedScope,
                    grantDurationSeconds = 60,
                });
            Assert.Equal(HttpStatusCode.Created, exactConfirmationResponse.StatusCode);
            using var exactConfirmation = JsonDocument.Parse(
                await exactConfirmationResponse.Content.ReadAsStringAsync());
            var confirmationCapability = exactConfirmation.RootElement
                .GetProperty("confirmationCapability")
                .GetString()!;

            using var confirmedApproveResponse = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-requests/{Uri.EscapeDataString(approvalRequestId)}/approve",
                new
                {
                    humanConfirmed = true,
                    confirmationCapability,
                    approvedScope,
                    grantDurationSeconds = 60,
                });
            Assert.Equal(HttpStatusCode.OK, confirmedApproveResponse.StatusCode);
            using var approved = JsonDocument.Parse(await confirmedApproveResponse.Content.ReadAsStringAsync());
            Assert.True(approved.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(
                !approved.RootElement.GetProperty("request").TryGetProperty("grantId", out var responseGrant) ||
                responseGrant.ValueKind == JsonValueKind.Null);

            using var reusedConfirmation = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-requests/{Uri.EscapeDataString(approvalRequestId)}/approve",
                new
                {
                    confirmationCapability,
                    approvedScope,
                    grantDurationSeconds = 60,
                });
            Assert.Equal(HttpStatusCode.Forbidden, reusedConfirmation.StatusCode);

            using var statusResponse = await PostJsonAsync(
                http,
                $"http://127.0.0.1:{brokerPort}/api/test-agent/sessions/status",
                new
                {
                    sessionId,
                    readCapabilityId,
                    envelope = new
                    {
                        requestId = "status-approved-request",
                        idempotencyKey = "status-approved-request",
                        target,
                        correlation,
                        provenance,
                        intent = "Read the approved request.",
                        readCapabilityId,
                        policyVersion = MauiTestAgentProtocolVersions.PolicyVersion,
                    },
                });
            using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            var delivered = Assert.Single(status.RootElement
                .GetProperty("snapshot")
                .GetProperty("approvalRequests")
                .EnumerateArray());
            Assert.Equal(MauiTestAgentApprovalStates.Approved, delivered.GetProperty("state").GetString());
            Assert.False(string.IsNullOrWhiteSpace(delivered.GetProperty("grantId").GetString()));
            Assert.Single(delivered.GetProperty("approvedScope").GetProperty("allowedActions").EnumerateArray());

            using var pendingAfterKillResponse = await PostJsonAsync(
                http,
                $"http://127.0.0.1:{brokerPort}/api/test-agent/approvals/request",
                new
                {
                    envelope = new
                    {
                        schema = 1,
                        requestId = "request-approval-after-kill",
                        idempotencyKey = "request-approval-after-kill",
                        target,
                        correlation,
                        provenance,
                        intent = "Tap AddButton after review",
                        readCapabilityId,
                        deadlineMs = 30_000,
                        policyVersion = MauiTestAgentProtocolVersions.PolicyVersion,
                    },
                    kind = MauiTestAgentApprovalKinds.DraftChange,
                    scope = new
                    {
                        allowedActions = new[] { MauiTestAgentActions.Tap },
                        allowedSelectors = new[] { "automationId:AddButton" },
                        allowedSideEffectClasses = new[] { "ui" },
                        maxActionCount = 1,
                        maxValueBytes = 0,
                    },
                });
            Assert.Equal(HttpStatusCode.OK, pendingAfterKillResponse.StatusCode);
            using var pendingAfterKill = JsonDocument.Parse(
                await pendingAfterKillResponse.Content.ReadAsStringAsync());
            var pendingAfterKillId = pendingAfterKill.RootElement
                .GetProperty("request")
                .GetProperty("approvalRequestId")
                .GetString()!;

            previewFlags.KillSwitches.Add("agent-authoring");

            using var disabledBegin = await PostJsonAsync(
                http,
                $"http://127.0.0.1:{brokerPort}/api/test-agent/sessions/begin",
                new { });
            Assert.Equal(HttpStatusCode.NotFound, disabledBegin.StatusCode);
            Assert.Contains(
                "agent-authoring",
                await disabledBegin.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);

            using var disabledGrant = await PostTrustedJsonAsync(
                http,
                $"http://127.0.0.1:{brokerPort}/api/test-agent/grants/issue",
                hostApprovalToken,
                new { });
            Assert.Equal(HttpStatusCode.NotFound, disabledGrant.StatusCode);

            using var safeStatus = await PostJsonAsync(
                http,
                $"http://127.0.0.1:{brokerPort}/api/test-agent/sessions/status",
                new
                {
                    sessionId,
                    readCapabilityId,
                    envelope = new
                    {
                        requestId = "status-after-kill-switch",
                        idempotencyKey = "status-after-kill-switch",
                        target,
                        correlation,
                        provenance,
                        intent = "Read the retained request after the kill switch.",
                        readCapabilityId,
                        policyVersion = MauiTestAgentProtocolVersions.PolicyVersion,
                    },
                });
            Assert.Equal(HttpStatusCode.OK, safeStatus.StatusCode);

            using var killedInbox = JsonDocument.Parse(
                await http.GetStringAsync($"{inspectorBase}/api/workbench/agent-requests"));
            Assert.Contains(
                killedInbox.RootElement.GetProperty("requests").EnumerateArray(),
                request => string.Equals(
                    request.GetProperty("approvalRequestId").GetString(),
                    pendingAfterKillId,
                    StringComparison.Ordinal) &&
                    string.Equals(
                        request.GetProperty("state").GetString(),
                        MauiTestAgentApprovalStates.Pending,
                        StringComparison.Ordinal));

            using var disabledApproval = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-requests/{Uri.EscapeDataString(pendingAfterKillId)}/approve",
                new { humanConfirmed = true });
            Assert.Equal(HttpStatusCode.NotFound, disabledApproval.StatusCode);

            using var disabledConfirmation = await PostTrustedJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/approval-confirmations/issue",
                hostApprovalToken,
                new
                {
                    action = "agent-request-approve",
                    subjectId = pendingAfterKillId,
                    approvedScope,
                    grantDurationSeconds = 60,
                });
            Assert.Equal(HttpStatusCode.NotFound, disabledConfirmation.StatusCode);

            using var safeReject = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-requests/{Uri.EscapeDataString(pendingAfterKillId)}/reject",
                new
                {
                    humanConfirmed = true,
                    reasonCode = "feature-killed",
                });
            Assert.Equal(HttpStatusCode.OK, safeReject.StatusCode);
        }
        finally
        {
            cancellation.Cancel();
            broker.Dispose();
            await brokerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AgentRequest_NativeHostReviewsAndDecidesWithoutTheBrowserReadToken()
    {
        var brokerPort = FreePort();
        var agentPort = FreePort();
        var previewFlags = PreviewTestFeatures.AllEnabled();
        var hostApprovalToken = Guid.NewGuid().ToString("N");
        using var broker = new BrokerServer(
            brokerPort,
            TimeSpan.FromMinutes(1),
            previewFlags,
            trustedHostApprovalVerifier: supplied =>
                string.Equals(supplied, hostApprovalToken, StringComparison.Ordinal));
        using var cancellation = new CancellationTokenSource();
        var brokerTask = broker.RunAsync(cancellation.Token);
        await WaitForBrokerAsync(brokerPort);

        using var agent = new AgentHttpServer(agentPort);
        agent.MapGet("/api/v1/agent/status", _ => Task.FromResult(HttpResponse.Json(new
        {
            running = true,
            agent = new { name = "DevFlow", instanceId = "host-approval-instance", version = "1" },
            app = new { name = "Host Approval Test", build = "build-host", packageId = "com.example.host", version = "1.0" },
            device = new { platform = "android", deviceType = "emulator", idiom = "phone" },
            capabilities = new { ui = true, mutations = true },
            route = "/todos",
        })));
        agent.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{brokerPort}/ws/agent"), CancellationToken.None);
        await SendAsync(socket, $$"""
            {"type":"register","project":"host-approval-test","tfm":"net10.0-android","platform":"android","appName":"Host Approval Test","currentPort":{{agentPort}}}
            """);
        await ReceiveAsync(socket);

        try
        {
            using var http = new HttpClient();
            using var agents = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{brokerPort}/api/agents"));
            var registration = agents.RootElement[0];
            var agentId = registration.GetProperty("id").GetString()!;
            var instanceId = registration.GetProperty("instanceId").GetString()!;
            var target = new MauiTestAgentTarget
            {
                AgentId = agentId,
                AgentInstanceId = instanceId,
            };
            var provenance = new
            {
                actorKind = "agent",
                actorId = "host-approval-agent",
                channel = "mcp",
                provider = "host-owned",
            };

            using var beginResponse = await PostJsonAsync(
                http,
                $"http://127.0.0.1:{brokerPort}/api/test-agent/sessions/begin",
                new
                {
                    envelope = new
                    {
                        schema = 1,
                        requestId = "begin-host-approval",
                        idempotencyKey = "begin-host-approval",
                        target,
                        correlation = new { },
                        provenance,
                        intent = "Add one disposable todo",
                        deadlineMs = 30_000,
                        policyVersion = MauiTestAgentProtocolVersions.PolicyVersion,
                    },
                    targetState = new { agentId, agentInstanceId = instanceId, seedFingerprint = "seed-host" },
                });
            Assert.Equal(HttpStatusCode.OK, beginResponse.StatusCode);
            using var begin = JsonDocument.Parse(await beginResponse.Content.ReadAsStringAsync());
            var snapshot = begin.RootElement.GetProperty("snapshot");
            target.AppBuildFingerprint = snapshot.GetProperty("target")
                .GetProperty("appBuildFingerprint")
                .GetString();
            var sessionId = snapshot.GetProperty("sessionId").GetString()!;
            var readCapabilityId = snapshot.GetProperty("readCapabilityId").GetString()!;
            var plan = snapshot.GetProperty("plan");
            var correlation = new
            {
                authoringSessionId = sessionId,
                planId = plan.GetProperty("planId").GetString(),
                planRevision = plan.GetProperty("revision").GetInt32(),
                planDigest = snapshot.GetProperty("planDigest").GetString(),
                flowId = plan.GetProperty("flow").GetProperty("flowId").GetString(),
                flowRevision = snapshot.GetProperty("flowRevision").GetInt32(),
                flowDigest = snapshot.GetProperty("flowDigest").GetString(),
            };

            var approvedScope = new
            {
                allowedActions = new[] { MauiTestAgentActions.Tap },
                allowedSelectors = new[] { "automationId:AddButton" },
                allowedSideEffectClasses = new[] { "ui" },
                maxActionCount = 1,
                maxValueBytes = 0,
            };

            async Task<string> SubmitAsync(string key)
            {
                using var response = await PostJsonAsync(
                    http,
                    $"http://127.0.0.1:{brokerPort}/api/test-agent/approvals/request",
                    new
                    {
                        envelope = new
                        {
                            schema = 1,
                            requestId = key,
                            idempotencyKey = key,
                            target,
                            correlation,
                            provenance,
                            intent = "Tap AddButton after review",
                            readCapabilityId,
                            deadlineMs = 30_000,
                            policyVersion = MauiTestAgentProtocolVersions.PolicyVersion,
                        },
                        kind = MauiTestAgentApprovalKinds.DraftChange,
                        scope = approvedScope,
                    });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var submitted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return submitted.RootElement.GetProperty("request").GetProperty("approvalRequestId").GetString()!;
            }

            var approveId = await SubmitAsync("request-host-approve");
            var rejectId = await SubmitAsync("request-host-reject");

            var inspectorBase = $"http://127.0.0.1:{brokerPort}/inspector/{Uri.EscapeDataString(agentId)}";

            // Without any credential the browser-facing gate still refuses every workbench route.
            using var deniedList = await http.GetAsync($"{inspectorBase}/api/workbench/agent-requests");
            Assert.Equal(HttpStatusCode.Forbidden, deniedList.StatusCode);
            using var deniedReject = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-requests/{Uri.EscapeDataString(rejectId)}/reject",
                new { humanConfirmed = true, reasonCode = "no-credential" });
            Assert.Equal(HttpStatusCode.Forbidden, deniedReject.StatusCode);

            // A wrong host token is not a credential either.
            using var wrongTokenList = await GetTrustedAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-requests",
                "not-the-host-token");
            Assert.Equal(HttpStatusCode.Forbidden, wrongTokenList.StatusCode);

            // The native host reviews with its own token and never needs the per-process read token.
            using var listResponse = await GetTrustedAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-requests",
                hostApprovalToken);
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            using var listing = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
            Assert.Equal(2, listing.RootElement.GetProperty("pendingCount").GetInt32());
            Assert.False(listing.RootElement.GetProperty("browserApprovalAvailable").GetBoolean());

            using var confirmationResponse = await PostTrustedJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/approval-confirmations/issue",
                hostApprovalToken,
                new
                {
                    action = "agent-request-approve",
                    subjectId = approveId,
                    approvedScope,
                    grantDurationSeconds = 60,
                });
            Assert.Equal(HttpStatusCode.Created, confirmationResponse.StatusCode);
            using var confirmation = JsonDocument.Parse(await confirmationResponse.Content.ReadAsStringAsync());
            var confirmationCapability = confirmation.RootElement.GetProperty("confirmationCapability").GetString()!;

            using var approveResponse = await PostTrustedJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-requests/{Uri.EscapeDataString(approveId)}/approve",
                hostApprovalToken,
                new
                {
                    humanConfirmed = true,
                    confirmationCapability,
                    approvedScope,
                    grantDurationSeconds = 60,
                    decidedBy = new { actorId = "maui-cli-operator", channel = "cli", provider = "maui-cli" },
                });
            Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
            using var approved = JsonDocument.Parse(await approveResponse.Content.ReadAsStringAsync());
            Assert.True(approved.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("cli/maui-cli", approved.RootElement.GetProperty("request").GetProperty("decidedBy").GetString());

            using var rejectResponse = await PostTrustedJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-requests/{Uri.EscapeDataString(rejectId)}/reject",
                hostApprovalToken,
                new
                {
                    humanConfirmed = true,
                    reasonCode = "host-rejected",
                    decidedBy = new { actorId = "maui-cli-operator", channel = "cli", provider = "maui-cli" },
                });
            Assert.Equal(HttpStatusCode.OK, rejectResponse.StatusCode);
            using var rejected = JsonDocument.Parse(await rejectResponse.Content.ReadAsStringAsync());
            Assert.Equal("cli/maui-cli", rejected.RootElement.GetProperty("request").GetProperty("decidedBy").GetString());

            // The grant really was delivered through the restricted session status, so the CLI
            // ceremony authorizes exactly what the Workbench ceremony authorizes.
            using var statusResponse = await PostJsonAsync(
                http,
                $"http://127.0.0.1:{brokerPort}/api/test-agent/sessions/status",
                new
                {
                    sessionId,
                    readCapabilityId,
                    envelope = new
                    {
                        requestId = "status-host-approved",
                        idempotencyKey = "status-host-approved",
                        target,
                        correlation,
                        provenance,
                        intent = "Read the host-approved request.",
                        readCapabilityId,
                        policyVersion = MauiTestAgentProtocolVersions.PolicyVersion,
                    },
                });
            using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            var delivered = status.RootElement
                .GetProperty("snapshot")
                .GetProperty("approvalRequests")
                .EnumerateArray()
                .Single(request => string.Equals(
                    request.GetProperty("approvalRequestId").GetString(),
                    approveId,
                    StringComparison.Ordinal));
            Assert.Equal(MauiTestAgentApprovalStates.Approved, delivered.GetProperty("state").GetString());
            Assert.False(string.IsNullOrWhiteSpace(delivered.GetProperty("grantId").GetString()));
            Assert.Equal("cli/maui-cli", delivered.GetProperty("decidedBy").GetString());
        }
        finally
        {
            cancellation.Cancel();
            broker.Dispose();
            await brokerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task WorkbenchDecision_WithoutHostToken_StaysOnTheWorkbenchChannel()
    {
        var brokerPort = FreePort();
        var agentPort = FreePort();
        var previewFlags = PreviewTestFeatures.AllEnabled();
        var hostApprovalToken = Guid.NewGuid().ToString("N");
        using var broker = new BrokerServer(
            brokerPort,
            TimeSpan.FromMinutes(1),
            previewFlags,
            trustedHostApprovalVerifier: supplied =>
                string.Equals(supplied, hostApprovalToken, StringComparison.Ordinal));
        using var cancellation = new CancellationTokenSource();
        var brokerTask = broker.RunAsync(cancellation.Token);
        await WaitForBrokerAsync(brokerPort);

        using var agent = new AgentHttpServer(agentPort);
        agent.MapGet("/api/v1/agent/status", _ => Task.FromResult(HttpResponse.Json(new
        {
            running = true,
            agent = new { name = "DevFlow", instanceId = "workbench-channel-instance", version = "1" },
            app = new { name = "Workbench Channel Test", build = "build-wb", packageId = "com.example.wb", version = "1.0" },
            device = new { platform = "android", deviceType = "emulator", idiom = "phone" },
            capabilities = new { ui = true, mutations = true },
            route = "/todos",
        })));
        agent.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{brokerPort}/ws/agent"), CancellationToken.None);
        await SendAsync(socket, $$"""
            {"type":"register","project":"workbench-channel-test","tfm":"net10.0-android","platform":"android","appName":"Workbench Channel Test","currentPort":{{agentPort}}}
            """);
        await ReceiveAsync(socket);

        try
        {
            using var http = new HttpClient();
            using var agents = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{brokerPort}/api/agents"));
            var registration = agents.RootElement[0];
            var agentId = registration.GetProperty("id").GetString()!;
            var instanceId = registration.GetProperty("instanceId").GetString()!;
            var target = new MauiTestAgentTarget
            {
                AgentId = agentId,
                AgentInstanceId = instanceId,
            };
            var provenance = new
            {
                actorKind = "agent",
                actorId = "workbench-channel-agent",
                channel = "mcp",
                provider = "host-owned",
            };

            using var beginResponse = await PostJsonAsync(
                http,
                $"http://127.0.0.1:{brokerPort}/api/test-agent/sessions/begin",
                new
                {
                    envelope = new
                    {
                        schema = 1,
                        requestId = "begin-workbench-channel",
                        idempotencyKey = "begin-workbench-channel",
                        target,
                        correlation = new { },
                        provenance,
                        intent = "Add one disposable todo",
                        deadlineMs = 30_000,
                        policyVersion = MauiTestAgentProtocolVersions.PolicyVersion,
                    },
                    targetState = new { agentId, agentInstanceId = instanceId, seedFingerprint = "seed-wb" },
                });
            using var begin = JsonDocument.Parse(await beginResponse.Content.ReadAsStringAsync());
            var snapshot = begin.RootElement.GetProperty("snapshot");
            target.AppBuildFingerprint = snapshot.GetProperty("target")
                .GetProperty("appBuildFingerprint")
                .GetString();
            var sessionId = snapshot.GetProperty("sessionId").GetString()!;
            var readCapabilityId = snapshot.GetProperty("readCapabilityId").GetString()!;
            var plan = snapshot.GetProperty("plan");
            var correlation = new
            {
                authoringSessionId = sessionId,
                planId = plan.GetProperty("planId").GetString(),
                planRevision = plan.GetProperty("revision").GetInt32(),
                planDigest = snapshot.GetProperty("planDigest").GetString(),
                flowId = plan.GetProperty("flow").GetProperty("flowId").GetString(),
                flowRevision = snapshot.GetProperty("flowRevision").GetInt32(),
                flowDigest = snapshot.GetProperty("flowDigest").GetString(),
            };

            using var requestResponse = await PostJsonAsync(
                http,
                $"http://127.0.0.1:{brokerPort}/api/test-agent/approvals/request",
                new
                {
                    envelope = new
                    {
                        schema = 1,
                        requestId = "request-workbench-channel",
                        idempotencyKey = "request-workbench-channel",
                        target,
                        correlation,
                        provenance,
                        intent = "Tap AddButton after review",
                        readCapabilityId,
                        deadlineMs = 30_000,
                        policyVersion = MauiTestAgentProtocolVersions.PolicyVersion,
                    },
                    kind = MauiTestAgentApprovalKinds.DraftChange,
                    scope = new
                    {
                        allowedActions = new[] { MauiTestAgentActions.Tap },
                        allowedSelectors = new[] { "automationId:AddButton" },
                        allowedSideEffectClasses = new[] { "ui" },
                        maxActionCount = 1,
                        maxValueBytes = 0,
                    },
                });
            using var submitted = JsonDocument.Parse(await requestResponse.Content.ReadAsStringAsync());
            var approvalRequestId = submitted.RootElement
                .GetProperty("request")
                .GetProperty("approvalRequestId")
                .GetString()!;

            var inspectorBase = $"http://127.0.0.1:{brokerPort}/inspector/{Uri.EscapeDataString(agentId)}";
            // The broker creates an Inspector lazily on the first request routed to it.
            using var priming = await http.GetAsync($"{inspectorBase}/api/workbench/agent-requests");
            Assert.Equal(HttpStatusCode.Forbidden, priming.StatusCode);
            var inspector = await GetInspectorAsync(broker, agentId);
            var inspectorToken = (string)typeof(InspectorServer)
                .GetField("_readToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(inspector)!;
            http.DefaultRequestHeaders.Add("X-DevFlow-Inspector-Token", inspectorToken);

            // A browser caller cannot claim host provenance: it holds only the read token, so the
            // decision is recorded on the workbench channel no matter what it sends.
            using var rejectResponse = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/agent-requests/{Uri.EscapeDataString(approvalRequestId)}/reject",
                new
                {
                    humanConfirmed = true,
                    reasonCode = "not-now",
                    decidedBy = new { actorId = "spoofed", channel = "cli", provider = "maui-cli" },
                });
            Assert.Equal(HttpStatusCode.OK, rejectResponse.StatusCode);
            using var rejected = JsonDocument.Parse(await rejectResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                "workbench/inspector-server",
                rejected.RootElement.GetProperty("request").GetProperty("decidedBy").GetString());
        }
        finally
        {
            cancellation.Cancel();
            broker.Dispose();
            await brokerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task WorkbenchRepairValidate_ReportsItselfUnavailableWhileNoLifecycleAttesterExists()
    {
        var brokerPort = FreePort();
        var agentPort = FreePort();
        using var broker = new BrokerServer(
            brokerPort,
            TimeSpan.FromMinutes(1),
            previewFlags: PreviewTestFeatures.AllEnabled());
        using var cancellation = new CancellationTokenSource();
        var brokerTask = broker.RunAsync(cancellation.Token);
        await WaitForBrokerAsync(brokerPort);

        using var agent = new AgentHttpServer(agentPort);
        agent.MapGet("/api/v1/agent/status", _ => Task.FromResult(HttpResponse.Json(new
        {
            running = true,
            agent = new { name = "DevFlow", instanceId = "repair-instance", version = "1" },
            app = new { name = "Repair Test", build = "build-repair", packageId = "com.example.repair", version = "1.0" },
            device = new { platform = "android", deviceType = "emulator", idiom = "phone" },
            capabilities = new { ui = true, logs = true, mutations = true },
            route = "/checkout",
            window = "main",
            modal = "none",
            locale = "en-US",
            theme = "light",
            orientation = "portrait",
            displayProfile = "phone",
        })));
        agent.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{brokerPort}/ws/agent"), CancellationToken.None);
        await SendAsync(socket, $$"""
            {"type":"register","project":"workbench-repair-test","tfm":"net10.0","platform":"android","appName":"Repair Test","currentPort":{{agentPort}}}
            """);
        await ReceiveAsync(socket);

        try
        {
            using var http = new HttpClient();
            using var agents = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{brokerPort}/api/agents"));
            var agentId = agents.RootElement[0].GetProperty("id").GetString()!;
            var inspectorBase = $"http://127.0.0.1:{brokerPort}/inspector/{Uri.EscapeDataString(agentId)}";

            _ = await http.GetAsync($"{inspectorBase}/");
            var inspector = await GetInspectorAsync(broker, agentId);
            var inspectorToken = (string)typeof(InspectorServer)
                .GetField("_readToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(inspector)!;
            http.DefaultRequestHeaders.Add("X-DevFlow-Inspector-Token", inspectorToken);

            using var target = JsonDocument.Parse(await http.GetStringAsync($"{inspectorBase}/api/workbench/target"));
            Assert.False(target.RootElement.GetProperty("broker")
                .GetProperty("repairValidationAvailable").GetBoolean());

            using var validate = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/repair/{Uri.EscapeDataString("missing-proposal")}/validate",
                new { validationGrant = "not-a-grant" });
            using var validateBody = JsonDocument.Parse(await validate.Content.ReadAsStringAsync());

            // No component can attest a hard reset, so the broker supplies no lifecycle host and the
            // workbench keeps saying so. Advertising availability here would promise a validation
            // that structurally cannot pass.
            Assert.Equal(503, (int)validate.StatusCode);
            Assert.True(validateBody.RootElement.GetProperty("hostFallback").GetBoolean());
            Assert.False(validateBody.RootElement.GetProperty("ok").GetBoolean());
        }
        finally
        {
            cancellation.Cancel();
            broker.Dispose();
            await brokerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static MauiFlow AssertOnlyFlow() => new()
    {
        Name = "inspector-workbench",
        Steps =
        {
            new FlowStep
            {
                Seq = 1,
                Action = FlowActions.Assert,
                Asserts = new()
                {
                    new FlowAssert
                    {
                        Kind = "exists",
                        Verify = false,
                        Selector = new FlowSelector { AutomationId = "label" },
                    },
                },
            },
        },
    };

    private static MauiFlow FailingTapFlow() => new()
    {
        Name = "inspector-workbench-failure",
        Steps =
        {
            new FlowStep
            {
                Seq = 1,
                Action = FlowActions.Tap,
                Target = new FlowSelector { AutomationId = "missing-control" },
            },
        },
    };

    private static string FlowRunJson(string secret)
        => $$"""
            {
              "schema": 1,
              "runId": "foreign-run",
              "flowDigest": "flow",
              "outcome": { "status": "failed" },
              "failure": {
                "class": "assertion-failed",
                "code": "assertion-failed",
                "message": "{{secret}}"
              },
              "steps": [],
              "events": [],
              "artifacts": []
            }
            """;

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, object value)
        => await client.PostAsync(url, new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"));

    private static async Task<HttpResponseMessage> PostTrustedJsonAsync(
        HttpClient client,
        string url,
        string hostApprovalToken,
        object value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("X-DevFlow-Host-Approval-Token", hostApprovalToken);
        request.Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetTrustedAsync(
        HttpClient client,
        string url,
        string hostApprovalToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-DevFlow-Host-Approval-Token", hostApprovalToken);
        return await client.SendAsync(request);
    }

    private static async Task<InspectorServer> GetInspectorAsync(BrokerServer broker, string agentId)
    {
        var field = typeof(BrokerServer).GetField("_inspectors", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var inspectors = (IReadOnlyDictionary<string, InspectorServer>)field.GetValue(broker)!;
        for (var attempt = 0; attempt < 40; attempt++)
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
        for (var attempt = 0; attempt < 40; attempt++)
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
        => await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

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
}
