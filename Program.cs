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
            // 1. 診断用子プロセスの場合はWinMM列挙だけを実行して終了（debug_device.logは上書き・初期化・削除しない）
            if (args != null && args.Length > 0 && Array.Exists(args, a => string.Equals(a, "--enum-winmm", StringComparison.OrdinalIgnoreCase)))
            {
                var result = MidiDeviceDiagnostics.GetInProcessWinMmPortsResult();
                if (!result.Success)
                {
                    Console.Error.WriteLine(result.ErrorMessage ?? "WinMM列挙失敗");
                    Environment.ExitCode = 1;
                    return;
                }

                foreach (var p in result.Ports)
                {
                    Console.WriteLine(p);
                }
                Environment.ExitCode = 0;
                return;
            }

            // 2. 普段はdebug_device.logを出力しない。
            //    通常起動時はディスク上の既存ログファイルを削除してクリーンにする（子プロセスでは削除しない）。
            //    引数で --debug, --log, -d が指定された場合のみログ出力を有効化して上書き初期化する。
            bool enableDebugLog = args != null && args.Length > 0 && Array.Exists(args, a =>
                string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--log", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-d", StringComparison.OrdinalIgnoreCase)
            );

            DiagnosticLogger.Initialize(overwrite: true, enable: enableDebugLog, deleteIfExistsWhenDisabled: true);
            if (enableDebugLog)
            {
                DiagnosticLogger.Log("AppStart", "midi2key デバッグログセッションを開始しました。");
            }

            // 3. WinFormsのメイン画面を起動
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}
