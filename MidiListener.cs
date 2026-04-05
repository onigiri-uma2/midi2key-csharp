using System;
using System.Collections.Generic;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Core;

namespace MidiToKeyApp
{
    /// <summary>
    /// PCに接続されたMIDI機器からの入力を監視し、鍵盤（ノート）が押された/離されたイベントを検知するクラス。
    /// ドライバレベルの操作には Melanchall.DryWetMidi ライブラリを使用しています。
    /// </summary>
    public class MidiListener : IDisposable
    {
        // 監視中のMIDI入力デバイスを保持するリスト
        private List<InputDevice> _devices = new List<InputDevice>();
        
        /// <summary>
        /// MIDIノートの状態が変わった（鍵盤が押された、または離された）時に発火するイベント。
        /// 引数1(int): 押されたノート番号（0-127）
        /// 引数2(bool): キーが押された(true)、または離されたか(false)
        /// </summary>
        public event Action<int, bool>? OnNoteChange;
        
        /// <summary>
        /// 現在OSが認識しているすべてのMIDI入力ポート名（デバイス名）を取得します。
        /// </summary>
        /// <returns>MIDI入力ポート名のリスト</returns>
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
        /// <param name="portNames">監視対象とするポート名のリスト</param>
        public void Start(IEnumerable<string> portNames)
        {
            // すでに監視中のデバイスがあれば一旦すべて停止してクリアする
            Stop();
            
            foreach (var name in portNames)
            {
                try
                {
                    // OSから該当する名前のMIDI入力デバイスを取得
                    var inputDevice = InputDevice.GetByName(name);
                    
                    // イベントハンドラを登録し、監視を開始
                    inputDevice.EventReceived += OnEventReceived;
                    inputDevice.StartEventsListening();
                    
                    _devices.Add(inputDevice);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error opening MIDI port {name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// すべてのMIDIデバイスの監視を停止し、リソースを解放します。
        /// </summary>
        public void Stop()
        {
            foreach (var device in _devices)
            {
                try
                {
                    // メモリリーク防止のためイベント登録を解除し、デバイスを破棄
                    device.EventReceived -= OnEventReceived;
                    device.Dispose();
                }
                catch { }
            }
            _devices.Clear();
        }

        /// <summary>
        /// MIDIデバイスから生のMIDI信号を受け取った際に呼び出されるコールバック。
        /// </summary>
        private void OnEventReceived(object? sender, MidiEventReceivedEventArgs e)
        {
            var midiEvent = e.Event;
            
            // ノートオン（鍵盤を押した信号）の場合
            if (midiEvent is NoteOnEvent noteOn)
            {
                // Velocity > 0 であれば押下、0の場合は（一部のMIDI機器用）離上扱いとする
                OnNoteChange?.Invoke(noteOn.NoteNumber, noteOn.Velocity > 0);
            }
            // ノートオフ（鍵盤を離した信号）の場合
            else if (midiEvent is NoteOffEvent noteOff)
            {
                OnNoteChange?.Invoke(noteOff.NoteNumber, false);
            }
        }

        /// <summary>
        /// クラスが破棄される際に、適切にMIDIデバイスを閉じるための必須処理。
        /// </summary>
        public void Dispose()
        {
            Stop();
        }
    }
}
