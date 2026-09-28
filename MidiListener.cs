using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Core;

namespace MidiToKeyApp
{
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
        void CheckDeviceHealth();
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

        public static IEnumerable<string> GetPortNames()
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error enumerating MIDI ports: {ex.Message}");
            }
            return names;
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
        /// </summary>
        /// <param name="portNames">監視対象のポート名リスト</param>
        /// <param name="specificGeneration">指定するGeneration（省略時は自動インクリメント）</param>
        /// <returns>ポート開始処理の結果</returns>
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

            // 同名ポートの重複指定や複数接続に対応するため、ポート名ごとに要求された数を管理
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

                    bool success = false;
                    List<QueuedMidiEvent> flushedEvents = new();
                    try
                    {
                        // 1. イベント購読
                        device.EventReceived += OnEventReceived;

                        // 2. 内部管理リストへの登録（Start前に登録してOpening中のイベントを取りこぼさない）
                        lock (_lock)
                        {
                            _devices.Add(info);
                        }

                        // 3. 監視開始
                        device.StartEventsListening();

                        // 4. 成功時にActiveへ遷移し、Opening中に溜まったイベントを取り出し
                        lock (_lock)
                        {
                            info.State = DeviceState.Active;
                            while (info.PendingEvents.Count > 0)
                            {
                                flushedEvents.Add(info.PendingEvents.Dequeue());
                            }
                        }

                        openedPorts.Add(new MidiPortInfo(deviceId, device.Name, DeviceState.Active));
                        success = true;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Log("Start", $"ポート {targetPortName} 開始失敗: {ex.Message}");

                        // 失敗時のロールバック
                        lock (_lock)
                        {
                            _devices.Remove(info);
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

                    if (success)
                    {
                        // Opening中に届いたイベントを受信順にディスパッチ（ロック外で安全に発火）
                        foreach (var qe in flushedEvents)
                        {
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
        /// </summary>
        public void Stop()
        {
            List<OpenDeviceInfo> devicesToStop;
            lock (_lock)
            {
                devicesToStop = new List<OpenDeviceInfo>(_devices);
                foreach (var d in devicesToStop)
                {
                    d.State = DeviceState.Closing;
                    d.PendingEvents.Clear();
                }
                _devices.Clear();
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
                    info.State = DeviceState.Closed;
                }
            }
        }

        /// <summary>
        /// デバイスの接続状態を再確認し、切断されたデバイスがあれば検出して解放・通知します。
        /// WindowsのWM_DEVICECHANGE等から呼び出されます。
        /// </summary>
        public void CheckDeviceHealth()
        {
            DiagnosticLogger.Log("CheckDeviceHealth", $"実行開始: Gen={CurrentGeneration}, 監視デバイス数={_devices.Count}");

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
                // 列挙自体が失敗した場合は特定できないため、切断元不明として全停止通知
                OnDeviceDisconnected?.Invoke(new MidiDeviceDisconnectedData(string.Empty, string.Empty, CurrentGeneration, $"デバイス再列挙例外: {ex.Message}"));
                return;
            }

            DiagnosticLogger.Log("CheckDeviceHealth", $"再列挙結果: [{string.Join(", ", currentOnlineNames)}]");

            List<OpenDeviceInfo> disconnectedDevices = new();
            bool hasAmbiguousDisconnection = false;
            string ambiguousPortName = "";

            lock (_lock)
            {
                var activeList = _devices.Where(d => d.State == DeviceState.Active).ToList();
                DiagnosticLogger.Log("CheckDeviceHealth", $"現在Activeなデバイス: [{string.Join(", ", activeList.Select(d => $"{d.DeviceId}({d.DeviceName})"))}]");

                // デバイス名ごとに個数を比較
                var activeGroups = activeList.GroupBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase);

                foreach (var group in activeGroups)
                {
                    string portName = group.Key;
                    int activeCount = group.Count();
                    int onlineCount = currentOnlineNames.Count(n => string.Equals(n, portName, StringComparison.OrdinalIgnoreCase));

                    if (onlineCount < activeCount)
                    {
                        int lostCount = activeCount - onlineCount;
                        DiagnosticLogger.Log("CheckDeviceHealth", $"ポート '{portName}' が減少検知: Active={activeCount}, Online={onlineCount}, 減少={lostCount}");

                        if (onlineCount == 0)
                        {
                            // その名前のデバイスが全滅した場合、所属する全インスタンスが切断されたと特定可能
                            foreach (var devInfo in group)
                            {
                                devInfo.State = DeviceState.Closing;
                                _devices.Remove(devInfo);
                                disconnectedDevices.Add(devInfo);
                            }
                        }
                        else
                        {
                            // 同名デバイスが複数あり、一部のみ減少した場合: どのインスタンスが切断されたか判別不能
                            hasAmbiguousDisconnection = true;
                            ambiguousPortName = portName;
                            DiagnosticLogger.Log("CheckDeviceHealth", $"同名ポート '{portName}' の切断元特定不能: 安全停止のため全デバイスを解放対象にします");

                            // 安全側として該当グループのActiveデバイスをすべてClosingにする
                            foreach (var devInfo in group)
                            {
                                devInfo.State = DeviceState.Closing;
                                _devices.Remove(devInfo);
                                disconnectedDevices.Add(devInfo);
                            }
                        }
                    }
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
                    info.State = DeviceState.Closed;
                }
            }

            if (hasAmbiguousDisconnection)
            {
                // 同名デバイスのどちらが切断されたか特定できない場合は切断元不明として全停止通知
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

            if (disconnectedDevices.Count == 0)
            {
                DiagnosticLogger.Log("CheckDeviceHealth", "監視対象デバイスはすべて再列挙一覧に存在します（※実機で取り外されている場合はドライバ側のキャッシュの可能性があります）");
            }
        }

        /// <summary>
        /// テスト用・疑似イベント用：指定したデバイス（または全デバイス）の切断をシミュレートします。
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
                    t.State = DeviceState.Closing;
                    _devices.Remove(t);
                    disconnectedDevices.Add(t);
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
                    info.State = DeviceState.Closed;
                }

                OnDeviceDisconnected?.Invoke(new MidiDeviceDisconnectedData(info.DeviceId, info.DeviceName, info.Generation, reason));
            }

            if (disconnectedDevices.Count == 0 && string.IsNullOrEmpty(deviceId))
            {
                // 全切断シミュレートでリストが空の場合でも通知
                OnDeviceDisconnected?.Invoke(new MidiDeviceDisconnectedData(string.Empty, string.Empty, gen, reason));
            }
        }

        private void OnEventReceived(object? sender, MidiEventReceivedEventArgs e)
        {
            string deviceId = "unknown";
            string deviceName = "unknown";
            long generation = -1;
            DeviceState state = DeviceState.Closed;
            OpenDeviceInfo? targetInfo = null;

            lock (_lock)
            {
                targetInfo = _devices.Find(d => ReferenceEquals(d.Device, sender));
                if (targetInfo != null)
                {
                    deviceId = targetInfo.DeviceId;
                    deviceName = targetInfo.DeviceName;
                    generation = targetInfo.Generation;
                    state = targetInfo.State;
                }
            }

            // 登録されていないデバイスや旧Generationのイベントは破棄
            if (generation == -1 || targetInfo == null) return;

            // Closing / Closed 状態のイベントは破棄
            if (state == DeviceState.Closing || state == DeviceState.Closed) return;

            var midiEvent = e.Event;

            if (midiEvent is NoteOnEvent noteOn)
            {
                bool isDown = noteOn.Velocity > 0;
                var noteData = new MidiNoteData(deviceId, deviceName, noteOn.Channel, noteOn.NoteNumber, noteOn.Velocity, isDown, generation);

                if (state == DeviceState.Opening)
                {
                    // Opening中はキューイング
                    lock (_lock)
                    {
                        if (targetInfo.PendingEvents.Count < 256)
                        {
                            targetInfo.PendingEvents.Enqueue(new QueuedMidiEvent(noteData, null));
                        }
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
                    lock (_lock)
                    {
                        if (targetInfo.PendingEvents.Count < 256)
                        {
                            targetInfo.PendingEvents.Enqueue(new QueuedMidiEvent(noteData, null));
                        }
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
                    lock (_lock)
                    {
                        if (targetInfo.PendingEvents.Count < 256)
                        {
                            targetInfo.PendingEvents.Enqueue(new QueuedMidiEvent(null, controlData));
                        }
                    }
                    return;
                }

                OnControlReceived?.Invoke(controlData);
                OnControlChange?.Invoke(controlChange.ControlNumber, controlChange.ControlValue);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
