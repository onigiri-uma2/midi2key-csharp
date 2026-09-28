using System;
using System.IO;

namespace MidiToKeyApp
{
    /// <summary>
    /// MIDIデバイスの切断検知、Windowsデバイス変更通知、ポート列挙の診断ログを記録する静的クラス。
    /// 通常起動時に既存ログを上書きし、実行中のログサイズを上限（1MB）以内に制限します。
    /// 診断用子プロセス（--enum-winmm）からは上書きされません。
    /// </summary>
    public static class DiagnosticLogger
    {
        private static readonly object _lock = new();
        private static string _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_device.log");
        private static long _maxLogSizeBytes = 1024 * 1024; // 1MB
        private static bool _isEnabled = false;

        public static bool IsEnabled
        {
            get { lock (_lock) return _isEnabled; }
            set { lock (_lock) _isEnabled = value; }
        }

        public static string LogFilePath
        {
            get { lock (_lock) return _logFilePath; }
            set { lock (_lock) _logFilePath = value; }
        }

        public static long MaxLogSizeBytes
        {
            get { lock (_lock) return _maxLogSizeBytes; }
            set { lock (_lock) _maxLogSizeBytes = value; }
        }

        /// <summary>
        /// ログ機能を初期化します。
        /// 普段は debug_device.log を出力せず、明示的に有効化（--debug 引数等）された場合のみ出力します。
        /// 診断用子プロセス（--enum-winmm）からは呼び出さないでください。
        /// </summary>
        public static void Initialize(bool overwrite = true, string? customPath = null, bool? enable = null)
        {
            lock (_lock)
            {
                if (enable.HasValue)
                {
                    _isEnabled = enable.Value;
                }

                if (!string.IsNullOrEmpty(customPath))
                {
                    _logFilePath = customPath;
                }

                if (!_isEnabled)
                {
                    return;
                }

                if (overwrite)
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(_logFilePath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        File.WriteAllText(_logFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SYSTEM] DiagnosticLogger initialized (Session Start){Environment.NewLine}");
                    }
                    catch
                    {
                        // ログ初期化失敗によりアプリをクラッシュさせない
                    }
                }
            }
        }

        /// <summary>
        /// 診断ログをファイルおよび標準出力へ書き込みます。
        /// </summary>
        public static void Log(string message)
        {
            Log("INFO", message);
        }

        /// <summary>
        /// 診断ログをファイルおよび標準出力へ書き込みます。
        /// </summary>
        /// <param name="category">カテゴリ（例: WM_DEVICECHANGE, CheckDeviceHealth, InputDevice.GetAll, ReleaseDeviceInputs 等）</param>
        /// <param name="message">ログメッセージ本文</param>
        public static void Log(string category, string message)
        {
            lock (_lock)
            {
                if (!_isEnabled)
                {
                    return;
                }
            }

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}";
            try
            {
                Console.WriteLine(line);
                lock (_lock)
                {
                    if (!_isEnabled) return;
                    EnforceLogSizeLimit();
                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // ロギング失敗によりアプリをクラッシュさせない
            }
        }

        /// <summary>
        /// ログファイルが上限サイズを超えている場合、古い内容を整理してサイズを制限します。
        /// </summary>
        private static void EnforceLogSizeLimit()
        {
            try
            {
                if (!File.Exists(_logFilePath)) return;

                var fi = new FileInfo(_logFilePath);
                if (fi.Length > _maxLogSizeBytes)
                {
                    // サイズ制限超過: 後半の約半分を残す
                    string content = File.ReadAllText(_logFilePath);
                    int halfLength = content.Length / 2;
                    int nextLineIdx = content.IndexOf(Environment.NewLine, halfLength, StringComparison.Ordinal);
                    string kept = (nextLineIdx >= 0 && nextLineIdx < content.Length - 1)
                        ? content.Substring(nextLineIdx + Environment.NewLine.Length)
                        : "";

                    string truncationNotice = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SYSTEM] [Log size limit reached ({_maxLogSizeBytes / 1024}KB). Truncated earlier entries.]{Environment.NewLine}";
                    File.WriteAllText(_logFilePath, truncationNotice + kept);
                }
            }
            catch
            {
                // 整理失敗時もクラッシュさせない
            }
        }
    }
}
