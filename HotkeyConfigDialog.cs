using System;
using System.Drawing;
using System.Windows.Forms;

namespace MidiToKeyApp
{
    /// <summary>
    /// グローバルトグルホットキーの設定ダイアログ。
    /// </summary>
    public class HotkeyConfigDialog : Form
    {
        private readonly CheckBox _chkEnabled;
        private readonly CheckBox _chkCtrl;
        private readonly CheckBox _chkAlt;
        private readonly CheckBox _chkShift;
        private readonly CheckBox _chkWin;
        private readonly ComboBox _cmbKey;
        private readonly Label _lblWarn;
        private readonly Button _btnOk;
        private readonly Button _btnCancel;

        public HotkeySettings ResultSettings { get; private set; }

        public HotkeyConfigDialog(HotkeySettings currentSettings)
        {
            ResultSettings = new HotkeySettings
            {
                Enabled = currentSettings.Enabled,
                Modifiers = currentSettings.Modifiers,
                Key = currentSettings.Key
            };

            this.Text = "トグルホットキー設定";
            this.Width = 360;
            this.Height = 270;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Font = new Font("Yu Gothic UI", 9);

            _chkEnabled = new CheckBox { Text = "グローバルトグルホットキーを有効にする", Top = 15, Left = 20, Width = 300, Checked = currentSettings.Enabled };

            var grp = new GroupBox { Text = "キーの組み合わせ", Top = 45, Left = 20, Width = 305, Height = 105 };

            _chkCtrl = new CheckBox { Text = "Ctrl", Top = 25, Left = 15, Width = 55 };
            _chkAlt = new CheckBox { Text = "Alt", Top = 25, Left = 75, Width = 55 };
            _chkShift = new CheckBox { Text = "Shift", Top = 25, Left = 135, Width = 60 };
            _chkWin = new CheckBox { Text = "Win", Top = 25, Left = 200, Width = 55 };

            var lblKey = new Label { Text = "メインキー:", Top = 65, Left = 15, AutoSize = true };
            _cmbKey = new ComboBox { Top = 62, Left = 90, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };

            // 主要なファンクションキーおよび一般キーを候補に追加
            for (int i = 1; i <= 12; i++)
            {
                _cmbKey.Items.Add($"F{i}");
            }
            string[] otherKeys = { "Pause", "ScrollLock", "Insert", "Home", "PageUp", "Delete", "End", "PageDown" };
            foreach (var k in otherKeys)
            {
                _cmbKey.Items.Add(k);
            }

            // 現在のキーを選択
            int selectIdx = _cmbKey.FindStringExact(currentSettings.Key);
            if (selectIdx >= 0)
            {
                _cmbKey.SelectedIndex = selectIdx;
            }
            else
            {
                _cmbKey.Items.Add(currentSettings.Key);
                _cmbKey.SelectedItem = currentSettings.Key;
            }

            // モディファイアの反映
            string mods = currentSettings.Modifiers.ToLowerInvariant();
            if (mods.Contains("ctrl")) _chkCtrl.Checked = true;
            if (mods.Contains("alt")) _chkAlt.Checked = true;
            if (mods.Contains("shift")) _chkShift.Checked = true;
            if (mods.Contains("win")) _chkWin.Checked = true;

            grp.Controls.Add(_chkCtrl);
            grp.Controls.Add(_chkAlt);
            grp.Controls.Add(_chkShift);
            grp.Controls.Add(_chkWin);
            grp.Controls.Add(lblKey);
            grp.Controls.Add(_cmbKey);

            _lblWarn = new Label
            {
                Text = "※Steamやゲーム独自のショートカットと競合する場合があります。\n※MIDIマッピングキーと同じキーは設定できません。",
                Top = 155,
                Left = 20,
                Width = 310,
                Height = 35,
                ForeColor = Color.DarkSlateGray,
                Font = new Font("Yu Gothic UI", 8)
            };

            _btnOk = new Button { Text = "OK", Top = 195, Left = 145, Width = 85, Height = 28, DialogResult = DialogResult.OK };
            _btnCancel = new Button { Text = "キャンセル", Top = 195, Left = 240, Width = 85, Height = 28, DialogResult = DialogResult.Cancel };

            _btnOk.Click += (s, e) => {
                if (_chkEnabled.Checked)
                {
                    if (!_chkCtrl.Checked && !_chkAlt.Checked && !_chkShift.Checked && !_chkWin.Checked)
                    {
                        MessageBox.Show("誤操作防止のため、Ctrl, Alt, Shift, Win のいずれか1つ以上の修飾キーを指定してください。", "設定確認");
                        this.DialogResult = DialogResult.None;
                        return;
                    }
                }

                var modParts = new System.Collections.Generic.List<string>();
                if (_chkCtrl.Checked) modParts.Add("Ctrl");
                if (_chkAlt.Checked) modParts.Add("Alt");
                if (_chkShift.Checked) modParts.Add("Shift");
                if (_chkWin.Checked) modParts.Add("Win");

                ResultSettings = new HotkeySettings
                {
                    Enabled = _chkEnabled.Checked,
                    Modifiers = string.Join("+", modParts),
                    Key = _cmbKey.SelectedItem?.ToString() ?? "F9"
                };
            };

            this.Controls.Add(_chkEnabled);
            this.Controls.Add(grp);
            this.Controls.Add(_lblWarn);
            this.Controls.Add(_btnOk);
            this.Controls.Add(_btnCancel);
            this.AcceptButton = _btnOk;
            this.CancelButton = _btnCancel;
        }
    }
}
