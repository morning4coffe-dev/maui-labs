using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests.Fixtures;

internal sealed record PlatformProcessResult(string StandardOutput, string StandardError, int ExitCode);

/// <summary>Small injectable process boundary used by Android lifecycle tests.</summary>
internal interface IPlatformProcessRunner
{
    Task<PlatformProcessResult> RunAsync(
        string fileName,
        string arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken = default);
}

internal sealed class SystemPlatformProcessRunner : IPlatformProcessRunner
{
    public async Task<PlatformProcessResult> RunAsync(
        string fileName,
        string arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)
            ?? throw PlatformFlowLifecycleException.Infrastructure($"Failed to start '{fileName}'.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            return new PlatformProcessResult(stdoutTask.Result, stderrTask.Result, process.ExitCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw PlatformFlowLifecycleException.Infrastructure(
                $"Process timed out after {timeoutSeconds}s: {fileName} {arguments}");
        }
    }
}

internal enum AndroidResetStrategy
{
    PmClear,
    UninstallReinstall,
}

/// <summary>
/// Android-only ADB operations. It contains no flow execution behavior and is intentionally
/// isolated from the public Testing package.
/// </summary>
internal sealed class AndroidDeviceLifecycleOperations
{
    const string Success = "Success";
    readonly IPlatformProcessRunner _processRunner;
    readonly string _adbPath;
    readonly string _serialNumber;
    readonly string _packageId;
    readonly int _agentPort;

    public AndroidDeviceLifecycleOperations(
        IPlatformProcessRunner processRunner,
        string adbPath,
        string serialNumber,
        string packageId,
        int agentPort)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _adbPath = string.IsNullOrWhiteSpace(adbPath) ? throw new ArgumentException("ADB path is required.", nameof(adbPath)) : adbPath;
        _serialNumber = string.IsNullOrWhiteSpace(serialNumber) ? throw new ArgumentException("Android serial is required.", nameof(serialNumber)) : serialNumber;
        _packageId = string.IsNullOrWhiteSpace(packageId) ? throw new ArgumentException("Package ID is required.", nameof(packageId)) : packageId;
        _agentPort = agentPort > 0 ? agentPort : throw new ArgumentOutOfRangeException(nameof(agentPort));
    }

    public async Task<MauiFlowResetResult> HardResetAsync(
        AndroidResetStrategy strategy,
        MauiTestResetRequirement? requirement,
        CancellationToken cancellationToken = default)
    {
        await RunCheckedAsync($"shell am force-stop {_packageId}", "terminate the Android app", 15, cancellationToken).ConfigureAwait(false);

        switch (strategy)
        {
            case AndroidResetStrategy.PmClear:
            {
                var clear = await RunCheckedAsync($"shell pm clear {_packageId}", "clear Android app data", 30, cancellationToken)
                    .ConfigureAwait(false);
                if (!clear.StandardOutput.Contains(Success, StringComparison.OrdinalIgnoreCase))
                {
                    throw PlatformFlowLifecycleException.Infrastructure(
                        $"adb pm clear did not confirm success for '{_packageId}': {TrimOutput(clear.StandardOutput, clear.StandardError)}");
                }

                return CreateResetResult("pm-clear", requirement, appStateSucceeded: true);
            }

            case AndroidResetStrategy.UninstallReinstall:
                await RunCheckedAsync($"uninstall {_packageId}", "uninstall the Android app", 60, cancellationToken).ConfigureAwait(false);
                return CreateResetResult("uninstall-reinstall", requirement, appStateSucceeded: true);

            default:
                throw PlatformFlowLifecycleException.Precondition($"Unsupported Android reset strategy '{strategy}'.");
        }
    }

    public async Task InstallAsync(string apkPath, bool replaceExisting, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apkPath))
            throw PlatformFlowLifecycleException.Infrastructure("The Android APK path is empty.");

        var verb = replaceExisting ? "install -r -t" : "install -t";
        await RunCheckedAsync($"{verb} \"{apkPath}\"", "install the Android Debug APK", 120, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureAgentPortForwardAsync(CancellationToken cancellationToken = default)
    {
        // Remove only this test's exact local forward. Never touch unrelated user forwards.
        await RunAsync($"forward --remove tcp:{_agentPort}", 15, cancellationToken).ConfigureAwait(false);
        await RunCheckedAsync(
            $"forward tcp:{_agentPort} tcp:{_agentPort}",
            "create the DevFlow ADB forward",
            30,
            cancellationToken).ConfigureAwait(false);

        if (!await IsAgentPortForwardEstablishedAsync(cancellationToken).ConfigureAwait(false))
        {
            throw PlatformFlowLifecycleException.Infrastructure(
                $"ADB forward tcp:{_agentPort}->tcp:{_agentPort} was not visible after creation on '{_serialNumber}'.");
        }
    }

    public async Task<bool> IsAgentPortForwardEstablishedAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync("forward --list", 15, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            return false;

        var expected = $"tcp:{_agentPort}";
        foreach (var rawLine in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = rawLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length < 2)
                continue;

            var offset = columns.Length >= 3 ? 1 : 0;
            if (columns.Length >= 3 && !string.Equals(columns[0], _serialNumber, StringComparison.OrdinalIgnoreCase))
                continue;

            if (columns.Length > offset + 1 &&
                string.Equals(columns[offset], expected, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(columns[offset + 1], expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<int> LaunchAsync(CancellationToken cancellationToken = default)
    {
        var resolution = await RunCheckedAsync(
            $"shell cmd package resolve-activity --brief -c android.intent.category.LAUNCHER {_packageId}",
            "resolve the Android launch activity",
            30,
            cancellationToken).ConfigureAwait(false);
        var activity = resolution.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(static value => value.Contains('/'));

        if (string.IsNullOrWhiteSpace(activity))
        {
            throw PlatformFlowLifecycleException.Infrastructure(
                $"Could not resolve a launcher activity for '{_packageId}': {TrimOutput(resolution.StandardOutput, resolution.StandardError)}");
        }

        await RunCheckedAsync($"shell am force-stop {_packageId}", "terminate the Android app before launch", 15, cancellationToken)
            .ConfigureAwait(false);
        var launched = await RunCheckedAsync($"shell am start -W -n {activity}", "launch the Android app", 45, cancellationToken)
            .ConfigureAwait(false);
        if (launched.StandardOutput.Contains("Error:", StringComparison.OrdinalIgnoreCase))
        {
            throw PlatformFlowLifecycleException.Infrastructure(
                $"Android launch returned an error: {TrimOutput(launched.StandardOutput, launched.StandardError)}");
        }

        return await WaitForAppProcessAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> WaitForAppProcessAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pid = await TryGetAppProcessIdAsync(cancellationToken).ConfigureAwait(false);
            if (pid is > 0)
                return pid.Value;

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        throw PlatformFlowLifecycleException.Infrastructure(
            $"Android app process '{_packageId}' did not appear within {timeout.TotalSeconds:0}s.");
    }

    public async Task<int?> TryGetAppProcessIdAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync($"shell pidof {_packageId}", 15, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            return null;

        var value = result.StandardOutput
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return int.TryParse(value, out var processId) ? processId : null;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync($"shell am force-stop {_packageId}", 15, cancellationToken).ConfigureAwait(false);
        await RunAsync($"forward --remove tcp:{_agentPort}", 15, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, string>> CollectHostFactsAsync(CancellationToken cancellationToken = default)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["serial"] = _serialNumber,
            ["packageId"] = _packageId,
            ["agentPort"] = _agentPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        await AddFactAsync(facts, "adbState", "get-state", cancellationToken).ConfigureAwait(false);
        await AddFactAsync(facts, "forwards", "forward --list", cancellationToken).ConfigureAwait(false);
        await AddFactAsync(facts, "locale", "shell getprop persist.sys.locale", cancellationToken).ConfigureAwait(false);
        await AddFactAsync(facts, "orientation", "shell dumpsys input", cancellationToken).ConfigureAwait(false);
        await AddFactAsync(facts, "display", "shell wm size; wm density", cancellationToken).ConfigureAwait(false);
        return facts;
    }

    public async Task<string?> GetLocaleAsync(CancellationToken cancellationToken = default)
    {
        var locale = await RunAsync("shell getprop persist.sys.locale", 15, cancellationToken).ConfigureAwait(false);
        if (locale.ExitCode == 0 && !string.IsNullOrWhiteSpace(locale.StandardOutput))
            return locale.StandardOutput.Trim();

        var fallback = await RunAsync("shell getprop ro.product.locale", 15, cancellationToken).ConfigureAwait(false);
        return fallback.ExitCode == 0 ? NullIfWhiteSpace(fallback.StandardOutput) : null;
    }

    public async Task<string?> GetOrientationAsync(CancellationToken cancellationToken = default)
    {
        var input = await RunAsync("shell dumpsys input", 20, cancellationToken).ConfigureAwait(false);
        if (input.ExitCode != 0)
            return null;

        var match = Regex.Match(input.StandardOutput, @"(?:SurfaceOrientation|mCurrentOrientation)\s*[:=]\s*(?<value>\d+)");
        return match.Success ? match.Groups["value"].Value : null;
    }

    public async Task<string?> GetDisplayProfileAsync(CancellationToken cancellationToken = default)
    {
        var display = await RunAsync("shell wm size; wm density", 20, cancellationToken).ConfigureAwait(false);
        return display.ExitCode == 0 ? NullIfWhiteSpace(NormalizeDisplay(display.StandardOutput)) : null;
    }

    private MauiFlowResetResult CreateResetResult(
        string strategy,
        MauiTestResetRequirement? requirement,
        bool appStateSucceeded)
        => new()
        {
            Requested = true,
            Succeeded = appStateSucceeded,
            AppStateSucceeded = appStateSucceeded,
            // This sample has no external backend. Do not represent an app-only reset as proof
            // that an external side effect was reset.
            BackendTestDataSucceeded = false,
            Strategy = strategy,
            ResetIdentity = requirement?.ResetIdentity ?? $"android-{strategy}-v1",
            SeedFingerprint = requirement?.SeedFingerprint,
            BackendStateFingerprint = requirement?.BackendStateFingerprint,
            Reference = requirement?.Reference ?? new MauiFlowResetReference
            {
                Strategy = strategy,
                ResetId = $"android-{strategy}-v1",
                Scope = "app-private-state",
                Version = "1",
            },
            Outcome = new MauiFlowResetOutcome
            {
                Requested = true,
                Succeeded = appStateSucceeded,
                AppStateSucceeded = appStateSucceeded,
                BackendTestDataSucceeded = false,
                CompletedAt = DateTimeOffset.UtcNow,
                Message = "Android app-private state was reset; no external backend reset was performed.",
            },
            Message = "Android app-private state reset completed.",
        };

    private async Task AddFactAsync(
        IDictionary<string, string> facts,
        string key,
        string command,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(command, 20, cancellationToken).ConfigureAwait(false);
        facts[key] = result.ExitCode == 0
            ? TrimOutput(result.StandardOutput, result.StandardError)
            : $"error:{result.ExitCode}:{TrimOutput(result.StandardOutput, result.StandardError)}";
    }

    private async Task<PlatformProcessResult> RunAsync(
        string arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
        => await _processRunner.RunAsync(
            _adbPath,
            $"-s {_serialNumber} {arguments}",
            timeoutSeconds,
            cancellationToken).ConfigureAwait(false);

    private async Task<PlatformProcessResult> RunCheckedAsync(
        string arguments,
        string action,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(arguments, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw PlatformFlowLifecycleException.Infrastructure(
                $"Failed to {action} (adb exit {result.ExitCode}): {TrimOutput(result.StandardOutput, result.StandardError)}");
        }

        return result;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeDisplay(string value)
        => string.Join(
            ";",
            value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static item => Regex.Replace(item, @"\s+", " ")));

    private static string TrimOutput(string stdout, string stderr)
    {
        var value = $"{stdout}\n{stderr}".Trim();
        return value.Length <= 512 ? value : value[..512];
    }
}
