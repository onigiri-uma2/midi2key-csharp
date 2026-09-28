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
    }

    /// <summary>
    /// 設定ファイル（settings.json）の読み込み、検証、および原子的保存を管理する静的ユーティリティクラス。
    /// </summary>
    public static class SettingsManager
    {
        /// <summary>
        /// 指定されたパスから設定を読み込み、内容を検証します。
        /// ファイルが存在しない場合は初回起動用デフォルト設定を返し、破損または内容が不正な場合は例外をスローします。
        /// </summary>
        /// <exception cref="JsonException">JSON形式が不正な場合</exception>
        /// <exception cref="InvalidDataException">データ項目や値が不正な場合</exception>
        public static AppSettings Load(string path)
        {
            if (!File.Exists(path))
            {
                var defaultSettings = new AppSettings();
                defaultSettings.Mapping = new Dictionary<string, string>
                {
                    { "48", "y" }, { "50", "u" }, { "52", "i" }, { "53", "o" }, { "55", "p" },
                    { "57", "h" }, { "59", "j" }, { "60", "k" }, { "62", "l" }, { "64", ";" },
                    { "65", "n" }, { "67", "m" }, { "69", "," }, { "71", "." }, { "72", "/" }
                };
                return defaultSettings;
            }

            string json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var settings = JsonSerializer.Deserialize<AppSettings>(json, options);

            ValidateSettings(settings);

            return settings!;
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

                // 変換先キーの検証: nullまたは空文字列は不正
                if (string.IsNullOrWhiteSpace(targetKey))
                {
                    throw new InvalidDataException($"マッピングキー '{noteKey}' の変換先キーが空または無効です。");
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
                    KeyboardLayout = settings.KeyboardLayout
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
