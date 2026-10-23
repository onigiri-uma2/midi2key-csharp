using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
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

    [TestMethod]
    public void ComparisonFailures_AreReportedAsFailuresInsteadOfSuccessfulEmptyLists()
    {
        var dryWet = PortEnumerationResult.Failed("backend timed out");
        var inProc = PortEnumerationResult.Failed("WinMM query failed");
        var winMmChild = OutOfProcessWinMmResult.Failed("child timed out", timedOut: true);
        var winRt = WinRtEnumerationResult.Failed("WinRT query failed");

        var report = MidiDeviceDiagnostics.CompareEndpointsCore(
            "fake test", dryWet, inProc, winMmChild, winRt, Array.Empty<MidiPortInfo>(), 12);

        Assert.IsFalse(report.DryWetMidiResult.Success);
        Assert.IsFalse(report.InProcessWinMmResult.Success);
        Assert.IsFalse(report.OutOfProcessWinMm.Success);
        Assert.IsFalse(report.WinRtResult.Success);
        Assert.IsTrue(report.HasDiscrepancy);
        StringAssert.Contains(report.DiscrepancySummary, "取得に失敗");
        Assert.AreEqual(0, report.DryWetMidiPorts.Count);
        Assert.AreEqual(0, report.InProcessWinMmPorts.Count);
        Assert.AreEqual(0, report.WinRtDevices.Count);
    }

    [TestMethod]
    public async Task ComparisonQueryTimeout_IsBoundedAndSharesOneHungProbe()
    {
        Type flightType = typeof(MidiDeviceDiagnostics).GetNestedType("SingleFlight`1", BindingFlags.NonPublic)!
            .MakeGenericType(typeof(PortEnumerationResult));
        object flight = Activator.CreateInstance(flightType, nonPublic: true)!;
        MethodInfo run = flightType.GetMethod("Run", BindingFlags.Instance | BindingFlags.Public)!;
        var pending = new TaskCompletionSource<PortEnumerationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int probeCalls = 0;
        Func<Task<PortEnumerationResult>> probe = () =>
        {
            Interlocked.Increment(ref probeCalls);
            entered.TrySetResult();
            return pending.Task;
        };

        try
        {
            var first = (Task<PortEnumerationResult>)run.Invoke(flight, new object[] { probe })!;
            var second = (Task<PortEnumerationResult>)run.Invoke(flight, new object[] { probe })!;
            Assert.AreSame(first, second, "Concurrent comparisons must share the outstanding native query.");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(1, probeCalls);

            MethodInfo awaitQuery = typeof(MidiDeviceDiagnostics)
                .GetMethod("AwaitQueryAsync", BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(typeof(PortEnumerationResult));
            Func<string, PortEnumerationResult> timedOut = error => PortEnumerationResult.Failed(error);
            var bounded = (Task<PortEnumerationResult>)awaitQuery.Invoke(null,
                new object[] { first, "fake comparison endpoint", timedOut })!;
            var failure = await bounded.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(failure.Success, "A timed out query must remain a failure, never an empty successful list.");
            StringAssert.Contains(failure.ErrorMessage!, "完了しませんでした");

            pending.SetResult(PortEnumerationResult.Succeeded(new[] { "Late Port" }));
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            var retry = (Task<PortEnumerationResult>)run.Invoke(flight, new object[] { probe })!;
            Assert.AreNotSame(first, retry, "A new query may begin after the previous native call has completed.");
            Assert.IsTrue((await retry.WaitAsync(TimeSpan.FromSeconds(2))).Success);
            Assert.AreEqual(2, probeCalls);
        }
        finally
        {
            pending.TrySetResult(PortEnumerationResult.Succeeded(Array.Empty<string>()));
        }
    }
}
