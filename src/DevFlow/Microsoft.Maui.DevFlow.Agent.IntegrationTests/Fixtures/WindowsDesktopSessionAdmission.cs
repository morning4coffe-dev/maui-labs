using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests.Fixtures;

/// <summary>
/// Determines whether the current Windows process can safely launch an unpackaged WinUI app.
/// </summary>
internal interface IWindowsDesktopSessionAdmissionProbe
{
    WindowsDesktopSessionAdmission Probe();
}

internal interface IWindowsWtsSessionApi
{
    bool TryGetConnectionState(int sessionId, out WindowsWtsConnectionState connectionState);
    bool TryGetDesktopLockState(int sessionId, out WindowsDesktopLockState desktopLockState);
}

internal interface IWindowsWinUiProcessStarter
{
    Process? Start(ProcessStartInfo startInfo);
}

internal enum WindowsWtsConnectionState
{
    Active = 0,
    Connected = 1,
    ConnectQuery = 2,
    Shadow = 3,
    Disconnected = 4,
    Idle = 5,
    Listen = 6,
    Reset = 7,
    Down = 8,
    Init = 9,
    Unknown = -1,
}

internal enum WindowsDesktopLockState
{
    Locked,
    Unlocked,
    Unknown,
}

internal enum WindowsDesktopSessionAdmissionResult
{
    Allowed,
    Rejected,
    Unavailable,
}

internal sealed record WindowsDesktopSessionAdmission(
    int? SessionId,
    WindowsWtsConnectionState? WtsConnectionState,
    WindowsDesktopLockState? DesktopLockState,
    WindowsDesktopSessionAdmissionResult Result,
    DateTimeOffset TimestampUtc,
    string Reason)
{
    public bool IsAllowed => Result == WindowsDesktopSessionAdmissionResult.Allowed;
}

/// <summary>
/// Uses the current process session ID plus WTS state. The legacy interactive-environment flag
/// is deliberately not consulted because it is insufficient for WinUI admission.
/// </summary>
internal sealed class WindowsDesktopSessionAdmissionProbe : IWindowsDesktopSessionAdmissionProbe
{
    readonly Func<bool> _isWindows;
    readonly Func<int> _getCurrentProcessSessionId;
    readonly IWindowsWtsSessionApi _wts;
    readonly Func<DateTimeOffset> _clock;

    public WindowsDesktopSessionAdmissionProbe()
        : this(
            OperatingSystem.IsWindows,
            GetCurrentProcessSessionId,
            new WindowsWtsSessionApi(),
            static () => DateTimeOffset.UtcNow)
    {
    }

    internal WindowsDesktopSessionAdmissionProbe(
        Func<bool> isWindows,
        Func<int> getCurrentProcessSessionId,
        IWindowsWtsSessionApi wts,
        Func<DateTimeOffset>? clock = null)
    {
        _isWindows = isWindows ?? throw new ArgumentNullException(nameof(isWindows));
        _getCurrentProcessSessionId = getCurrentProcessSessionId ?? throw new ArgumentNullException(nameof(getCurrentProcessSessionId));
        _wts = wts ?? throw new ArgumentNullException(nameof(wts));
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    public WindowsDesktopSessionAdmission Probe()
    {
        var timestamp = _clock();
        if (!_isWindows())
            return Unavailable(null, null, null, timestamp, "windows-host-required");

        int sessionId;
        try
        {
            sessionId = _getCurrentProcessSessionId();
        }
        catch
        {
            return Unavailable(null, null, null, timestamp, "current-process-session-unavailable");
        }

        if (sessionId < 0)
            return Unavailable(null, null, null, timestamp, "current-process-session-unavailable");
        if (sessionId == 0)
            return Rejected(sessionId, null, null, timestamp, "session-zero-not-desktop");

        WindowsWtsConnectionState connectionState;
        try
        {
            if (!_wts.TryGetConnectionState(sessionId, out connectionState) ||
                connectionState == WindowsWtsConnectionState.Unknown)
            {
                return Unavailable(sessionId, null, null, timestamp, "wts-connection-state-unavailable");
            }
        }
        catch
        {
            return Unavailable(sessionId, null, null, timestamp, "wts-connection-state-unavailable");
        }

        if (connectionState != WindowsWtsConnectionState.Active)
        {
            return Rejected(
                sessionId,
                connectionState,
                null,
                timestamp,
                $"wts-connection-state-{WindowsDesktopSessionDiagnostics.ToStableValue(connectionState)}");
        }

        WindowsDesktopLockState desktopLockState;
        try
        {
            if (!_wts.TryGetDesktopLockState(sessionId, out desktopLockState) ||
                desktopLockState == WindowsDesktopLockState.Unknown)
            {
                return Unavailable(
                    sessionId,
                    connectionState,
                    null,
                    timestamp,
                    "desktop-lock-state-unavailable");
            }
        }
        catch
        {
            return Unavailable(
                sessionId,
                connectionState,
                null,
                timestamp,
                "desktop-lock-state-unavailable");
        }

        if (desktopLockState != WindowsDesktopLockState.Unlocked)
        {
            return Rejected(
                sessionId,
                connectionState,
                desktopLockState,
                timestamp,
                "desktop-locked");
        }

        return new WindowsDesktopSessionAdmission(
            sessionId,
            connectionState,
            desktopLockState,
            WindowsDesktopSessionAdmissionResult.Allowed,
            timestamp,
            "active-unlocked-desktop");
    }

    static int GetCurrentProcessSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    static WindowsDesktopSessionAdmission Unavailable(
        int? sessionId,
        WindowsWtsConnectionState? connectionState,
        WindowsDesktopLockState? desktopLockState,
        DateTimeOffset timestamp,
        string reason)
        => new(
            sessionId,
            connectionState,
            desktopLockState,
            WindowsDesktopSessionAdmissionResult.Unavailable,
            timestamp,
            reason);

    static WindowsDesktopSessionAdmission Rejected(
        int? sessionId,
        WindowsWtsConnectionState? connectionState,
        WindowsDesktopLockState? desktopLockState,
        DateTimeOffset timestamp,
        string reason)
        => new(
            sessionId,
            connectionState,
            desktopLockState,
            WindowsDesktopSessionAdmissionResult.Rejected,
            timestamp,
            reason);
}

/// <summary>
/// Ensures a desktop admission result is observed before the fixture can call Process.Start.
/// </summary>
internal sealed class WindowsDesktopSessionLaunchGate
{
    readonly IWindowsDesktopSessionAdmissionProbe _probe;
    readonly IWindowsWinUiProcessStarter _processStarter;

    public WindowsDesktopSessionLaunchGate(
        IWindowsDesktopSessionAdmissionProbe probe,
        IWindowsWinUiProcessStarter processStarter)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
    }

    public WindowsDesktopSessionAdmission Admit() => _probe.Probe();

    public Process Start(ProcessStartInfo startInfo, WindowsDesktopSessionAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(admission);

        if (!admission.IsAllowed)
            throw CreateRejectionException(admission);

        return _processStarter.Start(startInfo)
            ?? throw PlatformFlowLifecycleException.Infrastructure(
                "Windows desktop admission succeeded, but the WinUI process could not be started.");
    }

    public static PlatformFlowLifecycleException CreateRejectionException(
        WindowsDesktopSessionAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return PlatformFlowLifecycleException.Infrastructure(
            "Windows desktop session admission failed before WinUI launch " +
            $"(session {admission.SessionId?.ToString(CultureInfo.InvariantCulture) ?? "<unavailable>"}, " +
            $"WTS {WindowsDesktopSessionDiagnostics.ToStableValue(admission.WtsConnectionState)}, " +
            $"desktop {WindowsDesktopSessionDiagnostics.ToStableValue(admission.DesktopLockState)}, " +
            $"reason {WindowsDesktopSessionDiagnostics.RedactReason(admission.Reason)}).");
    }
}

internal sealed class ProcessStartWindowsWinUiProcessStarter : IWindowsWinUiProcessStarter
{
    public Process? Start(ProcessStartInfo startInfo) => Process.Start(startInfo);
}

/// <summary>
/// Produces bounded, redacted desktop-session evidence. The values are all derived from a
/// fixed enum vocabulary; no account name, WinStation name, or raw command output is retained.
/// </summary>
internal static class WindowsDesktopSessionDiagnostics
{
    const int MaxReasonLength = 128;

    static readonly HashSet<string> AllowedReasons = new(StringComparer.Ordinal)
    {
        "active-unlocked-desktop",
        "windows-host-required",
        "current-process-session-unavailable",
        "session-zero-not-desktop",
        "wts-connection-state-unavailable",
        "wts-connection-state-active",
        "wts-connection-state-connected",
        "wts-connection-state-connect-query",
        "wts-connection-state-shadow",
        "wts-connection-state-disconnected",
        "wts-connection-state-idle",
        "wts-connection-state-listen",
        "wts-connection-state-reset",
        "wts-connection-state-down",
        "wts-connection-state-init",
        "desktop-lock-state-unavailable",
        "desktop-locked",
    };

    static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<string> WriteAsync(
        string artifactRoot,
        WindowsDesktopSessionAdmission admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        ArgumentNullException.ThrowIfNull(admission);

        var directory = Path.Combine(Path.GetFullPath(artifactRoot), "host-diagnostics");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "windows-session.json");
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(CreateRecord(admission), SerializerOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return path;
    }

    public static void AddProcessExitFacts(
        IDictionary<string, string?> facts,
        WindowsDesktopSessionAdmission? admission)
    {
        ArgumentNullException.ThrowIfNull(facts);

        facts["sessionId"] = admission?.SessionId?.ToString(CultureInfo.InvariantCulture);
        facts["wtsConnectionState"] = ToStableValue(admission?.WtsConnectionState);
        facts["desktopLockState"] = ToStableValue(admission?.DesktopLockState);
        facts["admissionResult"] = ToStableValue(admission?.Result);
        facts["admissionTimestampUtc"] = admission?.TimestampUtc.ToString("O", CultureInfo.InvariantCulture);
        facts["admissionReason"] = RedactReason(admission?.Reason);
    }

    public static string ToStableValue(WindowsWtsConnectionState? state)
        => state switch
        {
            WindowsWtsConnectionState.Active => "active",
            WindowsWtsConnectionState.Connected => "connected",
            WindowsWtsConnectionState.ConnectQuery => "connect-query",
            WindowsWtsConnectionState.Shadow => "shadow",
            WindowsWtsConnectionState.Disconnected => "disconnected",
            WindowsWtsConnectionState.Idle => "idle",
            WindowsWtsConnectionState.Listen => "listen",
            WindowsWtsConnectionState.Reset => "reset",
            WindowsWtsConnectionState.Down => "down",
            WindowsWtsConnectionState.Init => "init",
            _ => "unavailable",
        };

    public static string ToStableValue(WindowsDesktopLockState? state)
        => state switch
        {
            WindowsDesktopLockState.Unlocked => "unlocked",
            WindowsDesktopLockState.Locked => "locked",
            _ => "unavailable",
        };

    public static string ToStableValue(WindowsDesktopSessionAdmissionResult? result)
        => result switch
        {
            WindowsDesktopSessionAdmissionResult.Allowed => "allowed",
            WindowsDesktopSessionAdmissionResult.Rejected => "rejected",
            WindowsDesktopSessionAdmissionResult.Unavailable => "unavailable",
            _ => "unavailable",
        };

    public static string RedactReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) ||
            reason.Length > MaxReasonLength ||
            !AllowedReasons.Contains(reason))
        {
            return "redacted";
        }

        return reason;
    }

    static WindowsDesktopSessionDiagnosticRecord CreateRecord(WindowsDesktopSessionAdmission admission)
        => new()
        {
            SessionId = admission.SessionId,
            WtsConnectionState = ToStableValue(admission.WtsConnectionState),
            DesktopLockState = ToStableValue(admission.DesktopLockState),
            AdmissionResult = ToStableValue(admission.Result),
            AdmissionTimestampUtc = admission.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            Reason = RedactReason(admission.Reason),
        };

    sealed class WindowsDesktopSessionDiagnosticRecord
    {
        [JsonPropertyName("schema")]
        public int Schema { get; } = 1;

        [JsonPropertyName("kind")]
        public string Kind { get; } = "devflow-windows-desktop-session";

        [JsonPropertyName("sessionId")]
        public int? SessionId { get; init; }

        [JsonPropertyName("wtsConnectionState")]
        public string WtsConnectionState { get; init; } = "unavailable";

        [JsonPropertyName("desktopLockState")]
        public string DesktopLockState { get; init; } = "unavailable";

        [JsonPropertyName("admissionResult")]
        public string AdmissionResult { get; init; } = "unavailable";

        [JsonPropertyName("admissionTimestampUtc")]
        public string AdmissionTimestampUtc { get; init; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; init; } = "redacted";
    }
}

internal sealed class WindowsWtsSessionApi : IWindowsWtsSessionApi
{
    const int WtsConnectState = 8;
    const int WtsSessionInfoEx = 25;
    const int WtsInfoExLevel1 = 1;
    const int WtsSessionStateLocked = 0;
    const int WtsSessionStateUnlocked = 1;

    public bool TryGetConnectionState(int sessionId, out WindowsWtsConnectionState connectionState)
    {
        connectionState = WindowsWtsConnectionState.Unknown;
        if (!TryQuery(sessionId, WtsConnectState, out var buffer, out var bytesReturned))
            return false;

        try
        {
            if (bytesReturned < sizeof(int))
                return false;

            var value = Marshal.ReadInt32(buffer);
            connectionState = Enum.IsDefined(typeof(WindowsWtsConnectionState), value)
                ? (WindowsWtsConnectionState)value
                : WindowsWtsConnectionState.Unknown;
            return true;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    public bool TryGetDesktopLockState(int sessionId, out WindowsDesktopLockState desktopLockState)
    {
        desktopLockState = WindowsDesktopLockState.Unknown;
        if (!TryQuery(sessionId, WtsSessionInfoEx, out var buffer, out var bytesReturned))
            return false;

        try
        {
            if (bytesReturned < sizeof(int) * 4)
                return false;

            var level = Marshal.ReadInt32(buffer, 0);
            var reportedSessionId = Marshal.ReadInt32(buffer, sizeof(int));
            if (level != WtsInfoExLevel1 || reportedSessionId != sessionId)
                return false;

            var flags = Marshal.ReadInt32(buffer, sizeof(int) * 3);
            desktopLockState = flags switch
            {
                WtsSessionStateLocked => WindowsDesktopLockState.Locked,
                WtsSessionStateUnlocked => WindowsDesktopLockState.Unlocked,
                _ => WindowsDesktopLockState.Unknown,
            };
            return true;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    static bool TryQuery(int sessionId, int informationClass, out IntPtr buffer, out int bytesReturned)
    {
        buffer = IntPtr.Zero;
        bytesReturned = 0;
        try
        {
            var succeeded = WTSQuerySessionInformation(
                IntPtr.Zero,
                sessionId,
                informationClass,
                out buffer,
                out bytesReturned);
            if (succeeded && buffer != IntPtr.Zero)
                return true;

            if (buffer != IntPtr.Zero)
                WTSFreeMemory(buffer);

            buffer = IntPtr.Zero;
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        int sessionId,
        int wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [DllImport("Wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);
}
