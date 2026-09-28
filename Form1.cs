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
        private volatile bool _isTransitioning = false;

        // ポート一覧の非同期更新競合防止用
        private long _refreshPortsRequestId = 0;

        // 接続変更・切断警告の通知集約用（同一メッセージの重複抑止）
        private string? _lastAlertMessage = null;
        private DateTime _lastAlertTime = DateTime.MinValue;
        private readonly object _alertLock = new();

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

            lblStatus.Text = $"ステータス: 停止中 ({message})";
            lblStatus.ForeColor = Color.DarkGoldenrod;
            MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void RefreshPorts(bool isInitialLoad = false)
        {
            if (chkPorts == null) return;
            long currentRequestId = Interlocked.Increment(ref _refreshPortsRequestId);
            var checkedPorts = isInitialLoad ? settings.SelectedPorts : GetSelectedPorts();
            var enumResult = MidiListener.GetPortNames();

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

            var availablePorts = enumResult.Ports;
            
            // 新規プロセスWinMMの結果を非同期で確認し、真に利用可能なポートのみをUIに反映
            _ = System.Threading.Tasks.Task.Run(() => {
                var outProcResult = MidiDeviceDiagnostics.GetOutOfProcessWinMmResult(1000);
                if (IsHandleCreated && !IsDisposed)
                {
                    try
                    {
                        BeginInvoke(new Action(() => {
                            if (IsDisposed) return;
                            // 要求IDが最新でない古い非同期結果は破棄して競合を防止
                            if (Interlocked.Read(ref _refreshPortsRequestId) != currentRequestId)
                            {
                                DiagnosticLogger.Log($"[Form1] RefreshPorts: Discarded outdated result (req={currentRequestId}, latest={Interlocked.Read(ref _refreshPortsRequestId)})");
                                return;
                            }
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

            // 利用可能なポートの厳格な絞り込み:
            // 1. 新規プロセスWinMMで正常に0件（消失）と確認されたポートは、取り外し済みのため除外
            // 2. 新規プロセスWinMMで検出されていても親プロセスDryWetMIDIにないポートは除外
            // 3. 診断プロセス失敗時は親プロセスの一覧をそのまま使用
            List<string> validPorts;
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

            // ステータス表示の更新（接続不一致はステータス欄で案内）
            // 変換中（inputTracker.IsListening == true）はステータスを「停止中」へ書き換えない
            if (!inputTracker.IsListening)
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
                    lblStatus.ForeColor = Color.Red;
                }
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
            string verStr = version != null ? $" v{version.Major}.{version.Minor}.{version.Build}" : " v1.1.0";
            this.Text = $"midi2key C#{verStr}";
            this.Width = 430;
            this.Height = 550;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Yu Gothic UI", 9);

            var grpPorts = new GroupBox { Text = "🎛 MIDIポート選択", Width = 275, Height = 95, Top = 10, Left = 10 };
            chkPorts = new CheckedListBox { Top = 18, Left = 10, Width = 255, Height = 52, BorderStyle = BorderStyle.None, CheckOnClick = true };
            chkPorts.SelectedIndexChanged += (s, e) => chkPorts.ClearSelected();
            grpPorts.Controls.Add(chkPorts);

            var lblPortWarn = new Label { Text = "※機器の接続・切断後はmidi2keyを再起動してください", ForeColor = Color.DarkSlateGray, Top = 73, Left = 5, AutoSize = true, Font = new Font(this.Font.FontFamily, 7.5f) };
            grpPorts.Controls.Add(lblPortWarn);
            this.Controls.Add(grpPorts);

            var grpLayout = new GroupBox { Text = "🌐 キー配列", Top = 10, Left = 295, Width = 105, Height = 95 };
            rbJIS = new RadioButton { Text = "JIS", Top = 25, Left = 20, Width = 65 };
            rbUS = new RadioButton { Text = "US", Top = 55, Left = 20, Width = 65 };
            rbUS.CheckedChanged += (s, e) => {
                if (settings != null && rbUS.Checked) settings.KeyboardLayout = "US";
            };
            rbJIS.CheckedChanged += (s, e) => {
                if (settings != null && rbJIS.Checked) settings.KeyboardLayout = "JIS";
            };
            grpLayout.Controls.Add(rbJIS);
            grpLayout.Controls.Add(rbUS);
            this.Controls.Add(grpLayout);

            var lblList = new Label { Text = "📄 マッピング一覧", Top = 115, Left = 10, AutoSize = true };
            listMapping = new ListView 
            { 
                Top = 135, 
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

            var lblNote = new Label { Text = "🎹 ノート/ペダル", Top = 280, Left = 10, AutoSize = true };
            txtNote = new TextBox { Top = 300, Left = 10, Width = 75 };
            
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

            var lblKey = new Label { Text = "⌨ キー", Top = 280, Left = 95, AutoSize = true };
            txtKey = new TextBox { Top = 300, Left = 95, Width = 115, ReadOnly = true, BackColor = SystemColors.Window };
            
            // キー入力欄の「消去」ボタン
            var btnClearKey = new Button { Text = "消去", Top = 299, Left = 215, Width = 50, Height = 25 };
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
            var btnAdd = new Button { Text = "追加", Top = 275, Left = 315, Width = 85, Height = 25 };
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
            
            var btnDel = new Button { Text = "削除", Top = 305, Left = 315, Width = 85, Height = 25 };
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

            var sep = new Label { BorderStyle = BorderStyle.Fixed3D, Top = 340, Left = 10, Width = 390, Height = 2 };
            this.Controls.Add(sep);

            // 「上書き保存」ボタン
            var btnSave = new Button { Text = "上書き保存", Top = 355, Left = 10, Width = 85, Height = 28 };
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
            btnSaveAs = new Button { Text = "別名保存", Top = 355, Left = 105, Width = 85, Height = 28 };
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
            var btnLoad = new Button { Text = "設定読込", Top = 355, Left = 305, Width = 95, Height = 28 };
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

            var btnStart = new Button { Text = "変換開始", Top = 395, Left = 10, Width = 95, Height = 35, BackColor = Color.Green, ForeColor = Color.White, Font = new Font(this.Font, FontStyle.Bold) };
            btnStart.Click += (s, e) => StartConversion();

            var btnStop = new Button { Text = "変換停止", Top = 395, Left = 305, Width = 95, Height = 35, BackColor = Color.Red, ForeColor = Color.White, Font = new Font(this.Font, FontStyle.Bold) };
            btnStop.Click += (s, e) => StopConversion();

            // トグルホットキー表示＆変更ボタン
            lblHotkeyInfo = new Label { Top = 440, Left = 10, Width = 280, AutoSize = false, Text = "ホットキー: 有効 (Ctrl+Alt+F9)", ForeColor = Color.FromArgb(40, 40, 40) };
            btnHotkeyConfig = new Button { Text = "ホットキー設定...", Top = 435, Left = 295, Width = 105, Height = 26 };
            btnHotkeyConfig.Click += (s, e) => OpenHotkeyConfigDialog();

            lblStatus = new Label { Text = "ステータス: 停止中", Top = 475, Left = 10, Width = 390, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Red, Font = new Font(this.Font, FontStyle.Bold) };

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
                                    var beforeActive = midiListener.GetActivePorts();
                                    midiListener.CheckDeviceHealth(osDeviceNames, outProcResult);
                                    var afterActive = midiListener.GetActivePorts();
                                    RefreshPorts(false);

                                    // 監視中デバイスの切断は HandleDeviceDisconnected で通知される。
                                    // 切断が発生しなかった場合の接続変更判定:
                                    if (beforeActive.Count == afterActive.Count)
                                    {
                                        var currentPorts = MidiListener.GetPortNames();

                                        // 1. 新規プロセスでポートが検出されたが親プロセスに未反映の場合
                                        if (outProcResult.Success && outProcResult.Ports.Count > 0 &&
                                            (currentPorts.Ports.Count == 0 || outProcResult.Ports.Any(p => !currentPorts.Ports.Contains(p, StringComparer.OrdinalIgnoreCase))))
                                        {
                                            ShowAggregatedWarning(
                                                "MIDIデバイスの接続を検知しました。利用するにはmidi2keyを再起動してください。",
                                                "MIDIデバイス接続検知");
                                        }
                                        // 2. 接続状態を確認できない場合（タイムアウトまたは取得失敗）
                                        else if (!outProcResult.Success)
                                        {
                                            ShowAggregatedWarning(
                                                "MIDIデバイスの接続状態を確認できません。必要に応じてmidi2keyを再起動してください。",
                                                "MIDIデバイス状態確認");
                                        }
                                        // 3. 無関係なUSB機器の変更等（ポート変化なし）: 警告を表示しない
                                    }
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

                // 変換開始直前にも選択ポートが現在有効か検証（要件4・課題1）
                // 選択ポートごとに親プロセス（DryWetMIDI）および新規プロセスWinMMの両方の一覧と照合する
                var enumResult = MidiListener.GetPortNames();
                var outProc = MidiDeviceDiagnostics.GetOutOfProcessWinMmResult(1000);

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
                        "選択されたMIDIポートを開始できませんでした。機器が取り外されているか、MIDIバックエンドに未反映の可能性があります。機器を接続し直し、midi2keyを再起動してください。",
                        "MIDIポート開始不可",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
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
                        $"すべてのMIDIポートの開始に失敗しました。\n機器が取り外されているか、MIDIバックエンドに未反映の可能性があります。機器を接続し直し、midi2keyを再起動してください。\n\n詳細:\n{errDetails}",
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
                ShowAggregatedWarning(
                    "MIDIデバイスの切断を検知しました。変換を停止しました。機器を接続し直し、midi2keyを再起動してください。",
                    "MIDIデバイス切断検知");
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
