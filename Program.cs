using System;
using System.Windows.Forms;

namespace MidiToKeyApp
{
    internal static class Program
    {
        /// <summary>
        /// アプリケーションのメイン エントリ ポイント。
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // 1. 診断用子プロセスの場合はWinMM列挙だけを実行して終了（debug_device.logは上書き・初期化しない）
            if (args != null && args.Length > 0 && Array.Exists(args, a => string.Equals(a, "--enum-winmm", StringComparison.OrdinalIgnoreCase)))
            {
                var ports = MidiDeviceDiagnostics.GetInProcessWinMmPorts();
                foreach (var p in ports)
                {
                    Console.WriteLine(p);
                }
                return;
            }

            // 2. 通常起動の場合に限り、DiagnosticLoggerを初期化して既存ログを上書き
            DiagnosticLogger.Initialize(overwrite: true);
            DiagnosticLogger.Log("AppStart", "midi2key 通常セッションを開始しました。");

            // 3. WinFormsのメイン画面を起動
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}
