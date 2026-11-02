using System.CommandLine;
using System.Text.Json;
using Microsoft.Maui.Cli.DevFlow.Inspector;

namespace Microsoft.Maui.Cli.DevFlow;

public partial class DevFlowCommands
{
    private static Command CreateInspectCommand(Option<string> hostOption, Option<int> agentPortOption)
    {
        var command = new Command("inspect", "Serve the shared Inspector for a verified agent endpoint (including broker discovery failures)");
        var appOption = new Option<string>("--app") { Description = "Expected live app name", Required = true };
        var projectOption = new Option<string>("--project") { Description = "Local app .csproj for source inspection", Required = true };
        var listenOption = new Option<int>("--listen-port") { Description = "Loopback Inspector port", DefaultValueFactory = _ => 19225 };
        command.Add(appOption);
        command.Add(projectOption);
        command.Add(listenOption);
        command.SetAction(async (context, cancellationToken) =>
        {
            var project = Path.GetFullPath(context.GetValue(projectOption)!);
            if (!File.Exists(project) || !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Select an existing local app .csproj");
            var listenPort = context.GetValue(listenOption);
            var agentPort = context.GetValue(agentPortOption);
            if (listenPort is < 1 or > 65535 || agentPort is < 1 or > 65535 || listenPort == agentPort)
                throw new ArgumentException("Select distinct valid Inspector and agent ports");
            var host = context.GetValue(hostOption)!;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var endpoint = new UriBuilder("http", host, agentPort, "/api/v1/agent/status").Uri;
            using var response = await client.GetAsync(endpoint, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = status.RootElement;
            var app = root.GetProperty("app");
            var appName = app.GetProperty("name").GetString();
            if (root.GetProperty("running").GetBoolean() != true || appName != context.GetValue(appOption))
                throw new InvalidOperationException("Live app identity does not match --app; Inspector was not started");
            using var inspector = new InspectorServer(listenPort, host, agentPort, embedToken: null,
                agentId: null, appName: appName, platform: root.GetProperty("device").GetProperty("platform").GetString(),
                project: project, sessionId: root.GetProperty("agent").GetProperty("sessionId").GetString(),
                sourceWorkspace: XamlSourceWorkspace.Capture(Path.GetDirectoryName(project)));
            inspector.Start();
            Console.WriteLine(JsonSerializer.Serialize(new { inspectorUrl = $"http://127.0.0.1:{listenPort}/", app = appName, agentPort }));
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        });
        return command;
    }
}
