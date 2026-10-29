using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MidiToKeyApp;

namespace MidiToKeyApp.Tests;

[TestClass]
public sealed class Form1StartLifecycleTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowStyle(IntPtr handle, int index);

    [TestMethod]
    public async Task Ports_ArrowAndSpaceKeys_SelectAndToggleDevice()
    {
        await RunFormAsync(async form =>
        {
            var ports = form.ControlForTest<CheckedListBox>("chkPorts");
            form.TopMost = true;
            form.Activate();
            form.FocusPortsForTest();
            await Task.Delay(150);
            Assert.IsTrue(ports.Focused);
            ports.SelectedIndex = 0;
            Assert.AreEqual(0, ports.SelectedIndex);
            SendKeys.SendWait("{DOWN}");
            await WaitUntilAsync(() => ports.SelectedIndex == 1);
            bool wasChecked = ports.GetItemChecked(1);
            SendKeys.SendWait(" ");
            await WaitUntilAsync(() => ports.GetItemChecked(1) != wasChecked);
            CollectionAssert.Contains(form.SettingsForTest.SelectedPorts.ToArray(), "Port B");
            SendKeys.SendWait(" ");
            await WaitUntilAsync(() => ports.GetItemChecked(1) == wasChecked);
        }, new FakeMidiListener(), (_, _) => Task.FromResult(AvailablePortResult("Port A")));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HotkeyDialog_PreviewsSelection_AndSupportsApplyOrCancel(bool apply)
    {
        await RunFormAsync(form =>
        {
            var original = new HotkeySettings { Enabled = true, Modifiers = "Ctrl+Alt", Key = "F9" };
            using var dialog = new HotkeyConfigDialog(original);
            T Field<T>(string name) => (T)typeof(HotkeyConfigDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            dialog.Show(form);
            var enabled = Field<CheckBox>("_chkEnabled");
            var ctrl = Field<CheckBox>("_chkCtrl");
            var alt = Field<CheckBox>("_chkAlt");
            var key = Field<ComboBox>("_cmbKey");
            var ok = Field<Button>("_btnOk");
            ctrl.Checked = false; alt.Checked = false;
            Assert.IsFalse(ok.Enabled);
            string? invalidDirectory = Environment.GetEnvironmentVariable("MIDI2KEY_UI_SNAPSHOT_DIR");
            if (!string.IsNullOrEmpty(invalidDirectory))
            {
                using var bitmap = new System.Drawing.Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, dialog.Size));
                bitmap.Save(Path.Combine(invalidDirectory, "hotkey-dialog-disabled.png"));
            }
            enabled.Checked = false;
            Assert.IsFalse(key.Enabled);
            Assert.IsTrue(ok.Enabled);
            enabled.Checked = true; ctrl.Checked = true;
            key.SelectedItem = "F10";
            Assert.IsTrue(key.Enabled);
            Assert.IsTrue(ok.Enabled);
            foreach (Control control in new Control[] { enabled, ctrl, alt, key, ok, Field<Button>("_btnCancel"), Field<Label>("_lblWarn") })
                Assert.IsTrue(dialog.ClientRectangle.Contains(dialog.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))));
            string? directory = Environment.GetEnvironmentVariable("MIDI2KEY_UI_SNAPSHOT_DIR");
            if (!string.IsNullOrEmpty(directory))
            {
                using var bitmap = new System.Drawing.Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, dialog.Size));
                bitmap.Save(Path.Combine(directory, "hotkey-dialog.png"));
            }
            if (apply) ok.PerformClick();
            else Field<Button>("_btnCancel").PerformClick();
            Assert.AreEqual(apply ? "F10" : "F9", dialog.ResultSettings.Key);
            Assert.AreEqual(apply ? "Ctrl" : "Ctrl+Alt", dialog.ResultSettings.Modifiers);
            Assert.AreEqual("F9", original.Key);
            dialog.Close();
            return Task.CompletedTask;
        }, new FakeMidiListener(), (_, _) => Task.FromResult(AvailablePortResult("Port A")));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Tray_MinimizeEndsCaptureButKeepsSession_AndStopReleasesKeys(bool testMode)
    {
        var listener = new FakeMidiListener();
        await RunFormAsync(async form =>
        {
            form.SetMappingInputForTest("60", "a");
            form.ControlForTest<Button>("btnAdd").PerformClick();
            form.SetMappingInputForTest("61", "b");
            form.ControlForTest<Button>("btnAdd").PerformClick();
            form.FocusPortsForTest();
            form.ControlForTest<CheckBox>("chkTestMode").Checked = testMode;
            form.RefreshTrayForTest();
            form.TrayItemForTest("trayStart").PerformClick();
            await WaitUntilAsync(() => form.ListeningForTest && !form.TransitioningForTest);
            var port = listener.GetActivePorts().Single();
            listener.FireNote(new(port.DeviceId, port.DeviceName, 0, 60, 100, true, listener.CurrentGeneration));
            form.BeginCaptureForTest();
            Assert.IsTrue(form.CapturingForTest);
            form.WindowState = FormWindowState.Maximized;
            form.WindowState = FormWindowState.Minimized;
            await WaitUntilAsync(() => !form.Visible);
            Assert.IsFalse(form.ShowInTaskbar);
            Assert.IsTrue(form.ListeningForTest);
            Assert.IsFalse(form.CapturingForTest);
            listener.FireNote(new(port.DeviceId, port.DeviceName, 0, 61, 100, true, listener.CurrentGeneration));
            form.RefreshTrayForTest();
            Assert.IsFalse(form.TrayItemForTest("trayStart").Enabled);
            Assert.IsTrue(form.TrayItemForTest("trayStop").Enabled);
            StringAssert.Contains(form.TrayIconForTest.Text, testMode ? "テスト中" : "実行中");
            Assert.AreEqual(testMode ? 0 : 2, form.OutputForTest.KeyDownCount);
            form.TrayItemForTest("trayStop").PerformClick();
            await WaitUntilAsync(() => !form.ListeningForTest && !form.MidiBusyForTest);
            Assert.AreEqual(testMode ? 0 : 2, form.OutputForTest.KeyUpCount);
            form.TrayItemForTest("trayShow").PerformClick();
            Assert.IsTrue(form.Visible);
            Assert.IsTrue(form.ShowInTaskbar);
            Assert.AreEqual(FormWindowState.Maximized, form.WindowState);
        }, listener, (_, _) => Task.FromResult(AvailablePortResult("Port A")), (_, _) => Task.CompletedTask);
    }

    [TestMethod]
    public async Task MainButtons_ReflectIdlePendingRunningAndStoppedStates()
    {
        var probe = new TaskCompletionSource<MidiPortProbeService.ProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await RunFormAsync(async form =>
        {
            var start = form.ControlForTest<Button>("btnStartConversion");
            var stop = form.ControlForTest<Button>("btnStopConversion");
            form.FocusPortsForTest();
            form.RefreshTrayForTest();
            Assert.IsTrue(start.Enabled);
            Assert.IsFalse(stop.Enabled);
            start.PerformClick();
            Assert.IsFalse(start.Enabled);
            Assert.IsTrue(stop.Enabled);
            Assert.AreEqual("開始をキャンセル", stop.Text);
            string? directory = Environment.GetEnvironmentVariable("MIDI2KEY_UI_SNAPSHOT_DIR");
            if (!string.IsNullOrEmpty(directory))
            {
                using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, form.Size));
                bitmap.Save(Path.Combine(directory, "form-start-disabled.png"));
            }
            stop.PerformClick();
            probe.SetResult(AvailablePortResult("Port A"));
            await WaitUntilAsync(() => !form.TransitioningForTest && !form.MidiBusyForTest && start.Enabled);
            Assert.IsFalse(form.ListeningForTest);
            Assert.IsFalse(stop.Enabled);
            start.PerformClick();
            await WaitUntilAsync(() => form.ListeningForTest && !form.TransitioningForTest);
            form.RefreshTrayForTest();
            Assert.IsFalse(start.Enabled);
            Assert.IsTrue(stop.Enabled);
            Assert.AreEqual("変換停止", stop.Text);
            stop.PerformClick();
            await WaitUntilAsync(() => !form.ListeningForTest && !form.MidiBusyForTest && start.Enabled);
            Assert.IsFalse(stop.Enabled);
            form.ControlForTest<CheckedListBox>("chkPorts").SetItemChecked(0, false);
            form.RefreshTrayForTest();
            Assert.IsFalse(start.Enabled);
        }, new FakeMidiListener(), (_, _) => probe.Task, (_, _) => Task.CompletedTask);
    }

    [TestMethod]
    public async Task Tray_CancelsPendingStartWithoutStartingLate()
    {
        var probe = new TaskCompletionSource<MidiPortProbeService.ProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new FakeMidiListener();
        await RunFormAsync(async form =>
        {
            // Opening the real context menu refreshes enabled state after port discovery.
            form.RefreshTrayForTest();
            form.TrayItemForTest("trayStart").PerformClick();
            Assert.IsTrue(form.TransitioningForTest);
            Assert.AreEqual("開始をキャンセル", form.TrayItemForTest("trayStop").Text);
            form.TrayItemForTest("trayStop").PerformClick();
            probe.TrySetResult(AvailablePortResult("Port A"));
            await WaitUntilAsync(() => !form.TransitioningForTest && !form.MidiBusyForTest);
            Assert.IsFalse(form.ListeningForTest);
            Assert.AreEqual(0, listener.StartCount);
        }, listener, (_, _) => probe.Task, (_, _) => Task.CompletedTask);
    }

    [TestMethod]
    public async Task Tray_ExitRestoresWindowForUnsavedPrompt_CancelPreservesSession()
    {
        var listener = new FakeMidiListener();
        await RunFormAsync(async form =>
        {
            form.SetMappingInputForTest("60", "a");
            form.ControlForTest<Button>("btnAdd").PerformClick();
            form.FocusPortsForTest();
            form.StartForTest();
            await WaitUntilAsync(() => form.ListeningForTest && !form.TransitioningForTest);
            form.WindowState = FormWindowState.Minimized;
            await WaitUntilAsync(() => !form.Visible);
            form.PendingChangesResponseForTest = DialogResult.Cancel;
            form.TrayItemForTest("trayExit").PerformClick();
            Assert.AreEqual(1, form.PendingChangesPromptCountForTest);
            Assert.IsTrue(form.Visible);
            Assert.IsTrue(form.ShowInTaskbar);
            Assert.IsFalse(form.IsDisposed);
            Assert.IsTrue(form.ListeningForTest);
            Assert.AreEqual(0, listener.StopCount);
        }, listener, (_, _) => Task.FromResult(AvailablePortResult("Port A")), (_, _) => Task.CompletedTask);
    }

    [TestMethod]
    public async Task Tray_CleanExitDisposesIconAndReleasesKeys()
    {
        var listener = new FakeMidiListener();
        await RunFormAsync(async form =>
        {
            form.ReplaceMappingSnapshotForTest();
            form.AcceptSavedSettingsForTest();
            form.FocusPortsForTest();
            form.StartForTest();
            await WaitUntilAsync(() => form.ListeningForTest && !form.TransitioningForTest);
            var port = listener.GetActivePorts().Single();
            listener.FireNote(new(port.DeviceId, port.DeviceName, 0, 60, 100, true, listener.CurrentGeneration));
            form.AcceptSavedSettingsForTest();
            var tray = form.TrayIconForTest;
            form.TrayItemForTest("trayExit").PerformClick();
            Assert.IsTrue(form.IsDisposed);
            Assert.IsFalse(tray.Visible);
            Assert.AreEqual(1, form.OutputForTest.KeyUpCount);
        }, listener, (_, _) => Task.FromResult(AvailablePortResult("Port A")), (_, _) => Task.CompletedTask);
    }

    [TestMethod]
    public async Task TransposeControls_TrackDirtyStateAndShowShiftedMonitorTarget()
    {
        var listener = new FakeMidiListener();
        await RunFormAsync(async form =>
        {
            form.SetMappingInputForTest("72", "a");
            form.ControlForTest<Button>("btnAdd").PerformClick();
            form.AcceptSavedSettingsForTest();
            var transpose = form.ControlForTest<NumericUpDown>("numTranspose");
            form.ControlForTest<Button>("btnOctaveUp").PerformClick();
            Assert.AreEqual(12m, transpose.Value);
            Assert.AreEqual(12, form.SettingsForTest.TransposeSemitones);
            StringAssert.Contains(form.Text, "未保存");
            form.FocusPortsForTest();
            form.ControlForTest<CheckBox>("chkTestMode").Checked = true;
            form.StartForTest();
            await WaitUntilAsync(() => form.ListeningForTest && !form.TransitioningForTest);
            var port = listener.GetActivePorts().Single();
            listener.FireNote(new(port.DeviceId, port.DeviceName, 0, 60, 100, true, listener.CurrentGeneration));
            await WaitUntilAsync(() => form.ControlForTest<Label>("lblNoteMonitor").Text.Contains("⇒ 72"));
            StringAssert.Contains(form.ControlForTest<Label>("lblNoteMonitor").Text, "→ a");
            form.ControlForTest<Button>("btnResetTranspose").PerformClick();
            Assert.AreEqual(0m, transpose.Value);
            Assert.IsFalse(form.Text.Contains("未保存"));
            transpose.Value = 47;
            form.ControlForTest<Button>("btnOctaveUp").PerformClick();
            Assert.AreEqual(48m, transpose.Value);
            transpose.Value = -47;
            form.ControlForTest<Button>("btnOctaveDown").PerformClick();
            Assert.AreEqual(-48m, transpose.Value);
            Assert.AreEqual(0, form.OutputForTest.History.Count);
        }, listener, (_, _) => Task.FromResult(AvailablePortResult("Port A")), (_, _) => Task.CompletedTask, showOptions: true);
    }

    [TestMethod]
    public async Task TestMode_HotkeySessionAndMonitor_DoNotSendKeys_ThenResumeNormally()
    {
        var listener = new FakeMidiListener();
        await RunFormAsync(async form =>
        {
            form.ReplaceMappingSnapshotForTest();
            form.FocusPortsForTest();
            var mode = form.ControlForTest<CheckBox>("chkTestMode");
            mode.Checked = true;
            form.SendHotkeyForTest();
            Assert.IsFalse(mode.Enabled);
            await WaitUntilAsync(() => form.ListeningForTest && !form.TransitioningForTest);
            StringAssert.Contains(form.StatusForTest, "テスト中");
            var port = listener.GetActivePorts().Single();
            listener.FireNote(new(port.DeviceId, port.DeviceName, 0, 60, 100, true, listener.CurrentGeneration));
            await WaitUntilAsync(() => form.ControlForTest<Label>("lblNoteMonitor").Text.Contains("(60)"));
            listener.FireNote(new(port.DeviceId, port.DeviceName, 1, 127, 99, true, listener.CurrentGeneration));
            listener.FireControl(new(port.DeviceId, port.DeviceName, 1, 64, 127, listener.CurrentGeneration));
            await WaitUntilAsync(() => form.ControlForTest<Label>("lblNoteMonitor").Text.Contains("未割り当て") &&
                form.ControlForTest<Label>("lblPedalMonitor").Text.Contains("ON"));
            StringAssert.Contains(form.ControlForTest<Label>("lblNoteMonitor").Text, "Ch 2");
            Assert.AreEqual(0, form.OutputForTest.History.Count);
            form.SendHotkeyForTest();
            await WaitUntilAsync(() => !form.MidiBusyForTest && mode.Enabled);
            Assert.AreEqual("ノート: 受信なし", form.ControlForTest<Label>("lblNoteMonitor").Text);
            mode.Checked = false;
            form.SendHotkeyForTest();
            await WaitUntilAsync(() => form.ListeningForTest && !form.TransitioningForTest);
            port = listener.GetActivePorts().Single();
            listener.FireNote(new(port.DeviceId, port.DeviceName, 0, 60, 100, true, listener.CurrentGeneration));
            Assert.AreEqual(1, form.OutputForTest.KeyDownCount);
            form.SendHotkeyForTest();
            await WaitUntilAsync(() => !form.MidiBusyForTest);
            Assert.AreEqual(1, form.OutputForTest.KeyUpCount);
        }, listener, (_, _) => Task.FromResult(AvailablePortResult("Port A")), (_, _) => Task.CompletedTask);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Resizing_GrowsMappingList_AndKeepsCaptureControlsVisible(bool showOptions)
    {
        await RunFormAsync(async form =>
        {
            var list = form.ControlForTest<ListView>("listMapping");
            var note = form.ControlForTest<TextBox>("txtNote");
            var key = form.ControlForTest<TextBox>("txtKey");
            form.ApplyPortsForTest(new[] { "Port A", "Port B", "Port C" }, new[] { "Port A" });
            var ports = form.ControlForTest<CheckedListBox>("chkPorts");
            await WaitUntilAsync(() => (GetWindowStyle(ports.Handle, -16) & 0x00200000) != 0);
            ports.TopIndex = 2;
            Assert.IsTrue(ports.GetItemRectangle(2).IntersectsWith(ports.ClientRectangle));
            ports.TopIndex = 0;
            await CheckLayout("empty");
            for (int i = 0; i < 15; i++)
                form.SettingsForTest.Mapping[(60 + i).ToString()] = "a";
            form.RefreshMappingForTest();

            async Task CheckLayout(string snapshot)
            {
                form.PerformLayout();
                await Task.Delay(100);
                await WaitUntilAsync(() => (GetWindowStyle(list.Handle, -16) & 0x00100000) == 0);
                Assert.IsTrue(list.Columns.Cast<ColumnHeader>().Sum(column => column.Width) <=
                    list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth,
                    $"Mapping columns should fit beside the vertical scrollbar at {snapshot}.");
                Assert.AreEqual(0, GetWindowStyle(list.Handle, -16) & 0x00100000,
                    $"The mapping list should not have a horizontal scrollbar at {snapshot}.");
                var controls = new Control[] { list, note, key,
                    form.ControlForTest<RadioButton>("rbJIS"), form.ControlForTest<RadioButton>("rbUS"),
                    form.ControlForTest<Button>("btnHotkeyConfig"), form.ControlForTest<Label>("lblHotkeyInfo"),
                    form.ControlForTest<CheckBox>("chkTestMode"), form.ControlForTest<NumericUpDown>("numTranspose"),
                    form.ControlForTest<Button>("btnOctaveDown"), form.ControlForTest<Button>("btnOctaveUp"),
                    form.ControlForTest<Button>("btnResetTranspose"), form.ControlForTest<Label>("lblNoteMonitor"), form.ControlForTest<Label>("lblPedalMonitor") }.Where(c => c.Visible).ToArray();
                var bounds = controls.Select(c => form.RectangleToClient(c.RectangleToScreen(c.ClientRectangle))).ToArray();
                for (int i = 0; i < bounds.Length; i++)
                {
                    Assert.IsTrue(form.ClientRectangle.Contains(bounds[i]), $"{controls[i].Name} is outside the client area at {snapshot}.");
                    for (int j = i + 1; j < bounds.Length; j++)
                        Assert.IsFalse(bounds[i].IntersectsWith(bounds[j]), $"Controls overlap at {snapshot}.");
                }

                string? directory = Environment.GetEnvironmentVariable("MIDI2KEY_UI_SNAPSHOT_DIR");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, form.Size));
                    bitmap.Save(Path.Combine(directory, $"form-{snapshot}{(showOptions ? "-options" : "")}.png"));
                }
            }

            var transpose = form.ControlForTest<NumericUpDown>("numTranspose");
            var testMode = form.ControlForTest<CheckBox>("chkTestMode");
            Assert.AreEqual(showOptions, transpose.Visible);
            Assert.AreEqual(showOptions, testMode.Visible);
            Assert.AreEqual(showOptions, form.ControlForTest<Label>("lblNoteMonitor").Visible);
            Assert.AreEqual(showOptions, form.ControlForTest<Label>("lblPedalMonitor").Visible);
            await CheckLayout("default");
            transpose.Value = 12;
            testMode.Checked = true;
            StringAssert.Contains(form.Text, "移調 +12半音");
            StringAssert.Contains(form.Text, "テストモード");
            Assert.AreEqual(showOptions, transpose.Visible);
            await CheckLayout("active-options");
            transpose.Value = 0;
            testMode.Checked = false;
            Assert.IsFalse(form.Text.Contains("半音"));
            Assert.IsFalse(form.Text.Contains("テストモード"));
            form.Size = form.MinimumSize;
            await CheckLayout("minimum");
            int minimumHeight = list.Height;
            int minimumWidth = list.Width;
            form.Size = new System.Drawing.Size(form.Width + 300, form.Height + 250);
            await CheckLayout("expanded");
            Assert.IsTrue(list.Height > minimumHeight, "The mapping list should use extra vertical space.");
            Assert.IsTrue(list.Width > minimumWidth, "The mapping list should use extra horizontal space.");
        }, new FakeMidiListener(), (_, _) => Task.FromResult(AvailablePortResult("Port A")), showOptions: showOptions);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NoteFocus_FocusesNoteAndCapturesMidi_LeavingRestoresMode(bool conversionRunning)
    {
        var listener = new FakeMidiListener();
        await RunFormAsync(async form =>
        {
            form.FocusPortsForTest();
            if (conversionRunning)
            {
                form.ReplaceMappingSnapshotForTest();
                form.StartForTest();
                await WaitUntilAsync(() => form.ListeningForTest && !form.TransitioningForTest);
            }

            int initialStarts = listener.StartCount;
            form.ControlForTest<TextBox>("txtNote").Focus();
            await WaitUntilAsync(() => form.CapturingForTest && !form.MidiBusyForTest);
            Assert.IsTrue(form.ControlForTest<TextBox>("txtNote").Focused);
            form.RefreshTrayForTest();
            Assert.AreEqual(!conversionRunning, form.ControlForTest<Button>("btnStartConversion").Enabled);
            await WaitUntilAsync(() => form.ControlForTest<Label>("lblCaptureHint").Text.Contains("入力待ち", StringComparison.Ordinal));
            Assert.AreEqual(initialStarts + (conversionRunning ? 0 : 1), listener.StartCount);

            var port = listener.GetActivePorts().Single();
            listener.FireNote(new MidiNoteData(port.DeviceId, port.DeviceName, 0, 60, 100, true, listener.CurrentGeneration));
            await WaitUntilAsync(() => form.NoteTextForTest == "60");
            StringAssert.Contains(form.ControlForTest<Label>("lblCaptureHint").Text, "取得: 60");
            Assert.AreEqual(0, form.OutputForTest.KeyDownCount, "Captured notes must not emit mapped keys.");

            form.FocusPortsForTest();
            await WaitUntilAsync(() => !form.CapturingForTest && !form.MidiBusyForTest);
            Assert.AreEqual("", form.ControlForTest<Label>("lblCaptureHint").Text);
            Assert.AreEqual(conversionRunning, form.ListeningForTest);
            Assert.AreEqual(conversionRunning ? 0 : 1, listener.StopCount);
        }, listener, (_, _) => Task.FromResult(AvailablePortResult("Port A")), (_, _) => Task.CompletedTask);
    }

    [TestMethod]
    public async Task NoteFocus_WithoutSelectedPort_AllowsManualInputWithoutHint()
    {
        var listener = new FakeMidiListener();
        await RunFormAsync(form =>
        {
            form.FocusPortsForTest();
            form.ControlForTest<CheckedListBox>("chkPorts").SetItemChecked(0, false);
            form.ControlForTest<TextBox>("txtNote").Focus();
            Assert.AreEqual(0, listener.StartCount);
            Assert.IsFalse(form.CapturingForTest);
            Assert.AreEqual("", form.ControlForTest<Label>("lblCaptureHint").Text);
            form.ControlForTest<TextBox>("txtNote").Text = "60";
            form.FocusPortsForTest();
            Assert.AreEqual("60", form.NoteTextForTest);
            Assert.AreEqual("", form.ControlForTest<Label>("lblCaptureHint").Text);
            return Task.CompletedTask;
        }, listener, (_, _) => Task.FromResult(AvailablePortResult("Port A")));
    }

    [TestMethod]
    public async Task NoteFocus_WhilePortOpens_ShowsPreparationUntilReady()
    {
        var listener = new FakeMidiListener
        {
            StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ReleaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        try
        {
            await RunFormAsync(async form =>
            {
                form.FocusPortsForTest();
                form.ControlForTest<TextBox>("txtNote").Focus();
                await listener.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                string hint = form.ControlForTest<Label>("lblCaptureHint").Text;
                StringAssert.Contains(hint, "準備中");
                Assert.IsFalse(hint.Contains("入力待ち", StringComparison.Ordinal));
                listener.ReleaseStart.TrySetResult();
                await WaitUntilAsync(() => form.CapturingForTest && !form.MidiBusyForTest);
                await WaitUntilAsync(() => form.ControlForTest<Label>("lblCaptureHint").Text.Contains("入力待ち", StringComparison.Ordinal));
                form.FocusPortsForTest();
                await WaitUntilAsync(() => !form.CapturingForTest && !form.MidiBusyForTest);
            }, listener, (_, _) => Task.FromResult(AvailablePortResult("Port A")));
        }
        finally { listener.ReleaseStart.TrySetResult(); }
    }

    [TestMethod]
    public async Task SettingsTitle_TracksLayoutReversion_AndSaveClearsMappingChanges()
    {
        string path = Path.Combine(Path.GetTempPath(), $"midi2key-ui-{Guid.NewGuid():N}.json");
        try
        {
            await RunFormAsync(form =>
            {
                form.SetSettingsPathForTest(path);
                Assert.IsFalse(form.Text.Contains("未保存", StringComparison.Ordinal));
                StringAssert.Contains(form.Text, Path.GetFileName(path));

                form.ControlForTest<RadioButton>("rbJIS").Checked = true;
                StringAssert.Contains(form.Text, "*未保存");
                form.ControlForTest<RadioButton>("rbUS").Checked = true;
                Assert.IsFalse(form.Text.Contains("未保存", StringComparison.Ordinal),
                    "Returning to the saved layout should clear the dirty marker.");

                form.SetMappingInputForTest("60", "a");
                form.ControlForTest<Button>("btnAdd").PerformClick();
                StringAssert.Contains(form.Text, "*未保存");
                var save = form.ControlForTest<Button>("btnSave");
                Assert.AreEqual("上書き保存 *", save.Text);
                var tip = (ToolTip)typeof(Form1).GetField("inputToolTip", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                StringAssert.Contains(tip.GetToolTip(save), path);
                Assert.IsTrue(form.Text.IndexOf("*未保存", StringComparison.Ordinal) < form.Text.IndexOf(Path.GetFileName(path), StringComparison.Ordinal));
                save.PerformClick();
                Assert.AreEqual("上書き保存", save.Text);

                Assert.AreEqual("a", SettingsManager.Load(path).Mapping["60"]);
                Assert.IsFalse(form.Text.Contains("未保存", StringComparison.Ordinal));
                StringAssert.Contains(form.NotificationForTest, Path.GetFileName(path));
                return Task.CompletedTask;
            }, new FakeMidiListener(), (_, _) => Task.FromResult(AvailablePortResult("Port A")));
        }
        finally { File.Delete(path); }
    }

    [DataTestMethod]
    [DataRow("060", "60")]
    [DataRow("PeDaL", "pedal")]
    public async Task MappingAction_NormalizesInput_AndUpdatesExistingEntry(string input, string storedNote)
    {
        await RunFormAsync(form =>
        {
            var action = form.ControlForTest<Button>("btnAdd");
            var delete = form.ControlForTest<Button>("btnDeleteMapping");
            var list = form.ControlForTest<ListView>("listMapping");
            Assert.IsFalse(delete.Enabled);
            Assert.IsTrue(form.ControlForTest<Label>("lblEmptyMapping").Visible);
            form.SetMappingInputForTest(input, "a");
            Assert.AreEqual("追加", action.Text);
            action.PerformClick();
            Assert.AreEqual("更新", action.Text);
            Assert.AreEqual("a", form.SettingsForTest.Mapping[storedNote]);

            form.SetMappingInputForTest(input, "b");
            action.PerformClick();
            Assert.AreEqual(1, form.SettingsForTest.Mapping.Count);
            Assert.AreEqual("b", form.SettingsForTest.Mapping[storedNote]);
            Assert.AreEqual("更新", action.Text);
            Assert.AreEqual(storedNote, list.SelectedItems[0].Text);
            Assert.IsTrue(delete.Enabled);
            Assert.IsFalse(form.ControlForTest<Label>("lblEmptyMapping").Visible);
            StringAssert.Contains(form.ControlForTest<Label>("lblMappingHeading").Text, "1件");
            delete.PerformClick();
            Assert.AreEqual(0, form.SettingsForTest.Mapping.Count);
            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.IsFalse(delete.Enabled);
            StringAssert.Contains(form.ControlForTest<Label>("lblMappingHeading").Text, "0件");

            form.SetMappingInputForTest("61", "b");
            Assert.AreEqual("追加", action.Text);
            Assert.IsTrue(form.ControlForTest<Label>("lblEmptyMapping").Visible);
            form.FocusPortsForTest();
            form.ControlForTest<TextBox>("txtKey").Focus();
            Assert.AreEqual("", form.ControlForTest<TextBox>("txtKey").Text);
            return Task.CompletedTask;
        }, new FakeMidiListener(), (_, _) => Task.FromResult(AvailablePortResult("Port A")));
    }

    [TestMethod]
    public async Task SaveNotification_AndExpiry_PreserveRunningConversionStatus()
    {
        string path = Path.Combine(Path.GetTempPath(), $"midi2key-ui-{Guid.NewGuid():N}.json");
        try
        {
            await RunFormAsync(async form =>
            {
                form.SetSettingsPathForTest(path);
                form.StartForTest();
                await WaitUntilAsync(() => !form.TransitioningForTest);
                Assert.IsTrue(form.ListeningForTest);
                var status = form.ControlForTest<Label>("lblStatus");
                string runningText = status.Text;
                var runningColor = status.ForeColor;

                form.ControlForTest<Button>("btnSave").PerformClick();
                StringAssert.Contains(form.NotificationForTest, "保存完了");
                Assert.AreEqual(runningText, status.Text);
                Assert.AreEqual(runningColor, status.ForeColor);
                Assert.IsTrue(form.ListeningForTest);

                form.ExpireNotificationForTest();
                Assert.AreEqual("", form.NotificationForTest);
                Assert.AreEqual(runningText, status.Text);
                Assert.AreEqual(runningColor, status.ForeColor);
                Assert.IsTrue(form.ListeningForTest);
            }, new FakeMidiListener(), (_, _) => Task.FromResult(AvailablePortResult("Port A")),
                (_, _) => Task.CompletedTask);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task PortRefresh_PreservesSavedSelectionWithoutDirtyingSettings()
    {
        await RunFormAsync(form =>
        {
            form.ApplyPortsForTest(new[] { "Port B" }, new[] { "Port A" });
            CollectionAssert.AreEqual(new[] { "Port A" }, form.SettingsForTest.SelectedPorts);
            Assert.IsFalse(form.Text.Contains("未保存", StringComparison.Ordinal),
                "Refreshing a temporarily unavailable selected port should not edit saved settings.");

            form.ApplyPortsForTest(new[] { "Port A", "Port B" }, new[] { "Port A" });
            Assert.IsFalse(form.Text.Contains("未保存", StringComparison.Ordinal));
            var ports = form.ControlForTest<CheckedListBox>("chkPorts");
            ports.SetItemChecked(1, true);
            StringAssert.Contains(form.Text, "*未保存");
            CollectionAssert.AreEqual(new[] { "Port A", "Port B" }, form.SettingsForTest.SelectedPorts);
            ports.SetItemChecked(1, false);
            Assert.IsFalse(form.Text.Contains("未保存", StringComparison.Ordinal));
            return Task.CompletedTask;
        }, new FakeMidiListener(), (_, _) => Task.FromResult(AvailablePortResult("Port A", "Port B")));
    }

    [TestMethod]
    public async Task DirtyClose_CancelKeepsConversionRunning_AndDiscardStopsIt()
    {
        var listener = new FakeMidiListener();
        await RunFormAsync(async form =>
        {
            form.StartForTest();
            await WaitUntilAsync(() => !form.TransitioningForTest);
            Assert.IsTrue(form.ListeningForTest);
            form.SetMappingInputForTest("60", "a");
            form.ControlForTest<Button>("btnAdd").PerformClick();
            StringAssert.Contains(form.Text, "*未保存");

            form.PendingChangesResponseForTest = DialogResult.Cancel;
            Assert.IsTrue(form.TryDirtyCloseForTest().Cancel);
            Assert.AreEqual(1, form.PendingChangesPromptCountForTest);
            Assert.IsTrue(form.ListeningForTest,
                "Canceling the unsaved-settings prompt must preserve the conversion session.");
            Assert.AreEqual(0, listener.StopCount);
            StringAssert.Contains(form.Text, "*未保存");

            form.PendingChangesResponseForTest = DialogResult.No;
            Assert.IsFalse(form.TryDirtyCloseForTest().Cancel);
            Assert.AreEqual(2, form.PendingChangesPromptCountForTest);
            await WaitUntilAsync(() => !form.ListeningForTest && listener.StopCount > 0);
        }, listener, (_, _) => Task.FromResult(AvailablePortResult("Port A")),
            (_, _) => Task.CompletedTask);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HotkeyFromNoteCapture_AppliesFocusBeforeNativeStart(bool leaveNoteDuringStart)
    {
        var listener = new FakeMidiListener();
        try
        {
            await RunFormAsync(async form =>
            {
                form.ReplaceMappingSnapshotForTest();
                form.BeginCaptureForTest();
                await WaitUntilAsync(() => listener.StartCount == 1 && !form.MidiBusyForTest);

                listener.StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                listener.ReleaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
                listener.EmitNoteAfterBlockedStart = true;
                form.SendHotkeyForTest();
                await listener.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.IsTrue(form.CapturingForTest, "Capture must already be active before native start emits input.");
                if (leaveNoteDuringStart) form.FocusPortsForTest();

                listener.ReleaseStart.TrySetResult();
                await WaitUntilAsync(() => !form.TransitioningForTest);
                Assert.IsTrue(form.ListeningForTest);
                Assert.AreEqual(!leaveNoteDuringStart, form.CapturingForTest);
                Assert.AreEqual(leaveNoteDuringStart ? 1 : 0, form.OutputForTest.KeyDownCount);
                if (!leaveNoteDuringStart)
                    await WaitUntilAsync(() => form.NoteTextForTest == "60");
            }, listener, (_, _) => Task.FromResult(AvailablePortResult("Port A")),
                (_, _) => Task.CompletedTask);
        }
        finally { listener.ReleaseStart?.TrySetResult(); }
    }

    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public async Task ParentProbeFailure_UsesSuccessfulWinMmResultWithoutTreatingFailureAsEmpty(
        bool childSuccess, bool portPresent)
    {
        var listener = new FakeMidiListener();
        bool checkHealth = false;
        await RunFormAsync(async form =>
        {
            form.ReplaceMappingSnapshotForTest();
            form.StartForTest();
            await WaitUntilAsync(() => !form.TransitioningForTest);
            var port = listener.GetActivePorts().Single();
            listener.FireNote(new MidiNoteData(port.DeviceId, port.DeviceName, 0, 60, 100, true, listener.CurrentGeneration));
            Assert.AreEqual(1, form.OutputForTest.KeyDownCount);

            bool shouldStop = childSuccess && !portPresent;
            form.SuppressWarningForTest(shouldStop
                ? "MIDIデバイスの切断を検知しました。変換を停止しました。機器を接続し直し、midi2keyを再起動してください。"
                : "MIDIデバイスの接続状態を確認できません。必要に応じてmidi2keyを再起動してください。");
            checkHealth = true;
            form.TriggerHealthCheckForTest();
            await WaitUntilAsync(() => form.HealthCheckCompletedForTest && !form.MidiBusyForTest);
            // The health worker posts disconnect handling back to the UI message queue.
            await Task.Delay(100);
            Assert.AreEqual(!shouldStop, form.ListeningForTest);
            Assert.AreEqual(shouldStop ? 1 : 0, form.OutputForTest.KeyUpCount);
            Assert.AreEqual(shouldStop ? 0 : 1, listener.GetActivePorts().Count);
        }, listener, (_, _) => Task.FromResult(checkHealth
            ? new MidiPortProbeService.ProbeResult(PortEnumerationResult.Failed("backend timeout"),
                childSuccess ? OutOfProcessWinMmResult.Succeeded(portPresent ? new[] { "Port A" } : Array.Empty<string>())
                    : OutOfProcessWinMmResult.Failed("child timeout", timedOut: true))
            : AvailablePortResult("Port A")), (_, _) => Task.CompletedTask);
    }

    [DataTestMethod]
    [DataRow(Keys.Oem1, false, "*", WindowsInput.Native.VirtualKeyCode.OEM_1)]
    [DataRow(Keys.Oem3, false, "`", WindowsInput.Native.VirtualKeyCode.OEM_3)]
    [DataRow(Keys.Oem1, true, ":", WindowsInput.Native.VirtualKeyCode.OEM_1)]
    [DataRow(Keys.Oem3, true, "~", WindowsInput.Native.VirtualKeyCode.OEM_3)]
    public async Task ShiftedSymbolCapture_RoundTripsToOriginalKey(Keys key, bool isUs, string symbol,
        WindowsInput.Native.VirtualKeyCode expectedKey)
    {
        await RunFormAsync(form =>
        {
            string captured = form.FormatKeyForTest(new KeyEventArgs(key | Keys.Shift), isUs);
            Assert.AreEqual(symbol, captured);
            var resolved = KeyResolver.Resolve(captured, isUs ? "US" : "JIS");
            Assert.AreEqual(expectedKey, resolved.BaseKey);
            Assert.IsTrue(resolved.ShiftRequired);
            return Task.CompletedTask;
        }, new FakeMidiListener(), (_, _) => Task.FromResult(AvailablePortResult("Port A")));
    }

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
    public async Task CaptureStartTimeout_ReportsTimeoutAndStopsCapture()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var listener = new FakeMidiListener
        {
            StartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ReleaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        try
        {
            await RunFormAsync(async form =>
            {
                form.BeginCaptureForTest();
                await listener.StartEntered!.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await WaitUntilForAsync(() => form.StatusForTest.StartsWith("ステータス: キャプチャ開始タイムアウト", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

                Assert.IsFalse(form.CapturingForTest, "A timed-out capture attempt must be stopped.");
                Assert.AreEqual(0, form.OutputForTest.History.Count, "Capture timeout must not send keyboard output.");

                listener.ReleaseStart!.TrySetResult();
                await WaitUntilForAsync(() => listener.StopCount == 1, TimeSpan.FromSeconds(2));
                StringAssert.Contains(form.StatusForTest, "キャプチャ開始タイムアウト");
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A")));
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            listener.ReleaseStart!.TrySetResult();
        }
    }

    [TestMethod]
    public async Task CanceledCaptureStart_DoesNotReportStaleTimeout()
    {
        bool previousWarmup = InputWarmup.Enabled;
        InputWarmup.Enabled = false;
        var listener = new FakeMidiListener
        {
            StartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ReleaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        try
        {
            await RunFormAsync(async form =>
            {
                form.BeginCaptureForTest();
                await listener.StartEntered!.Task.WaitAsync(TimeSpan.FromSeconds(2));
                form.EndCaptureForTest();
                listener.ReleaseStart!.TrySetResult();
                await WaitUntilForAsync(() => listener.StopCount == 1 && !form.CapturingForTest, TimeSpan.FromSeconds(2));
                await Task.Delay(100);

                Assert.IsFalse(form.StatusForTest.Contains("キャプチャ開始タイムアウト", StringComparison.Ordinal),
                    "A canceled capture attempt must not publish a stale timeout after ownership ended.");
                Assert.AreEqual(0, form.OutputForTest.History.Count, "Canceled capture must not send keyboard output.");
            }, listener, (timeout, token) => Task.FromResult(AvailablePortResult("Port A")));
        }
        finally
        {
            InputWarmup.Enabled = previousWarmup;
            listener.ReleaseStart!.TrySetResult();
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
        Func<ResolvedMappingSnapshot, CancellationToken, Task>? warmup = null, bool showOptions = false)
    {
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new TestForm(listener, probe, warmup, showOptions);
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
                        if (!form.IsDisposed)
                        {
                            form.AcceptSavedSettingsForTest();
                            form.Close();
                        }
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

    private static async Task WaitUntilForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var limit = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < limit)
            await Task.Delay(10);
        Assert.IsTrue(condition(), $"The expected condition should be reached within {timeout}.");
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
            Func<ResolvedMappingSnapshot, CancellationToken, Task>? warmup, bool showOptions)
            : this(new FakeKeyboardOutput(), listener, probe, warmup, showOptions)
        {
        }

        private TestForm(FakeKeyboardOutput output, FakeMidiListener listener,
            Func<int, CancellationToken, Task<MidiPortProbeService.ProbeResult>> probe,
            Func<ResolvedMappingSnapshot, CancellationToken, Task>? warmup, bool showOptions)
            : base(output, listener, probe, skipWatcher: true, initialSettings: CreateSettings(), runWarmup: warmup, showOptions: showOptions)
        {
            OutputForTest = output;
            var ports = (CheckedListBox)typeof(Form1).GetField("chkPorts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            ports.Items.Add("Port A");
            ports.Items.Add("Port B");
            ports.SetItemChecked(0, true);
        }

        public FakeKeyboardOutput OutputForTest { get; }

        public DialogResult PendingChangesResponseForTest = DialogResult.No;
        public int PendingChangesPromptCountForTest { get; private set; }

        protected override DialogResult AskToSaveChanges()
        {
            PendingChangesPromptCountForTest++;
            return PendingChangesResponseForTest;
        }

        public FormClosingEventArgs TryDirtyCloseForTest()
        {
            var args = new FormClosingEventArgs(CloseReason.UserClosing, false);
            base.OnFormClosing(args);
            return args;
        }

        public T ControlForTest<T>(string name) where T : Control =>
            (T)typeof(Form1).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;

        public ToolStripMenuItem TrayItemForTest(string name) =>
            (ToolStripMenuItem)typeof(Form1).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        public NotifyIcon TrayIconForTest =>
            (NotifyIcon)typeof(Form1).GetField("trayIcon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        public void RefreshTrayForTest() =>
            typeof(Form1).GetMethod("UpdateTrayState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null);

        public AppSettings SettingsForTest =>
            (AppSettings)typeof(Form1).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;

        public string NotificationForTest => ControlForTest<Label>("lblNotification").Text;

        public void AcceptSavedSettingsForTest() =>
            typeof(Form1).GetMethod("AcceptSavedSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null);

        public void SetSettingsPathForTest(string path)
        {
            typeof(Form1).GetField("currentSettingsPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, path);
            AcceptSavedSettingsForTest();
        }

        public void SetMappingInputForTest(string note, string key)
        {
            ControlForTest<TextBox>("txtNote").Text = note;
            ControlForTest<TextBox>("txtKey").Text = key;
        }

        public void ExpireNotificationForTest()
        {
            var timer = typeof(Form1).GetField("notificationTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            typeof(System.Windows.Forms.Timer).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(timer, new object[] { EventArgs.Empty });
        }

        public void ApplyPortsForTest(string[] available, string[] selected) =>
            typeof(Form1).GetMethod("ApplyPortsToUi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this,
                new object[] { available, selected, OutOfProcessWinMmResult.Succeeded(available) });

        public bool MidiBusyForTest => ((MidiListenerLifecycle)typeof(Form1).GetField("midiLifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!).IsBusy;
        public bool HealthCheckCompletedForTest => (long)typeof(Form1).GetField("_latestCompletedHealthCheckRequestId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)! > 0;
        public string NoteTextForTest => ((TextBox)typeof(Form1).GetField("txtNote", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!).Text;

        public void FocusPortsForTest() => ((CheckedListBox)typeof(Form1).GetField("chkPorts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!).Focus();

        public void TriggerHealthCheckForTest()
        {
            var timer = typeof(Form1).GetField("deviceDebounceTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            typeof(System.Windows.Forms.Timer).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(timer, new object[] { EventArgs.Empty });
        }

        public void SuppressWarningForTest(string message)
        {
            typeof(Form1).GetField("_lastAlertMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, message);
            typeof(Form1).GetField("_lastAlertTime", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, DateTime.Now);
        }

        public string FormatKeyForTest(KeyEventArgs key, bool isUs) =>
            (string)typeof(Form1).GetMethod("FormatKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, new object[] { key, isUs })!;

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

        public void RefreshMappingForTest() =>
            typeof(Form1).GetMethod("RefreshMappingList", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, new object?[] { null });

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

        public void SimulateFormClosingForTest()
        {
            AcceptSavedSettingsForTest();
            base.OnFormClosing(new FormClosingEventArgs(CloseReason.UserClosing, false));
        }
    }
}
