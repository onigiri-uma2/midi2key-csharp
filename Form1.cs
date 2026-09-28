using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
        private Button btnReloadPorts = null!;
        private RadioButton rbJIS = null!;
        private RadioButton rbUS = null!;
        private ListView listMapping = null!;
        private TextBox txtNote = null!;
        private TextBox txtKey = null!;
        private Label lblStatus = null!;
        private Label lblHotkeyInfo = null!;
        private Button btnHotkeyConfig = null!;
        private Button btnSaveAs = null!;
        
        private IKeyboardMouseEvents? globalHook;
        private IMidiListener midiListener = null!;
        private KeySimulator keySimulator = null!;
        private InputTracker inputTracker = null!;
        private readonly HotkeyManager hotkeyManager = new();
        private System.Windows.Forms.Timer? deviceDebounceTimer;
        private WinRtMidiWatcher? _winRtWatcher;
        private Label lblPortSummary = null!;
        private volatile bool _isTransitioning = false;

        // キー送信エラーの通知集約用
        private int lastReportedErrorCode = 0;
        private uint lastReportedVkCode = 0;
        private DateTime lastReportedErrorTime = DateTime.MinValue;
        private int errorRepeatCount = 0;

        public Form1(IKeyboardOutput? output = null, IMidiListener? listener = null)
        {
            InitializeComponentProgrammatically();
            LoadInitialSettings();
            SetupDependencies(output, listener);
            InitWinRtWatcher();
        }
        
        private void SetupDependencies(IKeyboardOutput? output = null, IMidiListener? listener = null)
        {
            keySimulator = new KeySimulator(output);
            inputTracker = new InputTracker(keySimulator, () => settings);
            midiListener = listener ?? new MidiListener();

            // ノート/ペダル取得（キャプチャモード）時のUI非同期更新（デッドロック防止）
            inputTracker.OnInputCaptured += (capturedText) => {
                if (IsHandleCreated && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke(new Action(() => {
                            if (!IsDisposed)
                            {
                                txtNote.Text = capturedText;
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
            
            RefreshPorts(true);

            if (settings.KeyboardLayout == "US")
                rbUS.Checked = true;
            else
                rbJIS.Checked = true;

            RefreshMappingList();
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
                    if (IsHandleCreated && !IsDisposed)
                    {
                        try { BeginInvoke(new Action(() => UpdatePortSummaryUi())); } catch { }
                    }
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

        private void UpdatePortSummaryUi(OutOfProcessWinMmResult? outProcResult = null)
        {
            if (lblPortSummary == null) return;
            int osCount = _winRtWatcher?.Devices.Count ?? -1;
            int backendCount = chkPorts?.Items.Count ?? 0;
            string osText = osCount >= 0 ? $"{osCount}件" : "0件";
            string outProcText = outProcResult.HasValue
                ? (outProcResult.Value.Success ? $"{outProcResult.Value.Ports.Count}件" : "エラー")
                : "取得中...";
            lblPortSummary.Text = $"親プロセス: {backendCount}件 | 新規プロセス: {outProcText} | WinRT: {osText}";
        }

        private void RefreshPorts(bool isInitialLoad = false)
        {
            if (chkPorts == null) return;
            var checkedPorts = isInitialLoad ? settings.SelectedPorts : GetSelectedPorts();
            var enumResult = MidiListener.GetPortNames();

            if (!enumResult.Success)
            {
                DiagnosticLogger.Log($"[Form1] RefreshPorts failed to enumerate: {enumResult.ErrorMessage}");
                chkPorts.Items.Clear();
                UpdatePortSummaryUi();
                return;
            }

            var availablePorts = enumResult.Ports;
            
            // 新規プロセスWinMMの結果を非同期で確認
            _ = System.Threading.Tasks.Task.Run(() => {
                var outProcResult = MidiDeviceDiagnostics.GetOutOfProcessWinMmResult(1000);
                if (IsHandleCreated && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke(new Action(() => {
                            if (IsDisposed) return;
                            ApplyPortsToUi(availablePorts, checkedPorts, outProcResult);
                        }));
                    }
                    catch { }
                }
            });
        }

        private void ApplyPortsToUi(IReadOnlyList<string> availablePorts, IReadOnlyList<string> checkedPorts, OutOfProcessWinMmResult outProcResult)
        {
            chkPorts.Items.Clear();

            // ケース1: 新規プロセスで1件以上検出され、親プロセスDryWetMIDIで0件の場合
            if (availablePorts.Count == 0 && outProcResult.Success && outProcResult.Ports.Count > 0)
            {
                lblStatus.Text = "ステータス: 停止中 (接続検出・MIDIバックエンド未反映: 再起動してください)";
                lblStatus.ForeColor = Color.DarkGoldenrod;
                foreach (var p in outProcResult.Ports)
                {
                    chkPorts.Items.Add($"{p} [未反映: 要再起動]");
                }
            }
            // ケース2: 親プロセスで1件以上あるが、新規プロセスWinMMで0件（正常取得）の場合: 取り外し済みの可能性
            else if (availablePorts.Count > 0 && outProcResult.Success && outProcResult.Ports.Count == 0)
            {
                lblStatus.Text = "ステータス: 停止中 (機器取り外し済みの可能性: 再起動してください)";
                lblStatus.ForeColor = Color.DarkOrange;
                foreach (var port in availablePorts)
                {
                    chkPorts.Items.Add($"{port} (取り外し済みの可能性)");
                }
            }
            else
            {
                foreach (var port in availablePorts)
                {
                    int index = chkPorts.Items.Add(port);
                    if (checkedPorts.Contains(port))
                    {
                        chkPorts.SetItemChecked(index, true);
                    }
                }
            }

            UpdatePortSummaryUi(outProcResult);
        }

        /// <summary>
        /// ユーザー操作による手動ポート再読み込み。
        /// 変換実行中の場合は安全停止した上で再列挙し、選択状態を可能な範囲で維持します。
        /// </summary>
        private void ReloadPortsManually()
        {
            DiagnosticLogger.Log($"[Form1] Manual port reload triggered by user. IsListening={inputTracker.IsListening}");

            if (inputTracker.IsListening)
            {
                StopConversion();
                lblStatus.Text = "ステータス: 停止中 (ポート再読み込みのため安全停止)";
                lblStatus.ForeColor = Color.Red;
            }

            var previousSelected = GetSelectedPorts();
            var enumResult = MidiListener.GetPortNames();

            if (!enumResult.Success)
            {
                DiagnosticLogger.Log($"[Form1] ReloadPortsManually enumeration failed: {enumResult.ErrorMessage}");
                lblStatus.Text = "ステータス: 停止中 (ポート一覧更新失敗)";
                lblStatus.ForeColor = Color.Red;
                MessageBox.Show(
                    $"MIDIポート一覧の再読み込みに失敗しました。\n\n詳細: {enumResult.ErrorMessage}\n\nMIDIポートの接続状態を更新できませんでした。機器を接続し直しても反映されない場合は、midi2keyを再起動してください。",
                    "ポート再読み込みエラー",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            var availablePorts = enumResult.Ports;
            var outProcResult = MidiDeviceDiagnostics.GetOutOfProcessWinMmResult(1500);
            DiagnosticLogger.Log($"[Form1] ReloadPortsManually result: DryWetMIDI={availablePorts.Count}件, 新規プロセスWinMM={(outProcResult.Success ? $"{outProcResult.Ports.Count}件" : "失敗")}");

            ApplyPortsToUi(availablePorts, previousSelected, outProcResult);

            // 5系統比較診断をバックグラウンド実行してログ記録
            _ = System.Threading.Tasks.Task.Run(async () => {
                try
                {
                    var activePorts = midiListener.GetActivePorts();
                    await MidiDeviceDiagnostics.RunComparisonAsync("ManualReload", activePorts);
                }
                catch { }
            });

            // 第3項目のユーザー向け案内
            if (availablePorts.Count == 0 && outProcResult.Success && outProcResult.Ports.Count > 0)
            {
                MessageBox.Show(
                    $"接続は検出されていますが、MIDIバックエンドには未反映です。\n機器を利用するにはmidi2keyを再起動してください。\n\n検出デバイス: {string.Join(", ", outProcResult.Ports)}",
                    "MIDIポート未反映 (再起動が必要)",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            else if (availablePorts.Count > 0 && outProcResult.Success && outProcResult.Ports.Count == 0)
            {
                MessageBox.Show(
                    "親プロセスのポート一覧には残存していますが、新規プロセスのWinMMでは検出されませんでした。\n機器が物理的に取り外されている可能性があります。\n機器を接続し直しても反映されない場合は、midi2keyを再起動してください。",
                    "機器取り外し検出",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else if (availablePorts.Count == 0)
            {
                lblStatus.Text = "ステータス: 停止中 (利用可能なMIDIポートがありません)";
                lblStatus.ForeColor = Color.DarkGoldenrod;
                MessageBox.Show(
                    "利用可能なMIDIポートが検出されませんでした。\n\nMIDIポートの接続状態を更新できませんでした。機器を接続し直しても反映されない場合は、midi2keyを再起動してください。",
                    "MIDIポートなし",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                lblStatus.Text = $"ステータス: 停止中 (ポート再読み込み完了: {availablePorts.Count}件検出)";
                lblStatus.ForeColor = Color.Blue;
            }
        }

        /// <summary>
        /// マッピング辞書の内容をリストビュー（3列グリッド）に描画します。
        /// 88鍵盤（21〜108）などのノート番号には対応する音階名（例: C4 / ド）を併記します。
        /// </summary>
        private void RefreshMappingList()
        {
            if (listMapping == null) return;
            listMapping.Items.Clear();

            lock (settings.MappingLock)
            {
                var sortedKeys = settings.Mapping.Keys.OrderBy(k => {
                    if (k.Equals("pedal", StringComparison.OrdinalIgnoreCase)) return -1;
                    return int.TryParse(k, out int n) ? n : 999;
                });

                foreach (var key in sortedKeys)
                {
                    string targetKey = settings.Mapping[key];
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
                    item.SubItems.Add(typeOrNoteName);
                    item.SubItems.Add(targetKey);
                    listMapping.Items.Add(item);
                }
            }
        }

        /// <summary>
        /// UIコントロールの生成と配置。
        /// </summary>
        private void InitializeComponentProgrammatically()
        {
            var version = typeof(Form1).Assembly.GetName().Version;
            string verStr = version != null ? $" v{version.Major}.{version.Minor}.{version.Build}" : " v1.0.3";
            this.Text = $"midi2key C#{verStr}";
            this.Width = 430;
            this.Height = 585;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Yu Gothic UI", 9);

            var grpLayout = new GroupBox { Text = "🌐 キー配列", Top = 10, Left = 295, Width = 105, Height = 50 };
            rbUS = new RadioButton { Text = "US", Top = 20, Left = 55, Width = 45 };
            rbJIS = new RadioButton { Text = "JIS", Top = 20, Left = 10, Width = 45 };
            rbUS.CheckedChanged += (s, e) => {
                if (settings != null && rbUS.Checked) settings.KeyboardLayout = "US";
            };
            rbJIS.CheckedChanged += (s, e) => {
                if (settings != null && rbJIS.Checked) settings.KeyboardLayout = "JIS";
            };
            grpLayout.Controls.Add(rbUS);
            grpLayout.Controls.Add(rbJIS);
            this.Controls.Add(grpLayout);

            var grpPorts = new GroupBox { Text = "🎛 MIDIポート選択", Width = 275, Height = 120, Top = 10, Left = 10 };
            chkPorts = new CheckedListBox { Top = 18, Left = 10, Width = 255, Height = 58, BorderStyle = BorderStyle.None, CheckOnClick = true };
            chkPorts.SelectedIndexChanged += (s, e) => chkPorts.ClearSelected();
            grpPorts.Controls.Add(chkPorts);

            lblPortSummary = new Label { Text = "OS認識: 取得中... | バックエンド: 0件", ForeColor = Color.Navy, Top = 78, Left = 10, AutoSize = true, Font = new Font(this.Font.FontFamily, 8f) };
            grpPorts.Controls.Add(lblPortSummary);

            var lblPortWarn = new Label { Text = "※機器の抜き差し時は自動検知または再起動してください", ForeColor = Color.DarkSlateGray, Top = 98, Left = 5, AutoSize = true, Font = new Font(this.Font.FontFamily, 7.5f) };
            grpPorts.Controls.Add(lblPortWarn);
            this.Controls.Add(grpPorts);

            btnReloadPorts = new Button 
            { 
                Text = "🔄 ポート\n再読み込み", 
                Top = 65, 
                Left = 295, 
                Width = 105, 
                Height = 60, 
                Font = new Font(this.Font.FontFamily, 8.5f) 
            };
            btnReloadPorts.Click += (s, e) => ReloadPortsManually();
            this.Controls.Add(btnReloadPorts);

            var lblList = new Label { Text = "📄 マッピング一覧", Top = 135, Left = 10, AutoSize = true };
            listMapping = new ListView 
            { 
                Top = 155, 
                Left = 10, 
                Width = 390, 
                Height = 135,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false,
                HideSelection = false,
                OwnerDraw = true
            };
            listMapping.Columns.Add("ノート/信号", 85, HorizontalAlignment.Center);
            listMapping.Columns.Add("音名 / 種類", 145, HorizontalAlignment.Left);
            listMapping.Columns.Add("変換キー", 135, HorizontalAlignment.Left);

            // カラムヘッダーに上品な淡いブルーグレーの背景と境界線、濃紺の太字を設定
            listMapping.DrawColumnHeader += (s, e) => {
                if (e.Header == null) return;
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
            listMapping.DrawItem += (s, e) => e.DrawDefault = true;
            listMapping.DrawSubItem += (s, e) => e.DrawDefault = true;

            listMapping.SelectedIndexChanged += (s, e) => {
                if (listMapping.SelectedItems.Count == 0) return;
                var item = listMapping.SelectedItems[0];
                txtNote.Text = item.Text;
                txtKey.Text = item.SubItems.Count > 2 ? item.SubItems[2].Text : "";
            };
            this.Controls.Add(lblList);
            this.Controls.Add(listMapping);

            var lblNote = new Label { Text = "🎹 ノート/ペダル", Top = 300, Left = 10, AutoSize = true };
            txtNote = new TextBox { Top = 320, Left = 10, Width = 75 };
            
            // ノート取得モードと変換モードのライフサイクルおよびGeneration同期
            txtNote.Enter += (s, e) => {
                if (!inputTracker.IsListening)
                {
                    // 停止中のキャプチャ開始: InputTracker側でGenerationを発行してリスナーに渡す
                    long gen = inputTracker.StartCaptureSession();
                    midiListener.Start(GetSelectedPorts(), gen);
                }
                else
                {
                    // 変換実行中: リスナーは再起動せずキャプチャフラグのみON
                    inputTracker.SetCapturing(true);
                }
            };
            txtNote.Leave += (s, e) => {
                if (!inputTracker.IsListening)
                {
                    // 停止中のキャプチャ終了: キャプチャ専用リスナーを停止
                    inputTracker.StopCaptureSession();
                    midiListener.Stop();
                }
                else
                {
                    // 変換実行中: キャプチャフラグのみOFF（押下中ノートの抑止は維持）
                    inputTracker.SetCapturing(false);
                }
            };

            var lblKey = new Label { Text = "⌨ キー", Top = 300, Left = 95, AutoSize = true };
            txtKey = new TextBox { Top = 320, Left = 95, Width = 115, ReadOnly = true, BackColor = SystemColors.Window };
            
            // キー入力欄の「消去」ボタン
            var btnClearKey = new Button { Text = "消去", Top = 319, Left = 215, Width = 50, Height = 25 };
            btnClearKey.Click += (s, e) => {
                txtKey.Text = "";
            };

            txtKey.Enter += (s, e) => {
                txtKey.Text = "";
                if (globalHook == null)
                {
                    try {
                        globalHook = Hook.GlobalEvents();
                        globalHook.KeyDown += GlobalHook_KeyDown;
                    } catch { }
                }
            };
            txtKey.Leave += (s, e) => {
                if (globalHook != null)
                {
                    globalHook.KeyDown -= GlobalHook_KeyDown;
                    globalHook.Dispose();
                    globalHook = null;
                }
            };
            this.Deactivate += (s, e) => {
                if (globalHook != null)
                {
                    globalHook.KeyDown -= GlobalHook_KeyDown;
                    globalHook.Dispose();
                    globalHook = null;
                }
            };
            this.Activated += (s, e) => {
                if (txtKey.Focused && globalHook == null)
                {
                    try {
                        globalHook = Hook.GlobalEvents();
                        globalHook.KeyDown += GlobalHook_KeyDown;
                    } catch { }
                }
            };

            // ノート番号バリデーション（0〜127）、pedal の登録、およびキー厳格検証
            var btnAdd = new Button { Text = "追加", Top = 295, Left = 315, Width = 85, Height = 25 };
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

                // ホットキーとの衝突検証
                if (settings.Hotkey.Enabled)
                {
                    var hotkeyResolved = KeyResolver.Resolve(settings.Hotkey.Key, settings.KeyboardLayout);
                    var keyResolved = KeyResolver.Resolve(keyInput, settings.KeyboardLayout);
                    if (hotkeyResolved.IsValid && keyResolved.IsValid && hotkeyResolved.VkCode == keyResolved.VkCode)
                    {
                        MessageBox.Show(
                            $"キー '{keyInput}' は現在のトグルホットキー ({settings.Hotkey.Key}) と同一のため割り当てられません。\n誤発動を防ぐため別のキーを指定するか、ホットキー設定を変更してください。",
                            "キー衝突エラー",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        return;
                    }
                }

                if (noteInput.Equals("pedal", StringComparison.OrdinalIgnoreCase))
                {
                    lock (settings.MappingLock)
                    {
                        settings.Mapping["pedal"] = keyInput;
                    }
                    RefreshMappingList();
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
                    RefreshMappingList();
                }
                else
                {
                    MessageBox.Show("ノート番号は 0〜127 の数値、または「pedal」を入力してください。", "入力エラー");
                }
            };
            
            var btnDel = new Button { Text = "削除", Top = 325, Left = 315, Width = 85, Height = 25 };
            btnDel.Click += (s, e) => {
                if (listMapping.SelectedItems.Count > 0)
                {
                    var keyToRemove = listMapping.SelectedItems[0].Text;
                    lock (settings.MappingLock)
                    {
                        settings.Mapping.Remove(keyToRemove);
                    }
                    RefreshMappingList();
                }
            };

            this.Controls.Add(lblNote);
            this.Controls.Add(txtNote);
            this.Controls.Add(lblKey);
            this.Controls.Add(txtKey);
            this.Controls.Add(btnClearKey);
            this.Controls.Add(btnAdd);
            this.Controls.Add(btnDel);

            var sep = new Label { BorderStyle = BorderStyle.Fixed3D, Top = 360, Left = 10, Width = 390, Height = 2 };
            this.Controls.Add(sep);

            // 「上書き保存」ボタン
            var btnSave = new Button { Text = "上書き保存", Top = 375, Left = 10, Width = 85, Height = 28 };
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
                    lblStatus.Text = $"保存完了: {Path.GetFileName(currentSettingsPath)}";
                    lblStatus.ForeColor = Color.Blue;
                    MessageBox.Show($"設定を保存しました。\n保存先: {currentSettingsPath}", "保存完了");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("設定の保存に失敗しました: " + ex.Message, "エラー");
                }
            };

            // 「別名保存」ボタン
            btnSaveAs = new Button { Text = "別名保存", Top = 375, Left = 105, Width = 85, Height = 28 };
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
                            MessageBox.Show("別名保存が完了しました。\n保存先: " + currentSettingsPath, "保存完了");
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("設定の保存に失敗しました: " + ex.Message, "保存エラー");
                        }
                    }
                }
            };
            
            // 「設定読込」ボタン
            var btnLoad = new Button { Text = "設定読込", Top = 375, Left = 305, Width = 95, Height = 28 };
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
                            RefreshMappingList();
                            UpdateHotkeyRegistration();
                            MessageBox.Show("読込み完了しました: " + Path.GetFileName(currentSettingsPath), "読込完了");
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("設定ファイルの読み込みに失敗しました:\n" + ex.Message, "読込エラー");
                        }
                    }
                }
            };

            var btnStart = new Button { Text = "変換開始", Top = 415, Left = 10, Width = 95, Height = 35, BackColor = Color.Green, ForeColor = Color.White, Font = new Font(this.Font, FontStyle.Bold) };
            btnStart.Click += (s, e) => StartConversion();

            var btnStop = new Button { Text = "変換停止", Top = 415, Left = 305, Width = 95, Height = 35, BackColor = Color.Red, ForeColor = Color.White, Font = new Font(this.Font, FontStyle.Bold) };
            btnStop.Click += (s, e) => StopConversion();

            // トグルホットキー表示＆変更ボタン
            lblHotkeyInfo = new Label { Top = 460, Left = 10, Width = 280, AutoSize = false, Text = "ホットキー: 有効 (Ctrl+Alt+F9)", ForeColor = Color.FromArgb(40, 40, 40) };
            btnHotkeyConfig = new Button { Text = "ホットキー設定...", Top = 455, Left = 295, Width = 105, Height = 26 };
            btnHotkeyConfig.Click += (s, e) => OpenHotkeyConfigDialog();

            lblStatus = new Label { Text = "ステータス: 停止中", Top = 495, Left = 10, Width = 390, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Red, Font = new Font(this.Font, FontStyle.Bold) };

            this.Controls.Add(btnSave);
            this.Controls.Add(btnSaveAs);
            this.Controls.Add(btnLoad);
            this.Controls.Add(btnStart);
            this.Controls.Add(btnStop);
            this.Controls.Add(lblHotkeyInfo);
            this.Controls.Add(btnHotkeyConfig);
            this.Controls.Add(lblStatus);

            // デバイス切断・変更検知のデバウンスタイマー
            deviceDebounceTimer = new System.Windows.Forms.Timer { Interval = 300 };
            deviceDebounceTimer.Tick += (s, e) => {
                deviceDebounceTimer.Stop();
                DiagnosticLogger.Log($"[DebounceTimer] Tick fired. Invoking CheckDeviceHealth(). Gen={inputTracker.CurrentSessionId}");
                
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
                        var outProcResult = MidiDeviceDiagnostics.GetOutOfProcessWinMmResult(1500);
                        
                        if (IsHandleCreated && !IsDisposed)
                        {
                            try
                            {
                                BeginInvoke(new Action(() => {
                                    if (IsDisposed) return;
                                    midiListener.CheckDeviceHealth(osDeviceNames, outProcResult);
                                    RefreshPorts(false);
                                }));
                            }
                            catch { }
                        }

                        var activePorts = midiListener.GetActivePorts();
                        await MidiDeviceDiagnostics.RunComparisonAsync("DeviceChangeDebounce", activePorts);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Log($"[DebounceTimer] Error during health check: {ex.Message}");
                    }
                });
            };
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
                        lblStatus.Text = $"ホットキー設定更新: {(newHotkey.Enabled ? $"{newHotkey.Modifiers}+{newHotkey.Key}" : "無効")}";
                        lblStatus.ForeColor = Color.Blue;
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
        }

        /// <summary>
        /// トグルホットキー受信時の変換状態切り替え処理。
        /// </summary>
        private void ToggleConversion()
        {
            if (_isTransitioning) return;

            if (inputTracker.IsListening)
            {
                StopConversion();
            }
            else
            {
                StartConversion();
            }
        }

        /// <summary>
        /// 変換処理を開始します。ポート開始結果（全成功・一部成功・全失敗）に応じた適切な処理とロールバックを行います。
        /// </summary>
        private void StartConversion()
        {
            if (_isTransitioning || inputTracker.IsListening) return;
            _isTransitioning = true;

            try
            {
                var ports = GetSelectedPorts();
                if (ports.Count == 0)
                {
                    MessageBox.Show("MIDIポートを選択してください", "ポート未選択");
                    return;
                }
                
                UpdateSettingsFromUI();

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

                long sessionId = inputTracker.StartConversionSession();
                DiagnosticLogger.Log($"[Form1] StartConversion invoked. Generated Gen={sessionId}, Ports=[{string.Join(", ", ports)}]");
                var startResult = midiListener.Start(ports, sessionId);

                if (startResult.IsAllFailed)
                {
                    // 全ポート開始失敗時のロールバック
                    inputTracker.StopSession();
                    string errDetails = string.Join("\n", startResult.FailedPorts.Select(f => $"・{f.PortName}: {f.Reason}"));
                    lblStatus.Text = "ステータス: 停止中 (ポート開始失敗)";
                    lblStatus.ForeColor = Color.Red;
                    MessageBox.Show(
                        $"すべてのMIDIポートの開始に失敗しました。\n\n詳細:\n{errDetails}",
                        "ポート開始エラー",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                // もしノート入力欄にフォーカスがあれば、変換中キャプチャとして継続
                if (txtNote.Focused)
                {
                    inputTracker.SetCapturing(true);
                }

                if (startResult.IsPartialSuccess)
                {
                    // 一部ポート失敗時の正常ポート継続
                    string failedNames = string.Join(", ", startResult.FailedPorts.Select(f => f.PortName));
                    string openedNames = string.Join(", ", startResult.OpenedPorts.Select(o => o.DeviceName));
                    lblStatus.Text = $"ステータス: 実行中 ({startResult.OpenedPorts.Count}ポート 監視開始成功, 失敗: {failedNames})";
                    lblStatus.ForeColor = Color.DarkGoldenrod;
                    MessageBox.Show(
                        $"一部のポートの開始に失敗しました:\n・{failedNames}\n\n次の正常なポートで監視を開始しました:\n・{openedNames}",
                        "一部ポート開始警告",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                else
                {
                    lblStatus.Text = $"ステータス: 実行中 ({startResult.OpenedPorts.Count}ポート 監視開始成功)";
                    lblStatus.ForeColor = Color.Green;
                }
            }
            finally
            {
                _isTransitioning = false;
            }
        }

        /// <summary>
        /// 変換処理を安全に停止します。
        /// デッドロック防止のため、ロック外でMIDIリスナーを停止した上で全キーを解放します。
        /// 未解放キーが残っている場合はUIに警告表示を行います。
        /// </summary>
        private void StopConversion()
        {
            if (_isTransitioning) return;
            _isTransitioning = true;

            try
            {
                DiagnosticLogger.Log($"[Form1] StopConversion invoked. CurrentGen={inputTracker.CurrentSessionId}");

                // (1) 新規キー送信無効化、セッション無効化、全キー解放、内部状態クリア
                inputTracker.StopSession();

                // (2) ロック外でMIDIリスナーを停止し、受信スレッドの完了を安全に待機
                midiListener.Stop();

                // (3) 未解放キーがあれば再試行
                bool allReleased = keySimulator.RetryReleasePendingKeys();

                // (4) UIステータス更新
                if (allReleased && !keySimulator.HasUnreleasedKeys)
                {
                    lblStatus.Text = "ステータス: 停止中";
                    lblStatus.ForeColor = Color.Red;
                }
                else
                {
                    lblStatus.Text = $"ステータス: 停止中 (警告: 未解放キー {keySimulator.UnreleasedKeysCount} 件)";
                    lblStatus.ForeColor = Color.DarkOrange;
                }
            }
            finally
            {
                _isTransitioning = false;
            }
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
                DiagnosticLogger.Log($"[Form1] Full disconnect or unidentified device disconnect. Stopping conversion safely. RemainingActivePorts={activePorts.Count}");
                StopConversion();
                lblStatus.Text = "ステータス: 停止中 (MIDIデバイス切断検知)";
                lblStatus.ForeColor = Color.Red;
                MessageBox.Show(
                    $"MIDIデバイスの切断を検知したため、安全のためキーを解放して変換を停止しました。\n切断元: {(string.IsNullOrEmpty(data.DeviceName) ? "特定不能" : data.DeviceName)}\n理由: {data.Reason}\n\n※再開するには機器を接続し直し、変換開始ボタンまたはトグルホットキーで再開してください。",
                    "デバイス切断検知",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            else
            {
                DiagnosticLogger.Log($"[Form1] Partial device disconnected. ActivePorts={activePorts.Count}");
                lblStatus.Text = $"ステータス: 実行中 (切断: {data.DeviceName}, 残り {activePorts.Count} ポート)";
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

        private void GlobalHook_KeyDown(object? sender, KeyEventArgs e)
        {
            bool isUS = false;
            
            if (IsHandleCreated && !IsDisposed) {
                try {
                    BeginInvoke(new Action(() => {
                        if (IsDisposed) return;
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
                case 186: return isUS ? (shift ? ":" : ";") : (shift ? "+" : ":"); // Oem1
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

        private void UpdateSettingsFromUI()
        {
            settings.SelectedPorts = GetSelectedPorts();
            settings.KeyboardLayout = rbUS.Checked ? "US" : "JIS";
        }
        
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            hotkeyManager.Unregister();
            _winRtWatcher?.Dispose();
            deviceDebounceTimer?.Stop();
            deviceDebounceTimer?.Dispose();
            StopConversion();
            keySimulator.RetryReleasePendingKeys();
            midiListener?.Dispose();
            if (globalHook != null)
            {
                globalHook.KeyDown -= GlobalHook_KeyDown;
                globalHook.Dispose();
            }
            base.OnFormClosing(e);
        }
    }
}
