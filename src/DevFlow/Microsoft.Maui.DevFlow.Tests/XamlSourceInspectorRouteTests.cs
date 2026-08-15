using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Inspector;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class XamlSourceInspectorRouteTests
{
    [Fact]
    public async Task SourceRoutes_RequireInspectorTokenSeparateApprovalAndExplicitCapableHost()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "xaml-source-route-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var projectPath = Path.Combine(root, "TestApp.csproj");
        var sourcePath = Path.Combine(root, "MainPage.xaml");
        const string xaml = """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
                <Button Text="Save" />
            </ContentPage>
            """;
        await File.WriteAllTextAsync(projectPath, "<Project />");
        await File.WriteAllTextAsync(sourcePath, xaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var brokerPort = FreePort();
        var agentPort = FreePort();
        var hostApprovalToken = Guid.NewGuid().ToString("N");
        using var broker = new BrokerServer(
            brokerPort,
            TimeSpan.FromMinutes(1),
            previewFlags: PreviewTestFeatures.AllEnabled(),
            trustedHostApprovalVerifier: supplied =>
                string.Equals(supplied, hostApprovalToken, StringComparison.Ordinal));
        using var cancellation = new CancellationTokenSource();
        var brokerTask = broker.RunAsync(cancellation.Token);
        await WaitForBrokerAsync(brokerPort);

        var (line, column) = LineColumn(xaml, "<Button");
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xaml)), 0, 8).ToLowerInvariant();
        using var agent = new AgentHttpServer(agentPort);
        agent.MapGet("/api/v1/agent/status", _ => Task.FromResult(HttpResponse.Json(new
        {
            running = true,
            agent = new { name = "DevFlow", instanceId = "source-route-instance", version = "1" },
            app = new { name = "Source Route Test", build = "build-1", packageId = "com.example.source", version = "1.0" },
            device = new { platform = "windows", deviceType = "desktop", idiom = "desktop" },
            capabilities = new { ui = true, mutations = true },
            route = "/home",
        })));
        var element = new
        {
            id = "save",
            type = "Button",
            fullType = "Microsoft.Maui.Controls.Button",
            framework = "maui",
            isVisible = true,
            isEnabled = true,
            sourceFile = sourcePath,
            sourceLine = line,
            sourceColumn = column,
            sourceHash,
            sourceConfidence = "mapped",
        };
        agent.MapGet("/api/v1/ui/elements/save", _ => Task.FromResult(HttpResponse.Json(element)));
        agent.MapGet("/api/v1/ui/tree", _ => Task.FromResult(HttpResponse.Json(new[]
        {
            new
            {
                id = "root",
                type = "ContentPage",
                framework = "maui",
                isVisible = true,
                isEnabled = true,
                children = new[] { element },
            },
        })));
        agent.Start();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{brokerPort}/ws/agent"), CancellationToken.None);
        await SendAsync(socket, $$"""
            {"type":"register","project":"{{projectPath.Replace("\\", "\\\\")}}","tfm":"net10.0-windows10.0.19041.0","platform":"windows","appName":"Source Route Test","currentPort":{{agentPort}}}
            """);
        await ReceiveAsync(socket);

        try
        {
            using var http = new HttpClient();
            using var agents = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{brokerPort}/api/agents"));
            var agentId = agents.RootElement[0].GetProperty("id").GetString()!;
            var inspectorBase = $"http://127.0.0.1:{brokerPort}/inspector/{Uri.EscapeDataString(agentId)}";

            using var denied = await PostJsonAsync(http, $"{inspectorBase}/api/workbench/source/analyze", new
            {
                elementId = "save",
                proposedAutomationId = "SaveButton",
            });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            _ = await http.GetAsync($"{inspectorBase}/");
            var inspector = await GetInspectorAsync(broker, agentId);
            var token = (string)typeof(InspectorServer)
                .GetField("_readToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(inspector)!;
            http.DefaultRequestHeaders.Add("X-DevFlow-Inspector-Token", token);

            using var analyzed = await PostJsonAsync(http, $"{inspectorBase}/api/workbench/source/analyze", new
            {
                elementId = "save",
                proposedAutomationId = "SaveButton",
            });
            Assert.Equal(HttpStatusCode.OK, analyzed.StatusCode);
            using var analyzeBody = JsonDocument.Parse(await analyzed.Content.ReadAsStringAsync());
            Assert.True(analyzeBody.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(analyzeBody.RootElement.GetProperty("eligibility").GetProperty("eligible").GetBoolean());

            using var proposed = await PostJsonAsync(http, $"{inspectorBase}/api/workbench/source/propose", new
            {
                elementId = "save",
                proposedAutomationId = "SaveButton",
            });
            Assert.Equal(HttpStatusCode.Created, proposed.StatusCode);
            using var proposalBody = JsonDocument.Parse(await proposed.Content.ReadAsStringAsync());
            var snapshot = proposalBody.RootElement.GetProperty("proposal");
            var proposalId = snapshot.GetProperty("proposal").GetProperty("proposalId").GetString()!;
            Assert.Equal("proposed", snapshot.GetProperty("state").GetString());

            using var preview = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/source/{Uri.EscapeDataString(proposalId)}/preview",
                new { });
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

            var host = new
            {
                hostKind = "browser",
                canOpenNativeDiff = false,
                canDownloadPatch = true,
                canApplySource = false,
                isExplicitLocalHostAction = false,
            };
            using var approval = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/source/{Uri.EscapeDataString(proposalId)}/approve",
                new
                {
                    reviewer = "route-test-human",
                    humanConfirmed = true,
                    hostCapability = host,
                });
            Assert.Equal(HttpStatusCode.Forbidden, approval.StatusCode);

            using var confirmation = await PostTrustedJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/approval-confirmations/issue",
                hostApprovalToken,
                new
                {
                    action = "xaml-source-grant",
                    subjectId = proposalId,
                    kind = "apply",
                    reviewer = "route-test-human",
                    hostCapability = host,
                });
            Assert.Equal(HttpStatusCode.Created, confirmation.StatusCode);
            using var confirmationBody = JsonDocument.Parse(await confirmation.Content.ReadAsStringAsync());
            var confirmationCapability = confirmationBody.RootElement
                .GetProperty("confirmationCapability")
                .GetString()!;

            using var confirmedApproval = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/source/{Uri.EscapeDataString(proposalId)}/approve",
                new
                {
                    reviewer = "route-test-human",
                    humanConfirmed = true,
                    confirmationCapability,
                    hostCapability = host,
                });
            Assert.Equal(HttpStatusCode.OK, confirmedApproval.StatusCode);
            using var approvalBody = JsonDocument.Parse(await confirmedApproval.Content.ReadAsStringAsync());
            var grant = approvalBody.RootElement.GetProperty("grant").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(grant));
            Assert.Equal("approved", approvalBody.RootElement.GetProperty("proposal").GetProperty("state").GetString());

            using var apply = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/source/{Uri.EscapeDataString(proposalId)}/apply",
                new
                {
                    approvalGrant = grant,
                    humanConfirmed = true,
                    hostCapability = host,
                });
            Assert.Equal(HttpStatusCode.Conflict, apply.StatusCode);
            Assert.Equal(xaml, await File.ReadAllTextAsync(sourcePath));

            using var applyResult = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/source/{Uri.EscapeDataString(proposalId)}/apply-result",
                new { });
            Assert.Equal(HttpStatusCode.Conflict, applyResult.StatusCode);
        }
        finally
        {
            cancellation.Cancel();
            broker.Dispose();
            await brokerTask.WaitAsync(TimeSpan.FromSeconds(5));
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

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

    private static int FreePort() => TestPorts.Reserve();

    private static (int Line, int Column) LineColumn(string source, string marker)
    {
        var offset = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0);
        var line = 1;
        var lineStart = 0;
        for (var index = 0; index < offset; index++)
        {
            if (source[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }
        return (line, offset - lineStart + 2);
    }
}
