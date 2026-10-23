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

            InputWarmup.Enabled = !Array.Exists(args ?? Array.Empty<string>(), a =>
                string.Equals(a, "--no-input-warmup", StringComparison.OrdinalIgnoreCase));
            LatencyMeasurement.ConfigureInputWarmup(InputWarmup.Enabled);

            string? latencyArgument = Array.Find(args ?? Array.Empty<string>(), a =>
                string.Equals(a, "--measure-latency", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("--measure-latency=", StringComparison.OrdinalIgnoreCase));
            if (latencyArgument != null)
            {
                int equals = latencyArgument.IndexOf('=');
                string? path = equals < 0 ? null : latencyArgument[(equals + 1)..];
                LatencyMeasurement.Enable(string.IsNullOrWhiteSpace(path) ? null : path);
            }

            // 3. WinFormsのメイン画面を起動
            ApplicationConfiguration.Initialize();
            try
            {
                Application.Run(new Form1());
            }
            finally
            {
                if (LatencyMeasurement.Enabled || LatencyMeasurement.ExportPending)
                {
                    var export = LatencyMeasurement.ExportAfterStop();
                    while (!export.Success)
                    {
                        var choice = MessageBox.Show(
                            $"{export.PathOrError}\n\n計測結果はメモリに残っています。別の保存先を選んで再試行しますか？\nキャンセルすると計測結果は保存されません。",
                            "遅延計測の保存に失敗", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                        if (choice != DialogResult.Retry) break;

                        using var dialog = new SaveFileDialog
                        {
                            Title = "遅延計測結果の保存先を選択",
                            Filter = "JSON ファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
                            DefaultExt = "json",
                            AddExtension = true,
                            CheckFileExists = false,
                            OverwritePrompt = false,
                            FileName = $"latency-{DateTime.Now:yyyyMMdd-HHmmss}.json"
                        };
                        if (dialog.ShowDialog() != DialogResult.OK) continue;
                        export = LatencyMeasurement.ExportAfterStop(dialog.FileName);
                    }
                    if (export.Success)
                    {
                        MessageBox.Show($"遅延計測結果を保存しました:\n{export.PathOrError}", "遅延計測", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
        }
    }
}
