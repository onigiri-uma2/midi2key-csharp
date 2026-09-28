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
        private AppSettings settings;
        private string appDir = AppDomain.CurrentDomain.BaseDirectory;
        private string currentSettingsDir = AppDomain.CurrentDomain.BaseDirectory;
        private string currentSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        
        private CheckedListBox chkPorts;
        private RadioButton rbJIS;
        private RadioButton rbUS;
        private ListBox listMapping;
        private TextBox txtNote;
        private TextBox txtKey;
        private Label lblStatus;
        
        private IKeyboardMouseEvents globalHook;
        private MidiListener midiListener;
        private KeySimulator keySimulator;
        
        private volatile bool isListening = false;
        private volatile bool isCapturingNote = false;
        
        public Form1()
        {
            InitializeComponentProgrammatically();
            LoadInitialSettings();
            SetupDependencies();
        }
        
        private void SetupDependencies()
        {
            midiListener = new MidiListener();
            keySimulator = new KeySimulator();
            midiListener.OnNoteChange += MidiListener_OnNoteChange;
            midiListener.OnControlChange += MidiListener_OnControlChange;
        }

        /// <summary>
        /// コントロールチェンジ（サステインペダル CC 64 など）を受け取った際のイベントハンドラ。
        /// </summary>
        private void MidiListener_OnControlChange(int controlNumber, int value)
        {
            // CC 64 = ダンパー / サステインペダル
            if (controlNumber == 64)
            {
                bool isDown = value >= 64; // 64以上でペダル踏み込み、64未満で解放

                if (isCapturingNote && isDown)
                {
                    Invoke((MethodInvoker)delegate {
                        txtNote.Text = "pedal";
                    });
                    return;
                }

                if (isListening)
                {
                    string? mappedKey = null;
                    lock (settings.MappingLock)
                    {
                        if (settings.Mapping.TryGetValue("pedal", out var key))
                        {
                            mappedKey = key;
                        }
                    }

                    if (mappedKey != null)
                    {
                        keySimulator.SendKey(mappedKey, isDown, settings.KeyboardLayout);
                    }
                }
            }
        }

        /// <summary>
        /// MIDIポートから信号(NoteOn / NoteOff)を受け取った際に発火するイベントのハンドラ。
        /// 設定状況に応じて、テキストボックスへの入力記録か、実際のキー送信かを分岐させます。
        /// </summary>
        /// <param name="noteNumber">受信したMIDIノートの番号</param>
        /// <param name="isDown">押されているか(true)離されているか(false)</param>
        private void MidiListener_OnNoteChange(int noteNumber, bool isDown)
        {
            if (isCapturingNote && isDown)
            {
                Invoke((MethodInvoker)delegate {
                    txtNote.Text = noteNumber.ToString();
                });
                return;
            }
            
            if (isListening)
            {
                string noteKey = noteNumber.ToString();
                string? mappedKey = null;

                lock (settings.MappingLock)
                {
                    if (settings.Mapping.TryGetValue(noteKey, out var key))
                    {
                        mappedKey = key;
                    }
                }

                if (mappedKey != null)
                {
                    keySimulator.SendKey(mappedKey, isDown, settings.KeyboardLayout);
                }
            }
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
        /// マッピング辞書の内容をリストボックスに描画します。
        /// 88鍵盤（21〜108）などのノート番号には対応する音階名（例: C4 / ド）を併記します。
        /// </summary>
        private void RefreshMappingList()
        {
            listMapping.Items.Clear();
            List<KeyValuePair<string, string>> pairs;
            lock (settings.MappingLock)
            {
                pairs = settings.Mapping
                    .OrderBy(x => x.Key.Equals("pedal", StringComparison.OrdinalIgnoreCase) ? 9999 : (int.TryParse(x.Key, out int n) ? n : 9998))
                    .ToList();
            }
            foreach (var kvp in pairs)
            {
                if (kvp.Key.Equals("pedal", StringComparison.OrdinalIgnoreCase))
                {
                    listMapping.Items.Add($"Pedal (CC64 / サステイン) → {kvp.Value}");
                }
                else if (int.TryParse(kvp.Key, out int note))
                {
                    string noteName = MidiNoteHelper.GetNoteDisplayName(note);
                    listMapping.Items.Add($"Note {note} ({noteName}) → {kvp.Value}");
                }
                else
                {
                    listMapping.Items.Add($"{kvp.Key} → {kvp.Value}");
                }
            }
        }
        
        /// <summary>
        /// UIコントロール（ボタン、テキストボックス、リスト等の配置とサイズ）をプログラムコード上で手動定義します。
        /// デザイナを使わずに軽量かつ精緻な配置を実現しており、Pythonのtkinter版と同一のルック＆フィールを提供します。
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
            listMapping = new ListBox { Top = 155, Left = 10, Width = 390, Height = 135 };
            listMapping.SelectedIndexChanged += (s, e) => {
                if (listMapping.SelectedItem == null) return;
                var str = listMapping.SelectedItem.ToString();
                if (string.IsNullOrEmpty(str)) return;
                var parts = str.Split('→');
                if (parts.Length == 2)
                {
                    var left = parts[0].Trim();
                    if (left.StartsWith("Pedal", StringComparison.OrdinalIgnoreCase))
                    {
                        txtNote.Text = "pedal";
                    }
                    else
                    {
                        var numPart = left.Replace("Note", "").Trim();
                        int parenIndex = numPart.IndexOf('(');
                        if (parenIndex >= 0) numPart = numPart.Substring(0, parenIndex).Trim();
                        txtNote.Text = numPart;
                    }
                    txtKey.Text = parts[1].Trim();
                }
            };
            this.Controls.Add(lblList);
            this.Controls.Add(listMapping);

            var lblNote = new Label { Text = "🎹 ノート/ペダル", Top = 300, Left = 10, AutoSize = true };
            txtNote = new TextBox { Top = 320, Left = 10, Width = 75 };
            txtNote.Enter += (s, e) => {
                isCapturingNote = true;
                midiListener.Start(GetSelectedPorts());
            };
            txtNote.Leave += (s, e) => {
                isCapturingNote = false;
                if (!isListening) midiListener.Stop();
            };

            var lblKey = new Label { Text = "⌨ キー", Top = 300, Left = 95, AutoSize = true };
            txtKey = new TextBox { Top = 320, Left = 95, Width = 115, ReadOnly = true, BackColor = SystemColors.Window };
            
            // ⑥ キー入力欄の「消去」ボタン
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

            // ⑤ ノート番号バリデーション（0〜127）および pedal の登録
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
                        MessageBox.Show("MIDIノート番号は 0 〜 127 の範囲で入力してください。\n（一般的な88鍵盤ピアノは 21[A0] 〜 108[C8] です）", "範囲エラー");
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
                if (listMapping.SelectedIndex >= 0)
                {
                    var str = listMapping.SelectedItem?.ToString();
                    if (!string.IsNullOrEmpty(str))
                    {
                        var parts = str.Split('→');
                        var left = parts[0].Trim();
                        string keyToRemove;
                        if (left.StartsWith("Pedal", StringComparison.OrdinalIgnoreCase))
                        {
                            keyToRemove = "pedal";
                        }
                        else
                        {
                            var numPart = left.Replace("Note", "").Trim();
                            int parenIndex = numPart.IndexOf('(');
                            if (parenIndex >= 0) numPart = numPart.Substring(0, parenIndex).Trim();
                            keyToRemove = numPart;
                        }

                        lock (settings.MappingLock)
                        {
                            settings.Mapping.Remove(keyToRemove);
                        }
                        RefreshMappingList();
                    }
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

            // ⑦ 「上書き保存」ボタン
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

            // ⑦ 「別名保存」ボタン
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
                        currentSettingsPath = sfd.FileName;
                        currentSettingsDir = Path.GetDirectoryName(currentSettingsPath) ?? appDir;
                        SettingsManager.Save(currentSettingsPath, settings);
                        MessageBox.Show("別名保存が完了しました。\n保存先: " + currentSettingsPath, "保存完了");
                    }
                }
            };
            
            var btnLoad = new Button { Text = "設定読込", Top = 375, Left = 305, Width = 95, Height = 28 };
            btnLoad.Click += (s, e) => {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.InitialDirectory = Path.GetDirectoryName(currentSettingsPath);
                    ofd.Filter = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*";
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        try 
                        {
                            currentSettingsPath = ofd.FileName;
                            currentSettingsDir = Path.GetDirectoryName(currentSettingsPath) ?? appDir;
                            var newSettings = SettingsManager.Load(ofd.FileName);
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
                            MessageBox.Show("ファイルの読み込みに失敗しました: " + ex.Message);
                        }
                    }
                }
            };

            var btnStart = new Button { Text = "変換開始", Top = 415, Left = 10, Width = 95, Height = 35, BackColor = Color.Green, ForeColor = Color.White, Font = new Font(this.Font, FontStyle.Bold) };
            btnStart.Click += (s, e) => {
                var ports = GetSelectedPorts();
                if (ports.Count == 0) { MessageBox.Show("MIDIポートを選択してください"); return; }
                UpdateSettingsFromUI();
                isListening = true;
                lblStatus.Text = "ステータス: 実行中";
                lblStatus.ForeColor = Color.Green;
                midiListener.Start(ports);
            };

            // ① 停止時に押下中のすべてのキーを強制解放（ReleaseAllKeys）
            var btnStop = new Button { Text = "変換停止", Top = 415, Left = 305, Width = 95, Height = 35, BackColor = Color.Red, ForeColor = Color.White, Font = new Font(this.Font, FontStyle.Bold) };
            btnStop.Click += (s, e) => {
                isListening = false;
                lblStatus.Text = "ステータス: 停止中";
                lblStatus.ForeColor = Color.Red;
                midiListener.Stop();
                keySimulator.ReleaseAllKeys();
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
        /// ユーザーのキーボード入力をOSレベルで捕獲（フック）し、「キー設定の入力欄」にキー名称を反映させる処理。
        /// ユーザーが意図した物理キー操作を検知するため、MouseKeyHookライブラリを使用しています。
        /// </summary>
        private void GlobalHook_KeyDown(object sender, KeyEventArgs e)
        {
            bool isUS = false;
            
            if (IsHandleCreated) {
                Invoke((MethodInvoker)delegate {
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
                });
            }
        }

        /// <summary>
        /// OSレベルの生キー入力イベントデータから、JIS/US配列の違いやShiftキーの押下状態を考慮し、
        /// "ユーザーが見て直感的にわかる記号や文字（例：'¥' や '|' など）" へ動的に変換します。
        /// </summary>
        /// <param name="e">キャプチャしたキーボードイベント</param>
        /// <param name="isUS">US配列の場合はtrue、JIS配列の場合はfalse</param>
        /// <returns>変換された人間が読める文字列フォーマットのキー名</returns>
        private string FormatKey(KeyEventArgs e, bool isUS)
        {
            bool shift = e.Shift;
            int code = (int)e.KeyCode;
            
            // IME等に吸収されてKeyCodeがNone(0)になる場合のパッチ対策 (MouseKeyHook拡張機能)
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
                case 240: return "eisuy";    // Alphanumeric
                case 242: return "hiragana"; // Katakana/Hiragana/Romaji
            }

            return e.KeyCode.ToString().ToLower();
        }

        private List<string> GetSelectedPorts()
        {
            var ports = new List<string>();
            foreach (var item in chkPorts.CheckedItems)
            {
                ports.Add(item.ToString());
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
            keySimulator?.ReleaseAllKeys(); // 終了時にもキー押しっぱなしを強制解除
            midiListener?.Dispose();
            if (globalHook != null)
            {
                globalHook.KeyDown -= GlobalHook_KeyDown;
                globalHook.Dispose();
            }
            base.OnFormClosing(e);
        }
    }

    /// <summary>
    /// MIDIノート番号（0-127）から音階名（例: C4 / ド、A0 / ラ）への変換を行うヘルパークラス。
    /// 88鍵盤ピアノ（21[A0]〜108[C8]）の視覚的把握をサポートします。
    /// </summary>
    public static class MidiNoteHelper
    {
        private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        private static readonly string[] KanaNames = { "ド", "ド#", "レ", "レ#", "ミ", "ファ", "ファ#", "ソ", "ソ#", "ラ", "ラ#", "シ" };

        public static string GetNoteDisplayName(int noteNumber)
        {
            if (noteNumber < 0 || noteNumber > 127) return noteNumber.ToString();
            int octave = (noteNumber / 12) - 1; // MIDI規格: Note 60 = C4, Note 21 = A0
            string name = NoteNames[noteNumber % 12];
            string kana = KanaNames[noteNumber % 12];
            return $"{name}{octave} / {kana}";
        }
    }
}
