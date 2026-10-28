using Microsoft.VisualStudio.TestTools.UnitTesting;
using MidiToKeyApp;
using WindowsInput.Native;

namespace MidiToKeyApp.Tests;

[TestClass]
public class TransposeTests
{
    [DataTestMethod]
    [DataRow(true, 12, 60, 72)]
    [DataRow(false, 12, 60, 72)]
    [DataRow(true, -12, 60, 48)]
    [DataRow(false, -1, 60, 59)]
    public void Transpose_UsesShiftedMapping_AndPreservesHeldKeyRelease(bool publish, int offset, int raw, int mapped)
    {
        var settings = new AppSettings { TransposeSemitones = offset,
            Mapping = new() { [mapped.ToString()] = "a", [raw.ToString()] = "b", ["pedal"] = "space" } };
        var output = new FakeKeyboardOutput();
        var tracker = new InputTracker(new KeySimulator(output), () => settings);
        if (publish) tracker.PublishResolvedMapping(settings);
        long generation = tracker.StartSession();
        tracker.ProcessNoteEvent(new("d", "Keyboard", 0, raw, 100, true, generation));
        Assert.AreEqual(VirtualKeyCode.VK_A, output.History.Single().Key);
        settings.TransposeSemitones = 0;
        if (publish) tracker.PublishResolvedMapping(settings);
        tracker.ProcessNoteEvent(new("d", "Keyboard", 0, raw, 0, false, generation));
        Assert.AreEqual(new FakeKeyboardOutput.SentEvent(VirtualKeyCode.VK_A, false), output.History.Last());
        tracker.ProcessNoteEvent(new("d", "Keyboard", 0, raw, 100, true, generation));
        Assert.AreEqual(new FakeKeyboardOutput.SentEvent(VirtualKeyCode.VK_B, true), output.History.Last());
        tracker.StopSession();
        Assert.AreEqual(2, output.KeyDownCount);
        Assert.AreEqual(2, output.KeyUpCount);
    }

    [DataTestMethod]
    [DataRow(48, 127)]
    [DataRow(-48, 0)]
    public void OutOfRange_IsIgnoredWithoutClamping_AndPedalUnaffected(int offset, int raw)
    {
        var settings = new AppSettings { TransposeSemitones = offset,
            Mapping = new() { ["0"] = "a", ["127"] = "b", ["pedal"] = "space" } };
        var output = new FakeKeyboardOutput();
        var tracker = new InputTracker(new KeySimulator(output), () => settings);
        tracker.PublishResolvedMapping(settings);
        long generation = tracker.StartSession();
        tracker.ProcessNoteEvent(new("d", "Keyboard", 0, raw, 100, true, generation));
        tracker.ProcessNoteEvent(new("d", "Keyboard", 0, raw, 0, false, generation));
        Assert.AreEqual(0, output.History.Count);
        tracker.ProcessControlEvent(new("d", "Keyboard", 0, 64, 127, generation));
        tracker.ProcessControlEvent(new("d", "Keyboard", 0, 64, 0, generation));
        Assert.AreEqual(2, output.History.Count);
        Assert.IsTrue(output.History.All(e => e.Key == VirtualKeyCode.SPACE));
        tracker.StopSession();
    }

    [TestMethod]
    public void Capture_UsesShiftedNote_AndTestModeStillDoesNotSend()
    {
        var settings = new AppSettings { TransposeSemitones = 12, Mapping = new() { ["72"] = "a" } };
        var output = new FakeKeyboardOutput();
        var tracker = new InputTracker(new KeySimulator(output), () => settings);
        tracker.PublishResolvedMapping(settings);
        string? captured = null;
        tracker.OnInputCaptured += value => captured = value;
        long generation = tracker.StartConversionSession(true);
        tracker.SetCapturing(true);
        tracker.ProcessNoteEvent(new("d", "Keyboard", 0, 60, 100, true, generation));
        Assert.AreEqual("72", captured);
        tracker.ProcessNoteEvent(new("d", "Keyboard", 0, 60, 0, false, generation));
        captured = null;
        tracker.ProcessNoteEvent(new("d", "Keyboard", 0, 127, 100, true, generation));
        Assert.IsNull(captured);
        tracker.SetCapturing(false);
        tracker.ProcessNoteEvent(new("d", "Keyboard", 0, 60, 100, true, generation));
        tracker.StopSession();
        Assert.AreEqual(0, output.History.Count);
    }

    [TestMethod]
    public void Settings_PersistsTranspose_DefaultsLegacyToZero_RejectsInvalidValues()
    {
        string path = Path.Combine(Path.GetTempPath(), $"transpose-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"selected_ports\":[],\"mapping\":{},\"keyboard_layout\":\"JIS\"}");
            Assert.AreEqual(0, SettingsManager.Load(path).TransposeSemitones);
            var settings = new AppSettings { TransposeSemitones = -24 };
            SettingsManager.Save(path, settings);
            Assert.AreEqual(-24, SettingsManager.Load(path).TransposeSemitones);
            foreach (int invalid in new[] { -49, 49, int.MaxValue, int.MinValue })
            {
                settings.TransposeSemitones = invalid;
                Assert.ThrowsException<InvalidDataException>(() => SettingsManager.Save(path, settings));
                File.WriteAllText(path, $"{{\"transpose_semitones\":{invalid}}}");
                Assert.ThrowsException<InvalidDataException>(() => SettingsManager.Load(path));
            }
        }
        finally { File.Delete(path); }
    }
}
