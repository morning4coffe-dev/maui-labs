using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.Cli.DevFlow.Broker;

namespace Microsoft.Maui.Cli.DevFlow;

/// <summary>
/// <c>maui devflow devices</c> — the device layer at the command line.
/// <para>
/// Everything routes through the broker rather than a device host directly, so the CLI, the
/// Inspector, and the MCP server all share one view of which devices exist and which app is
/// running on each.
/// </para>
/// </summary>
internal static class DeviceCommands
{
    public static Command Create(
        Option<bool> jsonOption,
        Option<bool> noJsonOption,
        IDevFlowOutputWriter output)
    {
        var devicesCommand = new Command(
            "devices",
            "Inspect and control the emulators and simulators DevFlow can reach");

        devicesCommand.Add(CreateListCommand(jsonOption, noJsonOption, output));
        devicesCommand.Add(CreateControlCommand("boot", "Boot a device and wait until it is ready", jsonOption, noJsonOption, output));
        devicesCommand.Add(CreateControlCommand("shutdown", "Power a device off without erasing it", jsonOption, noJsonOption, output));

        return devicesCommand;
    }

    private static Command CreateListCommand(
        Option<bool> jsonOption,
        Option<bool> noJsonOption,
        IDevFlowOutputWriter output)
    {
        var command = new Command("list", "List devices, each paired with the app running inside it");

        command.SetAction(async (ctx, ct) =>
        {
            var isJson = output.ResolveJsonMode(ctx.GetValue(jsonOption), ctx.GetValue(noJsonOption));
            var brokerPort = await BrokerClient.EnsureBrokerRunningAsync() ?? BrokerServer.DefaultPort;
            var payload = await BrokerClient.ListDevicesAsync(brokerPort);

            if (payload is null)
            {
                output.WriteError(
                    "The DevFlow broker could not be reached.",
                    isJson,
                    retryable: true,
                    suggestions: ["Start it with 'maui devflow broker start'."]);
                return;
            }

            if (isJson)
            {
                output.WriteRawJson(payload);
                return;
            }

            WriteHumanReadable(payload, output, isJson);
        });

        return command;
    }

    private static Command CreateControlCommand(
        string action,
        string description,
        Option<bool> jsonOption,
        Option<bool> noJsonOption,
        IDevFlowOutputWriter output)
    {
        var deviceArg = new Argument<string>("device") { Description = "Device id from 'maui devflow devices list'" };
        var command = new Command(action, description) { deviceArg };

        command.SetAction(async (ctx, ct) =>
        {
            var isJson = output.ResolveJsonMode(ctx.GetValue(jsonOption), ctx.GetValue(noJsonOption));
            var deviceId = ctx.GetValue(deviceArg)!;
            var brokerPort = await BrokerClient.EnsureBrokerRunningAsync() ?? BrokerServer.DefaultPort;

            var result = await BrokerClient.ControlDeviceAsync(brokerPort, deviceId, action);

            if (!result.Success)
            {
                output.WriteError(result.Reason ?? $"The device refused {action}.", isJson);
                return;
            }

            output.WriteActionResult(true, action, deviceId, isJson, $"{deviceId}: {action} succeeded.");
        });

        return command;
    }

    /// <summary>
    /// Renders the device list for a human.
    /// <para>
    /// "No devices" and "no device layer" get different messages on purpose: the first is a
    /// prompt to create one, the second a prompt to install something. Conflating them leaves the
    /// reader with no idea which applies.
    /// </para>
    /// </summary>
    private static void WriteHumanReadable(string payload, IDevFlowOutputWriter output, bool isJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            output.WriteError("The broker returned an unreadable device list.", isJson);
            return;
        }

        var available = root?["available"]?.GetValue<bool>() ?? false;
        if (!available)
        {
            var reason = root?["reason"]?.GetValue<string>()
                ?? "No device host is available, so emulator and simulator control is disabled.";
            Console.WriteLine(reason);
            return;
        }

        if (root?["devices"] is not JsonArray devices || devices.Count == 0)
        {
            Console.WriteLine("No emulators or simulators were found. Create one, then run this again.");
            return;
        }

        foreach (var device in devices)
        {
            if (device is null)
                continue;

            var name = device["name"]?.GetValue<string>() ?? "(unnamed)";
            var id = device["id"]?.GetValue<string>() ?? "";
            var state = device["state"]?.GetValue<string>() ?? "unknown";
            var agentPort = device["agentPort"]?.GetValue<int?>();

            var app = agentPort is null ? "" : $"  - app on port {agentPort}";
            Console.WriteLine($"{name}  [{state}]  {id}{app}");
        }
    }
}
