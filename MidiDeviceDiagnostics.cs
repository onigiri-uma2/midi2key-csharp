using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Melanchall.DryWetMidi.Multimedia;

namespace MidiToKeyApp
{
    public readonly record struct WinRtMidiDeviceInfo(string Id, string Name, bool IsEnabled);

    public readonly record struct WinMmDeviceInfo(int Index, string Name, ushort Mid, ushort Pid);

    /// <summary>
    /// プロセス内WinMM列挙の結果型。
    /// 正常な0件とAPIエラー・例外を明確に区別します。
    /// </summary>
    public readonly record struct WinMmEnumerationResult(
        bool Success,
        IReadOnlyList<WinMmDeviceInfo> Devices,
        string? ErrorMessage = null,
        Exception? Exception = null
    )
    {
        public static WinMmEnumerationResult Succeeded(IReadOnlyList<WinMmDeviceInfo> devices) =>
            new(true, devices);

        public static WinMmEnumerationResult Failed(string error, Exception? ex = null) =>
            new(false, Array.Empty<WinMmDeviceInfo>(), error, ex);
    }

    /// <summary>
    /// 新規独立プロセスのWinMM列挙結果。
    /// 正常な0件、検出あり、タイムアウト、プロセス起動失敗を明確に区別します。
    /// </summary>
    public readonly record struct OutOfProcessWinMmResult(
        bool Success,
        IReadOnlyList<string> Ports,
        string? ErrorMessage = null,
        int ExitCode = -1,
        bool TimedOut = false
    )
    {
        public static OutOfProcessWinMmResult Succeeded(IReadOnlyList<string> ports, int exitCode = 0) =>
            new(true, ports, null, exitCode, false);

        public static OutOfProcessWinMmResult Failed(string error, int exitCode = -1, bool timedOut = false) =>
            new(false, Array.Empty<string>(), error, exitCode, timedOut);
    }

    public class MidiComparisonReport
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public string OsDescription { get; init; } = string.Empty;
        public string Trigger { get; init; } = string.Empty;
        public long Generation { get; init; }
        public IReadOnlyList<string> DryWetMidiPorts { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> InProcessWinMmPorts { get; init; } = Array.Empty<string>();
        public OutOfProcessWinMmResult OutOfProcessWinMm { get; init; } = OutOfProcessWinMmResult.Failed("未取得");
        public IReadOnlyList<string> OutOfProcessWinMmPorts => OutOfProcessWinMm.Ports;
        public IReadOnlyList<WinRtMidiDeviceInfo> WinRtDevices { get; init; } = Array.Empty<WinRtMidiDeviceInfo>();
        public IReadOnlyList<MidiPortInfo> ActiveMonitoredPorts { get; init; } = Array.Empty<MidiPortInfo>();
        public bool HasDiscrepancy { get; init; }
        public bool HasMismatch => HasDiscrepancy;
        public string DiscrepancySummary { get; init; } = string.Empty;
        public string Diagnosis => DiscrepancySummary;
    }

    /// <summary>
    /// 各MIDI列挙API（DryWetMIDI、プロセス内WinMM、独立プロセスWinMM、WinRT）の結果を収集・比較し、
    /// プロセス内キャッシュやOS認識状態の不一致を特定するための診断サービス。
    /// </summary>
    public static class MidiDeviceDiagnostics
    {
        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        public static extern uint midiInGetNumDevs();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MIDIINCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwSupport;
        }

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        public static extern uint midiInGetDevCaps(UIntPtr uDeviceID, out MIDIINCAPS caps, uint cbMidiInCaps);

        /// <summary>
        /// 同一プロセス内でWinMM (winmm.dll) を直接叩いてポート詳細一覧を取得します。
        /// 正常な0件とAPIエラー・例外を明確に区別した結果型を返します。
        /// </summary>
        public static WinMmEnumerationResult GetInProcessWinMmDevicesResult()
        {
            var list = new List<WinMmDeviceInfo>();
            try
            {
                uint count = midiInGetNumDevs();
                for (uint i = 0; i < count; i++)
                {
                    uint mmr = midiInGetDevCaps((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<MIDIINCAPS>());
                    if (mmr != 0)
                    {
                        string err = $"midiInGetDevCaps がデバイスID {i} に対しエラーコード {mmr} を返しました";
                        DiagnosticLogger.Log("MidiDiagnostics", err);
                        return WinMmEnumerationResult.Failed(err);
                    }

                    if (!string.IsNullOrEmpty(caps.szPname))
                    {
                        list.Add(new WinMmDeviceInfo((int)i, caps.szPname, caps.wMid, caps.wPid));
                    }
                }
                return WinMmEnumerationResult.Succeeded(list);
            }
            catch (Exception ex)
            {
                string err = $"InProcess WinMM query failed: {ex.Message}";
                DiagnosticLogger.Log("MidiDiagnostics", err);
                return WinMmEnumerationResult.Failed(err, ex);
            }
        }

        /// <summary>
        /// 同一プロセス内でWinMM (winmm.dll) を直接叩いてポート詳細一覧を取得します（互換用）。
        /// </summary>
        public static List<WinMmDeviceInfo> GetInProcessWinMmDevices()
        {
            var result = GetInProcessWinMmDevicesResult();
            return result.Success ? result.Devices.ToList() : new List<WinMmDeviceInfo>();
        }

        /// <summary>
        /// 同一プロセス内でWinMM (winmm.dll) を直接叩いてポート名一覧を取得します（結果型）。
        /// </summary>
        public static PortEnumerationResult GetInProcessWinMmPortsResult()
        {
            var devResult = GetInProcessWinMmDevicesResult();
            if (!devResult.Success)
            {
                return PortEnumerationResult.Failed(devResult.ErrorMessage ?? "WinMM列挙に失敗しました", devResult.Exception);
            }
            return PortEnumerationResult.Succeeded(devResult.Devices.Select(d => d.Name).ToList());
        }

        /// <summary>
        /// 同一プロセス内でWinMM (winmm.dll) を直接叩いてポート名一覧を取得します。
        /// </summary>
        public static List<string> GetInProcessWinMmPorts()
        {
            var result = GetInProcessWinMmPortsResult();
            return result.Success ? result.Ports.ToList() : new List<string>();
        }

        /// <summary>
        /// 新規の独立プロセスを一時起動してWinMMポート一覧を取得します。
        /// 正常な0件、検出あり、タイムアウト、プロセス起動失敗を明確に区別した結果型を返します。
        /// 標準出力の非同期読み取りとタイムアウト時の確実なプロセスKillを実施します。
        /// </summary>
        public static OutOfProcessWinMmResult GetOutOfProcessWinMmResult(int timeoutMs = 2000)
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                return OutOfProcessWinMmResult.Failed("実行可能ファイルが見つかりません (テスト環境または未配置)");
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--enum-winmm",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = new Process { StartInfo = psi };
                if (!proc.Start())
                {
                    return OutOfProcessWinMmResult.Failed("子プロセスの起動に失敗しました");
                }

                // 子プロセスの出力と終了待機のデッドロック防止: 非同期で読み取り開始
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(true); } catch { }
                    DiagnosticLogger.Log("MidiDiagnostics", $"OutOfProcess WinMM query timed out ({timeoutMs}ms).");
                    return OutOfProcessWinMmResult.Failed($"子プロセスの実行がタイムアウトしました ({timeoutMs}ms)", timedOut: true);
                }

                // プロセス終了後に読み取りタスクの完了を待機
                bool readCompleted = Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 1000);

                if (proc.ExitCode != 0)
                {
                    string err = stderrTask.IsCompleted ? stderrTask.Result : "";
                    DiagnosticLogger.Log("MidiDiagnostics", $"OutOfProcess WinMM query failed with exit code {proc.ExitCode}: {err}");
                    return OutOfProcessWinMmResult.Failed($"子プロセスが終了コード {proc.ExitCode} で終了しました: {err}", proc.ExitCode);
                }

                // 読み取りタスクが完了しなかった場合は、空文字を0件と誤判定しないようFailedを返す
                if (!readCompleted || !stdoutTask.IsCompleted)
                {
                    DiagnosticLogger.Log("MidiDiagnostics", "OutOfProcess WinMM standard output read timed out or failed to complete.");
                    return OutOfProcessWinMmResult.Failed("子プロセスの標準出力読み取りが完了しませんでした", timedOut: true);
                }

                string output = stdoutTask.Result;
                var ports = new List<string>();
                using var reader = new StringReader(output);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        ports.Add(trimmed);
                    }
                }

                return OutOfProcessWinMmResult.Succeeded(ports, proc.ExitCode);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("MidiDiagnostics", $"OutOfProcess WinMM query failed with exception: {ex.Message}");
                return OutOfProcessWinMmResult.Failed($"子プロセス実行例外: {ex.Message}");
            }
        }

        /// <summary>
        /// 互換用: 新規の独立プロセスを一時起動してWinMMポート一覧を取得します。
        /// </summary>
        public static List<string> GetOutOfProcessWinMmPorts(int timeoutMs = 2000)
        {
            var result = GetOutOfProcessWinMmResult(timeoutMs);
            return result.Success ? result.Ports.ToList() : new List<string>();
        }

        /// <summary>
        /// DryWetMIDIのInputDevice.GetAll()を用いてポート名一覧を取得します。
        /// </summary>
        public static List<string> GetDryWetMidiPorts()
        {
            var ports = new List<string>();
            try
            {
                var devices = InputDevice.GetAll();
                foreach (var d in devices)
                {
                    ports.Add(d.Name);
                    try { d.Dispose(); } catch { }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("MidiDiagnostics", $"DryWetMIDI query failed: {ex.Message}");
            }
            return ports;
        }

        /// <summary>
        /// WinRT (Windows.Devices.Midi / DeviceInformation) を用いてOSが認識しているMIDI入力デバイス一覧を取得します。
        /// </summary>
        public static async Task<List<WinRtMidiDeviceInfo>> GetWinRtDevicesAsync()
        {
            var list = new List<WinRtMidiDeviceInfo>();
            try
            {
                string selector = Windows.Devices.Midi.MidiInPort.GetDeviceSelector();
                var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
                foreach (var d in devices)
                {
                    list.Add(new WinRtMidiDeviceInfo(d.Id, d.Name, d.IsEnabled));
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("MidiDiagnostics", $"WinRT query failed: {ex.Message}");
            }
            return list;
        }

        /// <summary>
        /// 各データソース（DryWetMIDI、プロセス内WinMM、独立プロセスWinMM結果、WinRT、監視中ポート）の結果を静的に比較・評価します。
        /// 新規プロセスのWinMM結果を最重要視し、WinRTの0件を「OS上の非存在」と誤断定しないよう厳密に評価します。
        /// </summary>
        public static MidiComparisonReport CompareEndpoints(
            string trigger,
            PortEnumerationResult dryWetResult,
            IReadOnlyList<WinMmDeviceInfo> inProcWinMm,
            OutOfProcessWinMmResult outProcResult,
            IReadOnlyList<WinRtMidiDeviceInfo> winRtDevices,
            IReadOnlyList<MidiPortInfo> activeMonitoredPorts,
            long generation = 0)
        {
            string osDesc = RuntimeInformation.OSDescription;
            var dryWetPorts = dryWetResult.Success ? dryWetResult.Ports : Array.Empty<string>();
            var inProcPorts = inProcWinMm.Select(d => d.Name).ToList();
            var outProcPorts = outProcResult.Ports;

            var discrepancies = new List<string>();

            // 1. 新規プロセスWinMMと親プロセスWinMMの比較（正常に取得できた場合、0件も含めて厳格に比較）
            if (outProcResult.Success)
            {
                if (inProcWinMm.Count != outProcPorts.Count)
                {
                    if (inProcWinMm.Count > 0 && outProcPorts.Count == 0)
                    {
                        discrepancies.Add($"親プロセスWinMM ({inProcWinMm.Count}件) に対し、新規プロセスWinMMは0件です。デバイスが物理的に取り外された可能性があります（親プロセスのWinMMキャッシュ残存）。");
                    }
                    else if (inProcWinMm.Count == 0 && outProcPorts.Count > 0)
                    {
                        discrepancies.Add($"親プロセスWinMMは0件ですが、新規プロセスWinMMで {outProcPorts.Count}件 検出されました。接続は検出されていますが、MIDIバックエンドには未反映です。再起動してください。");
                    }
                    else
                    {
                        discrepancies.Add($"親プロセスWinMM ({inProcWinMm.Count}件) と新規プロセスWinMM ({outProcPorts.Count}件) のポート数が一致しません。同一プロセス内のWinMM列挙キャッシュが更新されていない可能性があります。");
                    }
                }
            }
            else
            {
                discrepancies.Add($"新規プロセスWinMMの取得に失敗しました: {outProcResult.ErrorMessage}");
            }

            // 2. WinRT (OS認識) の検証（WinRTが0件のとき「OS側でデバイスが存在しない」と断定しない）
            if (winRtDevices.Count > 0 && dryWetPorts.Count == 0)
            {
                discrepancies.Add($"WinRTでは {winRtDevices.Count}件 認識されていますが、DryWetMIDIでは0件です (WinRT/OSとMIDIバックエンドの認識に差異があります: 未反映・要再起動)。");
            }
            else if (winRtDevices.Count == 0 && dryWetPorts.Count > 0)
            {
                if (outProcResult.Success && outProcPorts.Count == 0)
                {
                    discrepancies.Add($"DryWetMIDIには {dryWetPorts.Count}件 のポートが残存していますが、新規プロセスWinMM・WinRTともに0件です (物理切断後の残存キャッシュの可能性)。");
                }
                else
                {
                    // WinRTが0件でも新規プロセスで検出されている場合は、WinRT非対応環境の可能性があるため断定しない
                    discrepancies.Add($"DryWetMIDIに {dryWetPorts.Count}件 のポートがありますが、WinRT MIDI列挙は0件です (※一部のMIDIデバイス・ドライバ環境ではWinRTに現れない場合があります)。");
                }
            }

            // 3. 監視中ポートの検証
            foreach (var act in activeMonitoredPorts)
            {
                // 新規プロセスWinMMで対象ポートが消えている場合（最も信頼性が高い判定）
                if (outProcResult.Success && !outProcPorts.Any(p => string.Equals(p, act.DeviceName, StringComparison.OrdinalIgnoreCase)))
                {
                    discrepancies.Add($"監視中ポート '{act.DeviceName}' (ID: {act.DeviceId}) は新規プロセスWinMMから消失しています（物理切断）。");
                }
                // WinRTで以前認識されていたがWinRTから消えた場合（WinRTが有効に機能している場合のみ）
                else if (winRtDevices.Count > 0 && !winRtDevices.Any(w => string.Equals(w.Name, act.DeviceName, StringComparison.OrdinalIgnoreCase)))
                {
                    discrepancies.Add($"監視中ポート '{act.DeviceName}' (ID: {act.DeviceId}) はWinRT MIDI列挙から消失しています（切断の疑い）。");
                }
            }

            bool hasDiscrepancy = discrepancies.Count > 0;
            string summary = hasDiscrepancy ? string.Join(" | ", discrepancies) : "全APIの列挙状態は整合しています。";

            return new MidiComparisonReport
            {
                Timestamp = DateTime.Now,
                OsDescription = osDesc,
                Trigger = trigger,
                Generation = generation,
                DryWetMidiPorts = dryWetPorts,
                InProcessWinMmPorts = inProcPorts,
                OutOfProcessWinMm = outProcResult,
                WinRtDevices = winRtDevices,
                ActiveMonitoredPorts = activeMonitoredPorts,
                HasDiscrepancy = hasDiscrepancy,
                DiscrepancySummary = summary
            };
        }

        /// <summary>
        /// 互換用: 各データソースの結果を静的に比較・評価します。
        /// </summary>
        public static MidiComparisonReport CompareEndpoints(
            string trigger,
            PortEnumerationResult dryWetResult,
            IReadOnlyList<WinMmDeviceInfo> inProcWinMm,
            IReadOnlyList<WinMmDeviceInfo> outProcWinMm,
            IReadOnlyList<WinRtMidiDeviceInfo> winRtDevices,
            IReadOnlyList<MidiPortInfo> activeMonitoredPorts,
            long generation = 0)
        {
            var outProcResult = OutOfProcessWinMmResult.Succeeded(outProcWinMm.Select(d => d.Name).ToList());
            return CompareEndpoints(trigger, dryWetResult, inProcWinMm, outProcResult, winRtDevices, activeMonitoredPorts, generation);
        }

        public static Task<MidiComparisonReport> RunComparisonAsync(
            string trigger,
            IReadOnlyList<MidiPortInfo>? activePorts = null,
            long generation = 0)
        {
            return RunComparisonAsync(trigger, generation, activePorts, queryOutOfProcess: true);
        }

        /// <summary>
        /// 5つのAPI / 状態（DryWetMIDI、プロセス内WinMM、独立プロセスWinMM、WinRT、監視中インスタンス）
        /// の結果を並行収集し、差分を判定して診断ログを出力します。
        /// </summary>
        public static async Task<MidiComparisonReport> RunComparisonAsync(
            string trigger,
            long generation,
            IReadOnlyList<MidiPortInfo>? activePorts = null,
            bool queryOutOfProcess = true)
        {
            string osDesc = RuntimeInformation.OSDescription;
            var active = activePorts ?? Array.Empty<MidiPortInfo>();

            // 各APIの列挙を取得
            var dryWetTask = Task.Run(() => GetDryWetMidiPorts());
            var inProcessWinMmTask = Task.Run(() => GetInProcessWinMmPorts());
            var outProcessWinMmTask = queryOutOfProcess 
                ? Task.Run(() => GetOutOfProcessWinMmResult()) 
                : Task.FromResult(OutOfProcessWinMmResult.Failed("クエリ無効"));
            var winRtTask = GetWinRtDevicesAsync();

            await Task.WhenAll(dryWetTask, inProcessWinMmTask, outProcessWinMmTask, winRtTask);

            var dryWet = dryWetTask.Result;
            var inProc = inProcessWinMmTask.Result;
            var outProcResult = outProcessWinMmTask.Result;
            var winRt = winRtTask.Result;

            var inProcDevs = inProc.Select((name, idx) => new WinMmDeviceInfo(idx, name, 0, 0)).ToList();

            var report = CompareEndpoints(
                trigger,
                PortEnumerationResult.Succeeded(dryWet),
                inProcDevs,
                outProcResult,
                winRt,
                active,
                generation);

            // 診断ログへの出力（第5項目: 取得元を明確に区別し、新規プロセスWinMMの結果を重視）
            string dryWetStr = string.Join(", ", dryWet);
            string inProcStr = string.Join(", ", inProc);
            string outProcStr = outProcResult.Success ? string.Join(", ", outProcResult.Ports) : $"(失敗: {outProcResult.ErrorMessage})";
            string winRtStr = string.Join(", ", winRt.Select(w => $"{w.Name} (Id: {w.Id})"));
            string activeStr = string.Join(", ", active.Select(a => $"{a.DeviceName} (DeviceId: {a.DeviceId})"));

            DiagnosticLogger.Log("MidiComparison",
                $"[トリガー: {trigger}] [OS: {osDesc}] [Gen: {generation}]\n" +
                $"  1. DryWetMIDI           ({dryWet.Count}件): [{dryWetStr}]\n" +
                $"  2. 親プロセスWinMM      ({inProc.Count}件): [{inProcStr}]\n" +
                $"  3. 新規プロセスWinMM    ({(outProcResult.Success ? $"{outProcResult.Ports.Count}件" : "エラー")}): [{outProcStr}]\n" +
                $"  4. WinRT MIDI列挙       ({winRt.Count}件): [{winRtStr}]\n" +
                $"  5. 監視中インスタンス   ({active.Count}件): [{activeStr}]\n" +
                $"  => 診断結果: {report.DiscrepancySummary}");

            return report;
        }
    }
}
