using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MidiToKeyApp
{
    /// <summary>
    /// Windowsのグローバルホットキー（RegisterHotKey / UnregisterHotKey）の登録・解除および検証を管理するクラス。
    /// </summary>
    public class HotkeyManager
    {
        public const int WM_HOTKEY = 0x0312;
        public const int DEFAULT_HOTKEY_ID = 9001;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private IntPtr _hWnd = IntPtr.Zero;
        private int _registeredId = 0;
        private HotkeySettings? _currentRegisteredSettings;

        public bool IsRegistered => _registeredId != 0;
        public HotkeySettings? CurrentRegisteredSettings => _currentRegisteredSettings;

        /// <summary>
        /// ホットキーの修飾キー文字列（"Ctrl+Alt" など）を解析してWin32修飾フラグに変換します。
        /// </summary>
        public static bool TryParseModifiers(string modifiersStr, out uint fsModifiers)
        {
            fsModifiers = MOD_NOREPEAT; // 連続発火防止
            if (string.IsNullOrWhiteSpace(modifiersStr)) return true;

            var parts = modifiersStr.Split(new[] { '+', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var p = part.Trim().ToLowerInvariant();
                switch (p)
                {
                    case "ctrl":
                    case "control":
                        fsModifiers |= MOD_CONTROL;
                        break;
                    case "alt":
                    case "menu":
                        fsModifiers |= MOD_ALT;
                        break;
                    case "shift":
                        fsModifiers |= MOD_SHIFT;
                        break;
                    case "win":
                    case "windows":
                        fsModifiers |= MOD_WIN;
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// ホットキーのキー文字列（"F9" など）から仮想キーコード(VK)を取得します。
        /// </summary>
        public static bool TryGetVkCode(string keyStr, string layout, out uint vkCode)
        {
            vkCode = 0;
            if (string.IsNullOrWhiteSpace(keyStr)) return false;

            var resolved = KeyResolver.Resolve(keyStr, layout);
            if (!resolved.IsValid || resolved.VkCode == 0) return false;

            vkCode = resolved.VkCode;
            return true;
        }

        /// <summary>
        /// ホットキーを登録します。MIDIマッピングとの衝突を事前に検証し、失敗時はロールバックを行います。
        /// </summary>
        public bool TryRegister(
            IntPtr hWnd,
            int id,
            HotkeySettings settings,
            IDictionary<string, string> mapping,
            string layout,
            out string? errorMessage)
        {
            errorMessage = null;

            if (!settings.Enabled)
            {
                Unregister();
                return true;
            }

            // 1. ホットキーとMIDIマッピングの衝突検証
            if (SettingsManager.IsHotkeyConflictingWithMapping(settings, mapping, layout))
            {
                errorMessage = $"ホットキー '{settings.Key}' はMIDIマッピング先のキーとして使用されているため登録できません。";
                return false;
            }

            // F12はシステム予約・デバッガ用のためホットキー候補から除外・拒否
            if (string.Equals(settings.Key?.Trim(), "F12", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "F12キーはWindowsシステム・デバッガ予約キーのためホットキーとして設定できません。";
                return false;
            }

            // 2. 修飾キーとメインキーの検証
            if (!TryParseModifiers(settings.Modifiers, out uint fsModifiers))
            {
                errorMessage = $"修飾キー '{settings.Modifiers}' が無効です。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(settings.Key) || !TryGetVkCode(settings.Key, layout, out uint vkCode))
            {
                errorMessage = $"メインキー '{settings.Key}' が無効です。";
                return false;
            }

            const uint VK_F12 = 0x7B;
            if (vkCode == VK_F12)
            {
                errorMessage = "F12キーはWindowsシステム・デバッガ予約キーのためホットキーとして設定できません。";
                return false;
            }

            // 以前の登録を退避
            var prevSettings = _currentRegisteredSettings;
            int prevId = _registeredId;
            IntPtr prevHwnd = _hWnd;

            // 一旦既存のホットキーを解除
            Unregister();

            // 3. RegisterHotKey呼び出し
            bool success = RegisterHotKey(hWnd, id, fsModifiers, vkCode);
            if (!success)
            {
                int win32Error = Marshal.GetLastWin32Error();
                errorMessage = $"ホットキーの登録に失敗しました (Win32エラー: {win32Error})。\n既に他のアプリケーションで使用されている可能性があります。";

                // ロールバック: 以前の設定が存在し、有効であった場合は復旧を試みる
                if (prevSettings != null && prevSettings.Enabled && prevHwnd != IntPtr.Zero)
                {
                    if (TryParseModifiers(prevSettings.Modifiers, out uint prevMods) &&
                        TryGetVkCode(prevSettings.Key, layout, out uint prevVk))
                    {
                        if (RegisterHotKey(prevHwnd, prevId, prevMods, prevVk))
                        {
                            _hWnd = prevHwnd;
                            _registeredId = prevId;
                            _currentRegisteredSettings = prevSettings;
                            errorMessage += "\n以前の有効なホットキー設定へ復旧しました。";
                        }
                    }
                }

                return false;
            }

            _hWnd = hWnd;
            _registeredId = id;
            _currentRegisteredSettings = new HotkeySettings
            {
                Enabled = settings.Enabled,
                Modifiers = settings.Modifiers,
                Key = settings.Key
            };

            return true;
        }

        /// <summary>
        /// 現在登録されているホットキーを解除します。
        /// </summary>
        public void Unregister()
        {
            if (_hWnd != IntPtr.Zero && _registeredId != 0)
            {
                try
                {
                    UnregisterHotKey(_hWnd, _registeredId);
                }
                catch { }
                finally
                {
                    _hWnd = IntPtr.Zero;
                    _registeredId = 0;
                    _currentRegisteredSettings = null;
                }
            }
        }
    }
}
