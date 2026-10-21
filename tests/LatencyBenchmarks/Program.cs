using System.Diagnostics;
using System.Text.Json;
using MidiToKeyApp;
using WindowsInput.Native;

// The recorder is the only replacement: InputTracker, KeySimulator and key resolution are production code.
var iterations = ArgumentValue("--iterations", 3000);
var warmup = ArgumentValue("--warmup", 1000);
var rounds = ArgumentValue("--rounds", 5);
var label = ArgumentText("--label", "current");
if (iterations < 1 || warmup < 0 || rounds < 1) throw new ArgumentException("Invalid run length.");

var overhead = MeasureEmptyOverhead();
var results = new List<object>();
foreach (var scenario in new[] { "repeated_note", "ten_note_chord", "pedal", "two_devices_parallel" })
{
    for (var round = 0; round < rounds; round++)
    {
        results.Add(Run(scenario, iterations, warmup, round));
    }
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    schema = 1,
    label,
    utc = DateTimeOffset.UtcNow,
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    processorCount = Environment.ProcessorCount,
    stopwatchFrequency = Stopwatch.Frequency,
    iterationsPerRound = iterations,
    warmupIterationsPerRound = warmup,
    rounds,
    emptyMeasurementOverhead = overhead,
    boundary = "InputTracker.ProcessNoteEvent/ProcessControlEvent entry to first IKeyboardOutput.SendHardwareKey callback and method return; synthetic prebuilt MIDI event; no device enumeration, MIDI driver, UI, native SendInput or receiving application",
    output = "Recording IKeyboardOutput; no OS key injection",
    results
}, new JsonSerializerOptions { WriteIndented = true }));

static int ArgumentValue(string name, int fallback) =>
    int.TryParse(ArgumentText(name, fallback.ToString()), out var value) ? value : throw new ArgumentException(name);

static string ArgumentText(string name, string fallback)
{
    var index = Array.IndexOf(Environment.GetCommandLineArgs(), name);
    var args = Environment.GetCommandLineArgs();
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}

static object MeasureEmptyOverhead()
{
    const int count = 100000;
    var ticks = new long[count];
    for (var i = 0; i < count; i++)
    {
        var start = Stopwatch.GetTimestamp();
        ticks[i] = Stopwatch.GetTimestamp() - start;
    }
    return Stats(ticks);
}

static object Run(string scenario, int iterations, int warmup, int round)
{
    var settings = new AppSettings { KeyboardLayout = "JIS" };
    var keys = new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j" };
    for (var i = 0; i < 10; i++) settings.Mapping[(60 + i).ToString()] = keys[i];
    settings.Mapping["pedal"] = "k";
    var output = new RecordingOutput();
    var simulator = new KeySimulator(output);
    var tracker = new InputTracker(simulator, () => settings);
#if !BASELINE
    tracker.PublishResolvedMapping(settings);
#endif
    var generation = tracker.StartConversionSession();
    var down = new MidiNoteData[2, 10];
    var up = new MidiNoteData[2, 10];
    for (var d = 0; d < 2; d++) for (var n = 0; n < 10; n++)
    {
        var device = d == 0 ? "device_a" : "device_b";
        down[d, n] = new MidiNoteData(device, device, 0, 60 + n, 100, true, generation);
        up[d, n] = new MidiNoteData(device, device, 0, 60 + n, 0, false, generation);
    }
    var pedalDown = new MidiControlData("device_a", "device_a", 0, 64, 127, generation);
    var pedalUp = new MidiControlData("device_a", "device_a", 0, 64, 0, generation);
    // Prime JIT, mappings and dictionaries on the same tracker used for measurement.
    RecordingOutput.CurrentProbe = new Probe();
    for (var i = 0; i < warmup; i++)
    {
        if (scenario == "pedal")
        {
            tracker.ProcessControlEvent(pedalDown);
            tracker.ProcessControlEvent(pedalUp);
        }
        else if (scenario == "ten_note_chord")
        {
            for (var n = 0; n < 10; n++) tracker.ProcessNoteEvent(down[0, n]);
            for (var n = 0; n < 10; n++) tracker.ProcessNoteEvent(up[0, n]);
        }
        else if (scenario == "two_devices_parallel")
        {
            tracker.ProcessNoteEvent(down[0, 0]);
            tracker.ProcessNoteEvent(up[0, 0]);
            tracker.ProcessNoteEvent(down[1, 1]);
            tracker.ProcessNoteEvent(up[1, 1]);
        }
        else
        {
            tracker.ProcessNoteEvent(down[0, 0]);
            tracker.ProcessNoteEvent(up[0, 0]);
        }
    }
    output.ResetCount();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var eventsPerIteration = scenario == "ten_note_chord" ? 20 : scenario == "two_devices_parallel" ? 4 : 2;
    var eventCount = iterations * eventsPerIteration;
    var elapsed = new long[eventCount];
    var firstOutput = new long[eventCount];
    var allocations = new long[eventCount];
    var bursts = scenario == "ten_note_chord" ? new long[iterations] : Array.Empty<long>();
    var expected = eventCount;
    long wallStart;
    long wallEnd;

    if (scenario == "two_devices_parallel")
    {
        using var gate = new ManualResetEventSlim(false);
        using var ready = new CountdownEvent(2);
        Exception? errorA = null, errorB = null;
        var a = new Thread(() => Worker(0, ref errorA));
        var b = new Thread(() => Worker(1, ref errorB));
        a.Start(); b.Start();
        ready.Wait();
        wallStart = Stopwatch.GetTimestamp();
        gate.Set();
        a.Join(); b.Join();
        wallEnd = Stopwatch.GetTimestamp();
        if (errorA != null || errorB != null) throw new AggregateException(new[] { errorA, errorB }.Where(e => e != null)!);

        void Worker(int device, ref Exception? error)
        {
            try
            {
                var probe = new Probe();
                RecordingOutput.CurrentProbe = probe;
                ready.Signal();
                gate.Wait();
                for (var i = 0; i < iterations; i++)
                {
                    var slot = device * iterations * 2 + i * 2;
                    MeasureNote(tracker, down[device, device], probe, elapsed, firstOutput, allocations, slot);
                    MeasureNote(tracker, up[device, device], probe, elapsed, firstOutput, allocations, slot + 1);
                }
            }
            catch (Exception ex) { error = ex; }
        }
    }
    else
    {
        var probe = new Probe();
        RecordingOutput.CurrentProbe = probe;
        wallStart = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++)
        {
            var index = i * eventsPerIteration;
            if (scenario == "pedal")
            {
                MeasureControl(tracker, pedalDown, probe, elapsed, firstOutput, allocations, index);
                MeasureControl(tracker, pedalUp, probe, elapsed, firstOutput, allocations, index + 1);
            }
            else if (scenario == "ten_note_chord")
            {
                var burstStart = Stopwatch.GetTimestamp();
                for (var n = 0; n < 10; n++) MeasureNote(tracker, down[0, n], probe, elapsed, firstOutput, allocations, index + n);
                bursts[i] = probe.LastOutput - burstStart;
                for (var n = 0; n < 10; n++) MeasureNote(tracker, up[0, n], probe, elapsed, firstOutput, allocations, index + 10 + n);
            }
            else
            {
                MeasureNote(tracker, down[0, 0], probe, elapsed, firstOutput, allocations, index);
                MeasureNote(tracker, up[0, 0], probe, elapsed, firstOutput, allocations, index + 1);
            }
        }
        wallEnd = Stopwatch.GetTimestamp();
    }

    if (output.Count != expected || tracker.ActiveNotesCount != 0 ||
        simulator.GetRefCount(VirtualKeyCode.VK_A) != 0 ||
        simulator.GetRefCount(VirtualKeyCode.VK_B) != 0 ||
        simulator.GetRefCount(VirtualKeyCode.VK_K) != 0)
        throw new InvalidOperationException($"{scenario}: outputs={output.Count}/{expected}, active={tracker.ActiveNotesCount}");
    return new
    {
        scenario, round, iterations, events = eventCount,
        expectedOutputs = expected, actualOutputs = output.Count,
        activeNotesAfter = tracker.ActiveNotesCount,
        firstOutputMicroseconds = Stats(firstOutput),
        processMicroseconds = Stats(elapsed),
        downFirstOutputMicroseconds = Stats(SelectPhase(firstOutput, scenario, true)),
        upFirstOutputMicroseconds = Stats(SelectPhase(firstOutput, scenario, false)),
        downProcessMicroseconds = Stats(SelectPhase(elapsed, scenario, true)),
        upProcessMicroseconds = Stats(SelectPhase(elapsed, scenario, false)),
        allocatedBytesPerEvent = StatsBytes(allocations),
        chordLastOutputFromBurstStartMicroseconds = bursts.Length > 0 ? Stats(bursts) : null,
        wallMilliseconds = TicksToMilliseconds(wallEnd - wallStart)
    };
}

static long[] SelectPhase(long[] values, string scenario, bool down)
{
    var result = new long[values.Length / 2];
    var width = scenario == "ten_note_chord" ? 20 : 2;
    var phaseWidth = width / 2;
    var cursor = 0;
    for (var baseIndex = 0; baseIndex < values.Length; baseIndex += width)
    for (var i = 0; i < phaseWidth; i++)
        result[cursor++] = values[baseIndex + (down ? 0 : phaseWidth) + i];
    return result;
}

static void MeasureNote(InputTracker tracker, MidiNoteData note, Probe probe, long[] elapsed, long[] first, long[] allocated, int index)
{
    probe.FirstOutput = 0;
    var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
    probe.Start = Stopwatch.GetTimestamp();
    tracker.ProcessNoteEvent(note);
    var end = Stopwatch.GetTimestamp();
    allocated[index] = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
    elapsed[index] = end - probe.Start;
    first[index] = probe.FirstOutput - probe.Start;
    if (probe.FirstOutput == 0) throw new InvalidOperationException("Expected output callback was absent.");
}

static void MeasureControl(InputTracker tracker, MidiControlData control, Probe probe, long[] elapsed, long[] first, long[] allocated, int index)
{
    probe.FirstOutput = 0;
    var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
    probe.Start = Stopwatch.GetTimestamp();
    tracker.ProcessControlEvent(control);
    var end = Stopwatch.GetTimestamp();
    allocated[index] = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
    elapsed[index] = end - probe.Start;
    first[index] = probe.FirstOutput - probe.Start;
    if (probe.FirstOutput == 0) throw new InvalidOperationException("Expected output callback was absent.");
}

static object Stats(long[] values)
{
    var sorted = (long[])values.Clone();
    Array.Sort(sorted);
    return new
    {
        mean = sorted.Average(v => TicksToMicroseconds(v)),
        p50 = TicksToMicroseconds(sorted[(int)Math.Ceiling(sorted.Length * .50) - 1]),
        p95 = TicksToMicroseconds(sorted[(int)Math.Ceiling(sorted.Length * .95) - 1]),
        p99 = TicksToMicroseconds(sorted[(int)Math.Ceiling(sorted.Length * .99) - 1]),
        max = TicksToMicroseconds(sorted[^1])
    };
}

static object StatsBytes(long[] values)
{
    var sorted = (long[])values.Clone();
    Array.Sort(sorted);
    return new
    {
        mean = sorted.Average(),
        p50 = sorted[(int)Math.Ceiling(sorted.Length * .50) - 1],
        p95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1],
        p99 = sorted[(int)Math.Ceiling(sorted.Length * .99) - 1],
        max = sorted[^1]
    };
}

static double TicksToMicroseconds(long ticks) => ticks * 1_000_000d / Stopwatch.Frequency;
static double TicksToMilliseconds(long ticks) => ticks * 1_000d / Stopwatch.Frequency;

sealed class Probe
{
    public long Start;
    public long FirstOutput;
    public long LastOutput;
}

sealed class RecordingOutput : IKeyboardOutput
{
    [ThreadStatic] public static Probe? CurrentProbe;
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public void ResetCount() => Volatile.Write(ref _count, 0);
    public int LastWin32Error => 0;

    public bool SendHardwareKey(VirtualKeyCode vk, bool isDown)
    {
        var timestamp = Stopwatch.GetTimestamp();
        var probe = CurrentProbe ?? throw new InvalidOperationException("Missing thread probe.");
        if (probe.FirstOutput == 0) probe.FirstOutput = timestamp;
        probe.LastOutput = timestamp;
        Interlocked.Increment(ref _count);
        return true;
    }
}
