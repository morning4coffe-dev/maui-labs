using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Maui.Cli.UnitTests.Fixtures;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

[Collection("CLI")]
public class DevFlowInspectCommandTests
{
    [Fact]
    public async Task WrongLiveApp_IsRejectedBeforeInspectorListens()
    {
        var project = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csproj");
        File.WriteAllText(project, "<Project />");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reply = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync();
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            var body = """{"running":true,"app":{"name":"Other app"}}""";
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}"));
        });
        try
        {
            var result = await new CliTestHarness(port).InvokeAsync("devflow", "inspect", "--app", "PocketTasks", "--project", project);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("identity does not match", result.StdErr);
            Assert.DoesNotContain("inspectorUrl", result.StdOut);
            await reply.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { File.Delete(project); }
    }

    [Fact]
    public async Task MissingLocalProject_FailsBeforeConnecting()
    {
        var result = await new CliTestHarness(1).InvokeAsync("devflow", "inspect", "--app", "PocketTasks", "--project", "missing-inspect-project.csproj");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("existing local app", result.StdErr);
    }
}
