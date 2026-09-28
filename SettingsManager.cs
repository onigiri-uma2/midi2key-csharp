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
        /// <summary>
        /// マルチスレッド（MIDI受信スレッドとUI操作）でのMapping辞書アクセスを安全に保護するためのロック用オブジェクト
        /// </summary>
        [JsonIgnore]
        public readonly object MappingLock = new object();

        /// <summary>
        /// ユーザーが選択（監視対象としてチェック）したMIDIポート名のリスト
        /// </summary>
        [JsonPropertyName("selected_ports")]
        public List<string> SelectedPorts { get; set; } = new List<string>();

        /// <summary>
        /// MIDIのノート番号（キー）と、それに対応するPCキーボードのキー文字（値）のマッピング辞書
        /// </summary>
        [JsonPropertyName("mapping")]
        public Dictionary<string, string> Mapping { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 使用している物理キーボードの配列種別（"JIS" または "US"）
        /// </summary>
        [JsonPropertyName("keyboard_layout")]
        public string KeyboardLayout { get; set; } = "JIS";
    }

    /// <summary>
    /// 設定ファイル（settings.json）の読み込みと保存を管理する静的ユーティリティクラス。
    /// </summary>
    public static class SettingsManager
    {
        /// <summary>
        /// 指定されたパスから設定を読み込みます。
        /// ファイルが存在しない場合は初回起動用のデフォルト設定を返し、破損している場合は例外をスローします。
        /// </summary>
        /// <param name="path">設定ファイル(settings.json)のファイルパス</param>
        /// <returns>読み込まれた設定データを含むAppSettingsオブジェクト</returns>
        /// <exception cref="FileNotFoundException">ファイルが見つからない場合</exception>
        /// <exception cref="JsonException">JSON形式が不正な場合</exception>
        /// <exception cref="InvalidDataException">データが空または無効な場合</exception>
        public static AppSettings Load(string path)
        {
            // 設定ファイルが存在しない場合のみ、初回起動用デフォルト設定を生成して返す
            if (!File.Exists(path))
            {
                var defaultSettings = new AppSettings();
                defaultSettings.Mapping = new Dictionary<string, string>
                {
                    // 「Sky 星を紡ぐ子どもたち」で使われる標準的なデフォルトマッピング
                    { "48", "y" }, { "50", "u" }, { "52", "i" }, { "53", "o" }, { "55", "p" },
                    { "57", "h" }, { "59", "j" }, { "60", "k" }, { "62", "l" }, { "64", ";" },
                    { "65", "n" }, { "67", "m" }, { "69", "," }, { "71", "." }, { "72", "/" }
                };
                return defaultSettings;
            }

            // ファイルが存在する場合は厳格に読み込み・解析を実行
            string json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var settings = JsonSerializer.Deserialize<AppSettings>(json, options);
            if (settings == null)
            {
                throw new InvalidDataException("設定ファイルのJSON解析結果が無効または空です。");
            }
            return settings;
        }

        /// <summary>
        /// 設定オブジェクトをJSON文字列に変換し、指定されたパスに保存します。
        /// </summary>
        /// <param name="path">保存先の設定ファイル(settings.json)のパス</param>
        /// <param name="settings">保存するAppSettingsオブジェクト</param>
        public static void Save(string path, AppSettings settings)
        {
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true, // 人間が読みやすいように改行とインデントを入れる
                // '<', '>', '|' などの記号が '\u007C' のようなUnicodeエスケープに変換されるのを防ぐ（生の文字として保存する）
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            
            string json = JsonSerializer.Serialize(settings, options);
            AllTextOrAtomicWrite(path, json);
        }

        private static void AllTextOrAtomicWrite(string path, string content)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, content);
        }
    }
}
