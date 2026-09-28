using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using WindowsInput.Native;

namespace MidiToKeyApp
{
    /// <summary>
    /// キーボード出力を抽象化するインターフェース。
    /// 実機Win32 API送信と、単体テスト用のモック出力を切り替え可能にします。
    /// </summary>
    public interface IKeyboardOutput
    {
        /// <summary>
        /// 物理キーを送信します。
        /// </summary>
        /// <param name="vk">仮想キーコード</param>
        /// <param name="isDown">押下時はtrue、解放時はfalse</param>
        /// <returns>送信が成功した場合はtrue、失敗した場合はfalse</returns>
        bool SendHardwareKey(VirtualKeyCode vk, bool isDown);
    }

    /// <summary>
    /// Windows APIのSendInputとHardware Scan Codeを使用して物理キー入力を送信する本番用実装クラス。
    /// </summary>
    public class WindowsKeyboardOutput : IKeyboardOutput
    {
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

        public bool SendHardwareKey(VirtualKeyCode vk, bool isDown)
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

                // 拡張キーの判定
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

                input.U.ki.wVk = 0;
                input.U.ki.wScan = scanCode;
                input.U.ki.dwFlags = flags;
            }
            else
            {
                // スキャンコードが存在しないキーへの安全なフォールバック
                input.U.ki.wVk = (ushort)vk;
                input.U.ki.wScan = 0;
                input.U.ki.dwFlags = flags;
            }

            uint result = SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT)));
            return result == 1;
        }
    }

    /// <summary>
    /// キー文字列から物理キーと修飾キー要求を解決した結果を表す不変レコード。
    /// </summary>
    public readonly record struct ResolvedKey(VirtualKeyCode BaseKey, bool ShiftRequired)
    {
        public bool IsValid => BaseKey != VirtualKeyCode.NONAME;
        public static readonly ResolvedKey None = new ResolvedKey(VirtualKeyCode.NONAME, false);
    }

    /// <summary>
    /// 文字列で表現されたキー名および配列種別（JIS/US）から、対応する仮想キーコードとShift要求を解決するユーティリティ。
    /// </summary>
    public static class KeyResolver
    {
        private static readonly Dictionary<string, (VirtualKeyCode, bool)> JisShiftMap = new()
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
            { "~", (VirtualKeyCode.OEM_7, true) },
            { "|", (VirtualKeyCode.OEM_5, true) },
            { "`", (VirtualKeyCode.OEM_3, false) },
            { "{", (VirtualKeyCode.OEM_4, true) },
            { "+", (VirtualKeyCode.OEM_PLUS, true) },
            { "*", (VirtualKeyCode.OEM_1, true) },
            { "}", (VirtualKeyCode.OEM_6, true) },
            { "<", (VirtualKeyCode.OEM_COMMA, true) },
            { ">", (VirtualKeyCode.OEM_PERIOD, true) },
            { "?", (VirtualKeyCode.OEM_2, true) },
            { "_", (VirtualKeyCode.OEM_102, true) }
        };

        private static readonly Dictionary<string, (VirtualKeyCode, bool)> UsShiftMap = new()
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

        public static ResolvedKey Resolve(string? keyName, string layout)
        {
            if (string.IsNullOrEmpty(keyName)) return ResolvedKey.None;

            bool isJis = layout == "JIS";
            var shiftMap = isJis ? JisShiftMap : UsShiftMap;

            if (shiftMap.TryGetValue(keyName, out var entry))
            {
                return new ResolvedKey(entry.Item1, entry.Item2);
            }

            var baseKey = ParseKey(keyName, isJis);
            bool shiftRequired = false;
            if (keyName.Length == 1 && char.IsUpper(keyName[0]))
            {
                shiftRequired = true;
            }

            return new ResolvedKey(baseKey, shiftRequired);
        }

        public static VirtualKeyCode ParseKey(string key, bool isJis)
        {
            key = key.ToLowerInvariant();

            // 1文字の数字（0-9）と英字（a-z）を Enum.TryParse より先に判定
            // ※ "1" が (VirtualKeyCode)1 (= VK_LBUTTON マウス左ボタン) に誤変換されるのを完全に防止
            if (key.Length == 1 && key[0] >= '0' && key[0] <= '9') return (VirtualKeyCode)((int)VirtualKeyCode.VK_0 + (key[0] - '0'));
            if (key.Length == 1 && key[0] >= 'a' && key[0] <= 'z') return (VirtualKeyCode)((int)VirtualKeyCode.VK_A + (key[0] - 'a'));

            if (Enum.TryParse<VirtualKeyCode>(key, true, out var vk)) return vk;

            return key switch
            {
                "esc" => VirtualKeyCode.ESCAPE,
                "escape" => VirtualKeyCode.ESCAPE,
                "pageup" => VirtualKeyCode.PRIOR,
                "pgup" => VirtualKeyCode.PRIOR,
                "pagedown" => VirtualKeyCode.NEXT,
                "pgdn" => VirtualKeyCode.NEXT,
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

    /// <summary>
    /// キーボード入力をシミュレートし、全キーの押下状態を一元化された参照カウントで管理するクラス。
    /// Shiftキーも例外扱いせず同一カウントで管理し、和音・連打・重複押下時のキー消失を防止します。
    /// </summary>
    public class KeySimulator
    {
        private readonly IKeyboardOutput _output;
        private readonly Dictionary<VirtualKeyCode, int> _keyRefCount = new();
        private readonly object _keyLock = new();

        public KeySimulator(IKeyboardOutput? output = null)
        {
            _output = output ?? new WindowsKeyboardOutput();
        }

        /// <summary>
        /// 指定された解決済みキー（BaseKeyおよびShift要求）を押下状態にします。
        /// 参照カウントが0→1になったキーのみ物理KeyDownを送信します。
        /// 送信失敗時はカウントを増やさず、直前のShift要求もロールバックします。
        /// </summary>
        public bool PressResolvedKey(ResolvedKey key)
        {
            if (!key.IsValid) return false;

            lock (_keyLock)
            {
                bool shiftPressedHere = false;

                // Shiftが必要な場合、先にShiftを押下
                if (key.ShiftRequired)
                {
                    if (!SendInternal(VirtualKeyCode.SHIFT, true, out bool stateChanged))
                    {
                        return false;
                    }
                    shiftPressedHere = stateChanged;
                }

                // ベースキーを押下
                if (!SendInternal(key.BaseKey, true, out _))
                {
                    // ベースキー送信失敗時のロールバック
                    if (key.ShiftRequired && shiftPressedHere)
                    {
                        SendInternal(VirtualKeyCode.SHIFT, false, out _);
                    }
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// 指定された解決済みキー（BaseKeyおよびShift要求）を解放状態にします。
        /// 参照カウントが1→0になったキーのみ物理KeyUpを送信します。
        /// </summary>
        public bool ReleaseResolvedKey(ResolvedKey key)
        {
            if (!key.IsValid) return false;

            lock (_keyLock)
            {
                // ベースキーを解放
                SendInternal(key.BaseKey, false, out _);

                // Shiftを解放
                if (key.ShiftRequired)
                {
                    SendInternal(VirtualKeyCode.SHIFT, false, out _);
                }

                return true;
            }
        }

        /// <summary>
        /// 内部で単一の仮想キーコードの参照カウント管理と物理キー送信を行います。
        /// </summary>
        private bool SendInternal(VirtualKeyCode vk, bool isDown, out bool stateChanged)
        {
            stateChanged = false;
            _keyRefCount.TryGetValue(vk, out int count);

            if (isDown)
            {
                if (count == 0)
                {
                    // 0 → 1: 物理KeyDownを送信
                    if (!_output.SendHardwareKey(vk, true))
                    {
                        return false; // 送信失敗時はカウントを増やさない
                    }
                    _keyRefCount[vk] = 1;
                    stateChanged = true;
                    return true;
                }
                else
                {
                    // 既に押下中: カウントのみ加算
                    _keyRefCount[vk] = count + 1;
                    return true;
                }
            }
            else
            {
                if (count <= 0)
                {
                    // 押されていないキーに対する不要なKeyUpは送信しない（負数防止）
                    _keyRefCount.Remove(vk);
                    return false;
                }
                else if (count == 1)
                {
                    // 1 → 0: 物理KeyUpを送信
                    _keyRefCount.Remove(vk);
                    stateChanged = true;
                    return _output.SendHardwareKey(vk, false);
                }
                else
                {
                    // 他のノート等によりまだ保持中: カウントのみ減算
                    _keyRefCount[vk] = count - 1;
                    return true;
                }
            }
        }

        /// <summary>
        /// 現在押下状態にあるすべてのキーを物理的に解放し、参照カウントを初期化します。
        /// </summary>
        public void ReleaseAllKeys()
        {
            lock (_keyLock)
            {
                foreach (var vk in _keyRefCount.Keys.ToList())
                {
                    _output.SendHardwareKey(vk, false);
                }
                _keyRefCount.Clear();
            }
        }

        /// <summary>
        /// 従来のSendKeyメソッド（互換用）。キー文字列と配列からキーを解決して押下/解放します。
        /// </summary>
        public void SendKey(string keyName, bool isDown, string layout)
        {
            var resolved = KeyResolver.Resolve(keyName, layout);
            if (!resolved.IsValid) return;

            if (isDown)
                PressResolvedKey(resolved);
            else
                ReleaseResolvedKey(resolved);
        }

        /// <summary>
        /// テスト用: 指定キーの現在の参照カウントを取得します。
        /// </summary>
        public int GetRefCount(VirtualKeyCode vk)
        {
            lock (_keyLock)
            {
                return _keyRefCount.TryGetValue(vk, out int count) ? count : 0;
            }
        }
    }
}
