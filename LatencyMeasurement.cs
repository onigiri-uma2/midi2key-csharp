using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace MidiToKeyApp
{
    /// <summary>
    /// Optional, bounded, in-memory tracing of the actual MIDI callback to native SendInput path.
    /// Nothing is written on an input callback. Timestamps share Stopwatch's monotonic clock.
    /// </summary>
    public static class LatencyMeasurement
    {
        private const int Capacity = 100_000;
        private static readonly object Gate = new();
        private static TracePoint[] Points = Array.Empty<TracePoint>();
        private static int _count;
        private static long _dropped;
        private static long _nextTrace;
        private static long _nextSend;
        private static bool _enabled;
        private static string? _requestedPath;
        private static DateTimeOffset _startedUtc;
        private static bool _inputWarmupEnabled = true;
        private static int _inputWarmupAttempts;
        private static int _inputWarmupCompleted;
        private static int _inputWarmupFailures;

        [ThreadStatic] private static long _currentTrace;
        [ThreadStatic] private static long _pendingSend;
        [ThreadStatic] private static long _pendingSendBeforeTicks;

        public static bool Enabled => Volatile.Read(ref _enabled);

        public static void ConfigureInputWarmup(bool enabled) => Volatile.Write(ref _inputWarmupEnabled, enabled);

        public static void RecordInputWarmupAttempt() => Interlocked.Increment(ref _inputWarmupAttempts);
        public static void RecordInputWarmupCompleted() => Interlocked.Increment(ref _inputWarmupCompleted);
        public static void RecordInputWarmupFailure() => Interlocked.Increment(ref _inputWarmupFailures);

        public readonly record struct TracePoint(long TraceId, long SendId, string Stage, long Timestamp,
            string? DeviceId = null, long Generation = 0, string? Detail = null, bool? Success = null);

        public readonly struct InputScope : IDisposable
        {
            private readonly long _previousTrace;
            private readonly long _previousSend;
            private readonly long _previousSendBeforeTicks;

            internal InputScope(long traceId)
            {
                _previousTrace = _currentTrace;
                _previousSend = _pendingSend;
                _previousSendBeforeTicks = _pendingSendBeforeTicks;
                _currentTrace = traceId;
                _pendingSend = 0;
                _pendingSendBeforeTicks = 0;
            }

            public void Dispose()
            {
                _currentTrace = _previousTrace;
                _pendingSend = _previousSend;
                _pendingSendBeforeTicks = _previousSendBeforeTicks;
            }
        }

        public static void Enable(string? outputPath = null)
        {
            lock (Gate)
            {
                if (_enabled) return;
                _count = 0;
                _dropped = 0;
                _nextTrace = 0;
                _nextSend = 0;
                _inputWarmupAttempts = 0;
                _inputWarmupCompleted = 0;
                _inputWarmupFailures = 0;
                Points = new TracePoint[Capacity];
                _requestedPath = outputPath;
                _startedUtc = DateTimeOffset.UtcNow;
                Volatile.Write(ref _enabled, true);
            }
        }

        public static long RecordMidiCallback()
        {
            if (!Enabled) return 0;
            long timestamp = Stopwatch.GetTimestamp();
            long id = Interlocked.Increment(ref _nextTrace);
            Add(new TracePoint(id, 0, "midi_callback", timestamp));
            return id;
        }

        public static void RecordMidiIdentity(long traceId, string deviceId, long generation, string eventType)
        {
            if (traceId == 0 || !Enabled) return;
            Add(new TracePoint(traceId, 0, "midi_identity", Stopwatch.GetTimestamp(), deviceId, generation, eventType));
        }

        public static InputScope BeginInput(long traceId) => new(traceId);

        public static void RecordTrackerEntry(long traceId)
        {
            if (traceId == 0 || !Enabled) return;
            Add(new TracePoint(traceId, 0, "tracker_entry", Stopwatch.GetTimestamp()));
        }

        public static void RecordSuppressed(long traceId, string reason)
        {
            if (traceId == 0 || !Enabled) return;
            Add(new TracePoint(traceId, 0, "suppressed", Stopwatch.GetTimestamp(), Detail: reason));
        }

        public static void RecordSendInputBefore()
        {
            if (!Enabled || _currentTrace == 0) return;
            long sendId = Interlocked.Increment(ref _nextSend);
            _pendingSend = sendId;
            _pendingSendBeforeTicks = Stopwatch.GetTimestamp();
        }

        public static void RecordSendInputAfter(bool success)
        {
            if (!Enabled || _currentTrace == 0 || _pendingSend == 0) return;
            long timestamp = Stopwatch.GetTimestamp();
            // Capture the return before acquiring the recorder lock. This interval
            // therefore measures the native call, excluding trace-buffer contention.
            lock (Gate)
            {
                if (_count <= Capacity - 2)
                {
                    Points[_count++] = new TracePoint(_currentTrace, _pendingSend, "sendinput_before", _pendingSendBeforeTicks);
                    Points[_count++] = new TracePoint(_currentTrace, _pendingSend, "sendinput_return", timestamp, Success: success);
                }
                else _dropped += 2;
            }
            _pendingSend = 0;
            _pendingSendBeforeTicks = 0;
        }

        private static void Add(TracePoint point)
        {
            lock (Gate)
            {
                if (_count < Capacity) Points[_count++] = point;
                else _dropped++;
            }
        }

        internal static void ResetForTesting()
        {
            lock (Gate)
            {
                Volatile.Write(ref _enabled, false);
                _count = 0;
                _dropped = 0;
                _nextTrace = 0;
                _nextSend = 0;
                _inputWarmupAttempts = 0;
                _inputWarmupCompleted = 0;
                _inputWarmupFailures = 0;
                _inputWarmupEnabled = true;
                Points = Array.Empty<TracePoint>();
                _requestedPath = null;
                _currentTrace = 0;
                _pendingSend = 0;
                _pendingSendBeforeTicks = 0;
            }
        }

        /// <summary>Write once after all listeners have stopped; never overwrites an existing file.</summary>
        public static (bool Success, string PathOrError) ExportAfterStop()
        {
            if (!Enabled) return (true, "計測無効");
            Volatile.Write(ref _enabled, false);
            TracePoint[] snapshot;
            long dropped;
            lock (Gate)
            {
                snapshot = new TracePoint[_count];
                Array.Copy(Points, snapshot, _count);
                dropped = _dropped;
                Points = Array.Empty<TracePoint>();
                _count = 0;
            }

            string path = _requestedPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "midi2key", $"latency-{_startedUtc:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(stream, new
                {
                    schemaVersion = 2,
                    source = "native-instrumentation",
                    inputWarmup = new
                    {
                        enabled = Volatile.Read(ref _inputWarmupEnabled),
                        attempts = Volatile.Read(ref _inputWarmupAttempts),
                        completed = Volatile.Read(ref _inputWarmupCompleted),
                        failures = Volatile.Read(ref _inputWarmupFailures),
                        scope = "managed InputTracker and KeySimulator with no-op output; excludes MidiListener and native SendInput"
                    },
                    startedUtc = _startedUtc,
                    endedUtc = DateTimeOffset.UtcNow,
                    stopwatchFrequency = Stopwatch.Frequency,
                    capacity = Capacity,
                    droppedPoints = dropped,
                    notes = "Each trace starts at MidiListener.OnEventReceived. A trace can have zero or multiple native sends. No send means no native SendInput was observed; inspect suppressed points. Times include callback locks and processing. Missing return may indicate an exception or a full buffer.",
                    points = snapshot
                }, new JsonSerializerOptions { WriteIndented = true });
                return (true, Path.GetFullPath(path));
            }
            catch (Exception ex)
            {
                return (false, $"計測結果を保存できませんでした ({path}): {ex.Message}");
            }
        }
    }
}
