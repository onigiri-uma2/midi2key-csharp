using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Core;

namespace MidiToKeyApp
{
    /// <summary>
    /// MIDIポート列挙の結果。
    /// 正常な0件と列挙失敗（例外やアクセス拒否）を明確に区別します。
    /// </summary>
    public readonly record struct PortEnumerationResult(
        bool Success,
        IReadOnlyList<string> Ports,
        string? ErrorMessage = null,
        Exception? Exception = null
    )
    {
        public static PortEnumerationResult Succeeded(IReadOnlyList<string> ports) =>
            new(true, ports);

        public static PortEnumerationResult Failed(string error, Exception? ex = null) =>
            new(false, Array.Empty<string>(), error, ex);
    }

    /// <summary>
    /// MIDIデバイスの接続状態。
    /// </summary>
    public enum DeviceState
    {
        Opening,
        Active,
        Closing,
        Closed
    }

    /// <summary>
    /// 監視対象MIDIポートの情報。
    /// </summary>
    public readonly record struct MidiPortInfo(string DeviceId, string DeviceName, DeviceState State);

    /// <summary>
    /// MIDIポート開始時のエラー情報。
    /// </summary>
    public readonly record struct MidiPortError(string PortName, string Reason, Exception? Exception = null);

    /// <summary>
    /// MIDIポート開始処理の結果。
    /// </summary>
    public readonly record struct MidiPortStartResult(
        long Generation,
        IReadOnlyList<MidiPortInfo> OpenedPorts,
        IReadOnlyList<MidiPortError> FailedPorts)
    {
        public bool IsAllSuccess => FailedPorts.Count == 0 && OpenedPorts.Count > 0;
        public bool IsPartialSuccess => OpenedPorts.Count > 0 && FailedPorts.Count > 0;
        public bool IsAllFailed => OpenedPorts.Count == 0;
    }

    /// <summary>
    /// MIDIデバイス切断イベントの詳細データ。
    /// 発生元Generation、一意DeviceId、デバイス名、切断理由を保持します。
    /// </summary>
    public readonly record struct MidiDeviceDisconnectedData(
        string DeviceId,
        string DeviceName,
        long Generation,
        string Reason
    );

    /// <summary>
    /// 受信したMIDIノートイベントの詳細情報。
    /// デバイスの一意識別子、チャンネル番号、発行元リスナーのGenerationを含みます。
    /// </summary>
    public readonly record struct MidiNoteData(string DeviceId, string DeviceName, int Channel, int NoteNumber, int Velocity, bool IsDown, long Generation);

    /// <summary>
    /// 受信したコントロールチェンジ（サステインペダル等）の詳細情報。
    /// </summary>
    public readonly record struct MidiControlData(string DeviceId, string DeviceName, int Channel, int ControlNumber, int ControlValue, long Generation);

    /// <summary>
    /// MIDI入力監視の抽象化インターフェース。
    /// テストでのMIDI入力模擬やリスナーの動作検証を可能にします。
    /// </summary>
    public interface IMidiListener : IDisposable
    {
        long CurrentGeneration { get; }
        event Action<MidiNoteData>? OnNoteReceived;
        event Action<MidiControlData>? OnControlReceived;
        event Action<MidiDeviceDisconnectedData>? OnDeviceDisconnected;
        MidiPortStartResult Start(IEnumerable<string> portNames, long? specificGeneration = null);
        void Stop();
        IReadOnlyList<MidiPortInfo> GetActivePorts();
        void CheckDeviceHealth(IEnumerable<string>? activeOsDeviceNames = null, OutOfProcessWinMmResult? outOfProcessWinMmResult = null);
        void SimulateDeviceDisconnected(string deviceId, string reason = "テストシミュレート");
    }

    /// <summary>
    /// PCに接続されたMIDI機器からの入力を監視し、鍵盤（ノート）やペダルのイベントを検知するクラス。
    /// 各セッションに一意のGenerationを紐付け、遅延到着した旧セッションイベントの識別を可能にします。
    /// </summary>
    public class MidiListener : IMidiListener
    {
        private readonly record struct QueuedMidiEvent(MidiNoteData? Note, MidiControlData? Control);

        private class OpenDeviceInfo
        {
            public required InputDevice Device { get; init; }
            public required string DeviceId { get; init; }
            public required string DeviceName { get; init; }
            public required long Generation { get; init; }
            public DeviceState State { get; set; } = DeviceState.Opening;
            public Queue<QueuedMidiEvent> PendingEvents { get; } = new(256);
            public object EventDispatchLock { get; } = new();
        }

        private readonly List<OpenDeviceInfo> _devices = new();
        private readonly object _lock = new();
        private long _currentGeneration = 0;
        private int _instanceCounter = 0;

        public long CurrentGeneration => Interlocked.Read(ref _currentGeneration);

        /// <summary>
        /// デバイスID、チャンネル、Generationを含む詳細ノートイベント
        /// </summary>
        public event Action<MidiNoteData>? OnNoteReceived;

        /// <summary>
        /// コントロールチェンジの詳細イベント
        /// </summary>
        public event Action<MidiControlData>? OnControlReceived;

        /// <summary>
        /// デバイス切断イベント。
        /// 発生元Generation、一意DeviceId、デバイス名、理由を通知します。
        /// 切断元が特定できない場合は DeviceId が空文字となります。
        /// </summary>
        public event Action<MidiDeviceDisconnectedData>? OnDeviceDisconnected;

        /// <summary>
        /// 互換用イベント
        /// </summary>
        public event Action<int, bool>? OnNoteChange;
        public event Action<int, int>? OnControlChange;

        /// <summary>
        /// 利用可能なMIDI入力ポートを列挙します。
        /// 正常な0件と列挙失敗を明瞭に区別した結果型を返します。
        /// </summary>
        public static PortEnumerationResult GetPortNames()
        {
            var names = new List<string>();
            try
            {
                var devices = InputDevice.GetAll();
                foreach (var device in devices)
                {
                    try
                    {
                        names.Add(device.Name);
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }
                return PortEnumerationResult.Succeeded(names);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("GetPortNames", $"MIDIポート列挙失敗: {ex.Message}");
                return PortEnumerationResult.Failed($"MIDIポート列挙例外: {ex.Message}", ex);
            }
        }

        public IReadOnlyList<MidiPortInfo> GetActivePorts()
        {
            lock (_lock)
            {
                return _devices
                    .Where(d => d.State == DeviceState.Active)
                    .Select(d => new MidiPortInfo(d.DeviceId, d.DeviceName, d.State))
                    .ToList();
            }
        }

        /// <summary>
        /// 指定されたポート名のMIDIデバイスを開き、イベントの監視を開始します。
        /// ロック順序のネストを排除し、EventDispatchLock単独でキューフラッシュと新着イベントの直列化を行います。
        /// </summary>
        public MidiPortStartResult Start(IEnumerable<string> portNames, long? specificGeneration = null)
        {
            Stop();

            var portList = portNames.ToList();
            if (portList.Count == 0)
            {
                return new MidiPortStartResult(CurrentGeneration, Array.Empty<MidiPortInfo>(), Array.Empty<MidiPortError>());
            }

            long gen = specificGeneration ?? Interlocked.Increment(ref _currentGeneration);
            Interlocked.Exchange(ref _currentGeneration, gen);

            var openedPorts = new List<MidiPortInfo>();
            var failedPorts = new List<MidiPortError>();

            ICollection<InputDevice> allDevices;
            try
            {
                allDevices = InputDevice.GetAll();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("Start", $"InputDevice.GetAll()失敗: {ex.Message}");
                foreach (var p in portList)
                {
                    failedPorts.Add(new MidiPortError(p, $"MIDIデバイス列挙失敗: {ex.Message}", ex));
                }
                return new MidiPortStartResult(gen, openedPorts, failedPorts);
            }

            var remainingRequestedPorts = new List<string>(portList);

            foreach (var device in allDevices)
            {
                int matchedIndex = remainingRequestedPorts.FindIndex(name => string.Equals(name, device.Name, StringComparison.OrdinalIgnoreCase));
                if (matchedIndex >= 0)
                {
                    string targetPortName = remainingRequestedPorts[matchedIndex];
                    remainingRequestedPorts.RemoveAt(matchedIndex);

                    int instId = Interlocked.Increment(ref _instanceCounter);
                    string deviceId = $"{device.Name}#{instId}";
                    var info = new OpenDeviceInfo
                    {
                        Device = device,
                        DeviceId = deviceId,
                        DeviceName = device.Name,
                        Generation = gen,
                        State = DeviceState.Opening
                    };

                    List<QueuedMidiEvent> flushedEvents = new();
                    try
                    {
                        // 1. イベント購読
                        device.EventReceived += OnEventReceived;

                        // 2. 内部管理リストへの登録（ネストなしで _lock 単独取得）
                        lock (_lock)
                        {
                            _devices.Add(info);
                        }

                        // 3. 監視開始
                        device.StartEventsListening();

                        // 4. 成功時にActiveへ遷移し、Opening中に溜まったイベントを EventDispatchLock の下でフラッシュ
                        // （※_lock とはネストしない。デッドロック防止）
                        lock (info.EventDispatchLock)
                        {
                            info.State = DeviceState.Active;
                            while (info.PendingEvents.Count > 0)
                            {
                                flushedEvents.Add(info.PendingEvents.Dequeue());
                            }

                            // EventDispatchLock 内でフラッシュイベントをディスパッチすることで、
                            // Active直後に届いた新着イベントが古いキューイベントを追い越すのを防止
                            foreach (var qe in flushedEvents)
                            {
                                if (info.State != DeviceState.Active) break;
                                if (qe.Note.HasValue)
                                {
                                    OnNoteReceived?.Invoke(qe.Note.Value);
                                    OnNoteChange?.Invoke(qe.Note.Value.NoteNumber, qe.Note.Value.IsDown);
                                }
                                else if (qe.Control.HasValue)
                                {
                                    OnControlReceived?.Invoke(qe.Control.Value);
                                    OnControlChange?.Invoke(qe.Control.Value.ControlNumber, qe.Control.Value.ControlValue);
                                }
                            }
                        }

                        openedPorts.Add(new MidiPortInfo(deviceId, device.Name, DeviceState.Active));
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Log("Start", $"ポート {targetPortName} 開始失敗: {ex.Message}");

                        // 失敗時のロールバック（ネストなしで順次処理）
                        lock (_lock)
                        {
                            _devices.Remove(info);
                        }

                        lock (info.EventDispatchLock)
                        {
                            info.State = DeviceState.Closed;
                            info.PendingEvents.Clear();
                        }

                        try
                        {
                            device.EventReceived -= OnEventReceived;
                        }
                        catch { }

                        try
                        {
                            device.Dispose();
                        }
                        catch { }

                        failedPorts.Add(new MidiPortError(targetPortName, ex.Message, ex));
                    }
                }
                else
                {
                    // 監視対象でないデバイスは解放
                    try
                    {
                        device.Dispose();
                    }
                    catch { }
                }
            }

            // 見つからなかったポートも失敗として記録
            foreach (var missingPort in remainingRequestedPorts)
            {
                failedPorts.Add(new MidiPortError(missingPort, "ポートが見つかりませんでした。"));
            }

            DiagnosticLogger.Log("Start", $"ポート開始完了: Gen={gen}, 成功={openedPorts.Count}, 失敗={failedPorts.Count}");
            return new MidiPortStartResult(gen, openedPorts, failedPorts);
        }

        /// <summary>
        /// すべてのMIDIデバイスの監視を停止し、リソースを解放します。
        /// _lock と EventDispatchLock のネストを完全に排除してデッドロックを防止します。
        /// </summary>
        public void Stop()
        {
            List<OpenDeviceInfo> devicesToStop;
            lock (_lock)
            {
                devicesToStop = new List<OpenDeviceInfo>(_devices);
                _devices.Clear();
            }

            // _lock 解放後に各デバイスの EventDispatchLock を個別に取得
            foreach (var d in devicesToStop)
            {
                lock (d.EventDispatchLock)
                {
                    d.State = DeviceState.Closing;
                    d.PendingEvents.Clear();
                }
            }

            foreach (var info in devicesToStop)
            {
                try
                {
                    info.Device.EventReceived -= OnEventReceived;
                    info.Device.Dispose();
                }
                catch { }
                finally
                {
                    lock (info.EventDispatchLock)
                    {
                        info.State = DeviceState.Closed;
                    }
                }
            }
        }

        /// <summary>
        /// デバイスの接続状態を再確認し、切断されたデバイスがあれば検出して解放・通知します。
        /// WinRTの0件を単独の切断根拠とせず、新規プロセスWinMMの結果を最重要視して判定します。
        /// </summary>
        public void CheckDeviceHealth(
            IEnumerable<string>? activeOsDeviceNames = null,
            OutOfProcessWinMmResult? outOfProcessWinMmResult = null)
        {
            int monitoringCount;
            lock (_lock)
            {
                monitoringCount = _devices.Count(d => d.State == DeviceState.Active);
            }

            DiagnosticLogger.Log("CheckDeviceHealth", $"実行開始: Gen={CurrentGeneration}, 監視デバイス数={monitoringCount}");

            if (monitoringCount == 0)
            {
                DiagnosticLogger.Log("CheckDeviceHealth", "監視中のMIDIデバイスはありません。");
                return;
            }

            List<string> currentOnlineNames = new();
            try
            {
                var currentDevices = InputDevice.GetAll();
                foreach (var dev in currentDevices)
                {
                    try
                    {
                        currentOnlineNames.Add(dev.Name);
                    }
                    finally
                    {
                        dev.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Log("CheckDeviceHealth", $"InputDevice.GetAll()失敗: {ex.Message}");
                OnDeviceDisconnected?.Invoke(new MidiDeviceDisconnectedData(string.Empty, string.Empty, CurrentGeneration, $"デバイス再列挙例外: {ex.Message}"));
                return;
            }

            HashSet<string>? osNamesSet = activeOsDeviceNames != null
                ? new HashSet<string>(activeOsDeviceNames, StringComparer.OrdinalIgnoreCase)
                : null;

            string outProcDesc = outOfProcessWinMmResult.HasValue
                ? (outOfProcessWinMmResult.Value.Success ? $"[{string.Join(", ", outOfProcessWinMmResult.Value.Ports)}]" : $"失敗({outOfProcessWinMmResult.Value.ErrorMessage})")
                : "未指定";

            DiagnosticLogger.Log("CheckDeviceHealth", $"再列挙結果: DryWetMIDI=[{string.Join(", ", currentOnlineNames)}], 新規プロセスWinMM={outProcDesc}, WinRT=[{(osNamesSet != null ? string.Join(", ", osNamesSet) : "未指定")}]");

            List<OpenDeviceInfo> disconnectedDevices = new();
            bool hasAmbiguousDisconnection = false;
            string ambiguousPortName = "";

            lock (_lock)
            {
                var activeList = _devices.Where(d => d.State == DeviceState.Active).ToList();
                DiagnosticLogger.Log("CheckDeviceHealth", $"現在Activeなデバイス: [{string.Join(", ", activeList.Select(d => $"{d.DeviceId}({d.DeviceName})"))}]");

                var activeGroups = activeList.GroupBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase);

                foreach (var group in activeGroups)
                {
                    string portName = group.Key;
                    int activeCount = group.Count();
                    int onlineCount = currentOnlineNames.Count(n => string.Equals(n, portName, StringComparison.OrdinalIgnoreCase));

                    // 第1項目の重要判定:
                    // 1. 新規プロセスWinMMで対象ポートが存在しているか
                    bool presentInOutOfProcess = outOfProcessWinMmResult.HasValue &&
                                                 outOfProcessWinMmResult.Value.Success &&
                                                 outOfProcessWinMmResult.Value.Ports.Any(p => string.Equals(p, portName, StringComparison.OrdinalIgnoreCase));

                    // 2. 新規プロセスWinMMで対象ポートが消失しているか（正常取得成功かつ存在しない）
                    bool missingInOutOfProcess = outOfProcessWinMmResult.HasValue &&
                                                 outOfProcessWinMmResult.Value.Success &&
                                                 !outOfProcessWinMmResult.Value.Ports.Any(p => string.Equals(p, portName, StringComparison.OrdinalIgnoreCase));

                    // 3. WinRTでの消失判定:
                    // ※実機KORG nanoKEY2等でWinRTが常に0件を返す環境があるため、WinRT全体が0件の場合は切断判定に使用しない！
                    // WinRTで1件以上他のデバイスが認識されている実績がある場合に限り、対象ポートの消失を切断根拠とする。
                    bool missingInWinRt = osNamesSet != null && osNamesSet.Count > 0 && !osNamesSet.Contains(portName);

                    bool isDisconnected = false;

                    if (presentInOutOfProcess)
                    {
                        // 新規プロセスWinMMで認識されているため、WinRTが0件であっても切断とは判断しない！
                        // （無関係なUSBのWM_DEVICECHANGEによる誤切断防止）
                        isDisconnected = false;
                        DiagnosticLogger.Log("CheckDeviceHealth", $"ポート '{portName}' は新規プロセスWinMMで検出されているため、接続中を維持します。");
                    }
                    else if (missingInOutOfProcess)
                    {
                        // 新規プロセスWinMMで消失が確認された場合（最も確実な切断根拠）
                        isDisconnected = true;
                        DiagnosticLogger.Log("CheckDeviceHealth", $"ポート '{portName}' は新規プロセスWinMMから消失しています。切断として処理します。");
                    }
                    else if (missingInWinRt)
                    {
                        isDisconnected = true;
                        DiagnosticLogger.Log("CheckDeviceHealth", $"ポート '{portName}' はDryWetMIDIに残存していますが、WinRT(OS認識)から消失しています。切断として処理します。");
                    }
                    else if (onlineCount < activeCount)
                    {
                        int lostCount = activeCount - onlineCount;
                        DiagnosticLogger.Log("CheckDeviceHealth", $"ポート '{portName}' が親プロセス列挙で減少検知: Active={activeCount}, Online={onlineCount}, 減少={lostCount}");

                        if (onlineCount == 0)
                        {
                            isDisconnected = true;
                        }
                        else
                        {
                            // 同名ポート複数時の一部減少
                            hasAmbiguousDisconnection = true;
                            ambiguousPortName = portName;
                            DiagnosticLogger.Log("CheckDeviceHealth", $"同名ポート '{portName}' の切断元特定不能: 安全停止のため全デバイスを解放対象にします");
                            isDisconnected = true;
                        }
                    }

                    if (isDisconnected)
                    {
                        foreach (var devInfo in group)
                        {
                            _devices.Remove(devInfo);
                            disconnectedDevices.Add(devInfo);
                        }
                    }
                }
            }

            // _lock 解放後に EventDispatchLock を個別に取得して状態変更（デッドロック防止）
            foreach (var devInfo in disconnectedDevices)
            {
                lock (devInfo.EventDispatchLock)
                {
                    devInfo.State = DeviceState.Closing;
                    devInfo.PendingEvents.Clear();
                }
            }

            // ロック外でクリーンアップとイベント通知
            foreach (var info in disconnectedDevices)
            {
                try
                {
                    info.Device.EventReceived -= OnEventReceived;
                    info.Device.Dispose();
                }
                catch { }
                finally
                {
                    lock (info.EventDispatchLock)
                    {
                        info.State = DeviceState.Closed;
                    }
                }
            }

            if (hasAmbiguousDisconnection)
            {
                DiagnosticLogger.Log("CheckDeviceHealth", $"同名デバイス切断元不明通知を発行: Port={ambiguousPortName}");
                OnDeviceDisconnected?.Invoke(new MidiDeviceDisconnectedData(string.Empty, ambiguousPortName, CurrentGeneration, "同名デバイスの切断元特定不能（安全全停止）"));
            }
            else
            {
                foreach (var info in disconnectedDevices)
                {
                    DiagnosticLogger.Log("CheckDeviceHealth", $"切断通知を発行: DeviceId={info.DeviceId}, Port={info.DeviceName}, Gen={info.Generation}");
                    OnDeviceDisconnected?.Invoke(new MidiDeviceDisconnectedData(info.DeviceId, info.DeviceName, info.Generation, "デバイス再列挙から消失"));
                }
            }

            if (disconnectedDevices.Count == 0 && monitoringCount > 0)
            {
                DiagnosticLogger.Log("CheckDeviceHealth", "監視対象デバイスはすべて再列挙一覧に存在します（※実機で取り外されている場合はドライバ側のキャッシュの可能性があります）");
            }
        }

        /// <summary>
        /// テスト用・疑似イベント用：指定したデバイス（または全デバイス）の切断をシミュレートします。
        /// _lock と EventDispatchLock のネストを排除しています。
        /// </summary>
        public void SimulateDeviceDisconnected(string deviceId, string reason = "テストシミュレート")
        {
            List<OpenDeviceInfo> disconnectedDevices = new();
            long gen = CurrentGeneration;

            lock (_lock)
            {
                var targets = string.IsNullOrEmpty(deviceId)
                    ? _devices.Where(d => d.State == DeviceState.Active).ToList()
                    : _devices.Where(d => d.DeviceId == deviceId && d.State == DeviceState.Active).ToList();

                foreach (var t in targets)
                {
                    _devices.Remove(t);
                    disconnectedDevices.Add(t);
                }
            }

            // _lock 解放後に EventDispatchLock を取得
            foreach (var t in disconnectedDevices)
            {
                lock (t.EventDispatchLock)
                {
                    t.State = DeviceState.Closing;
                    t.PendingEvents.Clear();
                }
            }

            foreach (var info in disconnectedDevices)
            {
                try
                {
                    info.Device.EventReceived -= OnEventReceived;
                    info.Device.Dispose();
                }
                catch { }
                finally
                {
                    lock (info.EventDispatchLock)
                    {
                        info.State = DeviceState.Closed;
                    }
                }

                OnDeviceDisconnected?.Invoke(new MidiDeviceDisconnectedData(info.DeviceId, info.DeviceName, info.Generation, reason));
            }

            if (disconnectedDevices.Count == 0 && string.IsNullOrEmpty(deviceId))
            {
                OnDeviceDisconnected?.Invoke(new MidiDeviceDisconnectedData(string.Empty, string.Empty, gen, reason));
            }
        }

        private void OnEventReceived(object? sender, MidiEventReceivedEventArgs e)
        {
            OpenDeviceInfo? targetInfo = null;

            lock (_lock)
            {
                targetInfo = _devices.Find(d => ReferenceEquals(d.Device, sender));
            }

            if (targetInfo == null) return;

            // EventDispatchLock により、Opening -> Active移行時のキューフラッシュと
            // 新着イベントの処理を直列化し、新着イベントの追い越しを防止（_lock とはネストしない）
            lock (targetInfo.EventDispatchLock)
            {
                string deviceId = targetInfo.DeviceId;
                string deviceName = targetInfo.DeviceName;
                long generation = targetInfo.Generation;
                DeviceState state = targetInfo.State;

                if (generation != Interlocked.Read(ref _currentGeneration)) return;
                if (state == DeviceState.Closing || state == DeviceState.Closed) return;

                var midiEvent = e.Event;

                if (midiEvent is NoteOnEvent noteOn)
                {
                    bool isDown = noteOn.Velocity > 0;
                    var noteData = new MidiNoteData(deviceId, deviceName, noteOn.Channel, noteOn.NoteNumber, noteOn.Velocity, isDown, generation);

                    if (state == DeviceState.Opening)
                    {
                        if (targetInfo.PendingEvents.Count < 256)
                        {
                            targetInfo.PendingEvents.Enqueue(new QueuedMidiEvent(noteData, null));
                        }
                        return;
                    }

                    OnNoteReceived?.Invoke(noteData);
                    OnNoteChange?.Invoke(noteOn.NoteNumber, isDown);
                }
                else if (midiEvent is NoteOffEvent noteOff)
                {
                    var noteData = new MidiNoteData(deviceId, deviceName, noteOff.Channel, noteOff.NoteNumber, noteOff.Velocity, false, generation);

                    if (state == DeviceState.Opening)
                    {
                        if (targetInfo.PendingEvents.Count < 256)
                        {
                            targetInfo.PendingEvents.Enqueue(new QueuedMidiEvent(noteData, null));
                        }
                        return;
                    }

                    OnNoteReceived?.Invoke(noteData);
                    OnNoteChange?.Invoke(noteOff.NoteNumber, false);
                }
                else if (midiEvent is ControlChangeEvent controlChange)
                {
                    var controlData = new MidiControlData(deviceId, deviceName, controlChange.Channel, controlChange.ControlNumber, controlChange.ControlValue, generation);

                    if (state == DeviceState.Opening)
                    {
                        if (targetInfo.PendingEvents.Count < 256)
                        {
                            targetInfo.PendingEvents.Enqueue(new QueuedMidiEvent(null, controlData));
                        }
                        return;
                    }

                    OnControlReceived?.Invoke(controlData);
                    OnControlChange?.Invoke(controlChange.ControlNumber, controlChange.ControlValue);
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
