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

            this.Text = "ホットキー設定";
            this.ClientSize = new Size(360, 280);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.ShowInTaskbar = false;
            this.AutoScaleMode = AutoScaleMode.Font;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Font = new Font("Yu Gothic UI", 9);

            _chkEnabled = new CheckBox { Text = "ホットキーを有効にする", Top = 15, Left = 20, Width = 300, Checked = currentSettings.Enabled };

            var grp = new GroupBox { Text = "変換の開始・停止を切り替えるキー", Top = 45, Left = 20, Width = 305, Height = 105 };

            _chkCtrl = new CheckBox { Text = "Ctrl", Top = 25, Left = 15, Width = 55 };
            _chkAlt = new CheckBox { Text = "Alt", Top = 25, Left = 75, Width = 55 };
            _chkShift = new CheckBox { Text = "Shift", Top = 25, Left = 135, Width = 60 };
            _chkWin = new CheckBox { Text = "Win", Top = 25, Left = 200, Width = 55 };

            var lblKey = new Label { Text = "メインキー:", Top = 65, Left = 15, AutoSize = true };
            _cmbKey = new ComboBox { Top = 62, Left = 90, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };

            // 主要なファンクションキー（F1〜F11。F12はシステム予約のため除外）および一般キーを候補に追加
            for (int i = 1; i <= 11; i++)
            {
                _cmbKey.Items.Add($"F{i}");
            }
            string[] otherKeys = { "Pause", "ScrollLock", "Insert", "Home", "PageUp", "Delete", "End", "PageDown" };
            foreach (var k in otherKeys)
            {
                _cmbKey.Items.Add(k);
            }

            // 現在のキーを選択（F12の場合は予約キーのため安全にF9へフォールバック）
            string currentKey = currentSettings.Key ?? "F9";
            if (string.Equals(currentKey.Trim(), "F12", StringComparison.OrdinalIgnoreCase))
            {
                currentKey = "F9";
            }

            int selectIdx = _cmbKey.FindStringExact(currentKey);
            if (selectIdx >= 0)
            {
                _cmbKey.SelectedIndex = selectIdx;
            }
            else
            {
                _cmbKey.Items.Add(currentKey);
                _cmbKey.SelectedItem = currentKey;
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
                Text = "※Steamやゲーム独自のショートカットと競合する場合があります。\n※MIDIマッピングキーと同じキーおよびF12は設定できません。",
                Top = 155,
                Left = 20,
                Width = 310,
                Height = 35,
                ForeColor = Color.DarkSlateGray,
                Font = new Font("Yu Gothic UI", 8)
            };

            _btnOk = new Button { Text = "適用", Top = 195, Left = 145, Width = 85, Height = 28, DialogResult = DialogResult.OK };
            _btnCancel = new Button { Text = "キャンセル", Top = 195, Left = 240, Width = 85, Height = 28, DialogResult = DialogResult.Cancel };

            _btnOk.Click += (s, e) => {
                string selectedKey = _cmbKey.SelectedItem?.ToString() ?? "F9";
                if (string.Equals(selectedKey.Trim(), "F12", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("F12キーはWindowsシステム・デバッガ予約キーのため設定できません。", "キー選択エラー");
                    this.DialogResult = DialogResult.None;
                    return;
                }

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
                    Key = selectedKey
                };
            };

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16),
                ColumnCount = 1, RowCount = 5 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 24, 104, 32, 54, 34 })
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            _chkEnabled.Dock = DockStyle.Fill; _chkEnabled.Margin = Padding.Empty;
            _chkEnabled.TabIndex = 0;
            grp.Dock = DockStyle.Fill; grp.Margin = Padding.Empty; grp.TabIndex = 1;
            _chkCtrl.TabIndex = 0; _chkAlt.TabIndex = 1; _chkShift.TabIndex = 2; _chkWin.TabIndex = 3;
            _cmbKey.TabIndex = 4; _cmbKey.AccessibleName = "ホットキーのメインキー";
            var preview = new Label { Dock = DockStyle.Fill, Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "ホットキーの設定内容" };
            _lblWarn.Dock = DockStyle.Fill; _lblWarn.Margin = Padding.Empty;
            _lblWarn.Text = "他のアプリのショートカットと競合する場合があります。\nMIDIの変換先キーおよびF12は設定できません。";
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty,
                FlowDirection = FlowDirection.RightToLeft, WrapContents = false, TabIndex = 2 };
            _btnCancel.Margin = Padding.Empty; _btnCancel.Height = 30; _btnCancel.TabIndex = 1;
            _btnOk.Margin = new Padding(0, 0, 8, 0); _btnOk.Height = 30; _btnOk.TabIndex = 0;
            actions.Controls.Add(_btnCancel); actions.Controls.Add(_btnOk);
            root.Controls.Add(_chkEnabled, 0, 0); root.Controls.Add(grp, 0, 1);
            root.Controls.Add(preview, 0, 2); root.Controls.Add(_lblWarn, 0, 3); root.Controls.Add(actions, 0, 4);
            Controls.Add(root);

            void UpdatePreview()
            {
                grp.Enabled = _chkEnabled.Checked;
                var parts = new System.Collections.Generic.List<string>();
                if (_chkCtrl.Checked) parts.Add("Ctrl");
                if (_chkAlt.Checked) parts.Add("Alt");
                if (_chkShift.Checked) parts.Add("Shift");
                if (_chkWin.Checked) parts.Add("Win");
                bool valid = parts.Count > 0;
                parts.Add(_cmbKey.SelectedItem?.ToString() ?? "F9");
                preview.Text = !_chkEnabled.Checked ? "無効：ホットキーでは切り替えません" :
                    valid ? string.Join(" + ", parts) : "Ctrl・Alt・Shift・Winから1つ以上選択してください";
                _btnOk.Enabled = !_chkEnabled.Checked || valid;
            }
            foreach (var check in new[] { _chkEnabled, _chkCtrl, _chkAlt, _chkShift, _chkWin })
                check.CheckedChanged += (_, _) => UpdatePreview();
            _cmbKey.SelectedIndexChanged += (_, _) => UpdatePreview();
            UpdatePreview();
            if (!SystemInformation.HighContrast)
            {
                foreach (var button in new[] { _btnOk, _btnCancel })
                {
                    bool primary = button == _btnOk;
                    button.FlatStyle = FlatStyle.Flat;
                    button.UseVisualStyleBackColor = false;
                    button.BackColor = primary ? Color.FromArgb(28, 100, 118) : Color.White;
                    button.ForeColor = primary ? Color.White : Color.FromArgb(40, 52, 65);
                    button.FlatAppearance.BorderColor = primary ? Color.FromArgb(28, 100, 118) : Color.FromArgb(184, 194, 204);
                    button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(23, 83, 99) : Color.FromArgb(237, 243, 247);
                }
            }
            this.AcceptButton = _btnOk;
            this.CancelButton = _btnCancel;
        }
    }
}
