using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using WindowsInput.Native; // Just used for VirtualKeyCode Enum

namespace MidiToKeyApp
{
    /// <summary>
    /// OSレベルでの仮想キーボード入力をシミュレートするクラス。
    /// Windows APIのSendInputとHardware Scan Codeを直接叩くことで、
    /// DirectInputを利用するPCゲームなどへの入力にも確実に対応します。
    /// </summary>
    public class KeySimulator
    {
        #region Win32 API Definitions
        
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        private const uint MAPVK_VK_TO_VSC = 0;
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

        #endregion

        /// <summary>
        /// JIS（日本語）キーボード特有の、Shiftキーを押しながら入力する記号のマッピングテーブル。
        /// </summary>
        private Dictionary<string, (VirtualKeyCode, bool)> _jisShiftMap = new Dictionary<string, (VirtualKeyCode, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            { "!", (VirtualKeyCode.VK_1, true) },
            { "\"", (VirtualKeyCode.VK_2, true) },
            { "#", (VirtualKeyCode.VK_3, true) },
            { "$", (VirtualKeyCode.VK_4, true) },
            { "%", (VirtualKeyCode.VK_5, true) },
            { "&", (VirtualKeyCode.VK_6, true) },
            { "'", (VirtualKeyCode.VK_7, true) },
            { "(", (VirtualKeyCode.VK_8, true) },
            { ")", (VirtualKeyCode.VK_9, true) },
            { "=", (VirtualKeyCode.OEM_MINUS, true) },
            { "~", (VirtualKeyCode.OEM_7, true) }, // caret key on JIS
            { "|", (VirtualKeyCode.OEM_5, true) }, // yen key on JIS
            { "`", (VirtualKeyCode.OEM_3, true) }, // @ key on JIS
            { "{", (VirtualKeyCode.OEM_4, true) }, // [ key
            { "+", (VirtualKeyCode.OEM_PLUS, true) }, // ; key on JIS
            { "*", (VirtualKeyCode.OEM_1, true) }, // : key on JIS
            { "}", (VirtualKeyCode.OEM_6, true) }, // ] key
            { "<", (VirtualKeyCode.OEM_COMMA, true) },
            { ">", (VirtualKeyCode.OEM_PERIOD, true) },
            { "?", (VirtualKeyCode.OEM_2, true) }, // / key
            { "_", (VirtualKeyCode.OEM_102, true) } // ro key
        };

        /// <summary>
        /// US（英語）キーボード特有の、Shiftキーを押しながら入力する記号のマッピングテーブル。
        /// </summary>
        private Dictionary<string, (VirtualKeyCode, bool)> _usShiftMap = new Dictionary<string, (VirtualKeyCode, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            { "!", (VirtualKeyCode.VK_1, true) },
            { "@", (VirtualKeyCode.VK_2, true) },
            { "#", (VirtualKeyCode.VK_3, true) },
            { "$", (VirtualKeyCode.VK_4, true) },
            { "%", (VirtualKeyCode.VK_5, true) },
            { "^", (VirtualKeyCode.VK_6, true) },
            { "&", (VirtualKeyCode.VK_7, true) },
            { "*", (VirtualKeyCode.VK_8, true) },
            { "(", (VirtualKeyCode.VK_9, true) },
            { ")", (VirtualKeyCode.VK_0, true) },
            { "_", (VirtualKeyCode.OEM_MINUS, true) },
            { "+", (VirtualKeyCode.OEM_PLUS, true) },
            { "{", (VirtualKeyCode.OEM_4, true) },
            { "}", (VirtualKeyCode.OEM_6, true) },
            { "|", (VirtualKeyCode.OEM_5, true) },
            { ":", (VirtualKeyCode.OEM_1, true) },
            { "\"", (VirtualKeyCode.OEM_7, true) },
            { "<", (VirtualKeyCode.OEM_COMMA, true) },
            { ">", (VirtualKeyCode.OEM_PERIOD, true) },
            { "?", (VirtualKeyCode.OEM_2, true) },
            { "~", (VirtualKeyCode.OEM_3, true) }
        };

        /// <summary>
        /// 物理キー送信コアメソッド。対象キーコードからスキャンコードを算出して送信します。
        /// スキャンコードが取得できないキーについては仮想キーコード直接送信にフォールバックします。
        /// </summary>
        private void SendRawKey(VirtualKeyCode vk, bool isDown)
        {
            ushort scanCode = (ushort)MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);

            uint flags = 0;
            if (!isDown) flags |= KEYEVENTF_KEYUP;

            INPUT input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.U.ki.time = 0;
            input.U.ki.dwExtraInfo = IntPtr.Zero;

            if (scanCode != 0)
            {
                // ハードウェアスキャンコードで送信（DirectInput・ゲーム対応）
                flags |= KEYEVENTF_SCANCODE;

                // 拡張キーの判定（矢印キー、テンキー記号等）
                switch (vk)
                {
                    case VirtualKeyCode.UP:
                    case VirtualKeyCode.DOWN:
                    case VirtualKeyCode.LEFT:
                    case VirtualKeyCode.RIGHT:
                    case VirtualKeyCode.HOME:
                    case VirtualKeyCode.END:
                    case VirtualKeyCode.PRIOR:
                    case VirtualKeyCode.NEXT:
                    case VirtualKeyCode.INSERT:
                    case VirtualKeyCode.DELETE:
                    case VirtualKeyCode.DIVIDE:
                    case VirtualKeyCode.RCONTROL:
                    case VirtualKeyCode.RMENU:
                        flags |= KEYEVENTF_EXTENDEDKEY;
                        break;
                }

                input.U.ki.wVk = 0; // スキャンコード指定時は wVk を0にするのがDirectInput対策として有効
                input.U.ki.wScan = scanCode;
                input.U.ki.dwFlags = flags;
            }
            else
            {
                // スキャンコードが存在しない特殊仮想キーへの安全なフォールバック
                input.U.ki.wVk = (ushort)vk;
                input.U.ki.wScan = 0;
                input.U.ki.dwFlags = flags;
            }

            SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        // キーごとの押下回数を管理する辞書（同じキーの重複押下や和音演奏時に途中でキーが離されるのを防止）
        private readonly Dictionary<VirtualKeyCode, int> _keyRefCount = new Dictionary<VirtualKeyCode, int>();
        // Shiftキー専用の参照カウント
        private int _shiftRefCount = 0;
        private readonly object _keyLock = new object();

        /// <summary>
        /// 指定されたキー文字に対応するキーコードをOSに送信します。
        /// 参照カウントにより、和音や連打時にもキーが途中で誤って離されないよう制御します。
        /// </summary>
        public void SendKey(string keyName, bool isDown, string layout)
        {
            if (string.IsNullOrEmpty(keyName)) return;
            
            bool isJis = layout == "JIS";
            var targetMap = isJis ? _jisShiftMap : _usShiftMap;

            VirtualKeyCode baseKey = VirtualKeyCode.NONAME;
            bool shiftRequired = false;

            if (targetMap.ContainsKey(keyName))
            {
                var entry = targetMap[keyName];
                baseKey = entry.Item1;
                shiftRequired = entry.Item2;
            }
            else
            {
                baseKey = ParseKey(keyName, isJis);
                // 大文字のアルファベットの場合、Shiftキーが必要
                if (keyName.Length == 1 && char.IsUpper(keyName[0]))
                {
                    shiftRequired = true;
                }
            }

            if (baseKey == VirtualKeyCode.NONAME) return;

            lock (_keyLock)
            {
                if (isDown)
                {
                    // Shiftキーの押下管理（0→1になった時のみ物理KeyDown）
                    if (shiftRequired)
                    {
                        if (_shiftRefCount == 0)
                        {
                            SendRawKey(VirtualKeyCode.SHIFT, true);
                        }
                        _shiftRefCount++;
                    }

                    // ベースキーの押下管理（0→1になった時のみ物理KeyDown）
                    _keyRefCount.TryGetValue(baseKey, out int count);
                    if (count == 0)
                    {
                        SendRawKey(baseKey, true);
                    }
                    _keyRefCount[baseKey] = count + 1;
                }
                else
                {
                    // ベースキーの解放管理（カウントが0になった時のみ物理KeyUp）
                    if (_keyRefCount.TryGetValue(baseKey, out int count) && count > 0)
                    {
                        count--;
                        if (count == 0)
                        {
                            SendRawKey(baseKey, false);
                            _keyRefCount.Remove(baseKey);
                        }
                        else
                        {
                            _keyRefCount[baseKey] = count;
                        }
                    }
                    else
                    {
                        SendRawKey(baseKey, false);
                        _keyRefCount.Remove(baseKey);
                    }

                    // Shiftキーの解放管理（カウントが0になった時のみ物理KeyUp）
                    if (shiftRequired)
                    {
                        if (_shiftRefCount > 0) _shiftRefCount--;
                        if (_shiftRefCount == 0)
                        {
                            SendRawKey(VirtualKeyCode.SHIFT, false);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 現在押下状態にあるすべてのキー（Shiftを含む）を強制的に解放します。
        /// 変換停止時やアプリ終了時にキーがOS上で押しっぱなしになるのを完全に防止します。
        /// </summary>
        public void ReleaseAllKeys()
        {
            lock (_keyLock)
            {
                foreach (var vk in _keyRefCount.Keys.ToList())
                {
                    SendRawKey(vk, false);
                }
                _keyRefCount.Clear();

                if (_shiftRefCount > 0)
                {
                    SendRawKey(VirtualKeyCode.SHIFT, false);
                    _shiftRefCount = 0;
                }
            }
        }

        private VirtualKeyCode ParseKey(string key, bool isJis)
        {
            key = key.ToLowerInvariant();
            
            // 1文字の数字（0-9）と英字（a-z）を Enum.TryParse より先に判定する
            // ※ Enum.TryParse に "1" などを渡すと数値文字列として扱われ、
            //    (VirtualKeyCode)1 （= VK_LBUTTON マウス左ボタン）等に誤変換されてキー入力が効かなくなるのを防ぐため
            if (key.Length == 1 && key[0] >= '0' && key[0] <= '9') return (VirtualKeyCode)((int)VirtualKeyCode.VK_0 + (key[0] - '0'));
            if (key.Length == 1 && key[0] >= 'a' && key[0] <= 'z') return (VirtualKeyCode)((int)VirtualKeyCode.VK_A + (key[0] - 'a'));

            if (Enum.TryParse<VirtualKeyCode>(key, true, out var vk)) return vk;

            return key switch
            {
                "esc" => VirtualKeyCode.ESCAPE,
                "escape" => VirtualKeyCode.ESCAPE,
                "pageup" => VirtualKeyCode.PRIOR,
                "pagedown" => VirtualKeyCode.NEXT,
                "tab" => VirtualKeyCode.TAB,
                "enter" => VirtualKeyCode.RETURN,
                "space" => VirtualKeyCode.SPACE,
                "shift" => VirtualKeyCode.SHIFT,
                "ctrl" => VirtualKeyCode.CONTROL,
                "alt" => VirtualKeyCode.MENU,
                "backspace" => VirtualKeyCode.BACK,
                ";" => isJis ? VirtualKeyCode.OEM_PLUS : VirtualKeyCode.OEM_1,
                ":" => VirtualKeyCode.OEM_1,
                "," => VirtualKeyCode.OEM_COMMA,
                "." => VirtualKeyCode.OEM_PERIOD,
                "/" => VirtualKeyCode.OEM_2,
                "-" => VirtualKeyCode.OEM_MINUS,
                "=" => VirtualKeyCode.OEM_PLUS,
                "[" => VirtualKeyCode.OEM_4,
                "]" => VirtualKeyCode.OEM_6,
                "\\" => VirtualKeyCode.OEM_5,
                "'" => VirtualKeyCode.OEM_7,
                "`" => VirtualKeyCode.OEM_3,
                "@" => VirtualKeyCode.OEM_3,
                "^" => VirtualKeyCode.OEM_7,
                "yen" => VirtualKeyCode.OEM_5,
                "ro" => VirtualKeyCode.OEM_102,
                
                // 特殊・日本語IMEキー
                "capslock" => VirtualKeyCode.CAPITAL,
                "zenkaku_hankaku" => VirtualKeyCode.KANJI,
                "henkan" => VirtualKeyCode.CONVERT,
                "muhenkan" => VirtualKeyCode.NONCONVERT,
                "hiragana" => VirtualKeyCode.KANA,
                "eisuy" => (VirtualKeyCode)240,
                
                _ => VirtualKeyCode.NONAME
            };
        }
    }
}
