using System.Reflection;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MidiToKeyApp;

namespace MidiToKeyApp.Tests;

[TestClass]
public sealed class MidiListenerLifecycleTests
{
    [TestMethod]
    public async Task BlockingNativeStart_RunsOffCaller_AndQueuesOneStopAfterLateCompletion()
    {
        var listener = new BlockingMidiListener();
        Type lifecycleType = typeof(Form1).Assembly.GetType("MidiToKeyApp.MidiListenerLifecycle", throwOnError: true)!;
        object lifecycle = Activator.CreateInstance(lifecycleType, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: new object[] { listener }, culture: null)!;
        MethodInfo startMethod = lifecycleType.GetMethod("StartAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo stopMethod = lifecycleType.GetMethod("StopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        using var cancellation = new CancellationTokenSource();
        int callerThread = Environment.CurrentManagedThreadId;
        var startTask = (Task<MidiPortStartResult>)startMethod.Invoke(lifecycle,
            new object?[] { new[] { "Fake Port" }, 7L, null, cancellation.Token })!;
        await listener.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreNotEqual(callerThread, listener.StartThreadId, "Synchronous native start must run away from the WinForms thread.");

        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();
        await AssertCanceledAsync(startTask.WaitAsync(cancellation.Token));
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250), "Cancellation should release the awaiting UI while native start is blocked.");

        var firstStop = (Task)stopMethod.Invoke(lifecycle, null)!;
        var duplicateStop = (Task)stopMethod.Invoke(lifecycle, null)!;
        Assert.AreSame(firstStop, duplicateStop, "Concurrent stop requests should share one pending native stop.");
        Assert.AreEqual(0, listener.StopCount, "Stop must wait until the in-flight native start returns.");

        listener.ReleaseStart.TrySetResult();
        await AssertCanceledAsync(startTask);
        await firstStop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, listener.StartCount);
        Assert.AreEqual(1, listener.StopCount, "A canceled late start must be balanced by exactly one stop.");
        Assert.AreNotEqual(callerThread, listener.StopThreadId);
    }

    [TestMethod]
    public void GetPortNames_DisposeFailureStillAttemptsEveryDeviceAndReturnsFailure()
    {
        Type listenerType = typeof(MidiListener);
        MethodInfo collect = listenerType.GetMethod("CollectPortNames", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(string));
        var attempted = new List<string>();

        var result = (PortEnumerationResult)collect.Invoke(null, new object[]
        {
            new[] { "first", "second", "third" },
            (Func<string, string>)(name => name),
            (Action<string>)(name =>
            {
                attempted.Add(name);
                if (name == "first") throw new InvalidOperationException("fake dispose failure");
            })
        })!;

        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, attempted,
            "A disposal failure must not prevent later enumerated devices from being released.");
        Assert.IsFalse(result.Success, "Any device disposal failure makes enumeration unreliable.");
        Assert.IsNotNull(result.Exception);
        StringAssert.Contains(result.Exception!.Message, "fake dispose failure");
    }

    [TestMethod]
    public void Stop_DisposeFailureIsReportedAfterEveryDeviceIsTried_AndAllDevicesBecomeInactive()
    {
        using var listener = new MidiListener();
        int failedReleaseAttempts = 0;
        int successfulReleaseAttempts = 0;
        listener.AddTestDevice("Failing Port", "fake-fail", 7, () =>
        {
            Interlocked.Increment(ref failedReleaseAttempts);
            throw new InvalidOperationException("fake dispose failure");
        });
        listener.AddTestDevice("Healthy Port", "fake-ok", 7, () =>
            Interlocked.Increment(ref successfulReleaseAttempts));

        var error = Assert.ThrowsException<AggregateException>(() => listener.Stop());

        Assert.AreEqual(1, failedReleaseAttempts);
        Assert.AreEqual(1, successfulReleaseAttempts, "A disposal error must not prevent cleanup of later devices.");
        Assert.AreEqual(0, listener.GetActivePorts().Count, "Failed disposal must still retire the device from input state.");
        StringAssert.Contains(error.Message, "解放に失敗");
    }

    [TestMethod]
    public void StartReleaseFailure_ContinuesReleasingDevices_StopsOpenedDevices_AndReportsFailure()
    {
        Type listenerType = typeof(MidiListener);
        MethodInfo tryRelease = listenerType.GetMethod("TryReleaseDuringStart", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo throwIfFailed = listenerType.GetMethod("ThrowIfStartReleaseFailed", BindingFlags.Static | BindingFlags.NonPublic)!;
        var errors = new List<Exception>();
        int failedReleaseCount = 0;
        int laterReleaseCount = 0;
        int openedDevicesStopCount = 0;

        tryRelease.Invoke(null, new object[] { "Failing unselected", (Action)(() =>
        {
            Interlocked.Increment(ref failedReleaseCount);
            throw new InvalidOperationException("fake dispose failure");
        }), errors, false });
        tryRelease.Invoke(null, new object[] { "Later unselected", (Action)(() => Interlocked.Increment(ref laterReleaseCount)), errors, false });

        var wrapped = Assert.ThrowsException<TargetInvocationException>(() =>
            throwIfFailed.Invoke(null, new object[] { errors, (Action)(() => Interlocked.Increment(ref openedDevicesStopCount)) }));

        Assert.IsInstanceOfType<AggregateException>(wrapped.InnerException);
        Assert.AreEqual(1, failedReleaseCount);
        Assert.AreEqual(1, laterReleaseCount, "A failed native release must not skip later devices.");
        Assert.AreEqual(1, openedDevicesStopCount, "Previously opened devices must be stopped after a release failure.");
    }

    [TestMethod]
    public void HealthCheck_DisposeFailureEmitsSafetyStopNotification_AndRemovesInputDevice()
    {
        using var listener = new MidiListener();
        int disposeAttempts = 0;
        listener.SetCurrentGenerationForTesting(4);
        listener.AddTestDevice("Gone Port", "fake-gone", 4, () =>
        {
            Interlocked.Increment(ref disposeAttempts);
            throw new InvalidOperationException("fake dispose failure");
        });
        MidiDeviceDisconnectedData? notification = null;
        listener.OnDeviceDisconnected += data => notification = data;

        Assert.ThrowsException<AggregateException>(() => listener.CheckDeviceHealth(
            activeOsDeviceNames: Array.Empty<string>(),
            outOfProcessWinMmResult: OutOfProcessWinMmResult.Succeeded(Array.Empty<string>()),
            targetGeneration: 4,
            onlineDeviceNames: Array.Empty<string>()));

        Assert.AreEqual(1, disposeAttempts);
        Assert.AreEqual(0, listener.GetActivePorts().Count);
        Assert.IsNotNull(notification);
        Assert.AreEqual(string.Empty, notification!.Value.DeviceId, "The notification must request a whole-session safety stop.");
        StringAssert.Contains(notification.Value.Reason, "解放失敗");
    }

    [TestMethod]
    public async Task StopFailure_DisablesFutureNativeStarts()
    {
        var listener = new BlockingMidiListener { ThrowOnStop = true };
        Type lifecycleType = typeof(Form1).Assembly.GetType("MidiToKeyApp.MidiListenerLifecycle", throwOnError: true)!;
        object lifecycle = Activator.CreateInstance(lifecycleType, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: new object[] { listener }, culture: null)!;
        MethodInfo startMethod = lifecycleType.GetMethod("StartAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo stopMethod = lifecycleType.GetMethod("StopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var start = (Task<MidiPortStartResult>)startMethod.Invoke(lifecycle,
            new object?[] { new[] { "Fake Port" }, 7L, null, CancellationToken.None })!;
        await listener.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        listener.ReleaseStart.TrySetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = (Task)stopMethod.Invoke(lifecycle, null)!;
        await Assert.ThrowsExceptionAsync<AggregateException>(async () => await stop.WaitAsync(TimeSpan.FromSeconds(2)));
        var restartError = Assert.ThrowsException<TargetInvocationException>(() => startMethod.Invoke(lifecycle,
            new object?[] { new[] { "Fake Port" }, 8L, null, CancellationToken.None }));
        Assert.IsInstanceOfType<InvalidOperationException>(restartError.InnerException);
        Assert.AreEqual(1, listener.StartCount, "A release failure must not allow another native start.");
    }

    private sealed class BlockingMidiListener : IMidiListener
    {
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCount;
        public int StopCount;
        public int StartThreadId;
        public int StopThreadId;
        public bool ThrowOnStop { get; set; }
        public long CurrentGeneration { get; private set; }
        public InitialPortSnapshot? CurrentInitialSnapshot { get; private set; }
        public event Action<MidiNoteData>? OnNoteReceived { add { } remove { } }
        public event Action<MidiControlData>? OnControlReceived { add { } remove { } }
        public event Action<MidiDeviceDisconnectedData>? OnDeviceDisconnected { add { } remove { } }

        public MidiPortStartResult Start(IEnumerable<string> portNames, long? specificGeneration = null, InitialPortSnapshot? initialSnapshot = null)
        {
            Interlocked.Increment(ref StartCount);
            StartThreadId = Environment.CurrentManagedThreadId;
            CurrentGeneration = specificGeneration ?? 0;
            CurrentInitialSnapshot = initialSnapshot;
            StartEntered.TrySetResult();
            ReleaseStart.Task.GetAwaiter().GetResult();
            return new MidiPortStartResult(CurrentGeneration,
                new[] { new MidiPortInfo("fake-id", "Fake Port", DeviceState.Active) }, Array.Empty<MidiPortError>());
        }

        public void Stop()
        {
            Interlocked.Increment(ref StopCount);
            StopThreadId = Environment.CurrentManagedThreadId;
            if (ThrowOnStop) throw new AggregateException("fake dispose failure");
        }

        public IReadOnlyList<MidiPortInfo> GetActivePorts() => Array.Empty<MidiPortInfo>();
        public void CheckDeviceHealth(IEnumerable<string>? activeOsDeviceNames = null,
            OutOfProcessWinMmResult? outOfProcessWinMmResult = null, long? targetGeneration = null) { }
        public void SimulateDeviceDisconnected(string deviceId, string reason = "テストシミュレート") { }
        public void Dispose() { }
    }

    private static async Task AssertCanceledAsync(Task task)
    {
        try
        {
            await task;
            Assert.Fail("Expected cancellation.");
        }
        catch (OperationCanceledException)
        {
            // TaskCanceledException is the standard WaitAsync cancellation subtype.
        }
    }
}
