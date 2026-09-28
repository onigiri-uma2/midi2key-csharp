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

        // キャプチャモード中に新規取得され、対応するNoteOffを受信するまでキー解放を抑止する入力元のセット
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
        /// 終了時にも、キャプチャ中に押されたノートに対する抑止セットは維持されます。
        /// </summary>
        public void SetCapturing(bool capturing)
        {
            _isCapturing = capturing;
        }

        /// <summary>
        /// MIDIノートイベント（NoteOn / NoteOff）を処理します。
        /// イベントに焼き付けられたGenerationを検証し、キャプチャ開始前の既存ノートを保護します。
        /// </summary>
        public void ProcessNoteEvent(MidiNoteData note)
        {
            // (1) Generationの検証（停止後や旧セッションの遅延イベントはロック取得前に即破棄）
            if (note.Generation != Interlocked.Read(ref _currentSessionId))
            {
                return;
            }

            var sourceKey = new MidiSourceKey(note.DeviceId, note.Channel, note.NoteNumber, false);

            lock (_stateLock)
            {
                if (note.Generation != Interlocked.Read(ref _currentSessionId)) return;

                if (note.IsDown)
                {
                    // --- NoteOn 処理 ---

                    // 【最優先】既に通常変換で押下中のノートであるか確認
                    if (_activeNotes.ContainsKey(sourceKey))
                    {
                        // 既に押下中の入力元であれば、キャプチャ中であっても重複NoteOnとして扱い、
                        // 抑止対象には追加せず、参照カウントの重複増加も防止する
                        return;
                    }

                    // キャプチャモード中の新規NoteOn
                    if (_isCapturing)
                    {
                        _suppressedSources.Add(sourceKey);
                        OnInputCaptured?.Invoke(note.NoteNumber.ToString());
                        return;
                    }

                    // 通常変換実行中でない場合は何もしない
                    if (!_isListening) return;

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
        /// </summary>
        public void ProcessControlEvent(MidiControlData cc)
        {
            if (cc.ControlNumber != 64) return; // サステインペダル以外は無視

            if (cc.Generation != Interlocked.Read(ref _currentSessionId))
            {
                return;
            }

            string pedalKey = $"{cc.DeviceId}:{cc.Channel}";
            var sourceKey = new MidiSourceKey(cc.DeviceId, cc.Channel, cc.ControlNumber, true);
            bool isDown = cc.ControlValue >= 64;

            lock (_stateLock)
            {
                if (cc.Generation != Interlocked.Read(ref _currentSessionId)) return;

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
                    // 既に通常変換で押下中であれば重複追加しない
                    if (_activeNotes.ContainsKey(sourceKey))
                    {
                        return;
                    }

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

        /// <summary>
        /// テスト用: 指定入力元がアクティブノートとして保持されているか確認します。
        /// </summary>
        public bool IsNoteActive(MidiSourceKey key)
        {
            lock (_stateLock) return _activeNotes.ContainsKey(key);
        }
    }
}
