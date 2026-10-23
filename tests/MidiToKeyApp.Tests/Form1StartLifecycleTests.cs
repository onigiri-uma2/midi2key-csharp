using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MidiToKeyApp;

namespace MidiToKeyApp.Tests;

[TestClass]
public sealed class Form1StartLifecycleTests
{
    [TestMethod]
    public async Task HotkeyDuringHangingProbe_CancelsStart_AndLateResultCannotStartListener()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedResult = new TaskCompletionSource<MidiPortProbeService.ProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new FakeMidiListener();

        try
        {
            await RunFormAsync(async form =>
            {
                form.StartForTest();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                form.SendHotkeyForTest();
                await WaitUntilAsync(() => !form.TransitioningForTest);
                Assert.AreEqual(0, listener.StartCount);

                delayedResult.SetResult(AvailablePortResult("Port A"));
                await Task.Delay(100);
                Assert.AreEqual(0, listener.StartCount, "A late probe result must not restart a canceled start attempt.");
                Assert.IsFalse(form.ListeningForTest);
            }, listener, (timeout, token) =>
            {
                entered.TrySetResult();
                return delayedResult.Task.WaitAsync(token);
            });
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            delayedResult.TrySetResult(AvailablePortResult("Port A"));
        }
    }

    [TestMethod]
    public async Task SettingsReplacedDuringProbe_AbortsStartAfterProbeCompletes()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<MidiPortProbeService.ProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new FakeMidiListener();

        try
        {
            await RunFormAsync(async form =>
            {
                form.StartForTest();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                form.ReplaceSettingsForTest(CreateSettings());
                releaseProbe.SetResult(AvailablePortResult());
                await WaitUntilAsync(() => !form.TransitioningForTest);
                Assert.AreEqual(0, listener.StartCount, "A start using a stale settings snapshot must be discarded.");
                Assert.IsFalse(form.ListeningForTest);
            }, listener, (timeout, token) =>
            {
                entered.TrySetResult();
                return releaseProbe.Task.WaitAsync(token);
            });
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            releaseProbe.TrySetResult(AvailablePortResult());
        }
    }

    [TestMethod]
    public async Task FormClosingDuringProbe_CancelsStart_AndLateResultCannotStartListener()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedResult = new TaskCompletionSource<MidiPortProbeService.ProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new FakeMidiListener();

        try
        {
            await RunFormAsync(async form =>
            {
                form.StartForTest();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                form.SimulateFormClosingForTest();
                await WaitUntilAsync(() => !form.TransitioningForTest);
                Assert.AreEqual(0, listener.StartCount);

                delayedResult.SetResult(AvailablePortResult("Port A"));
                await Task.Delay(100);
                Assert.AreEqual(0, listener.StartCount, "A late probe result must not start MIDI after form closing begins.");
                Assert.IsFalse(form.ListeningForTest);
            }, listener, (timeout, token) =>
            {
                entered.TrySetResult();
                return delayedResult.Task.WaitAsync(token);
            });
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            delayedResult.TrySetResult(AvailablePortResult("Port A"));
        }
    }

    [TestMethod]
    public async Task PortSelectionChangedDuringProbe_AbortsStart()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<MidiPortProbeService.ProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new FakeMidiListener();

        try
        {
            await RunFormAsync(async form =>
            {
                form.StartForTest();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                form.SelectOnlyPortForTest("Port B");
                releaseProbe.SetResult(AvailablePortResult("Port A", "Port B"));
                await WaitUntilAsync(() => !form.TransitioningForTest);
                Assert.AreEqual(0, listener.StartCount, "Changing selected ports invalidates the pending start snapshot.");
            }, listener, (timeout, token) =>
            {
                entered.TrySetResult();
                return releaseProbe.Task.WaitAsync(token);
            });
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            releaseProbe.TrySetResult(AvailablePortResult());
        }
    }

    [TestMethod]
    public async Task HotkeyDuringWarmup_CancelsPreparationWithoutStartingListener()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWarmup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new FakeMidiListener();

        try
        {
            await RunFormAsync(async form =>
            {
                form.StartForTest();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                form.SendHotkeyForTest();
                await WaitUntilAsync(() => !form.TransitioningForTest);
                releaseWarmup.TrySetResult();
                Assert.AreEqual(0, listener.StartCount, "Canceling warmup must stop the pending start before MIDI opens.");
                Assert.IsFalse(form.ListeningForTest);
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A")), async (snapshot, token) =>
            {
                entered.TrySetResult();
                await releaseWarmup.Task.WaitAsync(token);
            });
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            releaseWarmup.TrySetResult();
        }
    }

    [TestMethod]
    public async Task MappingSnapshotChangedDuringWarmup_AbortsStart()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWarmup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new FakeMidiListener();

        try
        {
            await RunFormAsync(async form =>
            {
                form.StartForTest();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                form.ReplaceMappingSnapshotForTest();
                releaseWarmup.SetResult();
                await WaitUntilAsync(() => !form.TransitioningForTest);
                Assert.AreEqual(0, listener.StartCount, "A start must not use the mapping snapshot that was replaced during warmup.");
                Assert.IsFalse(form.ListeningForTest);
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A")), async (snapshot, token) =>
            {
                entered.TrySetResult();
                await releaseWarmup.Task.WaitAsync(token);
            });
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            releaseWarmup.TrySetResult();
        }
    }

    [TestMethod]
    public async Task ValidProbe_StartsListener_AndHotkeyStopsIt()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var listener = new FakeMidiListener();

        try
        {
            await RunFormAsync(async form =>
            {
                form.StartForTest();
                await WaitUntilAsync(() => !form.TransitioningForTest);
                Assert.AreEqual(1, listener.StartCount, "The lifecycle guard must still permit a valid start.");
                Assert.IsTrue(form.ListeningForTest);

                form.SendHotkeyForTest();
                Assert.IsFalse(form.ListeningForTest, "The hotkey should stop a running conversion.");
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A")));
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
        }
    }

    private static async Task RunFormAsync(
        Func<TestForm, Task> scenario,
        FakeMidiListener listener,
        Func<int, CancellationToken, Task<MidiPortProbeService.ProbeResult>> probe,
        Func<ResolvedMappingSnapshot, CancellationToken, Task>? warmup = null)
    {
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new TestForm(listener, probe, warmup);
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        await scenario(form);
                        finished.TrySetResult(null);
                    }
                    catch (Exception ex)
                    {
                        finished.TrySetResult(ex);
                    }
                    finally
                    {
                        form.Close();
                    }
                };
                Application.Run(form);
            }
            catch (Exception ex)
            {
                finished.TrySetResult(ex);
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var error = await finished.Task.WaitAsync(TimeSpan.FromSeconds(8));
        thread.Join(TimeSpan.FromSeconds(2));
        if (error != null) throw new AssertFailedException(error.ToString());
        Assert.IsFalse(thread.IsAlive, "The form message loop should exit after the scenario.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < limit)
            await Task.Delay(10);
        Assert.IsTrue(condition(), "The start transition should finish within two seconds.");
    }

    private static AppSettings CreateSettings() => new()
    {
        KeyboardLayout = "US",
        SelectedPorts = new List<string> { "Port A" },
        Hotkey = new HotkeySettings { Enabled = false }
    };

    private static MidiPortProbeService.ProbeResult AvailablePortResult(params string[] ports) => new(
        PortEnumerationResult.Succeeded(ports),
        OutOfProcessWinMmResult.Succeeded(ports));

    private sealed class TestForm : Form1
    {
        public TestForm(FakeMidiListener listener, Func<int, CancellationToken, Task<MidiPortProbeService.ProbeResult>> probe,
            Func<ResolvedMappingSnapshot, CancellationToken, Task>? warmup)
            : base(new FakeKeyboardOutput(), listener, probe, skipWatcher: true, initialSettings: CreateSettings(), runWarmup: warmup)
        {
            var ports = (CheckedListBox)typeof(Form1).GetField("chkPorts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            ports.Items.Add("Port A");
            ports.Items.Add("Port B");
            ports.SetItemChecked(0, true);
        }

        public bool TransitioningForTest => (bool)typeof(Form1).GetField("_isTransitioning", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        public bool ListeningForTest => typeof(Form1).GetField("inputTracker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this) is InputTracker tracker && tracker.IsListening;

        public void StartForTest() => typeof(Form1).GetMethod("StartConversion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null);

        public void SendHotkeyForTest()
        {
            var message = Message.Create(Handle, 0x0312, (IntPtr)HotkeyManager.DEFAULT_HOTKEY_ID, IntPtr.Zero);
            base.WndProc(ref message);
        }

        public void ReplaceSettingsForTest(AppSettings settings) =>
            typeof(Form1).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, settings);

        public void ReplaceMappingSnapshotForTest()
        {
            var settings = (AppSettings)typeof(Form1).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            settings.Mapping["60"] = "a";
            var tracker = (InputTracker)typeof(Form1).GetField("inputTracker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            tracker.PublishResolvedMapping(settings);
        }

        public void SelectOnlyPortForTest(string name)
        {
            var ports = (CheckedListBox)typeof(Form1).GetField("chkPorts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            ports.SetItemChecked(0, false);
            ports.SetItemChecked(1, name == "Port B");
        }

        public void SimulateFormClosingForTest() => base.OnFormClosing(new FormClosingEventArgs(CloseReason.UserClosing, false));
    }
}
