using System;
using System.Collections.Generic;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Core;

namespace MidiToKeyApp
{
    /// <summary>
    /// 受信したMIDIノートイベントの詳細情報。
    /// デバイスの一意識別子、チャンネル番号を含み、同一ノート番号や同名デバイスの競合を防ぎます。
    /// </summary>
    public readonly record struct MidiNoteData(string DeviceId, string DeviceName, int Channel, int NoteNumber, int Velocity, bool IsDown);

    /// <summary>
    /// 受信したコントロールチェンジ（サステインペダル等）の詳細情報。
    /// </summary>
    public readonly record struct MidiControlData(string DeviceId, string DeviceName, int Channel, int ControlNumber, int ControlValue);

    /// <summary>
    /// PCに接続されたMIDI機器からの入力を監視し、鍵盤（ノート）やペダルのイベントを検知するクラス。
    /// ドライバレベルの操作には Melanchall.DryWetMidi ライブラリを使用しています。
    /// </summary>
    public class MidiListener : IDisposable
    {
        private class OpenDeviceInfo
        {
            public required InputDevice Device { get; init; }
            public required string DeviceId { get; init; }
            public required string DeviceName { get; init; }
        }

        // 監視中のMIDI入力デバイスの管理リスト
        private readonly List<OpenDeviceInfo> _devices = new();
        private readonly object _lock = new();

        /// <summary>
        /// デバイスIDやチャンネルなどの詳細情報を含むノートイベント
        /// </summary>
        public event Action<MidiNoteData>? OnNoteReceived;

        /// <summary>
        /// コントロールチェンジの詳細イベント
        /// </summary>
        public event Action<MidiControlData>? OnControlReceived;

        /// <summary>
        /// 互換用: ノート番号と押下状態のみを通知する簡易イベント
        /// </summary>
        public event Action<int, bool>? OnNoteChange;

        /// <summary>
        /// 互換用: コントロール番号と値のみを通知する簡易イベント
        /// </summary>
        public event Action<int, int>? OnControlChange;

        /// <summary>
        /// 現在OSが認識しているすべてのMIDI入力ポート名（デバイス名）を取得します。
        /// </summary>
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
        /// 同名のデバイスが存在する場合でも、インデックスにより一意のDeviceIdを付与して識別します。
        /// </summary>
        public void Start(IEnumerable<string> portNames)
        {
            Stop();

            var portSet = new HashSet<string>(portNames);
            if (portSet.Count == 0) return;

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
                                DeviceName = device.Name
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

            lock (_lock)
            {
                var info = _devices.Find(d => ReferenceEquals(d.Device, sender));
                if (info != null)
                {
                    deviceId = info.DeviceId;
                    deviceName = info.DeviceName;
                }
            }

            var midiEvent = e.Event;

            if (midiEvent is NoteOnEvent noteOn)
            {
                bool isDown = noteOn.Velocity > 0;
                var noteData = new MidiNoteData(deviceId, deviceName, noteOn.Channel, noteOn.NoteNumber, noteOn.Velocity, isDown);
                
                OnNoteReceived?.Invoke(noteData);
                OnNoteChange?.Invoke(noteOn.NoteNumber, isDown);
            }
            else if (midiEvent is NoteOffEvent noteOff)
            {
                var noteData = new MidiNoteData(deviceId, deviceName, noteOff.Channel, noteOff.NoteNumber, noteOff.Velocity, false);
                
                OnNoteReceived?.Invoke(noteData);
                OnNoteChange?.Invoke(noteOff.NoteNumber, false);
            }
            else if (midiEvent is ControlChangeEvent controlChange)
            {
                var controlData = new MidiControlData(deviceId, deviceName, controlChange.Channel, controlChange.ControlNumber, controlChange.ControlValue);
                
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
