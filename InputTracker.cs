using System;
using System.Collections.Generic;
using System.Threading;

namespace MidiToKeyApp
{
    /// <summary>
    /// MIDI入力元を一意に識別するための不変キー。
    /// デバイスID、チャンネル番号、ノートまたはコントロール番号、コントロールチェンジか否かを保持します。
    /// </summary>
    public readonly record struct MidiSourceKey(string DeviceId, int Channel, int Number, bool IsCC);

    /// <summary>
    /// MIDI入力イベントの状態遷移、重複防止、押下時キースナップショットの追跡、
    /// およびキャプチャモードと通常変換モードの排他・抑止を管理するサービスクラス。
    /// </summary>
    public class InputTracker
    {
        private readonly KeySimulator _keySimulator;
        private readonly Func<AppSettings> _settingsProvider;
        private readonly object _stateLock = new();

        // セッションID（Generation）: 停止・再起動時にインクリメントされ、旧セッションの遅延イベントを破棄します
        private long _currentSessionId = 0;

        // 変換実行中フラグ
        private volatile bool _isListening = false;

        // キャプチャモード（ノート/ペダル取得）フラグ
        private volatile bool _isCapturing = false;

        // NoteOn時点で解決されたキー情報のスナップショット（入力元 -> 解決キー）
        private readonly Dictionary<MidiSourceKey, ResolvedKey> _activeNotes = new();

        // ペダルの現在状態（"DeviceId:Channel" -> isPressed）
        private readonly Dictionary<string, bool> _pedalStates = new();

        // キャプチャモード中に取得され、対応するNoteOffを受信するまでキー解放を抑止する入力元のセット
        private readonly HashSet<MidiSourceKey> _suppressedSources = new();

        /// <summary>
        /// キャプチャモード中にノートまたはペダルを取得した際にUIへ通知するコールバック
        /// </summary>
        public event Action<string>? OnInputCaptured;

        public InputTracker(KeySimulator keySimulator, Func<AppSettings> settingsProvider)
        {
            _keySimulator = keySimulator;
            _settingsProvider = settingsProvider;
        }

        public long CurrentSessionId => Interlocked.Read(ref _currentSessionId);
        public bool IsListening => _isListening;
        public bool IsCapturing => _isCapturing;

        /// <summary>
        /// 変換セッションを開始します。新しいセッションIDを発行し、旧イベントを無効化します。
        /// </summary>
        public long StartSession()
        {
            lock (_stateLock)
            {
                long newSessionId = Interlocked.Increment(ref _currentSessionId);
                _isListening = true;
                _activeNotes.Clear();
                _pedalStates.Clear();
                _suppressedSources.Clear();
                return newSessionId;
            }
        }

        /// <summary>
        /// 変換セッションを停止し、押下中キーと内部状態を完全にクリアします。
        /// </summary>
        public void StopSession()
        {
            // まずセッションIDを更新して新規・進行中のイベントを破棄
            Interlocked.Increment(ref _currentSessionId);
            _isListening = false;

            lock (_stateLock)
            {
                _keySimulator.ReleaseAllKeys();
                _activeNotes.Clear();
                _pedalStates.Clear();
                _suppressedSources.Clear();
            }
        }

        /// <summary>
        /// キャプチャモード（ノート番号欄の自動入力待ち）の開始/終了を設定します。
        /// 終了時にも、押下中のノートに対する抑止セットは維持されます。
        /// </summary>
        public void SetCapturing(bool capturing)
        {
            _isCapturing = capturing;
        }

        /// <summary>
        /// MIDIノートイベント（NoteOn / NoteOff）を処理します。
        /// </summary>
        public void ProcessNoteEvent(MidiNoteData note, long sessionId)
        {
            // (1) セッションIDの検証（停止後や別セッションの遅延イベントは即破棄）
            if (sessionId != Interlocked.Read(ref _currentSessionId))
            {
                return;
            }

            var sourceKey = new MidiSourceKey(note.DeviceId, note.Channel, note.NoteNumber, false);

            lock (_stateLock)
            {
                // 再度セッションIDをロック内で確認
                if (sessionId != Interlocked.Read(ref _currentSessionId)) return;

                if (note.IsDown)
                {
                    // --- NoteOn 処理 ---
                    if (_isCapturing)
                    {
                        // キャプチャモード中のNoteOn: 抑止対象に追加し、キー送信は行わずUIへ通知
                        _suppressedSources.Add(sourceKey);
                        OnInputCaptured?.Invoke(note.NoteNumber.ToString());
                        return;
                    }

                    if (!_isListening) return;

                    // 重複NoteOnの対策: 既に押下中の入力元であれば二重カウントを防ぐ
                    if (_activeNotes.ContainsKey(sourceKey))
                    {
                        return;
                    }

                    // 現在の設定からキーを解決（NoteOn時のスナップショット作成）
                    var settings = _settingsProvider();
                    string noteStr = note.NoteNumber.ToString();
                    string? mappedKey = null;

                    lock (settings.MappingLock)
                    {
                        if (settings.Mapping.TryGetValue(noteStr, out var key))
                        {
                            mappedKey = key;
                        }
                    }

                    if (mappedKey != null)
                    {
                        var resolved = KeyResolver.Resolve(mappedKey, settings.KeyboardLayout);
                        if (resolved.IsValid)
                        {
                            if (_keySimulator.PressResolvedKey(resolved))
                            {
                                _activeNotes[sourceKey] = resolved;
                            }
                        }
                    }
                }
                else
                {
                    // --- NoteOff 処理 ---
                    // キャプチャ中に取得されたノートの解放であれば、抑止セットから除去して終了（キー解放はしない）
                    if (_suppressedSources.Remove(sourceKey))
                    {
                        return;
                    }

                    // キャプチャ開始前から押下されていた通常ノート、または通常変換中のノートを解放
                    if (_activeNotes.Remove(sourceKey, out var resolvedKey))
                    {
                        _keySimulator.ReleaseResolvedKey(resolvedKey);
                    }
                }
            }
        }

        /// <summary>
        /// コントロールチェンジ（サステインペダル CC64 など）を処理します。
        /// 状態遷移（64以上=押下、64未満=解放）を厳格に管理し、重複イベントを正規化します。
        /// </summary>
        public void ProcessControlEvent(MidiControlData cc, long sessionId)
        {
            if (cc.ControlNumber != 64) return; // サステインペダル以外は無視

            // セッションIDの検証
            if (sessionId != Interlocked.Read(ref _currentSessionId))
            {
                return;
            }

            string pedalKey = $"{cc.DeviceId}:{cc.Channel}";
            var sourceKey = new MidiSourceKey(cc.DeviceId, cc.Channel, cc.ControlNumber, true);
            bool isDown = cc.ControlValue >= 64;

            lock (_stateLock)
            {
                if (sessionId != Interlocked.Read(ref _currentSessionId)) return;

                _pedalStates.TryGetValue(pedalKey, out bool currentDown);

                // 状態に変化がない重複イベント（例: 127→127→100、または 63→0）は無視
                if (isDown == currentDown)
                {
                    return;
                }

                _pedalStates[pedalKey] = isDown;

                if (isDown)
                {
                    // --- ペダル踏み込み (PedalDown) ---
                    if (_isCapturing)
                    {
                        _suppressedSources.Add(sourceKey);
                        OnInputCaptured?.Invoke("pedal");
                        return;
                    }

                    if (!_isListening) return;

                    var settings = _settingsProvider();
                    string? mappedKey = null;

                    lock (settings.MappingLock)
                    {
                        if (settings.Mapping.TryGetValue("pedal", out var key))
                        {
                            mappedKey = key;
                        }
                    }

                    if (mappedKey != null)
                    {
                        var resolved = KeyResolver.Resolve(mappedKey, settings.KeyboardLayout);
                        if (resolved.IsValid)
                        {
                            if (_keySimulator.PressResolvedKey(resolved))
                            {
                                _activeNotes[sourceKey] = resolved;
                            }
                        }
                    }
                }
                else
                {
                    // --- ペダル解放 (PedalUp) ---
                    if (_suppressedSources.Remove(sourceKey))
                    {
                        return;
                    }

                    if (_activeNotes.Remove(sourceKey, out var resolvedKey))
                    {
                        _keySimulator.ReleaseResolvedKey(resolvedKey);
                    }
                }
            }
        }

        /// <summary>
        /// テスト用: 現在保持されているアクティブノート数を取得します。
        /// </summary>
        public int ActiveNotesCount
        {
            get
            {
                lock (_stateLock) return _activeNotes.Count;
            }
        }

        /// <summary>
        /// テスト用: 抑止対象の入力元数を取得します。
        /// </summary>
        public int SuppressedSourcesCount
        {
            get
            {
                lock (_stateLock) return _suppressedSources.Count;
            }
        }
    }
}
