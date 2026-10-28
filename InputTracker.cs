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
    public readonly record struct MidiMonitorSample(string DeviceId, string DeviceName, int Channel,
        int Number, int Value, bool IsDown, long Generation);

    public class InputTracker
    {
        private static readonly string[] NoteNumbers = CreateNoteNumbers();

        private static string[] CreateNoteNumbers()
        {
            var numbers = new string[128];
            for (int i = 0; i < numbers.Length; i++) numbers[i] = i.ToString();
            return numbers;
        }

        private struct CachedResolution
        {
            public string? Mapping;
            public string? Layout;
            public ResolvedKey Key;
        }

        private readonly KeySimulator _keySimulator;
        private readonly Func<AppSettings> _settingsProvider;
        private readonly object _stateLock = new();
        private readonly object _transitionLock = new();
        private ResolvedMappingSnapshot? _resolvedMapping;

        // セッションID（Generation）: 停止・再起動時にインクリメントされ、旧セッションの遅延イベントを破棄します
        private long _currentSessionId = 0;

        // 変換実行中フラグ
        private volatile bool _isListening = false;
        private volatile bool _testMode;
        public bool IsTestMode => _testMode;
        public bool MonitorEnabled { get; set; }
        private MidiMonitorSample? _lastNote;
        private MidiMonitorSample? _lastPedal;

        // Two latest values only: callbacks do not format strings or enqueue UI messages.
        public (MidiMonitorSample? Note, MidiMonitorSample? Pedal) ReadMonitor()
        {
            lock (_stateLock)
            {
                MidiMonitorSample? Current(MidiMonitorSample? sample) =>
                    (_isListening || _isCapturing) && sample is { } value &&
                    value.Generation == _currentSessionId && !_disconnectedDeviceIds.Contains(value.DeviceId)
                        ? sample : null;
                return (Current(_lastNote), Current(_lastPedal));
            }
        }

        // キャプチャモード（ノート/ペダル取得）フラグ
        private volatile bool _isCapturing = false;

        // NoteOn時点で解決されたキー情報のスナップショット（入力元 -> 解決キー）
        private readonly Dictionary<MidiSourceKey, ResolvedKey> _activeNotes = new();

        // ペダルの現在状態（DeviceId, Channel -> isPressed）
        private readonly Dictionary<(string DeviceId, int Channel), bool> _pedalStates = new();

        // マッピング辞書は編集可能なので、値と配列を毎回照合してから解決済みキーを使う
        private readonly CachedResolution[] _noteResolutions = new CachedResolution[128];
        private CachedResolution _pedalResolution;

        private static string NoteNumberText(int number) =>
            (uint)number < (uint)NoteNumbers.Length ? NoteNumbers[number] : number.ToString();

        private static ResolvedKey ResolveCached(string mapping, string layout, ref CachedResolution cache)
        {
            if (cache.Mapping != mapping || cache.Layout != layout)
            {
                cache.Mapping = mapping;
                cache.Layout = layout;
                cache.Key = KeyResolver.Resolve(mapping, layout);
            }
            return cache.Key;
        }

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
        /// 設定変更時に呼び出し、解決済みマッピングを原子的に公開します。
        /// 公開後の辞書の直接変更は次の公開まで反映されません。未公開の場合は従来の可変設定を参照します。
        /// </summary>
        public void PublishResolvedMapping(AppSettings settings) =>
            Volatile.Write(ref _resolvedMapping, ResolvedMappingSnapshot.Create(settings));

        /// <summary>現在公開済みの不変マッピングを取得します。ウォームアップ用の別インスタンスと共有できます。</summary>
        internal ResolvedMappingSnapshot? GetResolvedMappingSnapshot() => Volatile.Read(ref _resolvedMapping);

        internal void PublishResolvedMapping(ResolvedMappingSnapshot snapshot) =>
            Volatile.Write(ref _resolvedMapping, snapshot ?? throw new ArgumentNullException(nameof(snapshot)));

        private int TransposedNoteNumber(int number) => number +
            (Volatile.Read(ref _resolvedMapping)?.TransposeSemitones ?? _settingsProvider().TransposeSemitones);

        private ResolvedKey ResolveNote(int number)
        {
            var snapshot = Volatile.Read(ref _resolvedMapping);
            if (snapshot != null) return snapshot.GetNote(number);
            var settings = _settingsProvider();
            string? mapping;
            lock (settings.MappingLock)
            {
                int shifted = number + settings.TransposeSemitones;
                if ((uint)number >= 128 || (uint)shifted >= 128) return ResolvedKey.None;
                settings.Mapping.TryGetValue(NoteNumberText(shifted), out mapping);
            }
            if (mapping == null) return ResolvedKey.None;
            return (uint)number < (uint)_noteResolutions.Length
                ? ResolveCached(mapping, settings.KeyboardLayout, ref _noteResolutions[number])
                : KeyResolver.Resolve(mapping, settings.KeyboardLayout);
        }

        private ResolvedKey ResolvePedal()
        {
            var snapshot = Volatile.Read(ref _resolvedMapping);
            if (snapshot != null) return snapshot.Pedal;
            var settings = _settingsProvider();
            string? mapping;
            lock (settings.MappingLock)
                settings.Mapping.TryGetValue("pedal", out mapping);
            return mapping == null ? ResolvedKey.None : ResolveCached(mapping, settings.KeyboardLayout, ref _pedalResolution);
        }

        /// <summary>
        /// 通常の変換セッションを開始します。新しいGenerationを発行し、旧イベントを無効化します。
        /// </summary>
        public long StartSession() => StartConversionSession();

        public long StartConversionSession(bool testMode = false)
        {
            using var notifications = _keySimulator.DeferNotifications();
            lock (_transitionLock)
            lock (_stateLock)
            {
                long newSessionId = Interlocked.Increment(ref _currentSessionId);
                // 重複開始でも旧セッションの論理参照と物理押下を残さない。
                if ((_isListening && !_keySimulator.ReleaseAllKeys()) ||
                    !_keySimulator.TryPrepareStartConversion())
                {
                    _isListening = false;
                    _testMode = false;
                    _lastNote = _lastPedal = null;
                    _isCapturing = false;
                    _activeNotes.Clear();
                    _pedalStates.Clear();
                    _suppressedSources.Clear();
                    _disconnectedDeviceIds.Clear();
                    throw new InvalidOperationException("前のセッションのキーを解放できなかったため開始できません。");
                }
                _testMode = testMode;
                _lastNote = _lastPedal = null;
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
            lock (_transitionLock)
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
            lock (_transitionLock)
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
            using var notifications = _keySimulator.DeferNotifications();
            lock (_transitionLock)
            {
                // 状態ロックを待つ前に旧イベントを失効させる。Start/Captureとも直列化する。
                Interlocked.Increment(ref _currentSessionId);
                _isListening = false;
                _isCapturing = false;
                lock (_stateLock)
                {
                    _testMode = false;
                    _lastNote = _lastPedal = null;
                    _keySimulator.ReleaseAllKeys();
                    _activeNotes.Clear();
                    _pedalStates.Clear();
                    _suppressedSources.Clear();
                    _disconnectedDeviceIds.Clear();
                }
            }
        }

        /// <summary>
        /// 変換実行中のキャプチャモード（ノート番号欄の自動入力待ち）の開始/終了を設定します。
        /// 実行中のリスナーやGenerationは変更せず、キャプチャフラグのみを切り替えます。
        /// </summary>
        public void SetCapturing(bool capturing)
        {
            lock (_transitionLock)
            lock (_stateLock)
                _isCapturing = capturing;
        }

        /// <summary>
        /// MIDIノートイベント（NoteOn / NoteOff）を処理します。
        /// </summary>
        public void ProcessNoteEvent(MidiNoteData note)
        {
            using var inputScope = LatencyMeasurement.BeginInput(note.TraceId);
            LatencyMeasurement.RecordTrackerEntry(note.TraceId);
            // (1) Generationの検証（停止後や旧セッションの遅延イベントは即破棄）
            if (note.Generation != Interlocked.Read(ref _currentSessionId))
            {
                LatencyMeasurement.RecordSuppressed(note.TraceId, "stale_generation");
                return;
            }

            var sourceKey = new MidiSourceKey(note.DeviceId, note.Channel, note.NoteNumber, false);
            string? capturedInput = null;
            using var notifications = _keySimulator.DeferNotifications();
            try
            {
                lock (_stateLock)
                {
                if (note.Generation != Interlocked.Read(ref _currentSessionId))
                {
                    LatencyMeasurement.RecordSuppressed(note.TraceId, "stale_generation");
                    return;
                }

                // 切断済みDeviceIdからの遅延イベントを拒否
                if (_disconnectedDeviceIds.Contains(note.DeviceId))
                {
                    DiagnosticLogger.Log($"[InputTracker] Dropped note event from disconnected device '{note.DeviceId}': Note={note.NoteNumber}, IsDown={note.IsDown}, Gen={note.Generation}");
                    LatencyMeasurement.RecordSuppressed(note.TraceId, "disconnected_device");
                    return;
                }

                if (MonitorEnabled && (_isListening || _isCapturing))
                    _lastNote = new(note.DeviceId, note.DeviceName, note.Channel, note.NoteNumber,
                        note.Velocity, note.IsDown, note.Generation);

                if (note.IsDown)
                {
                    // --- NoteOn 処理 ---

                    // 1. 【最優先】既に通常変換で押下中のノートであるか確認
                    if (_activeNotes.ContainsKey(sourceKey))
                    {
                        LatencyMeasurement.RecordSuppressed(note.TraceId, "duplicate_note_on");
                        // 既に押下中の入力元であれば、キャプチャ中であっても重複NoteOnとして既存状態を維持
                        return;
                    }

                    // 2. キャプチャ抑止中であるか確認（キャプチャ終了後の重複NoteOn対策）
                    if (_suppressedSources.Contains(sourceKey))
                    {
                        LatencyMeasurement.RecordSuppressed(note.TraceId, "capture_suppressed");
                        // キャプチャ中に取得されNoteOff待ちの入力であれば、キャプチャ終了後も重複NoteOnを無視
                        return;
                    }

                    // 3. キャプチャモード中の新規NoteOn
                    if (_isCapturing)
                    {
                        _suppressedSources.Add(sourceKey);
                        int shifted = TransposedNoteNumber(note.NoteNumber);
                        if ((uint)shifted < 128) capturedInput = NoteNumberText(shifted);
                        LatencyMeasurement.RecordSuppressed(note.TraceId, "capture");
                        return;
                    }

                    // 4. 通常変換実行中でない場合は何もしない（停止中など）
                    if (!_isListening)
                    {
                        LatencyMeasurement.RecordSuppressed(note.TraceId, "not_listening");
                        return;
                    }

                    // 5. 通常変換の新規NoteOn処理
                    if (_testMode)
                    {
                        LatencyMeasurement.RecordSuppressed(note.TraceId, "test_mode");
                        return;
                    }
                    var resolved = ResolveNote(note.NoteNumber);
                    if (resolved.IsValid)
                    {
                        if (_keySimulator.PressResolvedKey(resolved))
                        {
                            _activeNotes[sourceKey] = resolved;
                        }
                    }
                    else LatencyMeasurement.RecordSuppressed(note.TraceId, "unmapped");
                }
                else
                {
                    // --- NoteOff 処理 ---

                    // キャプチャ中に取得されたノートの解放であれば、抑止セットから除去して終了（キー解放はしない）
                    if (_suppressedSources.Remove(sourceKey))
                    {
                        LatencyMeasurement.RecordSuppressed(note.TraceId, "capture_release");
                        return;
                    }

                    // キャプチャ開始前から押下されていた通常ノート、または通常変換中のノートを解放
                    if (_activeNotes.Remove(sourceKey, out var resolvedKey))
                    {
                        _keySimulator.ReleaseResolvedKey(resolvedKey);
                    }
                    else LatencyMeasurement.RecordSuppressed(note.TraceId, "no_active_note");
                }
                }
            }
            finally
            {
                if (capturedInput != null) OnInputCaptured?.Invoke(capturedInput);
            }
        }

        /// <summary>
        /// コントロールチェンジ（サステインペダル CC64 など）を処理します。
        /// </summary>
        public void ProcessControlEvent(MidiControlData cc)
        {
            using var inputScope = LatencyMeasurement.BeginInput(cc.TraceId);
            LatencyMeasurement.RecordTrackerEntry(cc.TraceId);
            if (cc.ControlNumber != 64)
            {
                LatencyMeasurement.RecordSuppressed(cc.TraceId, "unsupported_control");
                return;
            }

            if (cc.Generation != Interlocked.Read(ref _currentSessionId))
            {
                LatencyMeasurement.RecordSuppressed(cc.TraceId, "stale_generation");
                return;
            }

            var pedalKey = (cc.DeviceId, cc.Channel);
            var sourceKey = new MidiSourceKey(cc.DeviceId, cc.Channel, cc.ControlNumber, true);
            bool isDown = cc.ControlValue >= 64;

            string? capturedInput = null;
            using var notifications = _keySimulator.DeferNotifications();
            try
            {
                lock (_stateLock)
                {
                if (cc.Generation != Interlocked.Read(ref _currentSessionId))
                {
                    LatencyMeasurement.RecordSuppressed(cc.TraceId, "stale_generation");
                    return;
                }

                // 切断済みDeviceIdからの遅延イベントを拒否
                if (_disconnectedDeviceIds.Contains(cc.DeviceId))
                {
                    DiagnosticLogger.Log($"[InputTracker] Dropped CC64 event from disconnected device '{cc.DeviceId}': Val={cc.ControlValue}, Gen={cc.Generation}");
                    LatencyMeasurement.RecordSuppressed(cc.TraceId, "disconnected_device");
                    return;
                }

                if (MonitorEnabled && (_isListening || _isCapturing))
                    _lastPedal = new(cc.DeviceId, cc.DeviceName, cc.Channel, cc.ControlNumber,
                        cc.ControlValue, isDown, cc.Generation);

                _pedalStates.TryGetValue(pedalKey, out bool currentDown);

                // 状態に変化がない重複イベント（例: 127→127→100、または 63→0）は無視
                if (isDown == currentDown)
                {
                    LatencyMeasurement.RecordSuppressed(cc.TraceId, "duplicate_control");
                    return;
                }

                _pedalStates[pedalKey] = isDown;

                if (isDown)
                {
                    // --- ペダル踏み込み (PedalDown) ---
                    // 1. 既に通常変換で押下中であれば重複追加しない
                    if (_activeNotes.ContainsKey(sourceKey))
                    {
                        LatencyMeasurement.RecordSuppressed(cc.TraceId, "duplicate_control");
                        return;
                    }

                    // 2. キャプチャ抑止中であれば重複踏み込みを無視
                    if (_suppressedSources.Contains(sourceKey))
                    {
                        LatencyMeasurement.RecordSuppressed(cc.TraceId, "capture_suppressed");
                        return;
                    }

                    // 3. キャプチャモード中の新規踏み込み
                    if (_isCapturing)
                    {
                        _suppressedSources.Add(sourceKey);
                        capturedInput = "pedal";
                        LatencyMeasurement.RecordSuppressed(cc.TraceId, "capture");
                        return;
                    }

                    // 4. 通常変換実行中でない場合は何もしない
                    if (!_isListening)
                    {
                        LatencyMeasurement.RecordSuppressed(cc.TraceId, "not_listening");
                        return;
                    }

                    // 5. 通常変換処理
                    if (_testMode)
                    {
                        LatencyMeasurement.RecordSuppressed(cc.TraceId, "test_mode");
                        return;
                    }
                    var resolved = ResolvePedal();
                    if (resolved.IsValid)
                    {
                        if (_keySimulator.PressResolvedKey(resolved))
                        {
                            _activeNotes[sourceKey] = resolved;
                        }
                    }
                    else LatencyMeasurement.RecordSuppressed(cc.TraceId, "unmapped");
                }
                else
                {
                    // --- ペダル解放 (PedalUp) ---
                    if (_suppressedSources.Remove(sourceKey))
                    {
                        LatencyMeasurement.RecordSuppressed(cc.TraceId, "capture_release");
                        return;
                    }

                    if (_activeNotes.Remove(sourceKey, out var resolvedKey))
                    {
                        _keySimulator.ReleaseResolvedKey(resolvedKey);
                    }
                    else LatencyMeasurement.RecordSuppressed(cc.TraceId, "no_active_control");
                }
                }
            }
            finally
            {
                if (capturedInput != null) OnInputCaptured?.Invoke(capturedInput);
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
            using var notifications = _keySimulator.DeferNotifications();
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
                var pedalKeys = new List<(string DeviceId, int Channel)>();
                foreach (var pk in _pedalStates.Keys)
                {
                    if (pk.DeviceId == deviceId)
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
