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
        
        private bool isListening = false;
        private bool isCapturingNote = false;
        
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
            
            if (isListening && settings.Mapping.ContainsKey(noteNumber.ToString()))
            {
                string mappedKey = settings.Mapping[noteNumber.ToString()];
                keySimulator.SendKey(mappedKey, isDown, settings.KeyboardLayout);
            }
        }

        private void LoadInitialSettings()
        {
            string path = Path.Combine(appDir, "settings.json");
            settings = SettingsManager.Load(path);
            
            // Only call RefreshPorts if chkPorts is already initialized.
            // Wait, LoadInitialSettings is called in constructor AFTER InitializeComponentProgrammatically.
            // So chkPorts is not null.
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

        private void RefreshMappingList()
        {
            listMapping.Items.Clear();
            foreach (var kvp in settings.Mapping.OrderBy(x => int.Parse(x.Key)))
            {
                listMapping.Items.Add($"Note {kvp.Key} → {kvp.Value}");
            }
        }
        
        /// <summary>
        /// UIコントロール（ボタン、テキストボックス、リスト等の配置とサイズ）をプログラムコード上で手動定義します。
        /// デザイナを使わずに軽量かつ精緻な配置を実現しており、Pythonのtkinter版と同一のルック＆フィールを提供します。
        /// </summary>
        private void InitializeComponentProgrammatically()
        {
            this.Text = "midi2key C#";
            this.Width = 420;
            this.Height = 540;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.Font = new Font("Yu Gothic UI", 9);

            var grpLayout = new GroupBox { Text = "🌐 キー配列", Top = 10, Left = 290, Width = 100, Height = 50 };
            rbUS = new RadioButton { Text = "US", Top = 20, Left = 50, Width = 45 };
            rbJIS = new RadioButton { Text = "JIS", Top = 20, Left = 10, Width = 45 };
            grpLayout.Controls.Add(rbUS);
            grpLayout.Controls.Add(rbJIS);
            this.Controls.Add(grpLayout);

            var grpPorts = new GroupBox { Text = "🎛 MIDIポート選択", Width = 270, Height = 115, Top = 10, Left = 10 };
            chkPorts = new CheckedListBox { Top = 20, Left = 10, Width = 250, Height = 65, BorderStyle = BorderStyle.None, CheckOnClick = true };
            chkPorts.SelectedIndexChanged += (s, e) => chkPorts.ClearSelected();
            grpPorts.Controls.Add(chkPorts);

            var lblPortWarn = new Label { Text = "※機器の抜き差し時はアプリを再起動してください", ForeColor = Color.Red, Top = 88, Left = 5, AutoSize = true };
            grpPorts.Controls.Add(lblPortWarn);
            this.Controls.Add(grpPorts);
            
            var lblList = new Label { Text = "📄 マッピング一覧", Top = 135, Left = 10, AutoSize = true };
            listMapping = new ListBox { Top = 155, Left = 10, Width = 370, Height = 135 };
            listMapping.SelectedIndexChanged += (s, e) => {
                if (listMapping.SelectedItem == null) return;
                var str = listMapping.SelectedItem.ToString();
                var parts = str.Split('→');
                if (parts.Length == 2)
                {
                    txtNote.Text = parts[0].Replace("Note", "").Trim();
                    txtKey.Text = parts[1].Trim();
                }
            };
            this.Controls.Add(lblList);
            this.Controls.Add(listMapping);

            var lblNote = new Label { Text = "🎹 ノート番号", Top = 300, Left = 10, AutoSize = true };
            txtNote = new TextBox { Top = 320, Left = 10, Width = 60 };
            txtNote.Enter += (s, e) => {
                isCapturingNote = true;
                midiListener.Start(GetSelectedPorts());
            };
            txtNote.Leave += (s, e) => {
                isCapturingNote = false;
                if (!isListening) midiListener.Stop();
            };

            var lblKey = new Label { Text = "⌨ キー", Top = 300, Left = 100, AutoSize = true };
            txtKey = new TextBox { Top = 320, Left = 100, Width = 130, ReadOnly = true, BackColor = SystemColors.Window };
            
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

            var btnAdd = new Button { Text = "追加", Top = 295, Left = 320, Width = 60, Height = 25 };
            btnAdd.Click += (s, e) => {
                if (int.TryParse(txtNote.Text, out int note) && !string.IsNullOrEmpty(txtKey.Text))
                {
                    settings.Mapping[note.ToString()] = txtKey.Text;
                    RefreshMappingList();
                }
                else MessageBox.Show("ノート番号は数値で入力してください。", "エラー");
            };
            
            var btnDel = new Button { Text = "削除", Top = 325, Left = 320, Width = 60, Height = 25 };
            btnDel.Click += (s, e) => {
                if (listMapping.SelectedIndex >= 0)
                {
                    var str = listMapping.SelectedItem.ToString();
                    var note = str.Split('→')[0].Replace("Note", "").Trim();
                    settings.Mapping.Remove(note);
                    RefreshMappingList();
                }
            };

            this.Controls.Add(lblNote);
            this.Controls.Add(txtNote);
            this.Controls.Add(lblKey);
            this.Controls.Add(txtKey);
            this.Controls.Add(btnAdd);
            this.Controls.Add(btnDel);

            var sep = new Label { BorderStyle = BorderStyle.Fixed3D, Top = 365, Left = 10, Width = 370, Height = 2 };
            this.Controls.Add(sep);

            var btnSave = new Button { Text = "設定保存", Top = 380, Left = 10, Width = 80 };
            btnSave.Click += (s, e) => {
                UpdateSettingsFromUI();
                using (var sfd = new SaveFileDialog())
                {
                    sfd.InitialDirectory = currentSettingsDir;
                    sfd.Filter = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*";
                    sfd.DefaultExt = "json";
                    sfd.FileName = "settings.json";
                    if (sfd.ShowDialog() == DialogResult.OK)
                    {
                        currentSettingsDir = Path.GetDirectoryName(sfd.FileName);
                        SettingsManager.Save(sfd.FileName, settings);
                        MessageBox.Show("保存完了しました。");
                    }
                }
            };
            
            var btnLoad = new Button { Text = "設定読み込み", Top = 380, Left = 290, Width = 90 };
            btnLoad.Click += (s, e) => {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.InitialDirectory = currentSettingsDir;
                    ofd.Filter = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*";
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        try 
                        {
                            currentSettingsDir = Path.GetDirectoryName(ofd.FileName);
                            settings = SettingsManager.Load(ofd.FileName);
                            RefreshPorts(true);
                            if (settings.KeyboardLayout == "US") rbUS.Checked = true; else rbJIS.Checked = true;
                            RefreshMappingList();
                            MessageBox.Show("読込み完了しました。");
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("ファイルの読み込みに失敗しました: " + ex.Message);
                        }
                    }
                }
            };

            var btnStart = new Button { Text = "変換開始", Top = 420, Left = 10, Width = 80, Height = 35, BackColor = Color.Green, ForeColor = Color.White, Font = new Font(this.Font, FontStyle.Bold) };
            btnStart.Click += (s, e) => {
                var ports = GetSelectedPorts();
                if (ports.Count == 0) { MessageBox.Show("MIDIポートを選択してください"); return; }
                UpdateSettingsFromUI();
                isListening = true;
                lblStatus.Text = "ステータス: 実行中";
                lblStatus.ForeColor = Color.Green;
                midiListener.Start(ports);
            };

            var btnStop = new Button { Text = "変換停止", Top = 420, Left = 300, Width = 80, Height = 35, BackColor = Color.Red, ForeColor = Color.White, Font = new Font(this.Font, FontStyle.Bold) };
            btnStop.Click += (s, e) => {
                isListening = false;
                lblStatus.Text = "ステータス: 停止中";
                lblStatus.ForeColor = Color.Red;
                midiListener.Stop();
            };

            lblStatus = new Label { Text = "ステータス: 停止中", Top = 465, Left = 10, Width = 370, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Red };

            this.Controls.Add(btnSave);
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
