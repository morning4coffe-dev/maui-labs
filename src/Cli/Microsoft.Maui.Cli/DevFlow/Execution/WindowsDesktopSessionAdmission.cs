using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Microsoft.Maui.Cli.DevFlow.Execution;

internal interface IWindowsDesktopSessionAdmissionProbe
{
    WindowsDesktopSessionAdmission Probe();
}

internal interface IWindowsWtsSessionApi
{
    bool TryGetConnectionState(int sessionId, out WindowsWtsConnectionState connectionState);
    bool TryGetDesktopLockState(int sessionId, out WindowsDesktopLockState desktopLockState);
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
    string Reason)
{
    public bool IsAllowed => Result == WindowsDesktopSessionAdmissionResult.Allowed;
}

internal sealed class WindowsDesktopSessionAdmissionProbe : IWindowsDesktopSessionAdmissionProbe
{
    private readonly IFlowExecutionHostEnvironment _host;
    private readonly Func<int> _getCurrentProcessSessionId;
    private readonly IWindowsWtsSessionApi _wts;

    public WindowsDesktopSessionAdmissionProbe(IFlowExecutionHostEnvironment host)
        : this(host, GetCurrentProcessSessionId, new WindowsWtsSessionApi())
    {
    }

    internal WindowsDesktopSessionAdmissionProbe(
        IFlowExecutionHostEnvironment host,
        Func<int> getCurrentProcessSessionId,
        IWindowsWtsSessionApi wts)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _getCurrentProcessSessionId = getCurrentProcessSessionId ?? throw new ArgumentNullException(nameof(getCurrentProcessSessionId));
        _wts = wts ?? throw new ArgumentNullException(nameof(wts));
    }

    public WindowsDesktopSessionAdmission Probe()
    {
        if (!_host.IsWindows)
            return Unavailable(null, null, null, "windows-host-required");

        int sessionId;
        try
        {
            sessionId = _getCurrentProcessSessionId();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return Unavailable(null, null, null, "current-process-session-unavailable");
        }

        if (sessionId < 0)
            return Unavailable(null, null, null, "current-process-session-unavailable");
        if (sessionId == 0)
            return Rejected(sessionId, null, null, "session-zero-not-desktop");

        if (!_wts.TryGetConnectionState(sessionId, out var connectionState) ||
            connectionState == WindowsWtsConnectionState.Unknown)
        {
            return Unavailable(sessionId, null, null, "wts-connection-state-unavailable");
        }
        if (connectionState != WindowsWtsConnectionState.Active)
        {
            return Rejected(
                sessionId,
                connectionState,
                null,
                $"wts-connection-state-{ToStableValue(connectionState)}");
        }

        if (!_wts.TryGetDesktopLockState(sessionId, out var desktopLockState) ||
            desktopLockState == WindowsDesktopLockState.Unknown)
        {
            return Unavailable(
                sessionId,
                connectionState,
                null,
                "desktop-lock-state-unavailable");
        }
        if (desktopLockState != WindowsDesktopLockState.Unlocked)
        {
            return Rejected(
                sessionId,
                connectionState,
                desktopLockState,
                "desktop-locked");
        }

        return new WindowsDesktopSessionAdmission(
            sessionId,
            connectionState,
            desktopLockState,
            WindowsDesktopSessionAdmissionResult.Allowed,
            "active-unlocked-desktop");
    }

    private static int GetCurrentProcessSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    private static WindowsDesktopSessionAdmission Unavailable(
        int? sessionId,
        WindowsWtsConnectionState? connectionState,
        WindowsDesktopLockState? desktopLockState,
        string reason)
        => new(
            sessionId,
            connectionState,
            desktopLockState,
            WindowsDesktopSessionAdmissionResult.Unavailable,
            reason);

    private static WindowsDesktopSessionAdmission Rejected(
        int? sessionId,
        WindowsWtsConnectionState? connectionState,
        WindowsDesktopLockState? desktopLockState,
        string reason)
        => new(
            sessionId,
            connectionState,
            desktopLockState,
            WindowsDesktopSessionAdmissionResult.Rejected,
            reason);

    private static string ToStableValue(WindowsWtsConnectionState state)
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
            _ => "unknown",
        };
}

internal sealed class WindowsWtsSessionApi : IWindowsWtsSessionApi
{
    private const int WtsConnectState = 8;
    private const int WtsSessionInfoEx = 25;
    private const int WtsInfoExLevel1 = 1;
    private const int WtsSessionStateLocked = 0;
    private const int WtsSessionStateUnlocked = 1;

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

            desktopLockState = Marshal.ReadInt32(buffer, sizeof(int) * 3) switch
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

    private static bool TryQuery(int sessionId, int informationClass, out IntPtr buffer, out int bytesReturned)
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
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        int sessionId,
        int wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
