using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gma.System.MouseKeyHook; 

namespace MidiToKeyApp
{
    /// <summary>
    /// メインとなるUIおよびアプリケーションロジックを管理するフォームクラス。
    /// 安定したMIDI入力状態管理、セッションIDによる遅延イベント遮断、および原子的設定保存を統合します。
    /// </summary>
    public partial class Form1 : Form
    {
        private AppSettings settings = null!;
        private string appDir = AppDomain.CurrentDomain.BaseDirectory;
        private string currentSettingsDir = AppDomain.CurrentDomain.BaseDirectory;
        private string currentSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        private bool isCurrentSettingsCorrupted = false;
        
        private CheckedListBox chkPorts = null!;
        private RadioButton rbJIS = null!;
        private RadioButton rbUS = null!;
        private ListView listMapping = null!;
        private readonly ImageList mappingRowImages = new();
        private Label lblEmptyMapping = null!;
        private TextBox txtNote = null!;
        private TextBox txtKey = null!;
        private Label lblCaptureHint = null!;
        private string captureHintMessage = "";
        private Label lblMappingHeading = null!;
        private Button btnDeleteMapping = null!;
        private readonly ToolTip inputToolTip = new() { AutoPopDelay = 15000, InitialDelay = 350, ReshowDelay = 100 };
        private Label lblStatus = null!;
        private Label lblHotkeyInfo = null!;
        private Button btnHotkeyConfig = null!;
        private Button btnSaveAs = null!;
        private Button btnSave = null!;
        private Button btnStartConversion = null!;
        private Button btnStopConversion = null!;
        private Button btnAdd = null!;
        private Label lblNotification = null!;
        private NumericUpDown numTranspose = null!;
        private Button btnOctaveDown = null!;
        private Button btnOctaveUp = null!;
        private Button btnResetTranspose = null!;
        private CheckBox chkTestMode = null!;
        private readonly bool showOptions;

        private void UpdateOptionsSummary() => UpdateSettingsTitle();
        private Label lblNoteMonitor = null!;
        private Label lblPedalMonitor = null!;
        private readonly System.Windows.Forms.Timer monitorTimer = new() { Interval = 100 };
        private NotifyIcon? trayIcon;
        private AppIcons? appIcons;
        private ContextMenuStrip? trayMenu;
        private ToolStripMenuItem trayStatus = null!;
        private ToolStripMenuItem trayShow = null!;
        private ToolStripMenuItem trayStart = null!;
        private ToolStripMenuItem trayStop = null!;
        private ToolStripMenuItem trayExit = null!;
        private FormWindowState restoreWindowState = FormWindowState.Normal;

        private void InitializeTray()
        {
            appIcons = new AppIcons();
            Icon = appIcons.Application;
            trayStatus = new ToolStripMenuItem { Enabled = false };
            trayShow = new ToolStripMenuItem("画面を表示");
            trayStart = new ToolStripMenuItem("変換開始");
            trayStop = new ToolStripMenuItem("停止");
            trayExit = new ToolStripMenuItem("終了");
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.AddRange(new ToolStripItem[] { trayStatus, new ToolStripSeparator(), trayShow,
                trayStart, trayStop, new ToolStripSeparator(), trayExit });
            trayMenu.Opening += (s, e) => UpdateTrayState();
            trayShow.Click += (s, e) => RestoreFromTray();
            trayStart.Click += (s, e) => {
                // Move focus away from capture before starting, including when the menu is
                // opened while the main window is visible.
                ActiveControl = chkPorts;
                StartConversion();
                UpdateTrayState();
            };
            trayStop.Click += (s, e) => { StopConversion(); UpdateTrayState(); };
            trayExit.Click += (s, e) => { RestoreFromTray(); Close(); };
            trayIcon = new NotifyIcon { Icon = appIcons.ForState(TrayIconState.Stopped),
                ContextMenuStrip = trayMenu, Text = "midi2key", Visible = false };
            trayIcon.DoubleClick += (s, e) => RestoreFromTray();
            UpdateTrayState();
        }

        private void UpdateTrayState()
        {
            if (trayIcon == null || _isClosing || IsDisposed) return;
            string status = _isTransitioning ? "開始処理中" : lblStatus.Text.Replace("ステータス: ", "");
            trayStatus.Text = status;
            TrayIconState iconState = keySimulator.HasUnreleasedKeys || midiLifecycle.HasReleaseFailure
                ? TrayIconState.Warning
                : _isTransitioning ? TrayIconState.Busy
                : inputTracker.IsListening ? (inputTracker.IsTestMode ? TrayIconState.Test : TrayIconState.Running)
                : midiLifecycle.IsBusy ? TrayIconState.Busy
                : TrayIconState.Stopped;
            var icon = appIcons!.ForState(iconState);
            if (!ReferenceEquals(trayIcon.Icon, icon)) trayIcon.Icon = icon;
            string text = $"midi2key | {status}";
            trayIcon.Text = text.Length > 63 ? text[..60] + "..." : text;
            trayStart.Text = chkTestMode.Checked ? "テスト開始（キー送信なし）" : "変換開始";
            trayStart.Enabled = !_isTransitioning && !inputTracker.IsListening && !midiLifecycle.IsBusy &&
                !midiLifecycle.HasReleaseFailure && !keySimulator.HasUnreleasedKeys && GetSelectedPorts().Count > 0;
            trayStop.Text = _isTransitioning ? "開始をキャンセル" : "停止";
            trayStop.Enabled = _isTransitioning || inputTracker.IsListening || inputTracker.IsCapturing ||
                _captureCancellation != null || keySimulator.HasUnreleasedKeys;
            btnStartConversion.Text = chkTestMode.Checked ? "テスト開始" : "変換開始";
            // StartConversion retires capture and waits for MIDI cleanup itself. Keep
            // this button usable while the note field owns a capture session.
            btnStartConversion.Enabled = !_isTransitioning && !inputTracker.IsListening &&
                !midiLifecycle.HasReleaseFailure && !keySimulator.HasUnreleasedKeys && GetSelectedPorts().Count > 0;
            btnStopConversion.Text = _isTransitioning ? "開始をキャンセル" :
                inputTracker.IsListening ? (inputTracker.IsTestMode ? "テスト停止" : "変換停止") :
                inputTracker.IsCapturing || _captureCancellation != null ? "取得停止" : "変換停止";
            btnStopConversion.Enabled = trayStop.Enabled;
        }

        internal void RestoreFromTray()
        {
            if (_isClosing || IsDisposed) return;
            var state = restoreWindowState;
            ShowInTaskbar = true;
            Show();
            WindowState = state;
            ActiveControl = chkPorts;
            Activate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (trayIcon == null || _isClosing) return;
            if (WindowState != FormWindowState.Minimized)
            {
                restoreWindowState = WindowState;
                return;
            }
            // Background conversion must not stay in the editor's capture suppression mode.
            ActiveControl = chkPorts;
            if (inputTracker.IsListening) inputTracker.SetCapturing(false);
            else StopCapture();
            ShowInTaskbar = false;
            Hide();
            UpdateTrayState();
        }

        private string SessionDisplay => inputTracker.IsTestMode ? "テスト中・キー送信なし" : "実行中";

        private void UpdateMonitor()
        {
            if (_isClosing || IsDisposed) return;
            chkTestMode.Enabled = !_isTransitioning && !inputTracker.IsListening && !midiLifecycle.IsBusy;
            var sample = inputTracker.ReadMonitor();
            lblNoteMonitor.Text = FormatMonitor(sample.Note, false);
            lblPedalMonitor.Text = FormatMonitor(sample.Pedal, true);
            UpdateTrayState();
        }

        private string FormatMonitor(MidiMonitorSample? sample, bool pedal)
        {
            if (sample is not { } value) return pedal ? "ペダル: 受信なし" : "ノート: 受信なし";
            int shifted = value.Number + settings.TransposeSemitones;
            string key = pedal ? "pedal" : shifted.ToString();
            string target;
            lock (settings.MappingLock)
                target = !pedal && (uint)shifted >= 128 ? "移調後は範囲外" :
                    settings.Mapping.TryGetValue(key, out var mapped) ? mapped : "未割り当て";
            string name = pedal ? "ペダル" : $"{MidiNoteHelper.GetNoteDisplayName(value.Number)} ({value.Number})";
            if (!pedal && settings.TransposeSemitones != 0) name += $" ⇒ {shifted}";
            return $"{value.DeviceName} / Ch {value.Channel + 1}: {name} {(value.IsDown ? "ON" : "OFF")} [{value.Value}] → {target}";
        }
        private readonly System.Windows.Forms.Timer notificationTimer = new() { Interval = 4000 };
        private string savedSettings = "";
        private string baseTitle = "";
        private bool settingsReady;
        private bool applyingPorts;
        private bool sizingMappingColumns;
        private bool mappingColumnResizePending;

        private string SettingsFingerprint() => System.Text.Json.JsonSerializer.Serialize(new {
            Ports = settings.SelectedPorts.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            Mapping = settings.Mapping.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
            settings.KeyboardLayout,
            settings.TransposeSemitones,
            settings.Hotkey
        });
        private bool HasUnsavedChanges => settingsReady && SettingsFingerprint() != savedSettings;

        private void UpdateSettingsTitle()
        {
            if (!settingsReady) return;
            string activeOptions = settings.TransposeSemitones != 0 ? $" [移調 {settings.TransposeSemitones:+0;-0;0}半音]" : "";
            if (chkTestMode.Checked) activeOptions += " [テストモード]";
            bool dirty = HasUnsavedChanges;
            Text = $"{baseTitle} — {(dirty ? "*未保存 " : "")}{Path.GetFileName(currentSettingsPath)}{activeOptions}";
            btnSave.Text = dirty ? "上書き保存 *" : "上書き保存";
            btnSave.AccessibleName = dirty ? "上書き保存（未保存の変更あり）" : "上書き保存";
            inputToolTip.SetToolTip(btnSave, $"保存先: {currentSettingsPath}\nCtrl+Sでも上書き保存できます。\n{(dirty ? "未保存の変更があります。" : "現在の設定をこのファイルに保存します。")}");
        }

        private void AcceptSavedSettings()
        {
            savedSettings = SettingsFingerprint();
            settingsReady = true;
            UpdateSettingsTitle();
        }

        private void ShowNotification(string message)
        {
            lblNotification.Text = message;
            notificationTimer.Stop();
            notificationTimer.Start();
        }

        protected virtual DialogResult AskToSaveChanges() => MessageBox.Show(this,
            "設定に未保存の変更があります。保存しますか？", "未保存の設定",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

        private bool ConfirmPendingChanges()
        {
            if (!HasUnsavedChanges) return true;
            var result = AskToSaveChanges();
            if (result == DialogResult.Cancel) return false;
            if (result == DialogResult.No) return true;
            btnSave.PerformClick();
            return !HasUnsavedChanges;
        }

        private void UpdateMappingRowHeight()
        {
            int height = Math.Max((int)Math.Ceiling(22 * listMapping.DeviceDpi / 96.0), listMapping.Font.Height + 4);
            mappingRowImages.ImageSize = new Size(1, Math.Clamp(height, 1, 256));
        }

        private void UpdateEmptyMappingHint()
        {
            if (lblEmptyMapping == null) return;
            int top = Math.Max(24, (listMapping.ClientSize.Height - 44) / 2);
            lblEmptyMapping.SetBounds(8, top, Math.Max(0, listMapping.ClientSize.Width - 16),
                Math.Max(0, Math.Min(44, listMapping.ClientSize.Height - top)));
            lblEmptyMapping.Visible = listMapping.Items.Count == 0 && listMapping.ClientSize.Height >= 48;
        }

        private void UpdateMappingSelection()
        {
            bool selected = listMapping.SelectedItems.Count > 0;
            if (btnDeleteMapping != null) btnDeleteMapping.Enabled = selected;
            string selection = selected ? $" — 選択: {listMapping.SelectedItems[0].Text}" : "";
            lblMappingHeading.Text = $"マッピング一覧（{listMapping.Items.Count}件）{selection}";
        }

        private static bool IsValidMappingNote(string note) =>
            note.Equals("pedal", StringComparison.OrdinalIgnoreCase) ||
            (int.TryParse(note, out int number) && number >= 0 && number <= 127);

        private void SetCaptureHint(string message)
        {
            captureHintMessage = message;
            UpdateInputHint();
        }

        private bool IsHotkeyMappingConflict(string key)
        {
            if (settings?.Hotkey?.Enabled != true) return false;
            var hotkey = KeyResolver.Resolve(settings.Hotkey.Key, settings.KeyboardLayout);
            var target = KeyResolver.Resolve(key, settings.KeyboardLayout);
            return hotkey.IsValid && target.IsValid && hotkey.VkCode == target.VkCode;
        }

        private void UpdateInputHint()
        {
            string note = txtNote.Text.Trim();
            bool invalidNote = note.Length > 0 && !IsValidMappingNote(note);
            string key = txtKey.Text.Trim();
            bool invalidKey = key.Length > 0 && !KeyResolver.IsValidTargetKey(key, settings?.KeyboardLayout ?? "US");
            bool conflict = key.Length > 0 && IsHotkeyMappingConflict(key);
            bool invalid = invalidNote || invalidKey || conflict;
            lblCaptureHint.Text = invalidNote ? "ノートは0〜127、または pedal を入力" :
                invalidKey ? "この変換キーは未対応です。別のキーを指定" :
                conflict ? "ホットキーと同じキーは登録できません" :
                globalHook != null && key.Length == 0 ? "キー入力待ち: 割り当てるキーを押してください" : captureHintMessage;
            lblCaptureHint.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText :
                invalid ? Color.FromArgb(151, 52, 63) : Color.DarkSlateGray;
        }

        private void UpdateMappingAction()
        {
            if (settings == null || btnAdd == null) return;
            string note = txtNote.Text.Trim();
            if (int.TryParse(note, out int number)) note = number.ToString();
            if (note.Equals("pedal", StringComparison.OrdinalIgnoreCase)) note = "pedal";
            if (listMapping.SelectedItems.Count > 0 &&
                !string.Equals(listMapping.SelectedItems[0].Text, note, StringComparison.Ordinal))
                listMapping.SelectedItems[0].Selected = false;
            string key = txtKey.Text.Trim();
            lock (settings.MappingLock)
            {
                bool exists = settings.Mapping.TryGetValue(note, out string? currentKey);
                btnAdd.Text = exists ? "更新" : "追加";
                btnAdd.Enabled = IsValidMappingNote(note) && KeyResolver.IsValidTargetKey(key, settings.KeyboardLayout) &&
                    !IsHotkeyMappingConflict(key) &&
                    (!exists || !string.Equals(currentKey, key, StringComparison.Ordinal));
            }
            UpdateInputHint();
        }
        
        private IKeyboardMouseEvents? globalHook;
        private KeyEventHandler? keyCaptureHandler;
        private int keyCaptureGeneration;
        private IMidiListener midiListener = null!;
        private MidiListenerLifecycle midiLifecycle = null!;
        private KeySimulator keySimulator = null!;
        private InputTracker inputTracker = null!;
        private readonly HotkeyManager hotkeyManager = new();
        private System.Windows.Forms.Timer? deviceDebounceTimer;
        private WinRtMidiWatcher? _winRtWatcher;
        private volatile bool _isTransitioning = false;
        private volatile bool _isClosing;
        private CancellationTokenSource? _startCancellation;
        private CancellationTokenSource? _captureCancellation;
        private long _captureGeneration;
        private long _captureAttemptId;
        private static readonly TimeSpan MidiStartTimeout = TimeSpan.FromSeconds(3);
        private readonly Func<int, CancellationToken, Task<MidiPortProbeService.ProbeResult>> _probePorts;
        private readonly Func<ResolvedMappingSnapshot, CancellationToken, Task> _runWarmup;
        private readonly bool _skipWatcher;

        // ポート一覧の非同期更新競合防止用
        private long _refreshPortsRequestId = 0;

        // デバイスヘルスチェックの非同期更新競合防止用（要件5）
        private long _healthCheckRequestId = 0;
        private long _latestCompletedHealthCheckRequestId = 0;

        // 接続変更・切断警告の通知集約用（同一メッセージの重複抑止）
        private string? _lastAlertMessage = null;
        private DateTime _lastAlertTime = DateTime.MinValue;
        private readonly object _alertLock = new();

        // キー送信エラーの通知集約用
        private int lastReportedErrorCode = 0;
        private uint lastReportedVkCode = 0;
        private DateTime lastReportedErrorTime = DateTime.MinValue;
        private int errorRepeatCount = 0;

        public Form1(IKeyboardOutput? output = null, IMidiListener? listener = null, bool showOptions = false)
            : this(output, listener, MidiPortProbeService.ProbeAsync, skipWatcher: false, showOptions: showOptions)
        {
        }

        internal Form1(IKeyboardOutput? output, IMidiListener? listener,
            Func<int, CancellationToken, Task<MidiPortProbeService.ProbeResult>> probePorts, bool skipWatcher,
            AppSettings? initialSettings = null,
            Func<ResolvedMappingSnapshot, CancellationToken, Task>? runWarmup = null, bool showOptions = false)
        {
            this.showOptions = showOptions;
            _probePorts = probePorts;
            _runWarmup = runWarmup ?? ((snapshot, token) => Task.Run(() => InputWarmup.Run(snapshot, token), token));
            _skipWatcher = skipWatcher;
            InitializeComponentProgrammatically();
            SetupDependencies(output, listener);
            if (initialSettings == null)
            {
                LoadInitialSettings();
            }
            else
            {
                settings = initialSettings;
                if (settings.KeyboardLayout == "US") rbUS.Checked = true;
                else rbJIS.Checked = true;
                RefreshMappingList();
                inputTracker.PublishResolvedMapping(settings);
                UpdateHotkeyUi();
            }
            AcceptSavedSettings();
            InitializeTray();
            monitorTimer.Tick += (s, e) => UpdateMonitor();
            monitorTimer.Start();
            if (!skipWatcher) InitWinRtWatcher();
        }
        
        private void SetupDependencies(IKeyboardOutput? output = null, IMidiListener? listener = null)
        {
            keySimulator = new KeySimulator(output);
            inputTracker = new InputTracker(keySimulator, () => settings) { MonitorEnabled = true };
            midiListener = listener ?? new MidiListener();
            midiLifecycle = new MidiListenerLifecycle(midiListener);

            // ノート/ペダル取得（キャプチャモード）時のUI非同期更新（デッドロック防止）
            inputTracker.OnInputCaptured += (capturedText) => {
                long capturedSession = inputTracker.CurrentSessionId;
                if (IsHandleCreated && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke(new Action(() => {
                            if (!IsDisposed && !_isClosing && txtNote.Focused && inputTracker.IsCapturing &&
                                inputTracker.CurrentSessionId == capturedSession)
                            {
                                txtNote.Text = capturedText;
                                if (txtNote.Focused && inputTracker.IsCapturing)
                                    SetCaptureHint($"取得: {capturedText}");
                            }
                        }));
                    }
                    catch { }
                }
            };

            // イベント自身に保持されたGenerationを検証して処理
            midiListener.OnNoteReceived += (data) => inputTracker.ProcessNoteEvent(data);
            midiListener.OnControlReceived += (data) => inputTracker.ProcessControlEvent(data);

            // デバイス切断イベントの処理（通知元のGenerationを保持したレコードを受け取る）
            midiListener.OnDeviceDisconnected += (data) => {
                if (IsHandleCreated && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke(new Action(() => {
                            if (IsDisposed) return;
                            HandleDeviceDisconnected(data);
                        }));
                    }
                    catch { }
                }
            };

            // キー送信エラーのUI集約通知（ロック外で呼ばれる）
            keySimulator.OnKeySendError += (errorInfo) => {
                if (IsHandleCreated && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke(new Action(() => {
                            if (IsDisposed) return;
                            HandleKeySendError(errorInfo);
                        }));
                    }
                    catch { }
                }
            };
        }

        private void LoadInitialSettings()
        {
            currentSettingsPath = Path.Combine(appDir, "settings.json");
            currentSettingsDir = appDir;

            try
            {
                settings = SettingsManager.Load(currentSettingsPath);
                isCurrentSettingsCorrupted = false;
            }
            catch (Exception ex)
            {
                // 起動時の破損JSON保護: クラッシュを防ぎ、デフォルト設定で復旧
                isCurrentSettingsCorrupted = true;
                settings = SettingsManager.GetDefaultSettings();

                MessageBox.Show(
                    $"設定ファイル ({Path.GetFileName(currentSettingsPath)}) の読み込みに失敗したため、初期設定で起動しました。\n\n詳細: {ex.Message}\n\n※破損した元ファイルは上書きされず保護されています。",
                    "設定読み込み警告",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            
            if (settings.KeyboardLayout == "US")
                rbUS.Checked = true;
            else
                rbJIS.Checked = true;

            RefreshMappingList();
            inputTracker.PublishResolvedMapping(settings);
            UpdateHotkeyUi();
        }

        private void InitWinRtWatcher()
        {
            try
            {
                _winRtWatcher = new WinRtMidiWatcher();
                _winRtWatcher.OnDeviceAdded += (device) => {
                    DiagnosticLogger.Log($"[WinRT] Device Added: {device.Name} (Id={device.Id})");
                    if (IsHandleCreated && !IsDisposed)
                    {
                        try { BeginInvoke(new Action(() => TriggerDeviceDebounce("WinRT-Added"))); } catch { }
                    }
                };
                _winRtWatcher.OnDeviceRemoved += (device) => {
                    DiagnosticLogger.Log($"[WinRT] Device Removed: {device.Name} (Id={device.Id})");
                    if (IsHandleCreated && !IsDisposed)
                    {
                        try { BeginInvoke(new Action(() => TriggerDeviceDebounce("WinRT-Removed"))); } catch { }
                    }
                };
                _winRtWatcher.OnEnumerationCompleted += () => {
                    DiagnosticLogger.Log($"[WinRT] Initial enumeration completed. Devices count={_winRtWatcher.Devices.Count}");
                };
                _winRtWatcher.Start();
                DiagnosticLogger.Log("[WinRT] WinRtMidiWatcher started successfully.");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log($"[WinRT] Failed to start WinRtMidiWatcher: {ex.Message}");
            }
        }

        private void TriggerDeviceDebounce(string triggerSource)
        {
            DiagnosticLogger.Log($"[Form1] TriggerDeviceDebounce called from {triggerSource}");
            deviceDebounceTimer?.Stop();
            deviceDebounceTimer?.Start();
        }

        /// <summary>
        /// 接続変更等のユーザー向け警告メッセージを集約し、短時間の同一メッセージ連続ダイアログを抑止して表示します。
        /// </summary>
        private void ShowAggregatedWarning(string message, string title = "MIDIデバイス通知")
        {
            lock (_alertLock)
            {
                if (message == _lastAlertMessage && (DateTime.Now - _lastAlertTime).TotalSeconds < 5)
                {
                    return;
                }
                _lastAlertMessage = message;
                _lastAlertTime = DateTime.Now;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ShowAggregatedWarning(message, title)));
                return;
            }

            // 接続警告では実際の変換状態を維持し、安全停止を実行した場合にのみStopConversion等で停止中へ更新される
            MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// デバイスの接続変更（新規接続や状態取得失敗）を検知し、必要に応じて集約警告ダイアログを表示します。
        /// </summary>
        private void CheckConnectionChangeAndNotify(OutOfProcessWinMmResult outProcResult, PortEnumerationResult currentPorts)
        {
            // 1. 親プロセス側または新規プロセス側のいずれかで状態取得・列挙に失敗している場合
            if (!currentPorts.Success || !outProcResult.Success)
            {
                ShowAggregatedWarning(
                    "MIDIデバイスの接続状態を確認できません。必要に応じてmidi2keyを再起動してください。",
                    "MIDIデバイス状態確認");
                return;
            }

            // 2. 新規プロセスでポートが検出されたが親プロセスに未反映の場合（新規接続の検知）
            if (outProcResult.Ports.Count > 0 &&
                (currentPorts.Ports.Count == 0 || outProcResult.Ports.Any(p => !currentPorts.Ports.Contains(p, StringComparer.OrdinalIgnoreCase))))
            {
                ShowAggregatedWarning(
                    "MIDIデバイスの接続を検知しました。利用するにはmidi2keyを再起動してください。",
                    "MIDIデバイス接続検知");
            }
            // 3. 無関係なUSB機器の変更等（ポート変化なし）: 警告を表示しない
        }

        private void RefreshPorts(bool isInitialLoad = false)
        {
            if (chkPorts == null || _isClosing) return;
            long currentRequestId = Interlocked.Increment(ref _refreshPortsRequestId);
            var checkedPorts = isInitialLoad ? settings.SelectedPorts : GetSelectedPorts();
            // Both WinMM subprocess and DryWetMIDI enumeration can block for seconds.
            _ = Task.Run(async () => {
                var (enumResult, outProcResult) = await _probePorts(1000, CancellationToken.None);
                if (IsHandleCreated && !IsDisposed && !_isClosing)
                {
                    try
                    {
                        BeginInvoke(new Action(() => {
                            if (IsDisposed || _isClosing) return;
                            // 要求IDが最新でない古い非同期結果は破棄して競合を防止
                            if (Interlocked.Read(ref _refreshPortsRequestId) != currentRequestId)
                            {
                                DiagnosticLogger.Log($"[Form1] RefreshPorts: Discarded outdated result (req={currentRequestId}, latest={Interlocked.Read(ref _refreshPortsRequestId)})");
                                return;
                            }
                            if (!enumResult.Success)
                            {
                                DiagnosticLogger.Log($"[Form1] RefreshPorts failed to enumerate: {enumResult.ErrorMessage}");
                                chkPorts.Items.Clear();
                                if (!inputTracker.IsListening)
                                {
                                    lblStatus.Text = "ステータス: 停止中 (利用可能なMIDIポートがありません)";
                                    lblStatus.ForeColor = Color.DarkGoldenrod;
                                }
                                return;
                            }
                            ApplyPortsToUi(enumResult.Ports, checkedPorts, outProcResult);
                        }));
                    }
                    catch { }
                }
            });
        }

        private void ApplyPortsToUi(IReadOnlyList<string> availablePorts, IReadOnlyList<string> checkedPorts, OutOfProcessWinMmResult outProcResult)
        {
            List<string> validPorts;
            applyingPorts = true;
            try
            {
                chkPorts.Items.Clear();

                // 利用可能なポートの厳格な絞り込み:
                // 1. 新規プロセスWinMMで正常に0件（消失）と確認されたポートは、取り外し済みのため除外
                // 2. 新規プロセスWinMMで検出されていても親プロセスDryWetMIDIにないポートは除外
                // 3. 診断プロセス失敗時は親プロセスの一覧をそのまま使用
                if (outProcResult.Success && outProcResult.Ports.Count == 0)
                {
                    // 新規プロセスWinMMが正常に0件: デバイスが取り外されているため、親プロセスの残存ポートは利用不可
                    validPorts = new List<string>();
                }
                else if (outProcResult.Success)
                {
                    // 新規プロセスWinMMに含まれており、かつ親プロセスにも存在するポートのみ
                    var outSet = new HashSet<string>(outProcResult.Ports, StringComparer.OrdinalIgnoreCase);
                    validPorts = availablePorts.Where(p => outSet.Contains(p)).ToList();
                }
                else
                {
                    // 新規プロセスの確認失敗時は親プロセスの列挙をそのまま使用
                    validPorts = availablePorts.ToList();
                }

                // 純粋なポート名のみを追加（修飾文字列は一切追加しない）
                foreach (var port in validPorts)
                {
                    int index = chkPorts.Items.Add(port);
                    if (checkedPorts.Contains(port, StringComparer.OrdinalIgnoreCase))
                    {
                        chkPorts.SetItemChecked(index, true);
                    }
                }

            }
            finally { applyingPorts = false; }

            // ステータス表示の更新（接続不一致はステータス欄で案内）
            // 変換中（inputTracker.IsListening == true）はステータスを「停止中」へ書き換えない
            if (inputTracker == null || !inputTracker.IsListening)
            {
                if (availablePorts.Count == 0 && outProcResult.Success && outProcResult.Ports.Count > 0)
                {
                    lblStatus.Text = "ステータス: 停止中 (MIDIデバイス未反映: 再起動してください)";
                    lblStatus.ForeColor = Color.DarkGoldenrod;
                }
                else if (availablePorts.Count > 0 && outProcResult.Success && outProcResult.Ports.Count == 0)
                {
                    lblStatus.Text = "ステータス: 停止中 (MIDIデバイス切断: 再起動してください)";
                    lblStatus.ForeColor = Color.DarkOrange;
                }
                else if (validPorts.Count == 0)
                {
                    lblStatus.Text = "ステータス: 停止中 (利用可能なMIDIポートがありません)";
                    lblStatus.ForeColor = Color.DarkGoldenrod;
                }
                else
                {
                    lblStatus.Text = "ステータス: 停止中";
                    lblStatus.ForeColor = SystemColors.ControlText;
                }
            }
        }

        /// <summary>
        /// マッピング辞書の内容をリストビュー（3列グリッド）に描画します。
        /// 88鍵盤（21〜108）などのノート番号には対応する音階名（例: C4 / ド）を併記します。
        /// </summary>
        private void RefreshMappingList(string? selectedKey = null, bool resetEditor = false)
        {
            if (listMapping == null) return;
            int? previousTopIndex = selectedKey != null &&
                listMapping.Items.Cast<ListViewItem>().Any(item => item.Text == selectedKey)
                ? listMapping.TopItem?.Index : null;
            numTranspose.Value = settings.TransposeSemitones;
            KeyValuePair<string, string>[] mappingSnapshot;
            lock (settings.MappingLock)
            {
                mappingSnapshot = settings.Mapping.ToArray();
            }

            listMapping.Items.Clear();
            var sortedEntries = mappingSnapshot.OrderBy(entry => {
                if (entry.Key.Equals("pedal", StringComparison.OrdinalIgnoreCase)) return -1;
                return int.TryParse(entry.Key, out int n) ? n : 999;
            });

            foreach (var entry in sortedEntries)
            {
                string key = entry.Key;
                string targetKey = entry.Value;
                string typeOrNoteName;

                if (key.Equals("pedal", StringComparison.OrdinalIgnoreCase))
                {
                    typeOrNoteName = "サステインペダル";
                }
                else if (int.TryParse(key, out int note))
                {
                    typeOrNoteName = MidiNoteHelper.GetNoteDisplayName(note);
                }
                else
                {
                    typeOrNoteName = "-";
                }

                var item = new ListViewItem(key);
                if (!SystemInformation.HighContrast)
                    item.BackColor = listMapping.Items.Count % 2 == 0 ? Color.White : Color.FromArgb(243, 247, 249);
                item.SubItems.Add(typeOrNoteName);
                item.SubItems.Add(targetKey);
                listMapping.Items.Add(item);
                if (key == selectedKey)
                {
                    item.Selected = true;
                    item.Focused = true;
                }
            }
            if (previousTopIndex.HasValue && listMapping.Items.Count > 0)
                listMapping.TopItem = listMapping.Items[Math.Min(previousTopIndex.Value, listMapping.Items.Count - 1)];
            if (listMapping.SelectedItems.Count > 0) listMapping.SelectedItems[0].EnsureVisible();
            UpdateMappingSelection();
            UpdateEmptyMappingHint();
            QueueMappingColumnResize();
            if (resetEditor) ResetMappingEditor();
            UpdateMappingAction();
            UpdateSettingsTitle();
        }

        private void ResetMappingEditor()
        {
            txtNote.Clear();
            txtKey.Clear();
            SetCaptureHint("");
        }

        /// <summary>
        /// UIコントロールの生成と配置。
        /// </summary>
        private void InitializeComponentProgrammatically()
        {
            var version = typeof(Form1).Assembly.GetName().Version;
            string verStr = version != null ? $" v{version.Major}.{version.Minor}.{version.Build}" : " v1.2.1";
            this.Text = $"midi2key{verStr}";
            this.SuspendLayout();
            this.AutoScaleDimensions = new SizeF(96, 96);
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.ClientSize = new Size(380, 650);
            baseTitle = this.Text;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Yu Gothic UI", 9);

            var grpPorts = new GroupBox { Text = "MIDIポート選択", Width = 275, Height = 95, Top = 10, Left = 10 };
            chkPorts = new CheckedListBox { Top = 18, Left = 10, Width = 255, Height = 52, BorderStyle = BorderStyle.None, CheckOnClick = true };
            chkPorts.ItemCheck += (s, e) => {
                if (!settingsReady || applyingPorts) return;
                var selected = GetSelectedPorts();
                string port = chkPorts.Items[e.Index]?.ToString() ?? "";
                selected.RemoveAll(p => p == port);
                if (e.NewValue == CheckState.Checked) selected.Add(port);
                settings.SelectedPorts = selected;
                UpdateSettingsTitle();
            };
            chkPorts.AccessibleName = "使用するMIDIポート";
            chkPorts.AccessibleDescription = "矢印キーで移動し、Spaceキーで選択を切り替えます。複数選択できます。";
            grpPorts.Controls.Add(chkPorts);

            var lblPortWarn = new Label { Text = "※機器の接続・切断後はmidi2keyを再起動してください", ForeColor = Color.DarkSlateGray, Top = 73, Left = 5, AutoSize = true, Font = new Font(this.Font.FontFamily, 8.5f) };
            grpPorts.Controls.Add(lblPortWarn);
            this.Controls.Add(grpPorts);

            var grpLayout = new GroupBox { Text = "キー配列", Top = 10, Left = 295, Width = 105, Height = 95 };
            rbJIS = new RadioButton { Text = "JIS 日本語", Top = 25, Left = 10, Width = 95 };
            rbUS = new RadioButton { Text = "US 英語", Top = 55, Left = 10, Width = 95 };
            inputToolTip.SetToolTip(rbJIS, "日本語キーボードの配列で記号キーなどを変換します。");
            inputToolTip.SetToolTip(rbUS, "英語キーボードの配列で記号キーなどを変換します。");
            rbUS.CheckedChanged += (s, e) => {
                if (settings != null && rbUS.Checked) { settings.KeyboardLayout = "US"; inputTracker.PublishResolvedMapping(settings); UpdateSettingsTitle(); UpdateMappingAction(); }
            };
            rbJIS.CheckedChanged += (s, e) => {
                if (settings != null && rbJIS.Checked) { settings.KeyboardLayout = "JIS"; inputTracker.PublishResolvedMapping(settings); UpdateSettingsTitle(); UpdateMappingAction(); }
            };
            grpLayout.Controls.Add(rbJIS);
            grpLayout.Controls.Add(rbUS);
            this.Controls.Add(grpLayout);

            numTranspose = new NumericUpDown { Minimum = -48, Maximum = 48, Width = 62,
                AccessibleName = "移調（半音）", TextAlign = HorizontalAlignment.Center };
            btnOctaveDown = new Button { Text = "−12", AccessibleName = "1オクターブ下げる" };
            btnOctaveUp = new Button { Text = "+12", AccessibleName = "1オクターブ上げる" };
            btnResetTranspose = new Button { Text = "0に戻す" };
            btnOctaveDown.Click += (s, e) => numTranspose.Value = Math.Max(numTranspose.Minimum, numTranspose.Value - 12);
            btnOctaveUp.Click += (s, e) => numTranspose.Value = Math.Min(numTranspose.Maximum, numTranspose.Value + 12);
            btnResetTranspose.Click += (s, e) => numTranspose.Value = 0;
            numTranspose.ValueChanged += (s, e) => {
                if (settings == null) return;
                lock (settings.MappingLock) settings.TransposeSemitones = (int)numTranspose.Value;
                inputTracker.PublishResolvedMapping(settings);
                UpdateSettingsTitle();
                UpdateOptionsSummary();
                UpdateMonitor();
            };

            var lblList = lblMappingHeading = new Label { Text = "マッピング一覧", Top = 115, Left = 10, AutoSize = true };
            listMapping = new ListView 
            { 
                Top = 135, 
                Left = 10, 
                Width = 390, 
                Height = 135,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                ShowItemToolTips = false,
                MultiSelect = false,
                HideSelection = false,
                OwnerDraw = true
            };
            listMapping.SmallImageList = mappingRowImages;
            UpdateMappingRowHeight();
            listMapping.DpiChangedAfterParent += (_, _) => UpdateMappingRowHeight();
            listMapping.FontChanged += (_, _) => UpdateMappingRowHeight();
            lblEmptyMapping = new Label { Text = "マッピングはまだありません。\nノート/ペダルと変換キーを指定し、「追加」を押します。",
                TextAlign = ContentAlignment.MiddleCenter, BackColor = SystemColors.Window,
                ForeColor = SystemColors.GrayText, TabStop = false };
            listMapping.Controls.Add(lblEmptyMapping);
            listMapping.Columns.Add("ノート/信号", 85, HorizontalAlignment.Center);
            listMapping.Columns.Add("音名 / 種類", 145, HorizontalAlignment.Left);
            listMapping.Columns.Add("変換キー", 135, HorizontalAlignment.Left);

            // カラムヘッダーに上品な淡いブルーグレーの背景と境界線、濃紺の太字を設定
            listMapping.DrawColumnHeader += (s, e) => {
                if (e.Header == null) return;
                if (SystemInformation.HighContrast) { e.DrawDefault = true; return; }
                using (var bgBrush = new SolidBrush(Color.FromArgb(228, 236, 246)))
                {
                    e.Graphics.FillRectangle(bgBrush, e.Bounds);
                }
                using (var borderPen = new Pen(Color.FromArgb(200, 212, 228)))
                {
                    e.Graphics.DrawLine(borderPen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                    e.Graphics.DrawLine(borderPen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
                }
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding;
                if (e.Header.TextAlign == HorizontalAlignment.Center)
                    flags |= TextFormatFlags.HorizontalCenter;
                else if (e.Header.TextAlign == HorizontalAlignment.Right)
                    flags |= TextFormatFlags.Right;
                else
                    flags |= TextFormatFlags.Left;

                using (var headerFont = new Font(this.Font, FontStyle.Bold))
                {
                    TextRenderer.DrawText(e.Graphics, e.Header.Text, headerFont, e.Bounds, Color.FromArgb(35, 55, 85), flags);
                }
            };
            listMapping.AccessibleName = "マッピング一覧";
            listMapping.AccessibleDescription = "行を選択し、Enterまたはダブルクリックで変換キーを編集できます。";
            listMapping.MouseDoubleClick += (_, e) => {
                if (e.Button == MouseButtons.Left && listMapping.HitTest(e.Location).Item != null)
                    txtKey.Focus();
            };
            listMapping.PreviewKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) e.IsInputKey = true; };
            listMapping.KeyDown += (_, e) => {
                if (e.KeyCode == Keys.Enter && listMapping.SelectedItems.Count > 0)
                    e.Handled = true;
            };
            listMapping.KeyUp += (_, e) => {
                if (e.KeyCode != Keys.Enter || listMapping.SelectedItems.Count == 0) return;
                // Start capturing only after Enter is released, so it is not recorded
                // as the replacement key (including when Enter auto-repeats).
                e.Handled = true;
                txtKey.Focus();
            };
            listMapping.DrawItem += (s, e) => e.DrawDefault = true;
            listMapping.DrawSubItem += (s, e) => e.DrawDefault = true;

            listMapping.SelectedIndexChanged += (s, e) => {
                UpdateMappingSelection();
                if (listMapping.SelectedItems.Count == 0) return;
                var item = listMapping.SelectedItems[0];
                txtNote.Text = item.Text;
                txtKey.Text = item.SubItems.Count > 2 ? item.SubItems[2].Text : "";
            };
            this.Controls.Add(lblList);
            this.Controls.Add(listMapping);

            var lblNote = new Label { Text = "ノート/ペダル ⓘ", Top = 280, Left = 10, AutoSize = true };
            txtNote = new TextBox { Top = 300, Left = 10, Width = 75, PlaceholderText = "0〜127 / pedal" };
            txtNote.AccessibleName = "ノート番号またはペダル";
            txtNote.AccessibleDescription = "0から127、またはpedalを入力できます。フォーカス中はMIDI鍵盤やペダルからも取得できます。";
            lblCaptureHint = new Label { Text = "", ForeColor = Color.DarkSlateGray };
            
            // ノート取得モードと変換モードのライフサイクルおよびGeneration同期
            txtNote.Enter += (s, e) => {
                if (!inputTracker.IsListening)
                {
                    StartCapture();
                }
                else
                {
                    // 変換実行中: リスナーは再起動せずキャプチャフラグのみON
                    inputTracker.SetCapturing(true);
                    if (_isTransitioning)
                        SetCaptureHint("変換の開始処理中です…");
                    else ShowCaptureReady();
                }
            };
            txtNote.Leave += (s, e) => {
                if (!inputTracker.IsListening)
                {
                    // 停止中のキャプチャ終了: キャプチャ専用リスナーを停止
                    StopCapture();
                }
                else
                {
                    // 変換実行中: キャプチャフラグのみOFF（押下中ノートの抑止は維持）
                    inputTracker.SetCapturing(false);
                }
                SetCaptureHint("");
            };

            var lblKey = new Label { Text = "変換キー ⓘ", Top = 280, Left = 95, AutoSize = true };
            txtKey = new TextBox { Top = 300, Left = 95, Width = 115, ReadOnly = true, BackColor = SystemColors.Window, PlaceholderText = "選択してキーを入力" };
            
            const string noteHelp = "「ノート/ペダル」の入力欄を選択し、MIDI鍵盤やペダルを操作すると自動取得します。\n0〜127 または pedal の直接入力もできます。取得中は変換キーを送信しません。";
            const string keyHelp = "「変換キー」の入力欄を選択し、割り当てたいキーを押してください。\n選択すると現在の入力値は消えます。Shiftで入力する大文字・記号も登録できます。";
            inputToolTip.SetToolTip(lblNote, noteHelp);
            inputToolTip.SetToolTip(txtNote, noteHelp);
            inputToolTip.SetToolTip(lblKey, keyHelp);
            inputToolTip.SetToolTip(txtKey, keyHelp);
            txtNote.AccessibleDescription = noteHelp;
            txtKey.AccessibleName = "変換キー";
            txtKey.AccessibleDescription = keyHelp;

            txtKey.Enter += (s, e) => {
                txtKey.Text = "";
                StartTargetKeyCapture();
                UpdateInputHint();
            };
            txtKey.Leave += (s, e) => { StopTargetKeyCapture(); UpdateInputHint(); };
            this.Deactivate += (s, e) => { StopTargetKeyCapture(); UpdateInputHint(); };
            this.Activated += (s, e) => {
                if (txtKey.Focused) StartTargetKeyCapture();
                UpdateInputHint();
            };

            // ノート番号バリデーション（0〜127）、pedal の登録、およびキー厳格検証
            btnAdd = new Button { Text = "追加", Enabled = false, Top = 275, Left = 315, Width = 85, Height = 25 };
            txtNote.TextChanged += (s, e) => {
                // A changed value must not retain the result of an earlier MIDI capture.
                captureHintMessage = "";
                UpdateMappingAction();
            };
            txtKey.TextChanged += (s, e) => UpdateMappingAction();
            btnAdd.Click += (s, e) => {
                string noteInput = txtNote.Text.Trim();
                string keyInput = txtKey.Text.Trim();

                if (string.IsNullOrEmpty(noteInput))
                {
                    MessageBox.Show("ノート番号（または pedal）を入力してください。", "入力エラー");
                    return;
                }
                if (string.IsNullOrEmpty(keyInput))
                {
                    MessageBox.Show("割り当てるキーを設定してください。", "入力エラー");
                    return;
                }

                // 変換先キーの厳格な検証
                if (!KeyResolver.IsValidTargetKey(keyInput, settings.KeyboardLayout))
                {
                    MessageBox.Show(
                        $"指定された変換先キー '{keyInput}' は無効またはサポートされていません。\n有効なキーを指定してください。",
                        "キー検証エラー",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                // Keep the same guard for programmatic invocations of the action.
                if (IsHotkeyMappingConflict(keyInput))
                {
                    UpdateMappingAction();
                    return;
                }

                if (noteInput.Equals("pedal", StringComparison.OrdinalIgnoreCase))
                {
                    lock (settings.MappingLock)
                    {
                        settings.Mapping["pedal"] = keyInput;
                    }
                    inputTracker.PublishResolvedMapping(settings);
                    RefreshMappingList("pedal");
                    listMapping.Focus();
                    return;
                }

                if (int.TryParse(noteInput, out int note))
                {
                    if (note < 0 || note > 127)
                    {
                        MessageBox.Show("ノート番号は 0 〜 127 の範囲で入力してください。", "範囲エラー");
                        return;
                    }

                    lock (settings.MappingLock)
                    {
                        settings.Mapping[note.ToString()] = keyInput;
                    }
                    inputTracker.PublishResolvedMapping(settings);
                    RefreshMappingList(note.ToString());
                    listMapping.Focus();
                }
                else
                {
                    MessageBox.Show("ノート番号は 0〜127 の数値、または「pedal」を入力してください。", "入力エラー");
                }
            };
            
            var btnDel = btnDeleteMapping = new Button { Text = "選択を削除", Enabled = false, Top = 305, Left = 315, Width = 85, Height = 25 };
            btnDel.Click += (s, e) => {
                if (listMapping.SelectedItems.Count > 0)
                {
                    var selected = listMapping.SelectedItems[0];
                    var keyToRemove = selected.Text;
                    string? nextKey = selected.Index + 1 < listMapping.Items.Count
                        ? listMapping.Items[selected.Index + 1].Text
                        : selected.Index > 0 ? listMapping.Items[selected.Index - 1].Text : null;
                    lock (settings.MappingLock)
                    {
                        settings.Mapping.Remove(keyToRemove);
                    }
                    inputTracker.PublishResolvedMapping(settings);
                    RefreshMappingList(nextKey);
                    if (nextKey == null)
                    {
                        ResetMappingEditor();
                    }
                    listMapping.Focus();
                }
            };

            this.Controls.Add(lblNote);
            this.Controls.Add(txtNote);
            this.Controls.Add(lblKey);
            this.Controls.Add(txtKey);
            this.Controls.Add(btnAdd);
            this.Controls.Add(btnDel);

            var sep = new Label { BorderStyle = BorderStyle.Fixed3D, Top = 340, Left = 10, Width = 390, Height = 2 };
            this.Controls.Add(sep);

            // 「上書き保存」ボタン
            btnSave = new Button { Text = "上書き保存", Top = 355, Left = 10, Width = 85, Height = 28 };
            btnSave.Click += (s, e) => {
                if (isCurrentSettingsCorrupted)
                {
                    var confirm = MessageBox.Show(
                        $"元の設定ファイル ({Path.GetFileName(currentSettingsPath)}) は破損していたため保護されています。\nこのまま上書き保存しますか？\n（[いいえ] を選ぶと別名保存ダイアログが開きます）",
                        "破損ファイル保護確認",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Question);

                    if (confirm == DialogResult.No)
                    {
                        btnSaveAs.PerformClick();
                        return;
                    }
                    else if (confirm == DialogResult.Cancel)
                    {
                        return;
                    }
                }

                UpdateSettingsFromUI();
                try
                {
                    SettingsManager.Save(currentSettingsPath, settings);
                    isCurrentSettingsCorrupted = false;
                    AcceptSavedSettings();
                    ShowNotification($"保存完了: {Path.GetFileName(currentSettingsPath)}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("設定の保存に失敗しました: " + ex.Message, "エラー");
                }
            };

            // 「別名保存」ボタン
            btnSaveAs = new Button { Text = "別名保存…", Top = 355, Left = 105, Width = 85, Height = 28 };
            inputToolTip.SetToolTip(btnSaveAs, "別のファイルに保存します。以後の上書き保存先もそのファイルに変わります。");
            btnSaveAs.Click += (s, e) => {
                UpdateSettingsFromUI();
                using (var sfd = new SaveFileDialog())
                {
                    sfd.InitialDirectory = Path.GetDirectoryName(currentSettingsPath);
                    sfd.Filter = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*";
                    sfd.DefaultExt = "json";
                    sfd.FileName = Path.GetFileName(currentSettingsPath);
                    if (sfd.ShowDialog() == DialogResult.OK)
                    {
                        string targetPath = sfd.FileName;
                        try
                        {
                            SettingsManager.Save(targetPath, settings);
                            currentSettingsPath = targetPath;
                            currentSettingsDir = Path.GetDirectoryName(currentSettingsPath) ?? appDir;
                            isCurrentSettingsCorrupted = false;
                            AcceptSavedSettings();
                            ShowNotification($"保存完了: {Path.GetFileName(currentSettingsPath)}");
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("設定の保存に失敗しました: " + ex.Message, "保存エラー");
                        }
                    }
                }
            };
            
            // 「設定読込」ボタン
            var btnLoad = new Button { Text = "設定読込…", Top = 355, Left = 305, Width = 95, Height = 28 };
            btnLoad.Click += (s, e) => {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.InitialDirectory = Path.GetDirectoryName(currentSettingsPath);
                    ofd.Filter = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*";
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        string targetPath = ofd.FileName;
                        try 
                        {
                            var newSettings = SettingsManager.Load(targetPath);
                            if (!ConfirmPendingChanges()) return;
                            newSettings = SettingsManager.Load(targetPath);

                            if (inputTracker.IsListening)
                            {
                                StopConversion();
                            }

                            currentSettingsPath = targetPath;
                            currentSettingsDir = Path.GetDirectoryName(currentSettingsPath) ?? appDir;
                            isCurrentSettingsCorrupted = false;

                            lock (settings.MappingLock)
                            {
                                settings = newSettings;
                            }

                            RefreshPorts(true);
                            if (settings.KeyboardLayout == "US") rbUS.Checked = true; else rbJIS.Checked = true;
                            RefreshMappingList(resetEditor: true);
                            inputTracker.PublishResolvedMapping(settings);
                            UpdateHotkeyRegistration();
                            AcceptSavedSettings();
                            ShowNotification($"読込完了: {Path.GetFileName(currentSettingsPath)}");
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("設定ファイルの読み込みに失敗しました:\n" + ex.Message, "読込エラー");
                        }
                    }
                }
            };

            chkTestMode = new CheckBox { Text = "テストモード（キー送信なし）", AutoSize = true, Margin = Padding.Empty };
            chkTestMode.CheckedChanged += (s, e) => UpdateOptionsSummary();
            lblNoteMonitor = new Label { Text = "ノート: 受信なし", AutoEllipsis = true };
            lblPedalMonitor = new Label { Text = "ペダル: 受信なし", AutoEllipsis = true };
            var btnStart = btnStartConversion = new Button { Text = "変換開始", Top = 395, Left = 10, Width = 95, Height = 35, Font = new Font(this.Font, FontStyle.Bold) };
            btnStart.Click += (s, e) => { StartConversion(); UpdateTrayState(); };

            var btnStop = btnStopConversion = new Button { Text = "変換停止", Top = 395, Left = 305, Width = 95, Height = 35, Font = new Font(this.Font, FontStyle.Bold) };
            btnStop.Click += (s, e) => { StopConversion(); UpdateTrayState(); };

            // トグルホットキー表示＆変更ボタン
            lblHotkeyInfo = new Label { Top = 440, Left = 10, Width = 280, AutoSize = false, Text = "ホットキー: 有効 (Ctrl+Alt+F9)", ForeColor = Color.FromArgb(40, 40, 40) };
            btnHotkeyConfig = new Button { Text = "ホットキー設定…", Top = 435, Left = 295, Width = 105, Height = 26 };
            btnHotkeyConfig.Click += (s, e) => OpenHotkeyConfigDialog();

            lblStatus = new Label { Text = "ステータス: 停止中", Top = 475, Left = 10, Width = 390, Height = 48, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.ControlText, Font = new Font(this.Font, FontStyle.Bold) };

            lblNotification = new Label { Left = 10, Top = 528, Width = 390, Height = 24,
                AutoEllipsis = true, ForeColor = SystemColors.ControlText, TextAlign = ContentAlignment.MiddleCenter };
            notificationTimer.Tick += (s, e) => { notificationTimer.Stop(); lblNotification.Text = ""; };
            this.Controls.Add(lblNotification);
            this.Controls.Add(btnSave);
            this.Controls.Add(btnSaveAs);
            this.Controls.Add(btnLoad);
            this.Controls.Add(btnStart);
            this.Controls.Add(btnStop);
            this.Controls.Add(lblHotkeyInfo);
            this.Controls.Add(btnHotkeyConfig);
            this.Controls.Add(lblStatus);

            ArrangeResizableLayout(grpPorts, grpLayout, lblPortWarn, lblList, lblNote, lblKey,
                btnDel, sep, btnLoad, btnStart, btnStop);
            this.MinimumSize = this.SizeFromClientSize(new Size(380, 650));
            this.ResumeLayout(true);

            // デバイス切断・変更検知のデバウンスタイマー
            deviceDebounceTimer = new System.Windows.Forms.Timer { Interval = 300 };
            deviceDebounceTimer.Tick += (s, e) => {
                deviceDebounceTimer.Stop();
                long triggerGen = inputTracker.CurrentSessionId;
                long requestId = Interlocked.Increment(ref _healthCheckRequestId);
                DiagnosticLogger.Log($"[DebounceTimer] Tick fired. Invoking CheckDeviceHealth(). Gen={triggerGen}, RequestId={requestId}");
                
                // WinRTが認識している最新のデバイス名一覧を取得
                List<string>? osDeviceNames = null;
                if (_winRtWatcher != null && _winRtWatcher.IsWatcherRunning)
                {
                    osDeviceNames = _winRtWatcher.Devices.Select(d => d.Name).ToList();
                }

                // 新規プロセスWinMMの結果を非同期で取得してCheckDeviceHealthに引き渡す
                _ = System.Threading.Tasks.Task.Run(async () => {
                    try
                    {
                        var (parentPorts, outProcResult) = await _probePorts(1500, CancellationToken.None);
                        
                        if (IsHandleCreated && !IsDisposed && !_isClosing)
                        {
                            try
                            {
                                BeginInvoke(new Action(async () => {
                                    if (IsDisposed || _isClosing) return;

                                    // 要件5: 同一セッション内での古い列挙結果の後着上書き防止
                                    if (requestId < _latestCompletedHealthCheckRequestId)
                                    {
                                        DiagnosticLogger.Log($"[DebounceTimer] Ignored out-of-order health check result: RequestId={requestId}, LatestCompleted={_latestCompletedHealthCheckRequestId}");
                                        return;
                                    }
                                    _latestCompletedHealthCheckRequestId = requestId;

                                    if (inputTracker.IsListening)
                                    {
                                        // 変換実行中:
                                        // Generation検証（タイマー発火時からセッションが変わっていればリスナーのヘルスチェックはスキップ）
                                        if (triggerGen != inputTracker.CurrentSessionId)
                                        {
                                            DiagnosticLogger.Log($"[DebounceTimer] Ignored outdated session health check: TriggerGen={triggerGen}, CurrentGen={inputTracker.CurrentSessionId}");
                                            RefreshPorts(false);
                                            return;
                                        }

                                        IReadOnlyList<MidiPortInfo> beforeActive = midiListener.GetActivePorts();
                                        IReadOnlyList<MidiPortInfo> afterActive = beforeActive;
                                        // A successful fresh WinMM result is authoritative even if the
                                        // parent enumeration failed. Never interpret two failed probes as zero ports.
                                        if ((parentPorts.Success || outProcResult.Success) && !midiLifecycle.IsBusy)
                                        {
                                            try
                                            {
                                                (beforeActive, afterActive) = await midiLifecycle.CheckHealthAsync(
                                                    osDeviceNames, outProcResult, triggerGen, parentPorts.Ports)
                                                    .WaitAsync(MidiStartTimeout);
                                            }
                                            catch (TimeoutException)
                                            {
                                                DiagnosticLogger.Log("DeviceHealth", "MIDIデバイス確認が時間内に完了しませんでした。");
                                                StopConversion();
                                                return;
                                            }
                                            catch (InvalidOperationException) { return; }
                                            catch (Exception ex)
                                            {
                                                DiagnosticLogger.Log("DeviceHealth", $"MIDIデバイス確認失敗: {ex}");
                                                return;
                                            }
                                            if (_isClosing || IsDisposed || triggerGen != inputTracker.CurrentSessionId) return;
                                        }
                                        RefreshPorts(false);

                                        // 監視中デバイスの切断は HandleDeviceDisconnected で通知される。
                                        // 切断が発生しなかった場合の接続変更判定:
                                        if (beforeActive.Count == afterActive.Count)
                                        {
                                            CheckConnectionChangeAndNotify(outProcResult, parentPorts);
                                        }
                                    }
                                    else
                                    {
                                        // ① 変換停止中:
                                        // リスナー監視はないためCheckDeviceHealthは不要だが、ポート一覧更新と接続変更（再起動案内）を実行する
                                        RefreshPorts(false);
                                        CheckConnectionChangeAndNotify(outProcResult, parentPorts);
                                    }
                                }));
                            }
                            catch { }
                        }

                        if (DiagnosticLogger.IsEnabled)
                        {
                            var activePorts = midiListener.GetActivePorts();
                            await MidiDeviceDiagnostics.RunComparisonAsync(
                                "DeviceChangeDebounce", triggerGen, activePorts,
                                backendResult: parentPorts, winMmResult: outProcResult);
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Log($"[DebounceTimer] Error during health check: {ex.Message}");
                    }
                });
            };
        }

        private void ArrangeResizableLayout(GroupBox ports, GroupBox layout, Label portWarning,
            Label listHeading, Label noteLabel, Label keyLabel, Button delete,
            Label separator, Button load, Button start, Button stop)
        {
            // Only the mapping row absorbs extra height. Every other row keeps its readable size.
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10),
                ColumnCount = 1, RowCount = 10, TabIndex = 0 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 100, 24, 0, 76, 10, 36, 44, 24, 32, 30 })
                root.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
            root.RowStyles[9].SizeType = SizeType.Absolute;
            root.RowStyles[9].Height = showOptions ? 132 : 0;

            static void Fill(Control control)
            {
                control.Dock = DockStyle.Fill;
                control.Margin = Padding.Empty;
                if (control is Label label) label.AutoSize = false;
            }
            TableLayoutPanel Row(int columns)
            {
                var row = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                    ColumnCount = columns, RowCount = 1 };
                row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                return row;
            }

            var deviceRow = Row(2);
            deviceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            deviceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
            Fill(ports); ports.Margin = new Padding(0, 0, 10, 5);
            Fill(layout); layout.Margin = new Padding(0, 0, 0, 5);
            deviceRow.Controls.Add(ports, 0, 0);
            deviceRow.Controls.Add(layout, 1, 0);
            // The small warning wraps inside its group as the window or DPI changes.
            var portContents = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                Padding = new Padding(5, 2, 5, 0) };
            portContents.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            portContents.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            Fill(chkPorts); chkPorts.IntegralHeight = false;
            Fill(portWarning);
            portWarning.Text = "機器の接続・切断後は\nアプリを再起動してください";
            portContents.Controls.Add(chkPorts, 0, 0);
            portContents.Controls.Add(portWarning, 0, 1);
            ports.Controls.Add(portContents);
            root.Controls.Add(deviceRow, 0, 0);
            Fill(listHeading); listHeading.TextAlign = ContentAlignment.MiddleLeft;
            root.Controls.Add(listHeading, 0, 1);
            var transpose = Row(5);
            foreach (int width in new[] { 85, 60, 50, 50, 75 })
                transpose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
            Control[] transposeControls = { new Label { Text = "移調（半音）", TextAlign = ContentAlignment.MiddleLeft },
                numTranspose, btnOctaveDown, btnOctaveUp, btnResetTranspose };
            for (int i = 0; i < transposeControls.Length; i++)
            {
                Fill(transposeControls[i]);
                transposeControls[i].Margin = new Padding(0, 0, 6, 5);
                transposeControls[i].TabIndex = i;
                transpose.Controls.Add(transposeControls[i], i, 0);
            }
            Fill(listMapping); listMapping.Margin = new Padding(0, 0, 0, 8); listMapping.TabIndex = 1;
            root.Controls.Add(listMapping, 0, 2);
            listMapping.SizeChanged += (s, e) => { QueueMappingColumnResize(); UpdateEmptyMappingHint(); };
            listMapping.HandleCreated += (s, e) => QueueMappingColumnResize();

            var editor = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                ColumnCount = 3, RowCount = 3, TabIndex = 2 };
            foreach (var width in new[] { 102, 0, 85 })
                editor.ColumnStyles.Add(width == 0 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.Absolute, width));
            foreach (var height in new[] { 20, 28, 28 }) editor.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Fill(noteLabel); Fill(keyLabel);
            editor.Controls.Add(noteLabel, 0, 0); editor.Controls.Add(keyLabel, 1, 0);
            Control[] inputs = { txtNote, txtKey, btnAdd };
            for (int column = 0; column < inputs.Length; column++)
            {
                Fill(inputs[column]); inputs[column].Margin = new Padding(0, 0, column == 2 ? 0 : 6, 3);
                inputs[column].TabIndex = column;
                editor.Controls.Add(inputs[column], column, 1);
            }
            Fill(delete); delete.Dock = DockStyle.Top; delete.Height = 25; delete.Margin = Padding.Empty; delete.TabIndex = 3;
            editor.Controls.Add(delete, 2, 2);
            Fill(lblCaptureHint); lblCaptureHint.AutoEllipsis = true; lblCaptureHint.Padding = new Padding(0, 3, 0, 0);
            editor.Controls.Add(lblCaptureHint, 0, 2); editor.SetColumnSpan(lblCaptureHint, 2);
            root.Controls.Add(editor, 0, 3);
            Fill(separator); separator.Dock = DockStyle.Top; separator.Height = 2;
            root.Controls.Add(separator, 0, 4);

            TableLayoutPanel ActionRow(int tabIndex)
            {
                var row = Row(5); row.TabIndex = tabIndex;
                for (int column = 0; column < 5; column++)
                    row.ColumnStyles.Add(column % 2 == 0
                        ? new ColumnStyle(SizeType.Percent, 100f / 3)
                        : new ColumnStyle(SizeType.Absolute, 8));
                return row;
            }
            var files = ActionRow(3);
            Fill(btnSave); Fill(btnSaveAs); Fill(load);
            btnSave.Margin = btnSaveAs.Margin = load.Margin = new Padding(0, 0, 0, 8);
            files.Controls.Add(btnSave, 0, 0); files.Controls.Add(btnSaveAs, 2, 0); files.Controls.Add(load, 4, 0);
            root.Controls.Add(files, 0, 5);
            var conversion = ActionRow(4);
            Fill(start); Fill(stop); Fill(btnHotkeyConfig);
            start.Margin = stop.Margin = btnHotkeyConfig.Margin = new Padding(0, 0, 0, 9);
            start.TabIndex = 0; stop.TabIndex = 1; btnHotkeyConfig.TabIndex = 2;
            inputToolTip.SetToolTip(btnHotkeyConfig, "変換の開始・停止を切り替えるショートカットキーを設定します。");
            conversion.Controls.Add(start, 0, 0); conversion.Controls.Add(stop, 2, 0);
            conversion.Controls.Add(btnHotkeyConfig, 4, 0);
            root.Controls.Add(conversion, 0, 6);
            // Reuse the hotkey information line for brief notifications, keeping
            // the mapping list, input fields and conversion status at fixed positions.
            var feedback = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            Fill(lblHotkeyInfo); Fill(lblNotification);
            lblHotkeyInfo.TextAlign = lblNotification.TextAlign = ContentAlignment.MiddleLeft;
            feedback.Controls.Add(lblHotkeyInfo); feedback.Controls.Add(lblNotification);
            root.Controls.Add(feedback, 0, 7);
            Fill(lblStatus);
            root.Controls.Add(lblStatus, 0, 8);
            void UpdateNotificationVisibility()
            {
                bool hasNotification = !string.IsNullOrEmpty(lblNotification.Text);
                lblHotkeyInfo.Visible = !hasNotification;
                lblNotification.Visible = hasNotification;
                if (hasNotification) lblNotification.BringToFront();
            }
            lblNotification.TextChanged += (_, _) => UpdateNotificationVisibility();
            UpdateNotificationVisibility();
            var monitor = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                ColumnCount = 1, RowCount = 3 };
            monitor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            monitor.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
            monitor.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            monitor.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            var heading = new Label { Text = "入力モニター（最新の受信・現在の割当／送信結果ではありません）",
                Dock = DockStyle.Fill, AutoEllipsis = true, Margin = Padding.Empty };
            monitor.Controls.Add(heading, 0, 0);
            Fill(lblNoteMonitor); Fill(lblPedalMonitor);
            monitor.Controls.Add(lblNoteMonitor, 0, 1);
            monitor.Controls.Add(lblPedalMonitor, 0, 2);

            var optionsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                ColumnCount = 1, RowCount = 2, TabIndex = 6, Visible = showOptions };
            optionsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            optionsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            optionsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
            var optionsHeader = new Label { Text = "追加設定", Dock = DockStyle.Fill, Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleLeft };
            var optionsBody = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                ColumnCount = 1, RowCount = 3 };
            optionsBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            optionsBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            optionsBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            optionsBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            optionsBody.Controls.Add(monitor, 0, 2);
            optionsBody.Controls.Add(transpose, 0, 0);
            optionsBody.Controls.Add(chkTestMode, 0, 1);
            optionsLayout.Controls.Add(optionsHeader, 0, 0);
            optionsLayout.Controls.Add(optionsBody, 0, 1);
            root.Controls.Add(optionsLayout, 0, 9);
            StyleActionButtons(root, start, stop);
            Controls.Add(root);
        }

        private static void StyleActionButtons(Control container, Button start, Button stop)
        {
            // Preserve Windows' contrast palette when accessibility contrast mode is active.
            if (SystemInformation.HighContrast) return;
            foreach (Control control in container.Controls)
            {
                if (control is Button button)
                {
                    bool primary = button == start;
                    bool stopAction = button == stop;
                    button.FlatStyle = FlatStyle.Flat;
                    button.UseVisualStyleBackColor = false;
                    button.BackColor = primary ? Color.FromArgb(28, 100, 118) : Color.White;
                    button.ForeColor = primary ? Color.White :
                        stopAction ? Color.FromArgb(151, 52, 63) : Color.FromArgb(40, 52, 65);
                    button.FlatAppearance.BorderColor = primary ? Color.FromArgb(28, 100, 118) :
                        stopAction ? Color.FromArgb(207, 166, 172) : Color.FromArgb(184, 194, 204);
                    button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(23, 83, 99) :
                        stopAction ? Color.FromArgb(252, 239, 241) : Color.FromArgb(237, 243, 247);
                    button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(18, 68, 82) :
                        stopAction ? Color.FromArgb(245, 222, 226) : Color.FromArgb(221, 232, 240);
                    if (primary)
                    {
                        void UpdatePrimaryColors()
                        {
                            button.BackColor = button.Enabled ? Color.FromArgb(28, 100, 118) : Color.FromArgb(232, 236, 240);
                            button.ForeColor = button.Enabled ? Color.White : SystemColors.GrayText;
                            button.FlatAppearance.BorderColor = button.Enabled ? Color.FromArgb(28, 100, 118) : Color.FromArgb(200, 208, 216);
                        }
                        button.EnabledChanged += (_, _) => UpdatePrimaryColors();
                        UpdatePrimaryColors();
                    }
                }
                else if (control.HasChildren) StyleActionButtons(control, start, stop);
            }
        }

        private void ShowCaptureReady() => SetCaptureHint("入力待ち: 鍵盤・ペダルを操作してください");

        private void QueueMappingColumnResize()
        {
            if (sizingMappingColumns || listMapping == null || listMapping.IsDisposed) return;
            ResizeMappingColumns();
            if (mappingColumnResizePending || !listMapping.IsHandleCreated) return;
            mappingColumnResizePending = true;
            try
            {
                // SizeChanged runs inside the native resize message. Repeat after that message
                // finishes so the list recalculates its scroll range from the final column widths.
                listMapping.BeginInvoke(new Action(() => {
                    try
                    {
                        if (!_isClosing && !IsDisposed && !listMapping.IsDisposed && listMapping.IsHandleCreated)
                            ResizeMappingColumns();
                    }
                    finally { mappingColumnResizePending = false; }
                }));
            }
            catch (InvalidOperationException) { mappingColumnResizePending = false; }
        }

        private void ResizeMappingColumns()
        {
            if (sizingMappingColumns || listMapping == null || listMapping.Columns.Count < 3) return;
            sizingMappingColumns = true;
            try
            {
                // Updating native columns can itself resize the client area as scrollbars change.
                // Reserve scrollbar room throughout and avoid re-entering with partial widths.
                int width = Math.Max(0, listMapping.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
                listMapping.BeginUpdate();
                try
                {
                    // Shrink before growing: native ListView can retain the intermediate scroll
                    // range when a narrower first column is combined with the old later columns.
                    foreach (ColumnHeader column in listMapping.Columns) column.Width = 0;
                    listMapping.Columns[0].Width = width * 24 / 100;
                    listMapping.Columns[1].Width = width * 40 / 100;
                    listMapping.Columns[2].Width = width - listMapping.Columns[0].Width - listMapping.Columns[1].Width;
                }
                finally { listMapping.EndUpdate(); }
            }
            finally { sizingMappingColumns = false; }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Tests exercise the menu lifecycle without adding icons to the user's tray.
            if (trayIcon != null) trayIcon.Visible = !_skipWatcher;
            if (_skipWatcher) return;
            DiagnosticLogger.Log("[Form1] OnShown: Performing initial port refresh.");
            RefreshPorts(true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DiagnosticLogger.Log("[Form1] OnHandleCreated: Updating hotkey registration.");
            UpdateHotkeyRegistration();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            DiagnosticLogger.Log("[Form1] OnHandleDestroyed: Unregistering hotkey.");
            hotkeyManager.Unregister();
            base.OnHandleDestroyed(e);
        }

        /// <summary>
        /// ホットキー設定ダイアログを表示し、変更を反映します。
        /// </summary>
        private void OpenHotkeyConfigDialog()
        {
            using (var dlg = new HotkeyConfigDialog(settings.Hotkey))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    var newHotkey = dlg.ResultSettings;

                    // 1. 既存MIDIマッピングとの衝突検証
                    if (newHotkey.Enabled && SettingsManager.IsHotkeyConflictingWithMapping(newHotkey, settings.Mapping, settings.KeyboardLayout))
                    {
                        MessageBox.Show(
                            $"ホットキー '{newHotkey.Key}' は既にMIDIマッピング先として登録されています。\n誤動作を防ぐため、MIDIマッピングで使用されていないキーを指定してください。",
                            "ホットキー競合警告",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        return;
                    }

                    // 2. ホットキー登録とロールバック
                    if (hotkeyManager.TryRegister(this.Handle, HotkeyManager.DEFAULT_HOTKEY_ID, newHotkey, settings.Mapping, settings.KeyboardLayout, out string? errMsg))
                    {
                        settings.Hotkey = newHotkey;
                        UpdateHotkeyUi();
                        UpdateSettingsTitle();
                        ShowNotification("ホットキー設定を更新しました");
                    }
                    else
                    {
                        MessageBox.Show(errMsg ?? "ホットキーの登録に失敗しました。", "ホットキー登録エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        UpdateHotkeyUi();
                    }
                }
            }
        }

        /// <summary>
        /// 現在の設定に基づいてホットキーの登録を行います。
        /// </summary>
        private void UpdateHotkeyRegistration()
        {
            if (!IsHandleCreated || IsDisposed) return;

            if (settings?.Hotkey == null) return;

            if (hotkeyManager.TryRegister(this.Handle, HotkeyManager.DEFAULT_HOTKEY_ID, settings.Hotkey, settings.Mapping, settings.KeyboardLayout, out string? errMsg))
            {
                UpdateHotkeyUi();
            }
            else
            {
                UpdateHotkeyUi();
                if (!string.IsNullOrEmpty(errMsg))
                {
                    DiagnosticLogger.Log($"[Form1] Hotkey registration failed: {errMsg}");
                    Console.WriteLine($"ホットキー登録通知: {errMsg}");
                }
            }
        }

        private void UpdateHotkeyUi()
        {
            if (lblHotkeyInfo == null) return;

            if (settings?.Hotkey != null && settings.Hotkey.Enabled)
            {
                if (hotkeyManager.IsRegistered)
                {
                    lblHotkeyInfo.Text = $"ホットキー: 有効 ({settings.Hotkey.Modifiers}+{settings.Hotkey.Key})";
                    lblHotkeyInfo.ForeColor = Color.FromArgb(40, 40, 40);
                }
                else
                {
                    lblHotkeyInfo.Text = $"ホットキー: 登録失敗 ({settings.Hotkey.Modifiers}+{settings.Hotkey.Key})";
                    lblHotkeyInfo.ForeColor = Color.Red;
                }
            }
            else
            {
                lblHotkeyInfo.Text = "ホットキー: 無効";
                lblHotkeyInfo.ForeColor = Color.Gray;
            }
            UpdateMappingAction();
        }

        /// <summary>
        /// トグルホットキー受信時の変換状態切り替え処理。
        /// </summary>
        private void ToggleConversion()
        {
            if (_isTransitioning)
            {
                StopConversion();
                return;
            }

            if (inputTracker.IsListening)
            {
                StopConversion();
            }
            else
            {
                StartConversion();
            }
        }

        private async void StartCapture()
        {
            if (_isClosing || IsDisposed) return;
            if (_isTransitioning)
            {
                SetCaptureHint("変換の開始処理中です…");
                return;
            }
            if (inputTracker.IsListening ||
                _captureGeneration != 0 || (_captureCancellation != null && !_captureCancellation.IsCancellationRequested)) return;
            var ports = GetSelectedPorts();
            if (ports.Count == 0)
            {
                SetCaptureHint("");
                return;
            }
            SetCaptureHint("取得準備中: MIDIポートを開いています…");
            var cancellation = new CancellationTokenSource();
            _captureCancellation = cancellation;
            long attemptId = ++_captureAttemptId;
            long generation = 0;
            bool waitingForPreviousStop = false;
            try
            {
                if (midiLifecycle.IsBusy)
                {
                    waitingForPreviousStop = true;
                    SetCaptureHint("取得準備中: MIDIの停止を待っています…");
                    lblStatus.Text = "ステータス: MIDI停止処理待機中 (応答しない場合は再起動してください)";
                    lblStatus.ForeColor = Color.DarkGoldenrod;
                    await midiLifecycle.WaitForIdleAsync().WaitAsync(MidiStartTimeout, cancellation.Token);
                    waitingForPreviousStop = false;
                }
                if (attemptId != _captureAttemptId || _isClosing || IsDisposed ||
                    _isTransitioning || inputTracker.IsListening || cancellation.IsCancellationRequested ||
                    !txtNote.Focused || !ports.SequenceEqual(GetSelectedPorts(), StringComparer.OrdinalIgnoreCase)) return;

                generation = inputTracker.StartCaptureSession();
                _captureGeneration = generation;
                var startResult = await midiLifecycle.StartAsync(ports, generation, null, cancellation.Token)
                    .WaitAsync(MidiStartTimeout, cancellation.Token);
                if (attemptId != _captureAttemptId || _isClosing || IsDisposed || _isTransitioning ||
                    cancellation.IsCancellationRequested || inputTracker.IsListening ||
                    !txtNote.Focused || inputTracker.CurrentSessionId != generation ||
                    !ports.SequenceEqual(GetSelectedPorts(), StringComparer.OrdinalIgnoreCase))
                    StopCapture(generation);
                else if (startResult.IsAllFailed)
                {
                    StopCapture(generation);
                    string reason = startResult.FailedPorts.Count > 0
                        ? $"{startResult.FailedPorts[0].PortName}: {startResult.FailedPorts[0].Reason}"
                        : "MIDIポートを確認してください";
                    lblStatus.Text = $"ステータス: キャプチャ開始失敗 ({reason})";
                    lblStatus.ForeColor = Color.Red;
                    SetCaptureHint("取得失敗: MIDIポートを確認してください");
                    DiagnosticLogger.Log("Capture", $"すべてのMIDIポートの開始に失敗: {string.Join("; ", startResult.FailedPorts.Select(f => $"{f.PortName}: {f.Reason}"))}");
                }
                else if (startResult.IsPartialSuccess)
                {
                    string failedNames = string.Join(", ", startResult.FailedPorts.Select(f => f.PortName));
                    lblStatus.Text = $"ステータス: キャプチャ中 (一部ポート開始失敗: {failedNames})";
                    lblStatus.ForeColor = Color.DarkGoldenrod;
                    ShowCaptureReady();
                    DiagnosticLogger.Log("Capture", $"一部のMIDIポートの開始に失敗: {string.Join("; ", startResult.FailedPorts.Select(f => $"{f.PortName}: {f.Reason}"))}");
                }
                else
                {
                    ShowCaptureReady();
                    if (lblStatus.Text.StartsWith("ステータス: MIDI停止処理待機中", StringComparison.Ordinal) ||
                        lblStatus.Text.StartsWith("ステータス: キャプチャ開始失敗", StringComparison.Ordinal) ||
                        lblStatus.Text.StartsWith("ステータス: キャプチャ開始タイムアウト", StringComparison.Ordinal))
                    {
                        lblStatus.Text = "ステータス: 停止中";
                        lblStatus.ForeColor = SystemColors.ControlText;
                    }
                }
            }
            catch (Exception ex) when (ex is TimeoutException || ex is OperationCanceledException)
            {
                bool ownsAttempt = attemptId == _captureAttemptId && !_isClosing && !IsDisposed &&
                    !_isTransitioning && !inputTracker.IsListening && txtNote.Focused &&
                    ports.SequenceEqual(GetSelectedPorts(), StringComparer.OrdinalIgnoreCase);
                cancellation.Cancel();
                if (generation != 0 && attemptId == _captureAttemptId) StopCapture(generation);
                if (ex is TimeoutException)
                {
                    DiagnosticLogger.Log("Capture", "MIDIキャプチャ開始が時間内に完了しませんでした。");
                    if (ownsAttempt)
                    {
                        SetCaptureHint("取得タイムアウト: ステータスを確認");
                        if (midiLifecycle.HasReleaseFailure)
                        {
                            lblStatus.Text = "ステータス: MIDIポート解放失敗 (アプリを再起動してください)";
                            lblStatus.ForeColor = Color.DarkOrange;
                        }
                        else
                        {
                            lblStatus.Text = waitingForPreviousStop
                                ? "ステータス: キャプチャ開始タイムアウト (前のMIDI停止処理が完了しません。再起動してください)"
                                : "ステータス: キャプチャ開始タイムアウト (MIDIポートの応答を確認してください)";
                            lblStatus.ForeColor = Color.DarkGoldenrod;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                bool ownsAttempt = attemptId == _captureAttemptId && !_isClosing && !IsDisposed && txtNote.Focused;
                if (generation != 0 && attemptId == _captureAttemptId) StopCapture(generation);
                DiagnosticLogger.Log("Capture", $"MIDIキャプチャ開始失敗: {ex}");
                if (ownsAttempt) SetCaptureHint("取得失敗: MIDIポートを確認してください");
                if (!_isClosing && !IsDisposed && midiLifecycle.HasReleaseFailure)
                {
                    lblStatus.Text = "ステータス: MIDIポート解放失敗 (アプリを再起動してください)";
                    lblStatus.ForeColor = Color.DarkOrange;
                }
            }
            finally
            {
                if (ReferenceEquals(_captureCancellation, cancellation)) _captureCancellation = null;
                cancellation.Dispose();
            }
        }

        private void StopCapture(long? expectedGeneration = null)
        {
            if (expectedGeneration.HasValue && _captureGeneration != expectedGeneration.Value)
                return;

            ++_captureAttemptId;
            _captureCancellation?.Cancel();
            long generation = _captureGeneration;
            _captureGeneration = 0;
            if (!_isClosing && !IsDisposed)
                SetCaptureHint("");
            if (generation == 0 || inputTracker.CurrentSessionId != generation || inputTracker.IsListening)
                return;

            inputTracker.StopCaptureSession();
            if (!_isClosing && !IsDisposed &&
                lblStatus.Text.StartsWith("ステータス: キャプチャ中", StringComparison.Ordinal))
            {
                lblStatus.Text = "ステータス: 停止中";
                lblStatus.ForeColor = SystemColors.ControlText;
            }
            ObserveMidiStop(midiLifecycle.StopAsync());
        }

        private async void ObserveMidiStop(Task task)
        {
            bool stopped = false;
            Exception? stopError = null;
            try { await task; stopped = true; }
            catch (Exception ex)
            {
                stopError = ex;
                DiagnosticLogger.Log("MidiLifecycle", $"MIDI停止処理失敗: {ex}");
            }
            finally
            {
                if (!_isClosing && !IsDisposed && !inputTracker.IsListening &&
                    (stopError != null || midiLifecycle.HasReleaseFailure))
                {
                    lblStatus.Text = "ステータス: MIDIポート解放失敗 (アプリを再起動してください)";
                    lblStatus.ForeColor = Color.DarkOrange;
                }
                else if (!_isClosing && !IsDisposed && !inputTracker.IsListening &&
                    !_isTransitioning && !midiLifecycle.IsBusy &&
                    lblStatus.Text.StartsWith("ステータス: MIDI停止処理待機中", StringComparison.Ordinal))
                {
                    lblStatus.Text = stopped ? "ステータス: 停止中" : "ステータス: 停止失敗";
                    lblStatus.ForeColor = stopped ? SystemColors.ControlText : Color.DarkGoldenrod;
                }
            }
        }

        private static async void ObserveMidiCleanup(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { DiagnosticLogger.Log("MidiLifecycle", $"MIDI停止処理失敗: {ex}"); }
        }

        /// <summary>
        /// 変換処理を開始します。ポート開始結果（全成功・一部成功・全失敗）に応じた適切な処理とロールバックを行います。
        /// </summary>
        private async void StartConversion()
        {
            if (_isClosing || _isTransitioning || inputTracker.IsListening) return;
            bool testModeAtStart = chkTestMode.Checked;
            _isTransitioning = true;
            chkTestMode.Enabled = false;
            using var startCancellation = new CancellationTokenSource();
            _startCancellation = startCancellation;
            bool sessionStarted = false;

            try
            {
                // A hotkey can start conversion while the note field still owns a capture.
                // Retire that generation before waiting for its native stop.
                StopCapture();
                if (txtNote.Focused)
                    SetCaptureHint("変換の開始処理中です…");
                if (midiLifecycle.IsBusy)
                {
                    lblStatus.Text = "ステータス: MIDI停止処理待機中 (応答しない場合は再起動してください)";
                    lblStatus.ForeColor = Color.DarkGoldenrod;
                    await midiLifecycle.WaitForIdleAsync().WaitAsync(MidiStartTimeout, startCancellation.Token);
                    if (_isClosing || IsDisposed || startCancellation.IsCancellationRequested) return;
                }

                var ports = GetSelectedPorts();
                var settingsAtStart = settings;
                var mappingAtStart = inputTracker.GetResolvedMappingSnapshot();
                if (ports.Count == 0)
                {
                    MessageBox.Show("MIDIポートを選択してください", "ポート未選択");
                    return;
                }

                // 変換開始直前にも選択ポートが現在有効か検証（要件4・課題1）
                // 選択ポートごとに親プロセス（DryWetMIDI）および新規プロセスWinMMの両方の一覧と照合する
                lblStatus.Text = "ステータス: MIDIポート確認中";
                var (enumResult, outProc) = await _probePorts(1000, startCancellation.Token);
                if (_isClosing || IsDisposed) return;
                if (startCancellation.IsCancellationRequested ||
                    !ReferenceEquals(settings, settingsAtStart) ||
                    !ReferenceEquals(inputTracker.GetResolvedMappingSnapshot(), mappingAtStart) ||
                    !ports.SequenceEqual(GetSelectedPorts(), StringComparer.OrdinalIgnoreCase))
                {
                    lblStatus.Text = "ステータス: 停止中";
                    lblStatus.ForeColor = SystemColors.ControlText;
                    return;
                }

                bool isAnyPortInvalid = false;
                if (!enumResult.Success)
                {
                    isAnyPortInvalid = true;
                }
                else
                {
                    foreach (var port in ports)
                    {
                        // 1. 親プロセスのMIDIバックエンドに含まれているか
                        bool inBackend = enumResult.Ports.Contains(port, StringComparer.OrdinalIgnoreCase);

                        // 2. 新規プロセスWinMMに含まれているか（新規プロセス照合が成功している場合）
                        //    新規プロセスWinMMの一覧に対象ポートが存在しない場合は無効と判定
                        bool inOutOfProcess = !outProc.Success || outProc.Ports.Contains(port, StringComparer.OrdinalIgnoreCase);

                        if (!inBackend || !inOutOfProcess)
                        {
                            DiagnosticLogger.Log($"[Form1] StartConversion: Port verification failed for '{port}'. inBackend={inBackend}, inOutOfProcess={inOutOfProcess}");
                            isAnyPortInvalid = true;
                            break;
                        }
                    }
                }

                if (isAnyPortInvalid)
                {
                    lblStatus.Text = "ステータス: 停止中 (MIDIポート開始不可)";
                    lblStatus.ForeColor = Color.DarkGoldenrod;
                    MessageBox.Show(
                        enumResult.Success
                            ? "選択されたMIDIポートを開始できませんでした。機器が取り外されているか、MIDIバックエンドに未反映の可能性があります。機器を接続し直し、midi2keyを再起動してください。"
                            : $"MIDIポートを確認できませんでした: {enumResult.ErrorMessage}",
                        "MIDIポート開始不可",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                UpdateSettingsFromUI();

                if (InputWarmup.Enabled)
                {
                    var warmupSnapshot = inputTracker.GetResolvedMappingSnapshot()
                        ?? throw new InvalidOperationException("入力マッピングを取得できませんでした。");
                    lblStatus.Text = "ステータス: 入力処理を準備中";
                    LatencyMeasurement.RecordInputWarmupAttempt();
                    try
                    {
                        await _runWarmup(warmupSnapshot, startCancellation.Token);
                        LatencyMeasurement.RecordInputWarmupCompleted();
                    }
                    catch (OperationCanceledException) when (startCancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception warmupError)
                    {
                        // Warmup is an optimization. Its isolated state cannot affect a real session.
                        LatencyMeasurement.RecordInputWarmupFailure();
                        DiagnosticLogger.Log("InputWarmup", $"入力処理の事前準備に失敗: {warmupError}");
                    }

                    if (_isClosing || IsDisposed) return;
                    if (startCancellation.IsCancellationRequested ||
                        !ReferenceEquals(settings, settingsAtStart) ||
                        !ReferenceEquals(inputTracker.GetResolvedMappingSnapshot(), warmupSnapshot) ||
                        !ports.SequenceEqual(GetSelectedPorts(), StringComparer.OrdinalIgnoreCase))
                    {
                        lblStatus.Text = "ステータス: 停止中";
                        lblStatus.ForeColor = SystemColors.ControlText;
                        return;
                    }
                }

                // 未解放キーの再試行・復旧確認
                if (!keySimulator.TryPrepareStartConversion())
                {
                    MessageBox.Show(
                        $"キー解放に失敗した未解放キー（{keySimulator.UnreleasedKeysCount} 件）が残っているため、変換を開始できません。\nキー入力を解放してから再度お試しください。",
                        "未解放キー警告",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                long sessionId = inputTracker.StartConversionSession(testModeAtStart);
                sessionStarted = true;
                // Start can deliver queued notes before its task completes. Apply capture
                // first; subsequent focus changes are handled by the Enter/Leave events.
                inputTracker.SetCapturing(txtNote.Focused);
                var mappingForSession = inputTracker.GetResolvedMappingSnapshot();
                DiagnosticLogger.Log($"[Form1] StartConversion invoked. Generated Gen={sessionId}, Ports=[{string.Join(", ", ports)}]");
                var snapshot = InitialPortSnapshot.FromOutOfProcessResult(sessionId, outProc);
                var startResult = await midiLifecycle.StartAsync(ports, sessionId, snapshot, startCancellation.Token)
                    .WaitAsync(MidiStartTimeout, startCancellation.Token);
                if (_isClosing || IsDisposed || startCancellation.IsCancellationRequested ||
                    !ReferenceEquals(settings, settingsAtStart) ||
                    !ReferenceEquals(inputTracker.GetResolvedMappingSnapshot(), mappingForSession) ||
                    !ports.SequenceEqual(GetSelectedPorts(), StringComparer.OrdinalIgnoreCase))
                {
                    inputTracker.StopSession();
                    ObserveMidiCleanup(midiLifecycle.StopAsync());
                    return;
                }

                if (startResult.IsAllFailed)
                {
                    // 全ポート開始失敗時のロールバック
                    inputTracker.StopSession();
                    string errDetails = string.Join("\n", startResult.FailedPorts.Select(f => $"・{f.PortName}: {f.Reason}"));
                    lblStatus.Text = "ステータス: 停止中 (ポート開始失敗)";
                    lblStatus.ForeColor = Color.Red;
                    MessageBox.Show(
                        $"すべてのMIDIポートの開始に失敗しました。\n機器が取り外されているか、MIDIバックエンドに未反映の可能性があります。機器を接続し直し、midi2keyを再起動してください。\n\n詳細:\n{errDetails}",
                        "ポート開始エラー",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                if (startResult.IsPartialSuccess)
                {
                    // 一部ポート失敗時の正常ポート継続
                    string failedNames = string.Join(", ", startResult.FailedPorts.Select(f => f.PortName));
                    string openedNames = string.Join(", ", startResult.OpenedPorts.Select(o => o.DeviceName));
                    lblStatus.Text = $"ステータス: {SessionDisplay} ({startResult.OpenedPorts.Count}ポート 監視開始成功, 失敗: {failedNames})";
                    lblStatus.ForeColor = Color.DarkGoldenrod;
                    MessageBox.Show(
                        $"一部のポートの開始に失敗しました:\n・{failedNames}\n\n次の正常なポートで監視を開始しました:\n・{openedNames}",
                        "一部ポート開始警告",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                else
                {
                    lblStatus.Text = $"ステータス: {SessionDisplay} ({startResult.OpenedPorts.Count}ポート 監視開始成功)";
                    lblStatus.ForeColor = Color.Green;
                }
                if (txtNote.Focused) ShowCaptureReady();
            }
            catch (OperationCanceledException) when (startCancellation.IsCancellationRequested)
            {
                if (sessionStarted)
                {
                    inputTracker.StopSession();
                    ObserveMidiCleanup(midiLifecycle.StopAsync());
                }
                if (!_isClosing && !IsDisposed)
                {
                    lblStatus.Text = "ステータス: 停止中";
                    lblStatus.ForeColor = SystemColors.ControlText;
                }
            }
            catch (TimeoutException)
            {
                startCancellation.Cancel();
                if (sessionStarted)
                {
                    inputTracker.StopSession();
                    ObserveMidiCleanup(midiLifecycle.StopAsync());
                }
                if (!_isClosing && !IsDisposed)
                {
                    lblStatus.Text = "ステータス: 停止中 (MIDI開始待機がタイムアウト)";
                    lblStatus.ForeColor = Color.DarkGoldenrod;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("StartConversion", $"開始処理失敗: {ex}");
                if (sessionStarted)
                {
                    try { inputTracker.StopSession(); }
                    catch (Exception cleanupEx) { DiagnosticLogger.Log("StartConversion", $"入力状態の復旧失敗: {cleanupEx}"); }
                    ObserveMidiCleanup(midiLifecycle.StopAsync());
                }
                if (!_isClosing && !IsDisposed)
                {
                    lblStatus.Text = "ステータス: 停止中 (開始失敗)";
                    lblStatus.ForeColor = Color.Red;
                    MessageBox.Show($"変換を開始できませんでした: {ex.Message}", "開始エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                if (ReferenceEquals(_startCancellation, startCancellation)) _startCancellation = null;
                _isTransitioning = false;
                UpdateMonitor();
                if (!_isClosing && !IsDisposed && txtNote.Focused && !inputTracker.IsListening)
                    SetCaptureHint("取得未開始: ステータスを確認");
            }
        }

        /// <summary>
        /// 変換処理を安全に停止します。
        /// 入力の無効化とキー解放を直ちに行い、nativeポート停止をworkerへ依頼します。
        /// </summary>
        private void StopConversion()
        {
            _startCancellation?.Cancel();
            StopCapture();

            DiagnosticLogger.Log($"[Form1] StopConversion invoked. CurrentGen={inputTracker.CurrentSessionId}");

            // 新規キー送信無効化、セッション無効化、全キー解放
            inputTracker.StopSession();

            // 前のnative処理が完了した後でポートを閉じる
            var midiStopTask = midiLifecycle.StopAsync();

            bool allReleased = keySimulator.RetryReleasePendingKeys();

            if (allReleased && !keySimulator.HasUnreleasedKeys)
            {
                lblStatus.Text = "ステータス: MIDI停止処理待機中 (応答しない場合は再起動してください)";
                lblStatus.ForeColor = Color.DarkGoldenrod;
            }
            else
            {
                lblStatus.Text = $"ステータス: 停止中 (警告: 未解放キー {keySimulator.UnreleasedKeysCount} 件)";
                lblStatus.ForeColor = Color.DarkOrange;
            }
            ObserveMidiStop(midiStopTask);
        }

        /// <summary>
        /// デバイス切断イベント発生時のハンドラ。
        /// 切断通知に含まれる発生元Generationを検証し、旧セッション通知を破棄します。
        /// 切断元が特定できる場合は該当デバイスの入力のみを解放し、全切断または切断元特定不能時は全変換を安全停止します。
        /// </summary>
        private void HandleDeviceDisconnected(MidiDeviceDisconnectedData data)
        {
            DiagnosticLogger.Log($"[Form1] HandleDeviceDisconnected: DeviceId='{data.DeviceId}', Name='{data.DeviceName}', DisconnectGen={data.Generation}, CurrentGen={inputTracker.CurrentSessionId}, Reason='{data.Reason}'");

            // 切断通知が発生した時点のGenerationと、現在のセッションGenerationを検証
            if (data.Generation != inputTracker.CurrentSessionId)
            {
                DiagnosticLogger.Log($"[Form1] Disconnect notification dropped: Generation mismatch (NotificationGen={data.Generation} != CurrentGen={inputTracker.CurrentSessionId})");
                return;
            }

            // 切断されたデバイスの入力のみを解放（別デバイスの押下状態は維持）
            inputTracker.ReleaseDeviceInputs(data.DeviceId, data.Generation);
            RefreshPorts(false);

            var activePorts = midiListener.GetActivePorts();
            if (activePorts.Count == 0 || string.IsNullOrEmpty(data.DeviceId))
            {
                // 全ポート切断または同名等で切断元特定不能の場合: 安全側として変換全体を停止
                DiagnosticLogger.Log($"[Form1] Full disconnect or unidentified device disconnect. Stopping conversion safely. RemainingActivePorts={activePorts.Count}, Reason='{data.Reason}', UnreleasedKeys={keySimulator.UnreleasedKeysCount}");
                StopConversion();

                string warningMsg = data.Reason.Contains("MIDIポート解放失敗", StringComparison.Ordinal)
                    ? $"MIDIポートの解放に失敗したため変換を停止しました。{data.Reason}"
                    : data.Reason.Contains("同名のMIDIデバイスの一部切断") || data.Reason.Contains("同名")
                        ? "同名のMIDIデバイスの一部切断を検知しました。切断された機器を特定できないため、安全のため変換を停止しました。機器を接続し直し、midi2keyを再起動してください。"
                        : "MIDIデバイスの切断を検知しました。変換を停止しました。機器を接続し直し、midi2keyを再起動してください。";

                ShowAggregatedWarning(
                    warningMsg,
                    "MIDIデバイス切断検知");
            }
            else
            {
                DiagnosticLogger.Log($"[Form1] Partial device disconnected. ActivePorts={activePorts.Count}");
                lblStatus.Text = $"ステータス: {SessionDisplay} (切断: {data.DeviceName}, 残り {activePorts.Count} ポート)";
                lblStatus.ForeColor = Color.DarkGoldenrod;
            }
        }

        /// <summary>
        /// キー送信失敗時のエラーハンドラ。短時間の同一エラーを集約し、連続ダイアログを防止します。
        /// </summary>
        private void HandleKeySendError(KeySendErrorInfo errorInfo)
        {
            var now = DateTime.Now;
            bool isSameError = (now - lastReportedErrorTime).TotalSeconds < 2.0 &&
                               lastReportedErrorCode == errorInfo.Win32Error &&
                               lastReportedVkCode == errorInfo.VkCode;

            if (isSameError)
            {
                errorRepeatCount++;
                lblStatus.Text = $"キー送信エラー (VK:0x{errorInfo.VkCode:X2}, Win32:{errorInfo.Win32Error}) x{errorRepeatCount}";
                lblStatus.ForeColor = Color.DarkOrange;
            }
            else
            {
                errorRepeatCount = 1;
                lastReportedErrorCode = errorInfo.Win32Error;
                lastReportedVkCode = errorInfo.VkCode;
                lastReportedErrorTime = now;

                string win32Msg = new System.ComponentModel.Win32Exception(errorInfo.Win32Error).Message;
                lblStatus.Text = $"キー送信エラー: {errorInfo.KeyName} (Win32: {errorInfo.Win32Error})";
                lblStatus.ForeColor = Color.DarkOrange;

                MessageBox.Show(
                    $"キー送信に失敗しました。\n\n対象キー: {errorInfo.KeyName} (VK: 0x{errorInfo.VkCode:X2})\n操作: {(errorInfo.IsDown ? "押下" : "解放")}\nWin32エラー: {errorInfo.Win32Error} - {win32Msg}",
                    "キー送信エラー",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S) && !txtKey.Focused && !_isClosing)
            {
                btnSave.PerformClick();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;
            const int WM_DEVICECHANGE = 0x0219;
            const int DBT_DEVNODES_CHANGED = 0x0007;
            const int DBT_DEVICEARRIVAL = 0x8000;
            const int DBT_DEVICEREMOVEPENDING = 0x8003;
            const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyManager.DEFAULT_HOTKEY_ID)
            {
                ToggleConversion();
                return;
            }

            if (m.Msg == WM_DEVICECHANGE)
            {
                long wParam = m.WParam.ToInt64();
                DiagnosticLogger.Log($"[WndProc] WM_DEVICECHANGE received: wParam=0x{wParam:X4}, lParam=0x{m.LParam.ToInt64():X}");

                if (wParam == DBT_DEVNODES_CHANGED ||
                    wParam == DBT_DEVICEARRIVAL ||
                    wParam == DBT_DEVICEREMOVEPENDING ||
                    wParam == DBT_DEVICEREMOVECOMPLETE)
                {
                    DiagnosticLogger.Log($"[WndProc] Matched device change notification (0x{wParam:X4}). Scheduling CheckDeviceHealth debounce.");
                    TriggerDeviceDebounce($"WM_DEVICECHANGE (0x{wParam:X4})");
                }
                else
                {
                    DiagnosticLogger.Log($"[WndProc] WM_DEVICECHANGE wParam 0x{wParam:X4} ignored (unhandled sub-notification).");
                }
            }

            base.WndProc(ref m);
        }

        private void StartTargetKeyCapture()
        {
            if (globalHook != null || _isClosing) return;
            int generation = Interlocked.Increment(ref keyCaptureGeneration);
            try
            {
                globalHook = Hook.GlobalEvents();
                keyCaptureHandler = (sender, e) => GlobalHook_KeyDown(sender, e, generation);
                globalHook.KeyDown += keyCaptureHandler;
            }
            catch { StopTargetKeyCapture(); }
        }

        private void StopTargetKeyCapture()
        {
            Interlocked.Increment(ref keyCaptureGeneration);
            var hook = globalHook;
            var handler = keyCaptureHandler;
            globalHook = null;
            keyCaptureHandler = null;
            if (hook == null) return;
            if (handler != null) hook.KeyDown -= handler;
            hook.Dispose();
        }

        private void GlobalHook_KeyDown(object? sender, KeyEventArgs e, int generation)
        {
            bool isUS = false;
            
            if (IsHandleCreated && !IsDisposed) {
                try {
                    BeginInvoke(new Action(() => {
                        if (IsDisposed || _isClosing || globalHook == null || !txtKey.Focused ||
                            generation != Volatile.Read(ref keyCaptureGeneration)) return;
                        isUS = rbUS.Checked;
                        string keyName = FormatKey(e, isUS);
                        
                        if (keyName != "shift" && keyName != "ctrl" && keyName != "alt")
                        {
                            txtKey.Text = keyName;
                        }
                        else if (txtKey.Text == "")
                        {
                            txtKey.Text = keyName;
                        }
                    }));
                } catch { }
            }
        }

        private string FormatKey(KeyEventArgs e, bool isUS)
        {
            bool shift = e.Shift;
            int code = (int)e.KeyCode;
            
            if (code == 0 && e is Gma.System.MouseKeyHook.KeyEventArgsExt ext)
            {
                if (ext.ScanCode == 41) return "zenkaku_hankaku";
                if (ext.ScanCode == 58) return "capslock";
                if (ext.ScanCode == 112) return "hiragana";
            }

            if (code >= (int)Keys.A && code <= (int)Keys.Z)
            {
                string letter = e.KeyCode.ToString().ToLower();
                return shift ? letter.ToUpper() : letter;
            }

            if (code >= (int)Keys.D0 && code <= (int)Keys.D9)
            {
                string num = (code - (int)Keys.D0).ToString();
                if (!shift) return num;
                
                if (isUS)
                    return num switch { "1"=>"!", "2"=>"@", "3"=>"#", "4"=>"$", "5"=>"%", "6"=>"^", "7"=>"&", "8"=>"*", "9"=>"(", "0"=>")", _=>num };
                else
                    return num switch { "1"=>"!", "2"=>"\"", "3"=>"#", "4"=>"$", "5"=>"%", "6"=>"&", "7"=>"'", "8"=>"(", "9"=>")", _=>num };
            }

            switch (code)
            {
                case 186: return isUS ? (shift ? ":" : ";") : (shift ? "*" : ":"); // Oem1
                case 187: return isUS ? (shift ? "+" : "=") : (shift ? "+" : ";"); // OemPlus
                case 188: return shift ? "<" : ","; // Oemcomma
                case 189: return isUS ? (shift ? "_" : "-") : (shift ? "=" : "-"); // OemMinus
                case 190: return shift ? ">" : "."; // OemPeriod
                case 191: return shift ? "?" : "/"; // Oem2
                case 192: return isUS ? (shift ? "~" : "`") : (shift ? "`" : "@"); // Oem3
                case 219: return shift ? "{" : "["; // Oem4
                case 220: return shift ? "|" : "\\"; // Oem5
                case 221: return shift ? "}" : "]"; // Oem6
                case 222: return isUS ? (shift ? "\"" : "'") : (shift ? "~" : "^"); // Oem7
                case 226: return shift ? "_" : "\\"; // Oem102
                case (int)Keys.Return: return "enter";
                case (int)Keys.Back: return "backspace";
                case (int)Keys.Space: return "space";
                case (int)Keys.Escape: return "esc";
                case (int)Keys.Tab: return "tab";
                case (int)Keys.ShiftKey: case (int)Keys.LShiftKey: case (int)Keys.RShiftKey: return "shift";
                case (int)Keys.ControlKey: case (int)Keys.LControlKey: case (int)Keys.RControlKey: return "ctrl";
                case (int)Keys.Menu: case (int)Keys.LMenu: case (int)Keys.RMenu: return "alt";
                
                // 特殊キー / 日本語キーボード系
                case (int)Keys.Capital: return "capslock";
                case (int)Keys.KanjiMode: case 243: case 244: return "zenkaku_hankaku";
                case (int)Keys.IMEConvert: return "henkan";
                case (int)Keys.IMENonconvert: return "muhenkan";
                case 240: return "eisuy";
                case 242: return "hiragana";
            }

            return e.KeyCode.ToString().ToLower();
        }

        private List<string> GetSelectedPorts()
        {
            var ports = new List<string>();
            foreach (var item in chkPorts.CheckedItems)
            {
                if (item != null)
                {
                    ports.Add(item.ToString() ?? "");
                }
            }
            return ports;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inputToolTip.Dispose();
                notificationTimer.Dispose();
                monitorTimer.Dispose();
                trayIcon?.Dispose();
                trayMenu?.Dispose();
            }
            base.Dispose(disposing);
            if (disposing) { mappingRowImages.Dispose(); appIcons?.Dispose(); appIcons = null; }
        }

        private void UpdateSettingsFromUI()
        {
            settings.SelectedPorts = GetSelectedPorts();
            settings.KeyboardLayout = rbUS.Checked ? "US" : "JIS";
            inputTracker.PublishResolvedMapping(settings);
            UpdateSettingsTitle();
        }
        
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !ConfirmPendingChanges())
            {
                e.Cancel = true;
                base.OnFormClosing(e);
                return;
            }
            notificationTimer.Stop();
            monitorTimer.Stop();
            if (trayIcon != null) trayIcon.Visible = false;
            _isClosing = true;
            _startCancellation?.Cancel();
            _captureCancellation?.Cancel();
            Interlocked.Increment(ref _refreshPortsRequestId);
            Interlocked.Increment(ref _healthCheckRequestId);
            hotkeyManager.Unregister();
            _winRtWatcher?.Dispose();
            deviceDebounceTimer?.Stop();
            deviceDebounceTimer?.Dispose();
            StopConversion();
            keySimulator.RetryReleasePendingKeys();
            ObserveMidiCleanup(midiLifecycle.DisposeAsync());
            StopTargetKeyCapture();
            base.OnFormClosing(e);
        }
    }
}
