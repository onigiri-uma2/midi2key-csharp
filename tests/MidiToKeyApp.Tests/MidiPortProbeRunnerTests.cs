using Microsoft.VisualStudio.TestTools.UnitTesting;
using MidiToKeyApp;

namespace MidiToKeyApp.Tests;

[TestClass]
public sealed class MidiPortProbeRunnerTests
{
    [TestMethod]
    public async Task BackendHang_ReturnsBoundedFailure_AndReusesLateEnumeration()
    {
        var backend = new TaskCompletionSource<PortEnumerationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int backendCalls = 0;
        var runner = new MidiPortProbeRunner(
            () => { Interlocked.Increment(ref backendCalls); return backend.Task.GetAwaiter().GetResult(); },
            _ => OutOfProcessWinMmResult.Succeeded(Array.Empty<string>()),
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(100));

        var first = await runner.ProbeAsync(20).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(first.Backend.Success);
        StringAssert.Contains(first.Backend.ErrorMessage!, "タイムアウト");
        Assert.IsTrue(first.WinMm.Success);
        Assert.AreEqual(1, backendCalls);

        try
        {
            var secondCallWhileNativeProbeIsStillBlocked = runner.ProbeAsync(20);
            await Task.Delay(10);
            Assert.AreEqual(1, backendCalls, "A timed-out native enumeration must not be duplicated while still blocked.");
            backend.SetResult(PortEnumerationResult.Succeeded(new[] { "Late Port" }));
            var second = await secondCallWhileNativeProbeIsStillBlocked.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(second.Backend.Success);
            CollectionAssert.AreEqual(new[] { "Late Port" }, second.Backend.Ports.ToArray());
            Assert.AreEqual(1, backendCalls);
        }
        finally
        {
            backend.TrySetResult(PortEnumerationResult.Succeeded(Array.Empty<string>()));
        }
    }

    [TestMethod]
    public async Task CallerCancellation_EndsWait_AndLateCompletionCannotChangeCanceledAttempt()
    {
        var backend = new TaskCompletionSource<PortEnumerationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new MidiPortProbeRunner(
            () => backend.Task.GetAwaiter().GetResult(),
            _ => OutOfProcessWinMmResult.Succeeded(Array.Empty<string>()),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource();

        var canceledAttempt = runner.ProbeAsync(20, cancellation.Token);
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await canceledAttempt.WaitAsync(TimeSpan.FromSeconds(2)));
            backend.SetResult(PortEnumerationResult.Succeeded(new[] { "Late Port" }));
            var retry = await runner.ProbeAsync(20).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(retry.Backend.Success);
            CollectionAssert.AreEqual(new[] { "Late Port" }, retry.Backend.Ports.ToArray());
            Assert.IsTrue(canceledAttempt.IsCanceled, "Completing the shared native probe later must not revive its canceled caller.");
        }
        finally
        {
            backend.TrySetResult(PortEnumerationResult.Succeeded(Array.Empty<string>()));
        }
    }

    [TestMethod]
    public async Task ChildProbeHang_ReturnsTimedOutFailure()
    {
        var child = new TaskCompletionSource<OutOfProcessWinMmResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new MidiPortProbeRunner(
            () => PortEnumerationResult.Succeeded(Array.Empty<string>()),
            _ => child.Task.GetAwaiter().GetResult(),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(30));

        var result = await runner.ProbeAsync(20).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(result.Backend.Success);
        Assert.IsFalse(result.WinMm.Success);
        Assert.IsTrue(result.WinMm.TimedOut);
        child.TrySetResult(OutOfProcessWinMmResult.Succeeded(Array.Empty<string>()));
    }
}
