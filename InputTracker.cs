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

        // 現在のセッション内で切断・無効化されたDeviceIdのセット（同一Generation内の遅延イベントを拒否）
        private readonly HashSet<string> _disconnectedDeviceIds = new();

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
        /// 通常の変換セッションを開始します。新しいGenerationを発行し、旧イベントを無効化します。
        /// </summary>
        public long StartSession() => StartConversionSession();

        public long StartConversionSession()
        {
            lock (_stateLock)
            {
                long newSessionId = Interlocked.Increment(ref _currentSessionId);
                _isListening = true;
                _isCapturing = false;
                _activeNotes.Clear();
                _pedalStates.Clear();
                _suppressedSources.Clear();
                _disconnectedDeviceIds.Clear();
                return newSessionId;
            }
        }

        /// <summary>
        /// ノート番号欄へフォーカスした際、キャプチャモードを開始します。
        /// 停止中の場合は新しいGenerationを発行してMidiListenerへ同期させます。
        /// 変換実行中の場合はGenerationおよび既存の押下状態を維持します。
        /// </summary>
        public long StartCaptureSession()
        {
            lock (_stateLock)
            {
                _isCapturing = true;

                if (_isListening)
                {
                    // 変換実行中のキャプチャ開始: Generationを変更せず、既存の押下状態(_activeNotes)も維持
                    return Interlocked.Read(ref _currentSessionId);
                }
                else
                {
                    // 変換停止中のキャプチャ開始: 新しいGenerationを発行し、内部状態を整理
                    long newSessionId = Interlocked.Increment(ref _currentSessionId);
                    _activeNotes.Clear();
                    _pedalStates.Clear();
                    _suppressedSources.Clear();
                    _disconnectedDeviceIds.Clear();
                    return newSessionId;
                }
            }
        }

        /// <summary>
        /// キャプチャモードを終了します。
        /// 変換停止中の場合はGenerationを無効化し、キャプチャ専用の入力状態を整理します。
        /// 変換実行中の場合はGenerationを変更せず、キャプチャ中に押されたノートの抑止はNoteOffまで維持します。
        /// </summary>
        public void StopCaptureSession()
        {
            lock (_stateLock)
            {
                _isCapturing = false;

                if (!_isListening)
                {
                    // 変換停止中: Generationを無効化し、状態を整理
                    Interlocked.Increment(ref _currentSessionId);
                    _suppressedSources.Clear();
                    _pedalStates.Clear();
                    _disconnectedDeviceIds.Clear();
                }
                // 変換実行中の場合: Generationは変更せず、_suppressedSourcesはNoteOffまで維持
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
                _disconnectedDeviceIds.Clear();
            }
        }

        /// <summary>
        /// 変換実行中のキャプチャモード（ノート番号欄の自動入力待ち）の開始/終了を設定します。
        /// 実行中のリスナーやGenerationは変更せず、キャプチャフラグのみを切り替えます。
        /// </summary>
        public void SetCapturing(bool capturing)
        {
            _isCapturing = capturing;
        }

        /// <summary>
        /// MIDIノートイベント（NoteOn / NoteOff）を処理します。
        /// </summary>
        public void ProcessNoteEvent(MidiNoteData note)
        {
            // (1) Generationの検証（停止後や旧セッションの遅延イベントは即破棄）
            if (note.Generation != Interlocked.Read(ref _currentSessionId))
            {
                return;
            }

            var sourceKey = new MidiSourceKey(note.DeviceId, note.Channel, note.NoteNumber, false);

            lock (_stateLock)
            {
                if (note.Generation != Interlocked.Read(ref _currentSessionId)) return;

                // 切断済みDeviceIdからの遅延イベントを拒否
                if (_disconnectedDeviceIds.Contains(note.DeviceId))
                {
                    DiagnosticLogger.Log($"[InputTracker] Dropped note event from disconnected device '{note.DeviceId}': Note={note.NoteNumber}, IsDown={note.IsDown}, Gen={note.Generation}");
                    return;
                }

                if (note.IsDown)
                {
                    // --- NoteOn 処理 ---

                    // 1. 【最優先】既に通常変換で押下中のノートであるか確認
                    if (_activeNotes.ContainsKey(sourceKey))
                    {
                        // 既に押下中の入力元であれば、キャプチャ中であっても重複NoteOnとして既存状態を維持
                        return;
                    }

                    // 2. キャプチャ抑止中であるか確認（キャプチャ終了後の重複NoteOn対策）
                    if (_suppressedSources.Contains(sourceKey))
                    {
                        // キャプチャ中に取得されNoteOff待ちの入力であれば、キャプチャ終了後も重複NoteOnを無視
                        return;
                    }

                    // 3. キャプチャモード中の新規NoteOn
                    if (_isCapturing)
                    {
                        _suppressedSources.Add(sourceKey);
                        OnInputCaptured?.Invoke(note.NoteNumber.ToString());
                        return;
                    }

                    // 4. 通常変換実行中でない場合は何もしない（停止中など）
                    if (!_isListening) return;

                    // 5. 通常変換の新規NoteOn処理
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

                // 切断済みDeviceIdからの遅延イベントを拒否
                if (_disconnectedDeviceIds.Contains(cc.DeviceId))
                {
                    DiagnosticLogger.Log($"[InputTracker] Dropped CC64 event from disconnected device '{cc.DeviceId}': Val={cc.ControlValue}, Gen={cc.Generation}");
                    return;
                }

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
                    // 1. 既に通常変換で押下中であれば重複追加しない
                    if (_activeNotes.ContainsKey(sourceKey))
                    {
                        return;
                    }

                    // 2. キャプチャ抑止中であれば重複踏み込みを無視
                    if (_suppressedSources.Contains(sourceKey))
                    {
                        return;
                    }

                    // 3. キャプチャモード中の新規踏み込み
                    if (_isCapturing)
                    {
                        _suppressedSources.Add(sourceKey);
                        OnInputCaptured?.Invoke("pedal");
                        return;
                    }

                    // 4. 通常変換実行中でない場合は何もしない
                    if (!_isListening) return;

                    // 5. 通常変換処理
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

        public int ActiveNotesCount
        {
            get
            {
                lock (_stateLock) return _activeNotes.Count;
            }
        }

        public int SuppressedSourcesCount
        {
            get
            {
                lock (_stateLock) return _suppressedSources.Count;
            }
        }

        /// <summary>
        /// 指定されたデバイスが切断された際に、そのデバイスに属する入力状態（ノート、ペダル、キャプチャ抑止）のみを解放・整理します。
        /// 他の正常なデバイスの入力状態および参照カウントは維持されます。
        /// deviceIdが空または特定できない場合は、安全側としてセッション全体を停止します。
        /// </summary>
        public void ReleaseDeviceInputs(string deviceId, long generation)
        {
            if (generation != Interlocked.Read(ref _currentSessionId))
            {
                DiagnosticLogger.Log($"[InputTracker] ReleaseDeviceInputs ignored due to generation mismatch: Received Gen={generation}, Current Gen={CurrentSessionId}");
                return;
            }

            if (string.IsNullOrEmpty(deviceId))
            {
                // 切断元を特定できない場合は安全側として全セッション停止
                DiagnosticLogger.Log($"[InputTracker] ReleaseDeviceInputs: DeviceId is empty or unknown. Safely stopping full session.");
                StopSession();
                return;
            }

            List<ResolvedKey> keysToRelease = new();

            lock (_stateLock)
            {
                if (generation != Interlocked.Read(ref _currentSessionId))
                {
                    DiagnosticLogger.Log($"[InputTracker] ReleaseDeviceInputs lock entered but generation changed: Received Gen={generation}, Current Gen={CurrentSessionId}");
                    return;
                }

                // 1. DeviceIdを無効化（以降、同一Generation内の遅延イベントも拒否）
                _disconnectedDeviceIds.Add(deviceId);

                // 2. 切断デバイスのノート入力を抽出・削除
                var deviceNotes = new List<MidiSourceKey>();
                foreach (var kvp in _activeNotes)
                {
                    if (kvp.Key.DeviceId == deviceId)
                    {
                        deviceNotes.Add(kvp.Key);
                        keysToRelease.Add(kvp.Value);
                    }
                }

                foreach (var k in deviceNotes)
                {
                    _activeNotes.Remove(k);
                }

                // 3. 切断デバイスのペダル状態を削除
                var pedalKeys = new List<string>();
                foreach (var pk in _pedalStates.Keys)
                {
                    if (pk.StartsWith(deviceId + ":", StringComparison.Ordinal))
                    {
                        pedalKeys.Add(pk);
                    }
                }
                foreach (var pk in pedalKeys)
                {
                    _pedalStates.Remove(pk);
                }

                // 4. 切断デバイスのキャプチャ抑止状態を削除
                _suppressedSources.RemoveWhere(s => s.DeviceId == deviceId);

                // 5. 排他制御を保ったままキーシミュレータのキー解放を実行
                // （他デバイスと競合しているキーは参照カウントにより押下維持される）
                foreach (var rk in keysToRelease)
                {
                    _keySimulator.ReleaseResolvedKey(rk);
                }

                DiagnosticLogger.Log($"[InputTracker] ReleaseDeviceInputs succeeded for DeviceId='{deviceId}', Gen={generation}, ReleasedKeys={keysToRelease.Count}, RemainingActiveNotes={_activeNotes.Count}");
            }
        }

        public bool IsDeviceDisconnected(string deviceId)
        {
            lock (_stateLock) return _disconnectedDeviceIds.Contains(deviceId);
        }

        public bool IsNoteActive(MidiSourceKey key)
        {
            lock (_stateLock) return _activeNotes.ContainsKey(key);
        }

        public bool IsSourceSuppressed(MidiSourceKey key)
        {
            lock (_stateLock) return _suppressedSources.Contains(key);
        }
    }
}
