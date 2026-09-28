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
            // ※ "1" が (VirtualKeyCode)1 (= VK_LBUTTON マウス左ボタン) に誤変換されるのを防止
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
    /// Shiftキーのロールバック保証、KeyUp失敗時の状態保持と再試行をサポートします。
    /// </summary>
    public class KeySimulator
    {
        private readonly IKeyboardOutput _output;
        private readonly Dictionary<VirtualKeyCode, int> _keyRefCount = new();
        // 物理KeyUp送信に失敗し、OS上で物理KeyDownのままになっているキーの追跡セット
        private readonly HashSet<VirtualKeyCode> _unreleasedKeys = new();
        private readonly object _keyLock = new();

        public KeySimulator(IKeyboardOutput? output = null)
        {
            _output = output ?? new WindowsKeyboardOutput();
        }

        /// <summary>
        /// 指定された解決済みキー（BaseKeyおよびShift要求）を押下状態にします。
        /// ベースキー送信失敗時は、直前に追加したShift参照を必ずロールバックします。
        /// </summary>
        public bool PressResolvedKey(ResolvedKey key)
        {
            if (!key.IsValid) return false;

            lock (_keyLock)
            {
                bool shiftRefAdded = false;

                // Shiftが必要な場合、先にShiftの参照を追加
                if (key.ShiftRequired)
                {
                    if (!SendInternal(VirtualKeyCode.SHIFT, true, out _))
                    {
                        return false;
                    }
                    shiftRefAdded = true;
                }

                // ベースキーを押下
                if (!SendInternal(key.BaseKey, true, out _))
                {
                    // ベースキー送信失敗時は、今回追加したShift参照を必ず取り消す！
                    // （以前からShiftが押されていた場合でもカウントを1減らし、新規押下だった場合はKeyUpも送信）
                    if (shiftRefAdded)
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
        /// 単一の仮想キーコードの参照カウント管理と物理キー送信を行います。
        /// KeyUp送信失敗時は未解放状態として記録し、キーの追跡を維持します。
        /// </summary>
        private bool SendInternal(VirtualKeyCode vk, bool isDown, out bool stateChanged)
        {
            stateChanged = false;
            _keyRefCount.TryGetValue(vk, out int count);

            if (isDown)
            {
                if (count == 0)
                {
                    // もし以前のKeyUpが失敗してOS上で未解放のまま残っている場合
                    if (_unreleasedKeys.Contains(vk))
                    {
                        // 物理的には既に押下状態なので、再送信せずに未解放追跡から論理参照へ復旧
                        _unreleasedKeys.Remove(vk);
                        _keyRefCount[vk] = 1;
                        stateChanged = true;
                        return true;
                    }

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
                    stateChanged = true;
                    return true;
                }
            }
            else
            {
                if (count <= 0)
                {
                    // 押されていないキーに対する不要なKeyUpは送信しない（負数防止）
                    return false;
                }
                else if (count == 1)
                {
                    // 1 → 0: 物理KeyUpを送信
                    _keyRefCount.Remove(vk);
                    stateChanged = true;

                    if (!_output.SendHardwareKey(vk, false))
                    {
                        // KeyUp送信失敗！OS側で押下状態のままになっているため、未解放キーとして追跡
                        _unreleasedKeys.Add(vk);
                        return false;
                    }
                    else
                    {
                        _unreleasedKeys.Remove(vk);
                        return true;
                    }
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
        /// KeyUp送信に失敗して未解放状態にあるキーの解放を再試行します。
        /// </summary>
        public bool RetryReleasePendingKeys()
        {
            lock (_keyLock)
            {
                foreach (var vk in _unreleasedKeys.ToList())
                {
                    if (_output.SendHardwareKey(vk, false))
                    {
                        _unreleasedKeys.Remove(vk);
                    }
                }
                return _unreleasedKeys.Count == 0;
            }
        }

        /// <summary>
        /// 現在押下状態にあるすべてのキー（未解放キーを含む）を物理的に解放します。
        /// </summary>
        /// <returns>全キーの解放に成功した場合はtrue、失敗したキーが残っている場合はfalse</returns>
        public bool ReleaseAllKeys()
        {
            lock (_keyLock)
            {
                var keysToRelease = new HashSet<VirtualKeyCode>(_keyRefCount.Keys);
                foreach (var vk in _unreleasedKeys)
                {
                    keysToRelease.Add(vk);
                }

                _keyRefCount.Clear();

                foreach (var vk in keysToRelease)
                {
                    if (_output.SendHardwareKey(vk, false))
                    {
                        _unreleasedKeys.Remove(vk);
                    }
                    else
                    {
                        _unreleasedKeys.Add(vk);
                    }
                }

                return _unreleasedKeys.Count == 0;
            }
        }

        /// <summary>
        /// 従来のSendKeyメソッド（互換用）。
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

        /// <summary>
        /// テスト用: 指定キーが未解放（KeyUp失敗）状態として追跡されているか確認します。
        /// </summary>
        public bool IsKeyUnreleased(VirtualKeyCode vk)
        {
            lock (_keyLock)
            {
                return _unreleasedKeys.Contains(vk);
            }
        }

        /// <summary>
        /// 未解放キーが存在するかどうかを取得します。
        /// </summary>
        public bool HasUnreleasedKeys
        {
            get
            {
                lock (_keyLock)
                {
                    return _unreleasedKeys.Count > 0;
                }
            }
        }

        /// <summary>
        /// 現在追跡されている未解放キーの個数を取得します。
        /// </summary>
        public int UnreleasedKeysCount
        {
            get
            {
                lock (_keyLock)
                {
                    return _unreleasedKeys.Count;
                }
            }
        }

        /// <summary>
        /// 変換開始前の準備として未解放キーの解放を再試行し、開始可能かどうかを判定します。
        /// すべての未解放キーが解放された場合はtrue、未解放キーが残っている場合はfalseを返します。
        /// </summary>
        public bool TryPrepareStartConversion()
        {
            lock (_keyLock)
            {
                if (_unreleasedKeys.Count == 0) return true;
                RetryReleasePendingKeys();
                return _unreleasedKeys.Count == 0;
            }
        }

        /// <summary>
        /// テスト用: 未解放キーのコレクションを取得します。
        /// </summary>
        public IReadOnlyCollection<VirtualKeyCode> UnreleasedKeys
        {
            get
            {
                lock (_keyLock)
                {
                    return _unreleasedKeys.ToList();
                }
            }
        }
    }
}
