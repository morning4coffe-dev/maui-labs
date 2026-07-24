namespace Microsoft.Maui.Platforms.MacOS.Platform;

/// <summary>
/// Guarantees that a dialog's <see cref="DialogNativeRegistrationScope"/> is disposed exactly
/// once when the dialog's presentation completes, and that no exception can ever escape back
/// across the native AppKit boundary that invoked completion - whether the caller's result
/// callback succeeds, throws (falling back to <paramref name="onUnhandledException"/> below),
/// that fallback itself throws, or (defensively, since it should never happen) AppKit invokes
/// the sheet completion handler more than once. This matters because
/// <c>NSAlert.BeginSheetForResponse</c>'s completion handler runs later, from native code: an
/// unhandled managed exception there would cross the native boundary instead of surfacing as a
/// normal .NET exception, and would leave the corresponding MAUI
/// AlertArguments/PromptArguments/ActionSheetArguments task never completed - so callers of
/// <c>Complete</c> must always supply a fallback that sets a safe default result. Contains no
/// AppKit or MAUI dependencies so it can be exercised by plain unit tests.
/// </summary>
internal sealed class DialogCompletionScope
{
    readonly IDisposable _registrationScope;
    int _completed;

    public DialogCompletionScope(IDisposable registrationScope)
    {
        ArgumentNullException.ThrowIfNull(registrationScope);
        _registrationScope = registrationScope;
    }

    /// <summary>True once <see cref="Complete"/> has run (successfully or not). Exposed for testing.</summary>
    public bool IsCompleted => _completed != 0;

    /// <summary>
    /// Runs <paramref name="onCompleted"/> and then disposes the tracked registration scope,
    /// exactly once, guaranteeing the scope is disposed on every path. If
    /// <paramref name="onCompleted"/> throws, <paramref name="onUnhandledException"/> runs
    /// instead - typically to log the failure and set a safe fallback result - and if that
    /// fallback itself throws, the failure is swallowed (after the registration scope is still
    /// disposed) rather than ever escaping this method. Any call after the first is ignored, so
    /// a duplicate or re-entrant completion callback can never double-dispose the registration
    /// scope or run the result callback (or its fallback) more than once.
    /// </summary>
    public void Complete(Action onCompleted, Action<Exception> onUnhandledException)
    {
        ArgumentNullException.ThrowIfNull(onCompleted);
        ArgumentNullException.ThrowIfNull(onUnhandledException);

        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        try
        {
            try
            {
                onCompleted();
            }
            catch (Exception ex)
            {
                onUnhandledException(ex);
            }
        }
        catch
        {
            // The fallback itself failed. There is nothing more we can safely do here, but
            // this method must never let an exception escape into the native completion
            // callback that invoked it - the registration scope below is still disposed.
        }
        finally
        {
            _registrationScope.Dispose();
        }
    }
}
