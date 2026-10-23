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

    private sealed class BlockingMidiListener : IMidiListener
    {
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCount;
        public int StopCount;
        public int StartThreadId;
        public int StopThreadId;
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
