using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Devices.Enumeration;
using Windows.Devices.Midi;

namespace MidiToKeyApp
{
    public interface IWinRtMidiWatcher : IDisposable
    {
        bool IsAvailable { get; }
        bool IsEnumerationCompleted { get; }
        IReadOnlyList<WinRtMidiDeviceInfo> GetDevices();
        event Action<WinRtMidiDeviceInfo>? OnDeviceAdded;
        event Action<WinRtMidiDeviceInfo>? OnDeviceRemoved;
        event Action<WinRtMidiDeviceInfo>? OnDeviceUpdated;
        event Action? OnEnumerationCompleted;
        void Start();
        void Stop();
    }

    /// <summary>
    /// Windows.Devices.Enumeration.DeviceWatcherを用いて、OSレベルでMIDI入力デバイスの
    /// プラグ＆プレイ（接続・切断・更新）をリアルタイムに監視するクラス。
    /// DryWetMIDI/WinMMのプロセス内キャッシュ更新不具合を検知・補完するために使用します。
    /// </summary>
    public class WinRtMidiWatcher : IWinRtMidiWatcher
    {
        private DeviceWatcher? _watcher;
        private readonly Dictionary<string, WinRtMidiDeviceInfo> _devices = new();
        private readonly object _lock = new();

        public bool IsAvailable { get; private set; } = true;
        public bool IsEnumerationCompleted { get; private set; } = false;
        public bool IsWatcherRunning => _watcher != null;
        public IReadOnlyList<WinRtMidiDeviceInfo> Devices => GetDevices();

        public event Action<WinRtMidiDeviceInfo>? OnDeviceAdded;
        public event Action<WinRtMidiDeviceInfo>? OnDeviceRemoved;
        public event Action<WinRtMidiDeviceInfo>? OnDeviceUpdated;
        public event Action? OnEnumerationCompleted;

        public void Start()
        {
            try
            {
                string selector = MidiInPort.GetDeviceSelector();
                _watcher = DeviceInformation.CreateWatcher(selector);

                _watcher.Added += (s, info) =>
                {
                    var dev = new WinRtMidiDeviceInfo(info.Id, info.Name, info.IsEnabled);
                    lock (_lock)
                    {
                        _devices[info.Id] = dev;
                    }
                    DiagnosticLogger.Log("WinRtWatcher", $"Device Added: Name='{info.Name}', Id='{info.Id}'");
                    OnDeviceAdded?.Invoke(dev);
                };

                _watcher.Removed += (s, update) =>
                {
                    WinRtMidiDeviceInfo? dev = null;
                    lock (_lock)
                    {
                        if (_devices.Remove(update.Id, out var existing))
                        {
                            dev = existing;
                        }
                    }
                    var removedInfo = dev ?? new WinRtMidiDeviceInfo(update.Id, "Unknown", false);
                    DiagnosticLogger.Log("WinRtWatcher", $"Device Removed: Name='{removedInfo.Name}', Id='{update.Id}'");
                    OnDeviceRemoved?.Invoke(removedInfo);
                };

                _watcher.Updated += (s, update) =>
                {
                    WinRtMidiDeviceInfo? dev = null;
                    lock (_lock)
                    {
                        if (_devices.TryGetValue(update.Id, out var existing))
                        {
                            dev = existing;
                        }
                    }
                    if (dev.HasValue)
                    {
                        OnDeviceUpdated?.Invoke(dev.Value);
                    }
                };

                _watcher.EnumerationCompleted += (s, obj) =>
                {
                    IsEnumerationCompleted = true;
                    DiagnosticLogger.Log("WinRtWatcher", $"Enumeration completed. Total devices: {_devices.Count}");
                    OnEnumerationCompleted?.Invoke();
                };

                _watcher.Start();
                DiagnosticLogger.Log("WinRtWatcher", "DeviceWatcher started successfully.");
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                DiagnosticLogger.Log("WinRtWatcher", $"Failed to start WinRT DeviceWatcher: {ex.Message}. Falling back to standard WM_DEVICECHANGE.");
            }
        }

        public void Stop()
        {
            try
            {
                if (_watcher != null)
                {
                    _watcher.Stop();
                    _watcher = null;
                }
            }
            catch { }
        }

        public IReadOnlyList<WinRtMidiDeviceInfo> GetDevices()
        {
            lock (_lock)
            {
                return _devices.Values.ToList();
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
