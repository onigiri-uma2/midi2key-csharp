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

    public class MidiComparisonReport
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public string OsDescription { get; init; } = string.Empty;
        public string Trigger { get; init; } = string.Empty;
        public long Generation { get; init; }
        public IReadOnlyList<string> DryWetMidiPorts { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> InProcessWinMmPorts { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> OutOfProcessWinMmPorts { get; init; } = Array.Empty<string>();
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
        /// </summary>
        public static List<WinMmDeviceInfo> GetInProcessWinMmDevices()
        {
            var list = new List<WinMmDeviceInfo>();
            try
            {
                uint count = midiInGetNumDevs();
                for (uint i = 0; i < count; i++)
                {
                    if (midiInGetDevCaps((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<MIDIINCAPS>()) == 0)
                    {
                        if (!string.IsNullOrEmpty(caps.szPname))
                        {
                            list.Add(new WinMmDeviceInfo((int)i, caps.szPname, caps.wMid, caps.wPid));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("MidiDiagnostics", $"InProcess WinMM query failed: {ex.Message}");
            }
            return list;
        }

        /// <summary>
        /// 同一プロセス内でWinMM (winmm.dll) を直接叩いてポート名一覧を取得します。
        /// </summary>
        public static List<string> GetInProcessWinMmPorts()
        {
            return GetInProcessWinMmDevices().Select(d => d.Name).ToList();
        }

        /// <summary>
        /// 新規の独立プロセスを一時起動してWinMMポート一覧を取得します。
        /// 既存プロセス内でWinMMキャッシュが更新されない問題（同一プロセス内の静的列挙問題）を識別するために使用します。
        /// </summary>
        public static List<string> GetOutOfProcessWinMmPorts(int timeoutMs = 2000)
        {
            var ports = new List<string>();
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                // テスト実行中等でexeが見つからない場合
                return ports;
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

                using var proc = Process.Start(psi);
                if (proc == null) return ports;

                string output = proc.StandardOutput.ReadToEnd();
                if (proc.WaitForExit(timeoutMs))
                {
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
                }
                else
                {
                    try { proc.Kill(); } catch { }
                    DiagnosticLogger.Log("MidiDiagnostics", "OutOfProcess WinMM query timed out.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("MidiDiagnostics", $"OutOfProcess WinMM query failed: {ex.Message}");
            }
            return ports;
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
        /// 各データソース（DryWetMIDI、プロセス内WinMM、独立プロセスWinMM、WinRT、監視中ポート）の結果を静的に比較・評価します。
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
            string osDesc = RuntimeInformation.OSDescription;
            var dryWetPorts = dryWetResult.Success ? dryWetResult.Ports : Array.Empty<string>();
            var inProcPorts = inProcWinMm.Select(d => d.Name).ToList();
            var outProcPorts = outProcWinMm.Select(d => d.Name).ToList();

            var discrepancies = new List<string>();

            // 1. In-Process WinMM と Out-Of-Process WinMM の不一致検証
            if (outProcWinMm.Count > 0 && inProcWinMm.Count != outProcWinMm.Count)
            {
                discrepancies.Add($"同一プロセスWinMM ({inProcWinMm.Count}件) と新規プロセスWinMM ({outProcWinMm.Count}件) のポート数が一致しません。同一プロセス内のWinMM列挙キャッシュが更新されていない可能性があります。");
            }

            // 2. WinRT (OS認識) と DryWetMIDI/WinMM の不一致検証
            if (winRtDevices.Count > 0 && dryWetPorts.Count == 0)
            {
                discrepancies.Add($"WinRT/OSでは {winRtDevices.Count} 件のMIDIデバイスが認識されていますが、DryWetMIDIでは0件です (WinRT/OSとMIDIバックエンドの認識に差異があります: 未反映・要再起動)。");
            }
            else if (winRtDevices.Count == 0 && dryWetPorts.Count > 0)
            {
                discrepancies.Add($"DryWetMIDIには {dryWetPorts.Count} 件のポートが残存していますが、WinRT/OSでは0件です (物理切断後の残存キャッシュ: WinRT/OSとMIDIバックエンドの認識に差異があります)。");
            }

            // 3. 監視中インスタンスとOS認識の不一致検証
            foreach (var act in activeMonitoredPorts)
            {
                bool inWinRt = winRtDevices.Any(w => string.Equals(w.Name, act.DeviceName, StringComparison.OrdinalIgnoreCase));
                if (!inWinRt && winRtDevices.Count > 0)
                {
                    discrepancies.Add($"監視中ポート '{act.DeviceName}' (ID: {act.DeviceId}) はWinRT/OS認識から消失しています（切断の疑い）。");
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
                OutOfProcessWinMmPorts = outProcPorts,
                WinRtDevices = winRtDevices,
                ActiveMonitoredPorts = activeMonitoredPorts,
                HasDiscrepancy = hasDiscrepancy,
                DiscrepancySummary = summary
            };
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
            var outProcessWinMmTask = queryOutOfProcess ? Task.Run(() => GetOutOfProcessWinMmPorts()) : Task.FromResult(new List<string>());
            var winRtTask = GetWinRtDevicesAsync();

            await Task.WhenAll(dryWetTask, inProcessWinMmTask, outProcessWinMmTask, winRtTask);

            var dryWet = dryWetTask.Result;
            var inProc = inProcessWinMmTask.Result;
            var outProc = outProcessWinMmTask.Result;
            var winRt = winRtTask.Result;

            var inProcDevs = inProc.Select((name, idx) => new WinMmDeviceInfo(idx, name, 0, 0)).ToList();
            var outProcDevs = outProc.Select((name, idx) => new WinMmDeviceInfo(idx, name, 0, 0)).ToList();

            var report = CompareEndpoints(
                trigger,
                PortEnumerationResult.Succeeded(dryWet),
                inProcDevs,
                outProcDevs,
                winRt,
                active,
                generation);

            // 診断ログへの出力
            string dryWetStr = string.Join(", ", dryWet);
            string inProcStr = string.Join(", ", inProc);
            string outProcStr = string.Join(", ", outProc);
            string winRtStr = string.Join(", ", winRt.Select(w => $"{w.Name} (Id: {w.Id})"));
            string activeStr = string.Join(", ", active.Select(a => $"{a.DeviceName} (DeviceId: {a.DeviceId})"));

            DiagnosticLogger.Log("MidiComparison",
                $"[トリガー: {trigger}] [OS: {osDesc}] [Gen: {generation}]\n" +
                $"  1. DryWetMIDI           ({dryWet.Count}件): [{dryWetStr}]\n" +
                $"  2. In-Process WinMM     ({inProc.Count}件): [{inProcStr}]\n" +
                $"  3. Out-Of-Process WinMM ({outProc.Count}件): [{outProcStr}]\n" +
                $"  4. WinRT (OS認識)       ({winRt.Count}件): [{winRtStr}]\n" +
                $"  5. 監視中インスタンス   ({active.Count}件): [{activeStr}]\n" +
                $"  => 診断結果: {report.DiscrepancySummary}");

            return report;
        }
    }
}
