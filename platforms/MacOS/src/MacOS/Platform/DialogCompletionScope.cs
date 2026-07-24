namespace Microsoft.Maui.Platforms.MacOS.Platform;

/// <summary>
/// Guarantees that a dialog's <see cref="DialogNativeRegistrationScope"/> is disposed exactly
/// once when the dialog's presentation completes - whether completion runs the caller's result
/// callback successfully, that callback throws, or (defensively, since it should never happen)
/// AppKit invokes the sheet completion handler more than once. Used by
/// <c>AlertManagerSubscription</c> to move registration cleanup from a synchronous
/// try/finally around a blocking <c>NSAlert.RunModal()</c> call to an asynchronous
/// <c>NSAlert.BeginSheetForResponse</c> completion handler, without losing the "always
/// unregister, on every exit path" guarantee. Contains no AppKit or MAUI dependencies so it can
/// be exercised by plain unit tests.
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
    /// guaranteeing the scope is disposed even if <paramref name="onCompleted"/> throws. Any
    /// call after the first is ignored, so a duplicate or re-entrant completion callback can
    /// never double-dispose the registration scope or re-run the result callback.
    /// </summary>
    public void Complete(Action onCompleted)
    {
        ArgumentNullException.ThrowIfNull(onCompleted);

        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        try
        {
            onCompleted();
        }
        finally
        {
            _registrationScope.Dispose();
        }
    }
}
