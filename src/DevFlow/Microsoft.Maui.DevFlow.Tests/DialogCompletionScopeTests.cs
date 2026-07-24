using Microsoft.Maui.Platforms.MacOS.Platform;

namespace Microsoft.Maui.DevFlow.Tests;

/// <summary>
/// Tests for <see cref="DialogCompletionScope"/>, the helper that lets
/// <c>AlertManagerSubscription</c> defer disposing a dialog's
/// <see cref="DialogNativeRegistrationScope"/> until an AppKit sheet's completion handler runs,
/// instead of a synchronous try/finally around a blocking <c>NSAlert.RunModal()</c> call. It
/// also guarantees that this later, native-triggered completion can never let an exception
/// escape back across the native boundary, and that the corresponding MAUI result is always
/// set exactly once even when computing that result fails - see
/// <c>AlertManagerSubscription.PresentDialog</c>, whose <c>onUnhandledException</c> fallback is
/// the second argument to <see cref="DialogCompletionScope.Complete"/>. The helper has no
/// AppKit dependency, so it is compiled directly into this test project (see the Compile
/// Include in the .csproj) and exercised without needing an AppKit/macOS runtime.
/// </summary>
public class DialogCompletionScopeTests
{
    [Fact]
    public void Complete_RunsCallback_ThenDisposesRegistrationScope()
    {
        var disposed = false;
        var callbackRan = false;
        var registrationScope = new DisposableStub(() => disposed = true);
        var completionScope = new DialogCompletionScope(registrationScope);

        completionScope.Complete(
            () =>
            {
                callbackRan = true;
                // The registration scope must still be alive while the result callback runs, so
                // e.g. a button's registration is still valid when its result is being read.
                Assert.False(disposed);
            },
            ex => throw new InvalidOperationException("Fallback should not run when onCompleted succeeds", ex));

        Assert.True(callbackRan);
        Assert.True(disposed);
    }

    [Fact]
    public void Complete_WhenCallbackThrows_RunsFallbackInstead_AndDoesNotRethrow()
    {
        var disposed = false;
        var registrationScope = new DisposableStub(() => disposed = true);
        var completionScope = new DialogCompletionScope(registrationScope);
        Exception? observed = null;
        var thrown = new InvalidOperationException("Simulated failure while computing the result");

        var exception = Record.Exception(() =>
            completionScope.Complete(
                () => throw thrown,
                ex => observed = ex));

        // No exception may ever escape Complete - it runs on the native AppKit completion
        // callback's call stack, where an unhandled managed exception would cross into native
        // code instead of behaving like a normal .NET exception.
        Assert.Null(exception);
        Assert.Same(thrown, observed);
        Assert.True(disposed);
    }

    [Fact]
    public void Complete_WhenBothCallbackAndFallbackThrow_SwallowsFailure_AndStillDisposesOnce()
    {
        var disposeCount = 0;
        var registrationScope = new DisposableStub(() => disposeCount++);
        var completionScope = new DialogCompletionScope(registrationScope);

        var exception = Record.Exception(() =>
            completionScope.Complete(
                () => throw new InvalidOperationException("onCompleted failure"),
                _ => throw new InvalidOperationException("onUnhandledException failure too")));

        Assert.Null(exception);
        Assert.Equal(1, disposeCount);
        Assert.True(completionScope.IsCompleted);
    }

    [Fact]
    public void Complete_CalledTwice_OnlyRunsCallbackAndDisposesOnce()
    {
        var disposeCount = 0;
        var callbackCount = 0;
        var registrationScope = new DisposableStub(() => disposeCount++);
        var completionScope = new DialogCompletionScope(registrationScope);

        completionScope.Complete(() => callbackCount++, _ => { });
        completionScope.Complete(() => callbackCount++, _ => { });

        Assert.Equal(1, callbackCount);
        Assert.Equal(1, disposeCount);
    }

    [Fact]
    public void Complete_CalledTwice_AfterFirstCallThrew_StillOnlyRunsOnce()
    {
        // A duplicate/re-entrant native completion callback must not re-run the fallback (or
        // onCompleted) a second time, even when the first completion needed the fallback path.
        var disposeCount = 0;
        var completedCount = 0;
        var fallbackCount = 0;
        var registrationScope = new DisposableStub(() => disposeCount++);
        var completionScope = new DialogCompletionScope(registrationScope);

        completionScope.Complete(
            () => { completedCount++; throw new InvalidOperationException("first failure"); },
            _ => fallbackCount++);
        completionScope.Complete(
            () => completedCount++,
            _ => fallbackCount++);

        Assert.Equal(1, completedCount);
        Assert.Equal(1, fallbackCount);
        Assert.Equal(1, disposeCount);
    }

    [Fact]
    public void IsCompleted_ReflectsWhetherCompleteHasRun()
    {
        var completionScope = new DialogCompletionScope(new DisposableStub(() => { }));

        Assert.False(completionScope.IsCompleted);

        completionScope.Complete(() => { }, _ => { });

        Assert.True(completionScope.IsCompleted);
    }

    [Fact]
    public void Constructor_NullRegistrationScope_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DialogCompletionScope(null!));
    }

    [Fact]
    public void Complete_NullCallback_Throws()
    {
        var completionScope = new DialogCompletionScope(new DisposableStub(() => { }));

        Assert.Throws<ArgumentNullException>(() => completionScope.Complete(null!, _ => { }));
    }

    [Fact]
    public void Complete_NullUnhandledExceptionHandler_Throws()
    {
        var completionScope = new DialogCompletionScope(new DisposableStub(() => { }));

        Assert.Throws<ArgumentNullException>(() => completionScope.Complete(() => { }, null!));
    }

    sealed class DisposableStub : IDisposable
    {
        readonly Action _onDispose;

        public DisposableStub(Action onDispose) => _onDispose = onDispose;

        public void Dispose() => _onDispose();
    }
}
