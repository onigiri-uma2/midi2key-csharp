using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MidiToKeyApp
{
    /// <summary>
    /// アプリケーションの設定データを保持するクラス。
    /// JSONからデシリアライズ、およびJSONへシリアライズされるデータ構造を定義します。
    /// </summary>
    /// <summary>
    /// グローバルトグルホットキーの設定。
    /// </summary>
    public class HotkeySettings
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("modifiers")]
        public string Modifiers { get; set; } = "Ctrl+Alt";

        [JsonPropertyName("key")]
        public string Key { get; set; } = "F9";
    }

    /// <summary>
    /// アプリケーションの設定データを保持するクラス。
    /// JSONからデシリアライズ、およびJSONへシリアライズされるデータ構造を定義します。
    /// </summary>
    public class AppSettings
    {
        [JsonIgnore]
        public readonly object MappingLock = new object();

        [JsonPropertyName("selected_ports")]
        public List<string> SelectedPorts { get; set; } = new List<string>();

        [JsonPropertyName("mapping")]
        public Dictionary<string, string> Mapping { get; set; } = new Dictionary<string, string>();

        [JsonPropertyName("keyboard_layout")]
        public string KeyboardLayout { get; set; } = "JIS";

        [JsonPropertyName("hotkey")]
        public HotkeySettings Hotkey { get; set; } = new HotkeySettings();
    }

    /// <summary>
    /// 設定ファイル（settings.json）の読み込み、検証、および原子的保存を管理する静的ユーティリティクラス。
    /// </summary>
    public static class SettingsManager
    {
        /// <summary>
        /// 初回起動時や設定復旧用のデフォルト設定（Sky用15キー標準マッピング）を生成します。
        /// </summary>
        public static AppSettings GetDefaultSettings()
        {
            var defaultSettings = new AppSettings();
            defaultSettings.Mapping = new Dictionary<string, string>
            {
                { "48", "y" }, { "50", "u" }, { "52", "i" }, { "53", "o" }, { "55", "p" },
                { "57", "h" }, { "59", "j" }, { "60", "k" }, { "62", "l" }, { "64", ";" },
                { "65", "n" }, { "67", "m" }, { "69", "," }, { "71", "." }, { "72", "/" }
            };
            defaultSettings.Hotkey = new HotkeySettings();
            return defaultSettings;
        }

        public static AppSettings Load(string path)
        {
            if (!File.Exists(path))
            {
                return GetDefaultSettings();
            }

            string json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var settings = JsonSerializer.Deserialize<AppSettings>(json, options);

            if (settings == null)
            {
                throw new InvalidDataException("設定データがnullです。");
            }

            if (settings.Hotkey == null)
            {
                settings.Hotkey = new HotkeySettings();
            }

            NormalizeMapping(settings);
            ValidateSettings(settings);

            // ホットキーのメインキーとマッピング先キーの衝突を検証
            // 衝突が検出された場合はマッピングを優先し、ホットキーを無効化して安全に復旧
            if (settings.Hotkey.Enabled && IsHotkeyConflictingWithMapping(settings.Hotkey, settings.Mapping, settings.KeyboardLayout))
            {
                Console.WriteLine($"警告: ホットキー '{settings.Hotkey.Key}' がMIDIマッピング先と衝突しています。安全のためホットキーを無効化します。");
                settings.Hotkey.Enabled = false;
            }

            return settings;
        }

        /// <summary>
        /// PEDAL等の大文字表記を小文字のpedalへ正規化します。
        /// </summary>
        public static void NormalizeMapping(AppSettings settings)
        {
            if (settings.Mapping == null) return;

            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in settings.Mapping)
            {
                string key = kvp.Key;
                if (key.Equals("pedal", StringComparison.OrdinalIgnoreCase))
                {
                    key = "pedal";
                }
                normalized[key] = kvp.Value;
            }
            settings.Mapping = new Dictionary<string, string>(normalized);
        }

        /// <summary>
        /// ホットキーのメインキーがマッピング先に含まれているか衝突判定を行います。
        /// </summary>
        public static bool IsHotkeyConflictingWithMapping(HotkeySettings hotkey, IDictionary<string, string> mapping, string layout)
        {
            if (!hotkey.Enabled || string.IsNullOrWhiteSpace(hotkey.Key)) return false;

            var hotkeyResolved = KeyResolver.Resolve(hotkey.Key, layout);
            if (!hotkeyResolved.IsValid) return false;

            foreach (var kvp in mapping)
            {
                var mapResolved = KeyResolver.Resolve(kvp.Value, layout);
                if (mapResolved.IsValid && mapResolved.VkCode == hotkeyResolved.VkCode)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 読み込んだ設定オブジェクトの内容がアプリケーション仕様を満たしているか厳格に検証します。
        /// </summary>
        public static void ValidateSettings(AppSettings? settings)
        {
            if (settings == null)
            {
                throw new InvalidDataException("設定データがnullです。");
            }

            if (settings.SelectedPorts == null)
            {
                throw new InvalidDataException("selected_ports が指定されていません (null)。");
            }

            if (settings.Mapping == null)
            {
                throw new InvalidDataException("mapping が指定されていません (null)。");
            }

            if (settings.KeyboardLayout != "JIS" && settings.KeyboardLayout != "US")
            {
                throw new InvalidDataException($"無効なキーボード配列 '{settings.KeyboardLayout}' です。'JIS' または 'US' を指定してください。");
            }

            foreach (var kvp in settings.Mapping)
            {
                string noteKey = kvp.Key;
                string targetKey = kvp.Value;

                // キー名の検証: "pedal" または 0〜127 の数値
                if (!noteKey.Equals("pedal", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(noteKey, out int noteNum) || noteNum < 0 || noteNum > 127)
                    {
                        throw new InvalidDataException($"無効なマッピングキー '{noteKey}' です。0〜127の数値または 'pedal' である必要があります。");
                    }
                }

                // 変換先キーの厳格な検証
                if (!KeyResolver.IsValidTargetKey(targetKey, settings.KeyboardLayout))
                {
                    throw new InvalidDataException($"マッピングキー '{noteKey}' の変換先キー '{targetKey}' が無効です。");
                }
            }
        }

        /// <summary>
        /// 設定オブジェクトを一時ファイルへ書き込み、原子的置換（File.Replace / File.Move）により安全に保存します。
        /// </summary>
        public static void Save(string path, AppSettings settings)
        {
            ValidateSettings(settings);

            // 排他ロック下でマッピング辞書のスナップショットを作成
            AppSettings snapshot;
            lock (settings.MappingLock)
            {
                snapshot = new AppSettings
                {
                    SelectedPorts = new List<string>(settings.SelectedPorts),
                    Mapping = new Dictionary<string, string>(settings.Mapping),
                    KeyboardLayout = settings.KeyboardLayout,
                    Hotkey = new HotkeySettings
                    {
                        Enabled = settings.Hotkey?.Enabled ?? true,
                        Modifiers = settings.Hotkey?.Modifiers ?? "Ctrl+Alt",
                        Key = settings.Hotkey?.Key ?? "F9"
                    }
                };
            }

            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            
            string json = JsonSerializer.Serialize(snapshot, options);

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string actualDir = string.IsNullOrEmpty(dir) ? AppDomain.CurrentDomain.BaseDirectory : dir;
            string fileName = Path.GetFileName(path);
            string tempFile = Path.Combine(actualDir, $"{fileName}.tmp.{Guid.NewGuid():N}");

            try
            {
                // 一時ファイルへの完全な書き込み
                File.WriteAllText(tempFile, json);

                // 原子的置換
                if (File.Exists(path))
                {
                    File.Replace(tempFile, path, null);
                }
                else
                {
                    File.Move(tempFile, path);
                }
            }
            finally
            {
                // 一時ファイルが残っていれば安全に削除
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }
    }
}
