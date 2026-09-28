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
        
        private CheckedListBox chkPorts = null!;
        private RadioButton rbJIS = null!;
        private RadioButton rbUS = null!;
        private ListView listMapping = null!;
        private TextBox txtNote = null!;
        private TextBox txtKey = null!;
        private Label lblStatus = null!;
        
        private IKeyboardMouseEvents? globalHook;
        private MidiListener midiListener = null!;
        private KeySimulator keySimulator = null!;
        private InputTracker inputTracker = null!;

        public Form1()
        {
            InitializeComponentProgrammatically();
            LoadInitialSettings();
            SetupDependencies();
        }
        
        private void SetupDependencies()
        {
            keySimulator = new KeySimulator();
            inputTracker = new InputTracker(keySimulator, () => settings);
            midiListener = new MidiListener();

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
        }

        private void LoadInitialSettings()
        {
            currentSettingsPath = Path.Combine(appDir, "settings.json");
            currentSettingsDir = appDir;
            settings = SettingsManager.Load(currentSettingsPath);
            
            RefreshPorts(true);

            if (settings.KeyboardLayout == "US")
                rbUS.Checked = true;
            else
                rbJIS.Checked = true;

            RefreshMappingList();
        }

        private void RefreshPorts(bool isInitialLoad = false)
        {
            if (chkPorts == null) return;
            var checkedPorts = isInitialLoad ? settings.SelectedPorts : GetSelectedPorts();
            var availablePorts = MidiListener.GetPortNames();
            
            chkPorts.Items.Clear();
            foreach (var port in availablePorts)
            {
                int index = chkPorts.Items.Add(port);
                if (checkedPorts.Contains(port))
                {
                    chkPorts.SetItemChecked(index, true);
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
        /// UIコントロールの生成と配置（コミット 6a0af51 のデザインを完全復元）。
        /// </summary>
        private void InitializeComponentProgrammatically()
        {
            var version = typeof(Form1).Assembly.GetName().Version;
            string verStr = version != null ? $" v{version.Major}.{version.Minor}.{version.Build}" : " v1.0.2";
            this.Text = $"midi2key C#{verStr}";
            this.Width = 430;
            this.Height = 555;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Yu Gothic UI", 9);

            var grpLayout = new GroupBox { Text = "🌐 キー配列", Top = 10, Left = 295, Width = 105, Height = 50 };
            rbUS = new RadioButton { Text = "US", Top = 20, Left = 55, Width = 45 };
            rbJIS = new RadioButton { Text = "JIS", Top = 20, Left = 10, Width = 45 };
            rbUS.CheckedChanged += (s, e) => {
                if (rbUS.Checked) settings.KeyboardLayout = "US";
            };
            rbJIS.CheckedChanged += (s, e) => {
                if (rbJIS.Checked) settings.KeyboardLayout = "JIS";
            };
            grpLayout.Controls.Add(rbUS);
            grpLayout.Controls.Add(rbJIS);
            this.Controls.Add(grpLayout);

            var grpPorts = new GroupBox { Text = "🎛 MIDIポート選択", Width = 275, Height = 115, Top = 10, Left = 10 };
            chkPorts = new CheckedListBox { Top = 20, Left = 10, Width = 255, Height = 65, BorderStyle = BorderStyle.None, CheckOnClick = true };
            chkPorts.SelectedIndexChanged += (s, e) => chkPorts.ClearSelected();
            grpPorts.Controls.Add(chkPorts);

            var lblPortWarn = new Label { Text = "※機器の抜き差し時はアプリを再起動してください", ForeColor = Color.Red, Top = 88, Left = 5, AutoSize = true };
            grpPorts.Controls.Add(lblPortWarn);
            this.Controls.Add(grpPorts);

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
            
            // ノート取得モードと変換モードのライフサイクル分離
            txtNote.Enter += (s, e) => {
                inputTracker.SetCapturing(true);
                // 変換停止中であれば、ノート取得専用にMIDIリスナーを開始
                if (!inputTracker.IsListening)
                {
                    midiListener.Start(GetSelectedPorts());
                }
            };
            txtNote.Leave += (s, e) => {
                inputTracker.SetCapturing(false);
                // 変換停止中であれば、専用リスナーを停止。変換実行中なら停止しない！
                if (!inputTracker.IsListening)
                {
                    midiListener.Stop();
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

            // ノート番号バリデーション（0〜127）および pedal の登録
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
                UpdateSettingsFromUI();
                try
                {
                    SettingsManager.Save(currentSettingsPath, settings);
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
            var btnSaveAs = new Button { Text = "別名保存", Top = 375, Left = 105, Width = 85, Height = 28 };
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
                            // 候補パスへの原子的保存を先に実行
                            SettingsManager.Save(targetPath, settings);
                            // 保存成功後にのみファイルパスを更新
                            currentSettingsPath = targetPath;
                            currentSettingsDir = Path.GetDirectoryName(currentSettingsPath) ?? appDir;
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
                            // 先に解析と内容検証を実行（失敗時は例外発生で現設定を維持）
                            var newSettings = SettingsManager.Load(targetPath);

                            // 変換実行中であれば、安全に全キー解放・停止した上で新設定を適用
                            if (inputTracker.IsListening)
                            {
                                StopConversion();
                            }

                            currentSettingsPath = targetPath;
                            currentSettingsDir = Path.GetDirectoryName(currentSettingsPath) ?? appDir;

                            lock (settings.MappingLock)
                            {
                                settings = newSettings;
                            }

                            RefreshPorts(true);
                            if (settings.KeyboardLayout == "US") rbUS.Checked = true; else rbJIS.Checked = true;
                            RefreshMappingList();
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
            btnStart.Click += (s, e) => {
                if (inputTracker.IsListening) return; // 連打防止

                var ports = GetSelectedPorts();
                if (ports.Count == 0) { MessageBox.Show("MIDIポートを選択してください", "ポート未選択"); return; }
                
                UpdateSettingsFromUI();
                long sessionId = inputTracker.StartSession();
                midiListener.Start(ports, sessionId);

                lblStatus.Text = "ステータス: 実行中";
                lblStatus.ForeColor = Color.Green;
            };

            var btnStop = new Button { Text = "変換停止", Top = 415, Left = 305, Width = 95, Height = 35, BackColor = Color.Red, ForeColor = Color.White, Font = new Font(this.Font, FontStyle.Bold) };
            btnStop.Click += (s, e) => {
                StopConversion();
            };

            lblStatus = new Label { Text = "ステータス: 停止中", Top = 465, Left = 10, Width = 390, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Red };

            this.Controls.Add(btnSave);
            this.Controls.Add(btnSaveAs);
            this.Controls.Add(btnLoad);
            this.Controls.Add(btnStart);
            this.Controls.Add(btnStop);
            this.Controls.Add(lblStatus);
        }

        /// <summary>
        /// 変換処理を安全に停止します。
        /// デッドロック防止のため、ロック外でMIDIリスナーを停止した上で全キーを解放します。
        /// </summary>
        private void StopConversion()
        {
            // (1) 新規キー送信無効化、セッション無効化、全キー解放、内部状態クリア
            inputTracker.StopSession();

            // (2) ロック外でMIDIリスナーを停止し、受信スレッドの完了を安全に待機
            midiListener.Stop();

            // (3) 未解放キーがあれば再試行
            keySimulator.RetryReleasePendingKeys();

            // (4) UIステータス更新
            lblStatus.Text = "ステータス: 停止中";
            lblStatus.ForeColor = Color.Red;
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
            StopConversion();
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
