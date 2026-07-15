using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Maui.Cli.DevFlow.Inspector;
using Microsoft.Maui.DevFlow.Driver;

namespace Microsoft.Maui.DevFlow.Tests;

/// <summary>
/// End-to-end integration tests for the shared inspector's rich-property endpoints (m6):
/// an HTTP client hits InspectorServer's /api/getProperty and /api/setProperty, which proxy to a
/// loopback fake agent. Verifies the full route → proxy → agent path the canvas/VS Code shells use.
/// </summary>
public class InspectorPropertyEndpointTests
{
    [Fact]
    public async Task SetThenGetProperty_ProxiesToAgent()
    {
        await using var agent = new FakeAgent();
        var port = FreePort();
        var inspector = new InspectorServer(port, "127.0.0.1", agent.Port);
        inspector.Start();
        try
        {
            using var http = new HttpClient();

            var set = await http.PostAsync($"http://127.0.0.1:{port}/api/setProperty",
                Json("{\"elementId\":\"e1\",\"name\":\"Text\",\"value\":\"hello\"}"));
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.Contains("\"ok\":true", await set.Content.ReadAsStringAsync());
            Assert.Equal("hello", agent.Get("e1", "Text"));

            var get = await http.PostAsync($"http://127.0.0.1:{port}/api/getProperty",
                Json("{\"elementId\":\"e1\",\"name\":\"Text\"}"));
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Contains("\"value\":\"hello\"", await get.Content.ReadAsStringAsync());
        }
        finally
        {
            await inspector.StopAsync();
        }
    }

    [Fact]
    public async Task GetProperty_MissingFields_Returns400()
    {
        await using var agent = new FakeAgent();
        var port = FreePort();
        var inspector = new InspectorServer(port, "127.0.0.1", agent.Port);
        inspector.Start();
        try
        {
            using var http = new HttpClient();
            var res = await http.PostAsync($"http://127.0.0.1:{port}/api/getProperty",
                Json("{\"elementId\":\"e1\"}"));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
        finally
        {
            await inspector.StopAsync();
        }
    }

    [Fact]
    public async Task SetProperty_NonScalarValue_Returns400()
    {
        await using var agent = new FakeAgent();
        var port = FreePort();
        var inspector = new InspectorServer(port, "127.0.0.1", agent.Port);
        inspector.Start();
        try
        {
            using var http = new HttpClient();
            var res = await http.PostAsync($"http://127.0.0.1:{port}/api/setProperty",
                Json("{\"elementId\":\"e1\",\"name\":\"Text\",\"value\":null}"));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Null(agent.Get("e1", "Text")); // rejected → not written
        }
        finally
        {
            await inspector.StopAsync();
        }
    }

    [Fact]
    public async Task SetProperty_NumberValue_ProxiesInvariantString()
    {
        await using var agent = new FakeAgent();
        var port = FreePort();
        var inspector = new InspectorServer(port, "127.0.0.1", agent.Port);
        inspector.Start();
        try
        {
            using var http = new HttpClient();
            var res = await http.PostAsync($"http://127.0.0.1:{port}/api/setProperty",
                Json("{\"elementId\":\"e1\",\"name\":\"Opacity\",\"value\":0.5}"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("0.5", agent.Get("e1", "Opacity")); // JSON number → invariant "0.5"
        }
        finally
        {
            await inspector.StopAsync();
        }
    }

    [Fact]
    public async Task PersistProperty_RuntimeRejectsValue_DoesNotWriteSource()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "devflow-persist-endpoint", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var sourcePath = Path.Combine(tempRoot, "MainPage.xaml");
        const string xaml = """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
                <Label Text="Original" FontSize="28" />
            </ContentPage>
            """;
        await File.WriteAllTextAsync(Path.Combine(tempRoot, "TestApp.csproj"), "<Project />");
        await File.WriteAllTextAsync(sourcePath, xaml);
        var labelOffset = xaml.IndexOf("<Label", StringComparison.Ordinal);
        var lineStart = xaml.LastIndexOf('\n', labelOffset) + 1;
        var line = xaml[..labelOffset].Count(c => c == '\n') + 1;

        await using var agent = new FakeAgent(
            new ElementInfo
            {
                Id = "e1",
                SourceFile = sourcePath,
                SourceLine = line,
                SourceColumn = labelOffset - lineStart + 2,
                SourceHash = XamlSourcePropertyEditor.ComputeSourceHash(xaml),
            },
            rejectProperty: static (name, value) => name == "FontSize" && value.Length == 0);
        var port = FreePort();
        var inspector = new InspectorServer(
            port,
            "127.0.0.1",
            agent.Port,
            embedToken: null,
            agentId: "agent",
            appName: "TestApp",
            platform: "windows",
            project: Path.Combine(tempRoot, "TestApp.csproj"),
            sessionId: null);
        inspector.Start();
        try
        {
            using var http = new HttpClient();
            var page = await http.GetStringAsync($"http://127.0.0.1:{port}/");
            var token = Regex.Match(page, "<meta\\s+name=\"devflow-inspector-token\"\\s+content=\"([^\"]+)\"").Groups[1].Value;
            Assert.NotEmpty(token);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/persistProperty")
            {
                Content = Json("{\"elementId\":\"e1\",\"name\":\"FontSize\",\"value\":\"\"}")
            };
            request.Headers.Add("X-DevFlow-Inspector-Token", token);
            var response = await http.SendAsync(request);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(xaml, await File.ReadAllTextAsync(sourcePath));
        }
        finally
        {
            await inspector.StopAsync();
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("tok-abc", "tok-abc", true)]   // exact match → trusted local shell
    [InlineData("tok-abc", "tok-xyz", false)]  // wrong token → not trusted (stays DENY)
    [InlineData("tok-abc", "", false)]         // no token supplied
    [InlineData("tok-abc", null, false)]       // no token supplied
    [InlineData("", "tok-abc", false)]         // broker issued no token → never trusted
    [InlineData(null, "tok-abc", false)]
    public void IsTrustedEmbed_OnlyExactTokenMatchIsTrusted(string? embedToken, string? requestEmbed, bool expected)
    {
        Assert.Equal(expected, InspectorServer.IsTrustedEmbed(embedToken, requestEmbed));
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Minimal loopback agent answering GET/PUT /api/v1/ui/elements/{id}/properties/{name}.</summary>
    private sealed class FakeAgent : IAsyncDisposable
    {
        private static readonly Regex PropRoute = new("/api/v1/ui/elements/([^/]+)/properties/([^/?]+)", RegexOptions.Compiled);
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly Dictionary<string, string> _props = new(StringComparer.Ordinal);
        private readonly ElementInfo? _element;
        private readonly Func<string, string, bool>? _rejectProperty;

        public FakeAgent(
            ElementInfo? element = null,
            Func<string, string, bool>? rejectProperty = null)
        {
            _element = element;
            _rejectProperty = rejectProperty;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Loop(_cts.Token);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string? Get(string id, string name) => _props.TryGetValue($"{id}|{name}", out var v) ? v : null;

        private async Task Loop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(ct); }
                catch { break; }
                _ = Handle(client, ct);
            }
        }

        private async Task Handle(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var (method, path, body) = await ReadRequest(stream, ct);

                    string json = "{\"error\":\"not found\"}";
                    var status = "404 Not Found";
                    if (method == "GET" &&
                        _element is not null &&
                        path.Equals($"/api/v1/ui/elements/{_element.Id}", StringComparison.Ordinal))
                    {
                        json = JsonSerializer.Serialize(_element);
                        status = "200 OK";
                    }

                    var m = PropRoute.Match(path);
                    if (m.Success)
                    {
                        var key = $"{m.Groups[1].Value}|{m.Groups[2].Value}";
                        if (method == "PUT")
                        {
                            using var doc = JsonDocument.Parse(body);
                            var value = doc.RootElement.GetProperty("value").GetString() ?? "";
                            if (_rejectProperty?.Invoke(m.Groups[2].Value, value) == true)
                            {
                                json = "{\"error\":\"invalid property value\"}";
                                status = "400 Bad Request";
                            }
                            else
                            {
                                _props[key] = value;
                                json = "{\"success\":true}";
                                status = "200 OK";
                            }
                        }
                        else if (method == "GET")
                        {
                            var value = _props.TryGetValue(key, out var v) ? v : null;
                            json = $"{{\"value\":{JsonSerializer.Serialize(value)}}}";
                            status = "200 OK";
                        }
                    }

                    var payload = Encoding.UTF8.GetBytes(json);
                    var header = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(header), ct);
                    await stream.WriteAsync(payload, ct);
                    await stream.FlushAsync(ct);
                }
                catch { /* connection torn down — irrelevant */ }
            }
        }

        private static async Task<(string Method, string Path, string Body)> ReadRequest(NetworkStream stream, CancellationToken ct)
        {
            var buf = new byte[8192];
            var sb = new StringBuilder();
            int headerEnd;
            while ((headerEnd = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
            {
                var n = await stream.ReadAsync(buf, ct);
                if (n <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            var text = sb.ToString();
            var firstLine = text.Split("\r\n", 2)[0].Split(' ');
            var method = firstLine.Length > 0 ? firstLine[0] : "";
            var path = firstLine.Length > 1 ? firstLine[1] : "";

            var contentLength = 0;
            foreach (var line in text.Split("\r\n"))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);

            var body = headerEnd >= 0 ? text[(headerEnd + 4)..] : "";
            while (Encoding.UTF8.GetByteCount(body) < contentLength)
            {
                var n = await stream.ReadAsync(buf, ct);
                if (n <= 0) break;
                body += Encoding.UTF8.GetString(buf, 0, n);
            }
            return (method, path, body);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _loop; } catch { }
        }
    }
}
