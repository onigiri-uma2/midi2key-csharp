using Microsoft.VisualStudio.TestTools.UnitTesting;
using MidiToKeyApp;
using WindowsInput.Native;

namespace MidiToKeyApp.Tests;

[TestClass]
public class InputMonitorTests
{
    private static (InputTracker Tracker, FakeKeyboardOutput Output) Create()
    {
        var output = new FakeKeyboardOutput();
        var settings = new AppSettings { Mapping = new() { ["60"] = "a", ["61"] = "!", ["pedal"] = "shift" } };
        var tracker = new InputTracker(new KeySimulator(output), () => settings) { MonitorEnabled = true };
        tracker.PublishResolvedMapping(settings);
        return (tracker, output);
    }

    [TestMethod]
    public void TestSession_SuppressesChordsPedalAndCapture_ThenNormalSessionSends()
    {
        var (tracker, output) = Create();
        long generation = tracker.StartConversionSession(testMode: true);
        foreach (bool capture in new[] { false, true, false })
        {
            tracker.SetCapturing(capture);
            foreach (bool down in new[] { true, false })
            {
                foreach (int note in new[] { 60, 61 })
                    tracker.ProcessNoteEvent(new("device", "Keyboard", 0, note, 100, down, generation));
                tracker.ProcessControlEvent(new("device", "Keyboard", 0, 64, down ? 127 : 0, generation));
            }
        }
        tracker.StopSession();
        Assert.AreEqual(0, output.History.Count);
        Assert.AreEqual(0, tracker.ActiveNotesCount);
        generation = tracker.StartConversionSession();
        tracker.ProcessNoteEvent(new("device", "Keyboard", 0, 60, 100, true, generation));
        Assert.AreEqual(1, output.KeyDownCount);
        tracker.StopSession();
        Assert.AreEqual(1, output.KeyUpCount);
    }

    [TestMethod]
    public void TestSession_ReleasesPreviousKeys_AndRejectsFailedRelease()
    {
        var (tracker, output) = Create();
        long generation = tracker.StartSession();
        tracker.ProcessNoteEvent(new("device", "Keyboard", 0, 60, 100, true, generation));
        output.FailKeys.Add(VirtualKeyCode.VK_A);
        Assert.ThrowsException<InvalidOperationException>(() => tracker.StartConversionSession(true));
        Assert.IsFalse(tracker.IsListening);
        output.FailKeys.Clear();
        generation = tracker.StartConversionSession(true);
        Assert.AreEqual(1, output.KeyUpCount);
        tracker.ProcessNoteEvent(new("device", "Keyboard", 0, 60, 100, true, generation));
        Assert.AreEqual(1, output.KeyDownCount);
        tracker.StopSession();
    }

    [TestMethod]
    public void Monitor_KeepsLatestPerKind_RejectsStaleAndDisconnectedEvents()
    {
        var (tracker, output) = Create();
        long generation = tracker.StartConversionSession(true);
        for (int i = 0; i < 10000; i++)
            tracker.ProcessNoteEvent(new("device", "Keyboard", 1, 60 + i % 12, i % 128, true, generation));
        tracker.ProcessControlEvent(new("device", "Keyboard", 1, 64, 127, generation));
        var monitor = tracker.ReadMonitor();
        Assert.AreEqual(60 + 9999 % 12, monitor.Note!.Value.Number);
        Assert.AreEqual(127, monitor.Pedal!.Value.Value);
        tracker.ProcessNoteEvent(new("old", "Stale", 0, 0, 1, true, generation - 1));
        Assert.AreEqual(monitor, tracker.ReadMonitor());
        tracker.ReleaseDeviceInputs("device", generation);
        Assert.IsNull(tracker.ReadMonitor().Note);
        Assert.IsNull(tracker.ReadMonitor().Pedal);
        tracker.ProcessNoteEvent(new("device", "Keyboard", 1, 60, 100, true, generation));
        Assert.IsNull(tracker.ReadMonitor().Note);
        tracker.StopSession();
        Assert.AreEqual(0, output.History.Count);
    }

    [TestMethod]
    public void Monitor_ClearsOnStopAndNewSession()
    {
        var (tracker, _) = Create();
        long generation = tracker.StartCaptureSession();
        tracker.ProcessNoteEvent(new("device", "Keyboard", 0, 60, 100, true, generation));
        Assert.IsNotNull(tracker.ReadMonitor().Note);
        tracker.StopCaptureSession();
        Assert.IsNull(tracker.ReadMonitor().Note);
        tracker.StartConversionSession(true);
        Assert.IsNull(tracker.ReadMonitor().Note);
        tracker.StopSession();
        tracker.ProcessNoteEvent(new("device", "Keyboard", 0, 60, 100, true, generation));
        Assert.IsNull(tracker.ReadMonitor().Note);
    }
}
