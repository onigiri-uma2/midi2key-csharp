using System;
using System.Collections.Generic;
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
        /// </summary>
        private void SendRawKey(VirtualKeyCode vk, bool isDown)
        {
            ushort scanCode = (ushort)MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);

            uint flags = KEYEVENTF_SCANCODE;
            if (!isDown) flags |= KEYEVENTF_KEYUP;

            // 拡張キーの判定（矢印キーなど）
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
                    flags |= KEYEVENTF_EXTENDEDKEY;
                    break;
            }

            INPUT input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.U.ki.wVk = 0; // スキャンコード指定時は wVk を0にするのがDirectInput対策として有効
            input.U.ki.wScan = scanCode;
            input.U.ki.dwFlags = flags;
            input.U.ki.time = 0;
            input.U.ki.dwExtraInfo = IntPtr.Zero;

            SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>
        /// 指定されたキー文字に対応するキーコードをOSに送信します。
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
            }

            if (baseKey != VirtualKeyCode.NONAME)
            {
                if (isDown)
                {
                    if (shiftRequired) SendRawKey(VirtualKeyCode.SHIFT, true);
                    SendRawKey(baseKey, true);
                }
                else
                {
                    SendRawKey(baseKey, false);
                    if (shiftRequired) SendRawKey(VirtualKeyCode.SHIFT, false);
                }
            }
        }

        private VirtualKeyCode ParseKey(string key, bool isJis)
        {
            key = key.ToLowerInvariant();
            
            if (Enum.TryParse<VirtualKeyCode>(key, true, out var vk)) return vk;
            
            if (key.Length == 1 && key[0] >= 'a' && key[0] <= 'z') return (VirtualKeyCode)((int)VirtualKeyCode.VK_A + (key[0] - 'a'));
            if (key.Length == 1 && key[0] >= '0' && key[0] <= '9') return (VirtualKeyCode)((int)VirtualKeyCode.VK_0 + (key[0] - '0'));

            return key switch
            {
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
