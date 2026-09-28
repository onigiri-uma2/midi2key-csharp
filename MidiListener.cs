using System;
using System.Collections.Generic;
using System.Threading;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Core;

namespace MidiToKeyApp
{
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
    /// PCに接続されたMIDI機器からの入力を監視し、鍵盤（ノート）やペダルのイベントを検知するクラス。
    /// 各セッションに一意のGenerationを紐付け、遅延到着した旧セッションイベントの識別を可能にします。
    /// </summary>
    public class MidiListener : IDisposable
    {
        private class OpenDeviceInfo
        {
            public required InputDevice Device { get; init; }
            public required string DeviceId { get; init; }
            public required string DeviceName { get; init; }
            public required long Generation { get; init; }
        }

        private readonly List<OpenDeviceInfo> _devices = new();
        private readonly object _lock = new();
        private long _currentGeneration = 0;

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
        /// 互換用イベント
        /// </summary>
        public event Action<int, bool>? OnNoteChange;
        public event Action<int, int>? OnControlChange;

        public static IEnumerable<string> GetPortNames()
        {
            var names = new List<string>();
            foreach (var device in InputDevice.GetAll())
            {
                names.Add(device.Name);
            }
            return names;
        }

        /// <summary>
        /// 指定されたポート名のMIDIデバイスを開き、イベントの監視を開始します。
        /// </summary>
        /// <param name="portNames">監視対象のポート名リスト</param>
        /// <param name="specificGeneration">指定するGeneration（省略時は自動インクリメント）</param>
        /// <returns>このセッションに割り当てられたGeneration</returns>
        public long Start(IEnumerable<string> portNames, long? specificGeneration = null)
        {
            Stop();

            var portSet = new HashSet<string>(portNames);
            if (portSet.Count == 0) return Interlocked.Read(ref _currentGeneration);

            long gen = specificGeneration ?? Interlocked.Increment(ref _currentGeneration);
            Interlocked.Exchange(ref _currentGeneration, gen);

            lock (_lock)
            {
                int deviceIndex = 0;
                foreach (var device in InputDevice.GetAll())
                {
                    if (portSet.Contains(device.Name))
                    {
                        try
                        {
                            string deviceId = $"{deviceIndex}:{device.Name}";
                            var info = new OpenDeviceInfo
                            {
                                Device = device,
                                DeviceId = deviceId,
                                DeviceName = device.Name,
                                Generation = gen
                            };

                            device.EventReceived += OnEventReceived;
                            device.StartEventsListening();
                            _devices.Add(info);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error opening MIDI port {device.Name}: {ex.Message}");
                            device.Dispose();
                        }
                    }
                    else
                    {
                        device.Dispose();
                    }
                    deviceIndex++;
                }
            }

            return gen;
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
            }
        }

        private void OnEventReceived(object? sender, MidiEventReceivedEventArgs e)
        {
            string deviceId = "unknown";
            string deviceName = "unknown";
            long generation = -1;

            lock (_lock)
            {
                var info = _devices.Find(d => ReferenceEquals(d.Device, sender));
                if (info != null)
                {
                    deviceId = info.DeviceId;
                    deviceName = info.DeviceName;
                    generation = info.Generation;
                }
            }

            // 既にStopされて登録から消えているデバイスからのイベントは破棄
            if (generation == -1) return;

            var midiEvent = e.Event;

            if (midiEvent is NoteOnEvent noteOn)
            {
                bool isDown = noteOn.Velocity > 0;
                var noteData = new MidiNoteData(deviceId, deviceName, noteOn.Channel, noteOn.NoteNumber, noteOn.Velocity, isDown, generation);
                
                OnNoteReceived?.Invoke(noteData);
                OnNoteChange?.Invoke(noteOn.NoteNumber, isDown);
            }
            else if (midiEvent is NoteOffEvent noteOff)
            {
                var noteData = new MidiNoteData(deviceId, deviceName, noteOff.Channel, noteOff.NoteNumber, noteOff.Velocity, false, generation);
                
                OnNoteReceived?.Invoke(noteData);
                OnNoteChange?.Invoke(noteOff.NoteNumber, false);
            }
            else if (midiEvent is ControlChangeEvent controlChange)
            {
                var controlData = new MidiControlData(deviceId, deviceName, controlChange.Channel, controlChange.ControlNumber, controlChange.ControlValue, generation);
                
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
