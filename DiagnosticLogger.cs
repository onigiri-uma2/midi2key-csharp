using System;
using System.IO;

namespace MidiToKeyApp
{
    /// <summary>
    /// MIDIデバイスの切断検知、Windowsデバイス変更通知、ポート再列挙の診断ログを記録する静的クラス。
    /// 通常演奏時のパフォーマンスを損なわず、デバイス構成変更やエラー時のみ詳細を記録します。
    /// </summary>
    public static class DiagnosticLogger
    {
        private static readonly object _lock = new();
        private static readonly string LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_device.log");

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
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}";
            try
            {
                Console.WriteLine(line);
                lock (_lock)
                {
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // ロギング失敗によりアプリをクラッシュさせない
            }
        }
    }
}
