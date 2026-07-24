using Microsoft.Maui.Platforms.MacOS.Platform;

namespace Microsoft.Maui.DevFlow.Tests;

/// <summary>
/// Tests for <see cref="DialogCompletionScope"/>, the helper that lets
/// <c>AlertManagerSubscription</c> defer disposing a dialog's
/// <see cref="DialogNativeRegistrationScope"/> until an AppKit sheet's completion handler runs,
/// instead of a synchronous try/finally around a blocking <c>NSAlert.RunModal()</c> call. The
/// helper has no AppKit dependency, so it is compiled directly into this test project (see the
/// Compile Include in the .csproj) and exercised without needing an AppKit/macOS runtime.
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

        completionScope.Complete(() =>
        {
            callbackRan = true;
            // The registration scope must still be alive while the result callback runs, so
            // e.g. a button's registration is still valid when its result is being read.
            Assert.False(disposed);
        });

        Assert.True(callbackRan);
        Assert.True(disposed);
    }

    [Fact]
    public void Complete_WhenCallbackThrows_StillDisposesRegistrationScope()
    {
        var disposed = false;
        var registrationScope = new DisposableStub(() => disposed = true);
        var completionScope = new DialogCompletionScope(registrationScope);

        Assert.Throws<InvalidOperationException>(() =>
            completionScope.Complete(() => throw new InvalidOperationException("Simulated failure while setting the result")));

        Assert.True(disposed);
    }

    [Fact]
    public void Complete_CalledTwice_OnlyRunsCallbackAndDisposesOnce()
    {
        var disposeCount = 0;
        var callbackCount = 0;
        var registrationScope = new DisposableStub(() => disposeCount++);
        var completionScope = new DialogCompletionScope(registrationScope);

        completionScope.Complete(() => callbackCount++);
        completionScope.Complete(() => callbackCount++);

        Assert.Equal(1, callbackCount);
        Assert.Equal(1, disposeCount);
    }

    [Fact]
    public void IsCompleted_ReflectsWhetherCompleteHasRun()
    {
        var completionScope = new DialogCompletionScope(new DisposableStub(() => { }));

        Assert.False(completionScope.IsCompleted);

        completionScope.Complete(() => { });

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

        Assert.Throws<ArgumentNullException>(() => completionScope.Complete(null!));
    }

    sealed class DisposableStub : IDisposable
    {
        readonly Action _onDispose;

        public DisposableStub(Action onDispose) => _onDispose = onDispose;

        public void Dispose() => _onDispose();
    }
}
