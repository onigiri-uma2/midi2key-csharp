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
                await WaitUntilAsync(() => form.StatusForTest == "ステータス: 停止中");
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A")));
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
        }
    }

    [TestMethod]
    public async Task HotkeyDuringBlockingNativeStart_ReturnsToUi_AndLateInputCannotSendKeys()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var listener = new FakeMidiListener
        {
            StartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ReleaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            EmitNoteAfterBlockedStart = true
        };

        try
        {
            await RunFormAsync(async form =>
            {
                form.StartForTest();
                await listener.StartEntered!.Task.WaitAsync(TimeSpan.FromSeconds(3));
                form.SendHotkeyForTest();
                await WaitUntilAsync(() => !form.TransitioningForTest && !form.ListeningForTest);
                Assert.AreEqual(0, listener.StopCount, "Native stop is queued behind the blocked start.");

                listener.ReleaseStart!.TrySetResult();
                await WaitUntilAsync(() => listener.StopCount == 1);
                Assert.AreEqual(1, listener.StartCount);
                Assert.AreEqual(1, listener.StopCount, "The canceled start must be stopped exactly once after it returns.");
                Assert.AreEqual(0, form.OutputForTest.History.Count,
                    "An input callback emitted by the late native start must be ignored after cancellation.");
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A")));
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            listener.ReleaseStart!.TrySetResult();
        }
    }

    [TestMethod]
    public async Task BlockingNativeStop_DoesNotBlockWinFormsMessagePump()
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
                Assert.IsTrue(form.ListeningForTest);

                listener.StopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                listener.ReleaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                form.SendHotkeyForTest();
                await listener.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

                var uiCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                form.BeginInvoke(new Action(() => uiCallback.TrySetResult()));
                await uiCallback.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.IsFalse(form.ListeningForTest, "Session input must be disabled before the native stop finishes.");
                Assert.AreEqual(0, listener.StopCount, "The fake stop remains blocked until explicitly released.");

                // One start click while cleanup is pending should resume after the native stop completes.
                form.StartForTest();
                listener.ReleaseStop.TrySetResult();
                await WaitUntilAsync(() => !form.TransitioningForTest && listener.StartCount == 2);
                Assert.IsTrue(form.ListeningForTest, "The queued start should become the active session after stop completes.");
                Assert.AreEqual(1, listener.StopCount);
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A")));
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            listener.ReleaseStop?.TrySetResult();
        }
    }

    [TestMethod]
    public async Task NativeStopFailure_KeepsInputDisabled_AndShowsReleaseWarning()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var listener = new FakeMidiListener();

        try
        {
            await RunFormAsync(async form =>
            {
                form.StartForTest();
                await WaitUntilAsync(() => !form.TransitioningForTest && form.ListeningForTest);
                listener.ThrowOnStop = true;

                form.SendHotkeyForTest();
                await WaitUntilAsync(() => form.StatusForTest.Contains("解放失敗", StringComparison.Ordinal));

                Assert.IsFalse(form.ListeningForTest, "Input must stay disabled after native release fails.");
                Assert.AreEqual(1, listener.StartCount);
                Assert.AreEqual(1, listener.StopCount);
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A")));
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
        }
    }

    [TestMethod]
    public async Task CaptureReenterWhileNativeStopIsPending_RestartsOnceAfterCleanup()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var listener = new FakeMidiListener();

        try
        {
            await RunFormAsync(async form =>
            {
                form.BeginCaptureForTest();
                await WaitUntilAsync(() => listener.StartCount == 1);
                Assert.IsTrue(form.CapturingForTest);

                listener.StopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                listener.ReleaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                form.EndCaptureForTest();
                await listener.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                form.BeginCaptureForTest();
                await Task.Delay(50);
                Assert.AreEqual(1, listener.StartCount, "Reentry must wait for the outstanding native stop.");

                listener.ReleaseStop.TrySetResult();
                await WaitUntilAsync(() => listener.StartCount == 2 && form.CapturingForTest);
                await Task.Delay(50);
                Assert.AreEqual(2, listener.StartCount, "One reentry must create exactly one capture session.");
                Assert.AreEqual(1, listener.StopCount);
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A")));
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            listener.ReleaseStop?.TrySetResult();
        }
    }

    [TestMethod]
    public async Task CaptureStartWhenEveryPortFails_StopsCaptureAndReportsFailure()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var listener = new FakeMidiListener { FailAllPorts = true };

        try
        {
            await RunFormAsync(async form =>
            {
                form.SelectBothPortsForTest();
                form.BeginCaptureForTest();
                await WaitUntilAsync(() => listener.StartCount == 1 && listener.StopCount == 1);

                Assert.IsFalse(form.CapturingForTest, "Capture must be ended when none of the selected ports opened.");
                StringAssert.Contains(form.StatusForTest, "キャプチャ開始失敗");
                StringAssert.Contains(form.StatusForTest, "Port A");
                Assert.AreEqual(0, form.OutputForTest.History.Count, "Capture failure must not send keyboard output.");

                listener.FailAllPorts = false;
                form.BeginCaptureForTest();
                await WaitUntilAsync(() => listener.StartCount == 2 && form.CapturingForTest &&
                    !form.StatusForTest.Contains("キャプチャ開始失敗", StringComparison.Ordinal));
                Assert.IsFalse(form.StatusForTest.Contains("キャプチャ開始失敗", StringComparison.Ordinal),
                    "A successful retry must clear the prior capture failure status.");
                form.LeaveNoteForTest();
                await WaitUntilAsync(() => listener.StopCount == 2);
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A", "Port B")));
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
        }
    }

    [TestMethod]
    public async Task CaptureStartWhenOnePortFails_KeepsCaptureAndReportsPartialFailure()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var listener = new FakeMidiListener();
        listener.FailPorts.Add("Port B");

        try
        {
            await RunFormAsync(async form =>
            {
                form.SelectBothPortsForTest();
                form.BeginCaptureForTest();
                await WaitUntilAsync(() => listener.StartCount == 1 && form.CapturingForTest &&
                    form.StatusForTest.Contains("一部ポート開始失敗", StringComparison.Ordinal));

                StringAssert.Contains(form.StatusForTest, "一部ポート開始失敗");
                StringAssert.Contains(form.StatusForTest, "Port B");
                Assert.AreEqual(0, listener.StopCount, "A successfully opened port keeps the capture session active.");
                Assert.AreEqual(0, form.OutputForTest.History.Count, "Capture warnings must not send keyboard output.");
                form.LeaveNoteForTest();
                await WaitUntilAsync(() => listener.StopCount == 1);
                Assert.IsFalse(form.StatusForTest.Contains("キャプチャ中", StringComparison.Ordinal),
                    "Leaving the note field must clear the capture-only partial failure status.");
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A", "Port B")));
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
        }
    }

    [TestMethod]
    public async Task CaptureReentryThenLeave_DoesNotStartAfterPendingStopCompletes()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var listener = new FakeMidiListener();

        try
        {
            await RunFormAsync(async form =>
            {
                form.BeginCaptureForTest();
                await WaitUntilAsync(() => listener.StartCount == 1);
                listener.StopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                listener.ReleaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                form.EndCaptureForTest();
                await listener.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

                form.BeginCaptureForTest();
                form.EndCaptureForTest();
                listener.ReleaseStop.TrySetResult();
                await WaitUntilAsync(() => !form.CapturingForTest);
                await Task.Delay(100);
                Assert.AreEqual(1, listener.StartCount, "A stale reentry continuation must not open MIDI after leave.");
                Assert.AreEqual(1, listener.StopCount);
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A")));
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            listener.ReleaseStop?.TrySetResult();
        }
    }

    [TestMethod]
    public async Task CaptureReentryThenConversion_DoesNotStartCaptureAfterPendingStopCompletes()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var listener = new FakeMidiListener();

        try
        {
            await RunFormAsync(async form =>
            {
                form.BeginCaptureForTest();
                await WaitUntilAsync(() => listener.StartCount == 1);
                listener.StopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                listener.ReleaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                form.EndCaptureForTest();
                await listener.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

                form.BeginCaptureForTest();
                form.StartForTest();
                listener.ReleaseStop.TrySetResult();
                await WaitUntilAsync(() => listener.StartCount == 2 && form.ListeningForTest);
                await Task.Delay(100);
                Assert.AreEqual(2, listener.StartCount, "The stale capture continuation must not replace the conversion session.");
                Assert.IsTrue(form.ListeningForTest, "Conversion should remain the active session after the stale capture continuation finishes.");
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A")));
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            listener.ReleaseStop?.TrySetResult();
        }
    }

    [TestMethod]
    public async Task CaptureReentryThenFormClosing_DoesNotStartAfterPendingStopCompletes()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var listener = new FakeMidiListener();

        try
        {
            await RunFormAsync(async form =>
            {
                form.BeginCaptureForTest();
                await WaitUntilAsync(() => listener.StartCount == 1);
                listener.StopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                listener.ReleaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                form.EndCaptureForTest();
                await listener.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

                form.BeginCaptureForTest();
                form.SimulateFormClosingForTest();
                listener.ReleaseStop.TrySetResult();
                await WaitUntilAsync(() => !form.CapturingForTest);
                await Task.Delay(100);
                Assert.AreEqual(1, listener.StartCount, "A late capture continuation must not open MIDI after form closing begins.");
                Assert.IsTrue(listener.StopCount >= 1, "Form closing should finish the pending MIDI cleanup.");
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A")));
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            listener.ReleaseStop?.TrySetResult();
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
            : this(new FakeKeyboardOutput(), listener, probe, warmup)
        {
        }

        private TestForm(FakeKeyboardOutput output, FakeMidiListener listener,
            Func<int, CancellationToken, Task<MidiPortProbeService.ProbeResult>> probe,
            Func<ResolvedMappingSnapshot, CancellationToken, Task>? warmup)
            : base(output, listener, probe, skipWatcher: true, initialSettings: CreateSettings(), runWarmup: warmup)
        {
            OutputForTest = output;
            var ports = (CheckedListBox)typeof(Form1).GetField("chkPorts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            ports.Items.Add("Port A");
            ports.Items.Add("Port B");
            ports.SetItemChecked(0, true);
        }

        public FakeKeyboardOutput OutputForTest { get; }

        public bool TransitioningForTest => (bool)typeof(Form1).GetField("_isTransitioning", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        public bool ListeningForTest => typeof(Form1).GetField("inputTracker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this) is InputTracker tracker && tracker.IsListening;
        public bool CapturingForTest => typeof(Form1).GetField("inputTracker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this) is InputTracker tracker && tracker.IsCapturing;
        public string StatusForTest => ((Label)typeof(Form1).GetField("lblStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!).Text;

        public void StartForTest() => typeof(Form1).GetMethod("StartConversion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null);

        public void BeginCaptureForTest()
        {
            var note = (TextBox)typeof(Form1).GetField("txtNote", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            note.Focus();
            if (note.Focused)
                typeof(Form1).GetMethod("StartCapture", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null);
        }

        public void EndCaptureForTest() => typeof(Form1).GetMethod("StopCapture", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, new object?[] { null });

        public void LeaveNoteForTest()
        {
            var key = (TextBox)typeof(Form1).GetField("txtKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            key.Focus();
        }

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

        public void SelectBothPortsForTest()
        {
            var ports = (CheckedListBox)typeof(Form1).GetField("chkPorts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            ports.SetItemChecked(0, true);
            ports.SetItemChecked(1, true);
        }

        public void SimulateFormClosingForTest() => base.OnFormClosing(new FormClosingEventArgs(CloseReason.UserClosing, false));
    }
}
