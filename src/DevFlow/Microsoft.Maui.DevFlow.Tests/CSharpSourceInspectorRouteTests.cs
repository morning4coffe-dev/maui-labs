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

public sealed class CSharpSourceInspectorRouteTests
{
    [Fact]
    public async Task CSharpSourceRoutes_RequireIDEHandoffAcknowledgeExactHashesAndNeverBrokerWrite()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "csharp-source-route-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var projectPath = Path.Combine(root, "TestApp.csproj");
        var sourcePath = Path.Combine(root, "MainPage.cs");
        const string source = """
            using Microsoft.Maui.Controls;

            public class Page
            {
                void Build()
                {
                    var save = new Button { Text = "Save" };
                }
            }

            namespace Microsoft.Maui.Controls
            {
                public class Button
                {
                    public string AutomationId { get; set; } = "";
                    public string Text { get; set; } = "";
                }
            }
            """;
        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(sourcePath, source);

        var brokerPort = FreePort();
        var agentPort = FreePort();
        using var broker = new BrokerServer(brokerPort, TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        var brokerTask = broker.RunAsync(cancellation.Token);
        await WaitForBrokerAsync(brokerPort);

        var (line, column) = LineColumn(source, "new Button");
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)), 0, 8).ToLowerInvariant();
        using var agent = new AgentHttpServer(agentPort);
        agent.MapGet("/api/v1/agent/status", _ => Task.FromResult(HttpResponse.Json(new
        {
            running = true,
            agent = new { name = "DevFlow", instanceId = "csharp-source-route-instance", version = "1" },
            app = new { name = "C# Source Route Test", build = "build-1", packageId = "com.example.csharp", version = "1.0" },
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
            {"type":"register","project":"{{projectPath.Replace("\\", "\\\\")}}","tfm":"net10.0-windows10.0.19041.0","platform":"windows","appName":"C# Source Route Test","currentPort":{{agentPort}}}
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
            var token = (string)typeof(InspectorServer)
                .GetField("_readToken", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(inspector)!;
            http.DefaultRequestHeaders.Add("X-DevFlow-Inspector-Token", token);

            using var analyzed = await PostJsonAsync(http, $"{inspectorBase}/api/workbench/source/csharp/analyze", new
            {
                elementId = "save",
                proposedAutomationId = "SaveButton",
                sourceFile = sourcePath,
                sourceLine = line,
                sourceColumn = column,
                sourceHash,
                sourceConfidence = "roslyn-proven",
            });
            Assert.True(analyzed.StatusCode == HttpStatusCode.OK, await analyzed.Content.ReadAsStringAsync());
            using var analyzedBody = JsonDocument.Parse(await analyzed.Content.ReadAsStringAsync());
            Assert.True(analyzedBody.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("CSharp", analyzedBody.RootElement.GetProperty("language").GetString());
            Assert.True(analyzedBody.RootElement.GetProperty("eligibility").GetProperty("eligible").GetBoolean());

            using var proposed = await PostJsonAsync(http, $"{inspectorBase}/api/workbench/source/csharp/propose", new
            {
                elementId = "save",
                proposedAutomationId = "SaveButton",
                sourceFile = sourcePath,
                sourceLine = line,
                sourceColumn = column,
                sourceHash,
                sourceConfidence = "roslyn-proven",
            });
            Assert.Equal(HttpStatusCode.Created, proposed.StatusCode);
            using var proposedBody = JsonDocument.Parse(await proposed.Content.ReadAsStringAsync());
            var snapshot = proposedBody.RootElement.GetProperty("proposal");
            var proposal = snapshot.GetProperty("proposal");
            var proposalId = proposal.GetProperty("proposalId").GetString()!;
            var patchDigest = proposal.GetProperty("patchDigest").GetString()!;
            var baseDigest = proposal.GetProperty("baseContentDigest").GetString()!;
            var afterDigest = proposal.GetProperty("patch").GetProperty("afterDigest").GetString()!;

            using var preview = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/source/csharp/{Uri.EscapeDataString(proposalId)}/preview",
                new { });
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

            var canvas = new
            {
                hostKind = "copilot-canvas-ui",
                canOpenNativeDiff = false,
                canDownloadPatch = true,
                canApplyCSharpSource = false,
                isExplicitLocalHostAction = false,
            };
            var vscode = new
            {
                hostKind = "vscode",
                canOpenNativeDiff = true,
                canDownloadPatch = true,
                canApplyCSharpSource = true,
                isExplicitLocalHostAction = true,
            };
            using var approved = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/source/csharp/{Uri.EscapeDataString(proposalId)}/approve",
                new
                {
                    reviewer = "route-test-human",
                    humanConfirmed = true,
                    hostCapability = vscode,
                });
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            using var approvalBody = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
            var grant = approvalBody.RootElement.GetProperty("grant").GetString()!;

            using var canvasDenied = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/source/csharp/{Uri.EscapeDataString(proposalId)}/await-host-apply",
                new { hostCapability = canvas });
            Assert.Equal(HttpStatusCode.Conflict, canvasDenied.StatusCode);

            using var awaiting = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/source/csharp/{Uri.EscapeDataString(proposalId)}/await-host-apply",
                new { hostCapability = vscode });
            Assert.Equal(HttpStatusCode.OK, awaiting.StatusCode);

            using var begun = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/source/csharp/{Uri.EscapeDataString(proposalId)}/begin-host-apply",
                new
                {
                    approvalGrant = grant,
                    humanConfirmed = true,
                    hostCapability = vscode,
                });
            Assert.Equal(HttpStatusCode.OK, begun.StatusCode);

            using var acknowledged = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/source/csharp/{Uri.EscapeDataString(proposalId)}/apply-ack",
                new
                {
                    applied = true,
                    hostKind = "vscode",
                    preContentDigest = baseDigest,
                    appliedContentDigest = afterDigest,
                    patchDigest,
                    applyRunId = "vscode-route-test",
                });
            Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
            using var acknowledgedBody = JsonDocument.Parse(await acknowledged.Content.ReadAsStringAsync());
            Assert.Equal("applied", acknowledgedBody.RootElement
                .GetProperty("proposal").GetProperty("state").GetString());

            using var directApply = await PostJsonAsync(
                http,
                $"{inspectorBase}/api/workbench/source/csharp/{Uri.EscapeDataString(proposalId)}/apply",
                new { });
            Assert.Equal(HttpStatusCode.NotFound, directApply.StatusCode);
            Assert.Equal(source, await File.ReadAllTextAsync(sourcePath));
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

    private static (int Line, int Column) LineColumn(string text, string marker)
    {
        var offset = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0);
        var line = 1;
        var lineStart = 0;
        for (var index = 0; index < offset; index++)
        {
            if (text[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }
        return (line, offset - lineStart + 1);
    }
}
