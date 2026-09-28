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
    /// MIDIの入力を受けた際の処理フローの制御や、キー入力キャプチャのグローバルフックの管理を行います。
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

            // MIDIリスナーのイベントをInputTrackerへ中継
            midiListener.OnNoteReceived += (data) => inputTracker.ProcessNoteEvent(data, inputTracker.CurrentSessionId);
            midiListener.OnControlReceived += (data) => inputTracker.ProcessControlEvent(data, inputTracker.CurrentSessionId);
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

        private void InitializeComponentProgrammatically()
        {
            this.Text = "MIDI to Key Mapper v1.0.2";
            this.Size = new Size(430, 560);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;

            var lblPorts = new Label { Text = "🎹 MIDI ポート選択", Top = 10, Left = 10, AutoSize = true };
            chkPorts = new CheckedListBox { Top = 30, Left = 10, Width = 390, Height = 70 };
            
            var btnRefresh = new Button { Text = "再読込", Top = 105, Left = 325, Width = 75, Height = 25 };
            btnRefresh.Click += (s, e) => RefreshPorts();

            this.Controls.Add(lblPorts);
            this.Controls.Add(chkPorts);
            this.Controls.Add(btnRefresh);

            var gbLayout = new GroupBox { Text = "⌨ キーボード配列", Top = 100, Left = 10, Width = 200, Height = 45 };
            rbJIS = new RadioButton { Text = "日本語 (JIS)", Left = 10, Top = 18, AutoSize = true, Checked = true };
            rbUS = new RadioButton { Text = "英語 (US)", Left = 110, Top = 18, AutoSize = true };
            
            rbJIS.CheckedChanged += (s, e) => {
                if (rbJIS.Checked) UpdateSettingsFromUI();
            };
            rbUS.CheckedChanged += (s, e) => {
                if (rbUS.Checked) UpdateSettingsFromUI();
            };

            gbLayout.Controls.Add(rbJIS);
            gbLayout.Controls.Add(rbUS);
            this.Controls.Add(gbLayout);

            var lblList = new Label { Text = "🗺 マッピング設定一覧", Top = 150, Left = 10, AutoSize = true };
            
            listMapping = new ListView { 
                Top = 170, 
                Left = 10, 
                Width = 390, 
                Height = 120,
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

            // カラムヘッダーのカスタム描画（淡いブルーグレー背景・濃紺太字）
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

            var lblNote = new Label { Text = "🎹 ノート/ペダル", Top = 295, Left = 10, AutoSize = true };
            txtNote = new TextBox { Top = 315, Left = 10, Width = 75 };
            
            // ノート取得モードと変換モードの分離
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

            var lblKey = new Label { Text = "⌨ キー", Top = 295, Left = 95, AutoSize = true };
            txtKey = new TextBox { Top = 315, Left = 95, Width = 115, ReadOnly = true, BackColor = SystemColors.Window };
            
            var btnClearKey = new Button { Text = "消去", Top = 314, Left = 215, Width = 50, Height = 25 };
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

            var sep = new Label { BorderStyle = BorderStyle.Fixed3D, Top = 355, Left = 10, Width = 390, Height = 2 };
            this.Controls.Add(sep);

            // 「上書き保存」ボタン
            var btnSave = new Button { Text = "上書き保存", Top = 370, Left = 10, Width = 85, Height = 28 };
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
            var btnSaveAs = new Button { Text = "別名保存", Top = 370, Left = 105, Width = 85, Height = 28 };
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
                            // 候補パスへの保存を先に実行
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
            var btnLoad = new Button { Text = "設定読込", Top = 370, Left = 305, Width = 95, Height = 28 };
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
                            // 先に解析と検証を実行（失敗時は例外発生で現設定を維持）
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

            var btnStart = new Button { Text = "変換開始", Top = 410, Left = 10, Width = 95, Height = 35, BackColor = Color.Green, ForeColor = Color.White, Font = new Font(this.Font, FontStyle.Bold) };
            btnStart.Click += (s, e) => {
                if (inputTracker.IsListening) return; // 連打防止

                var ports = GetSelectedPorts();
                if (ports.Count == 0) { MessageBox.Show("MIDIポートを選択してください", "ポート未選択"); return; }
                
                UpdateSettingsFromUI();
                inputTracker.StartSession();
                midiListener.Start(ports);

                lblStatus.Text = "ステータス: 実行中";
                lblStatus.ForeColor = Color.Green;
            };

            var btnStop = new Button { Text = "変換停止", Top = 410, Left = 305, Width = 95, Height = 35, BackColor = Color.Red, ForeColor = Color.White, Font = new Font(this.Font, FontStyle.Bold) };
            btnStop.Click += (s, e) => {
                StopConversion();
            };

            lblStatus = new Label { Text = "ステータス: 停止中", Top = 460, Left = 10, Width = 390, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Red };

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

            // (3) UIステータス更新
            lblStatus.Text = "ステータス: 停止中";
            lblStatus.ForeColor = Color.Red;
        }

        /// <summary>
        /// ユーザーのキーボード入力をOSレベルで捕獲（フック）し、「キー設定の入力欄」にキー名称を反映させる処理。
        /// </summary>
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

        /// <summary>
        /// 生キー入力イベントから人間が直感的に理解できるキー名称文字列を生成します。
        /// </summary>
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
