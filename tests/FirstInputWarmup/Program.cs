using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MidiToKeyApp;
using WindowsInput.Native;

const int PairCount = 10;
var command = args.FirstOrDefault();
if (command == "--child")
{
    var mode = args.Length > 1 ? args[1] : throw new ArgumentException("Expected cold or warm mode.");
    Console.WriteLine(JsonSerializer.Serialize(RunChild(mode)));
    return;
}

var outputDirectory = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "results", "warmup-20261021");
Directory.CreateDirectory(outputDirectory);
var children = new List<ChildResult>();
for (var pair = 0; pair < PairCount; pair++)
{
    var first = pair % 2 == 0 ? "cold" : "warm";
    children.Add(await LaunchChild(first, pair));
    var second = first == "cold" ? "warm" : "cold";
    children.Add(await LaunchChild(second, pair));
}

var result = new
{
    schema = 1,
    generatedUtc = DateTimeOffset.UtcNow,
    pairs = PairCount,
    order = "alternating cold/warm order by pair",
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    processorCount = Environment.ProcessorCount,
    stopwatchFrequency = Stopwatch.Frequency,
    childResults = children
};
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(outputDirectory, "raw.json"), json);
var cold = children.Where(x => x.Mode == "cold").ToArray();
var warm = children.Where(x => x.Mode == "warm").ToArray();
var summary = new
{
    schema = 1,
    generatedUtc = DateTimeOffset.UtcNow,
    pairs = PairCount,
    cold = Summary(cold),
    warm = Summary(warm),
    warmupCostMicroseconds = Stats(warm.Select(x => x.WarmupMicroseconds).ToArray()),
    interpretation = "First NoteOn/NoteOff after each child's setup, without earlier ProcessNoteEvent calls on the measured tracker. No MIDI device and no OS keyboard output are used."
};
var summaryJson = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(outputDirectory, "summary.json"), summaryJson);
var report = $"""
# First-input warm-up comparison

Generated UTC: {DateTimeOffset.UtcNow:O}. Each sample comes from a fresh child process; cold and warm modes alternate in {PairCount} pairs (n=10 per mode). With this small sample, p95 equals the highest observed sample and is not a stable tail-latency estimate.

| Metric | Cold p50 / p95 / max (µs) | Warm p50 / p95 / max (µs) |
|---|---:|---:|
| NoteOn full processing | {Fmt(cold.Select(x => x.PressElapsedMicroseconds).ToArray())} | {Fmt(warm.Select(x => x.PressElapsedMicroseconds).ToArray())} |
| NoteOn to output callback | {Fmt(cold.Select(x => x.PressToOutputMicroseconds).ToArray())} | {Fmt(warm.Select(x => x.PressToOutputMicroseconds).ToArray())} |
| NoteOff full processing | {Fmt(cold.Select(x => x.ReleaseElapsedMicroseconds).ToArray())} | {Fmt(warm.Select(x => x.ReleaseElapsedMicroseconds).ToArray())} |
| NoteOff to output callback | {Fmt(cold.Select(x => x.ReleaseToOutputMicroseconds).ToArray())} | {Fmt(warm.Select(x => x.ReleaseToOutputMicroseconds).ToArray())} |
| Whole warm-up call | — | {Fmt(warm.Select(x => x.WarmupMicroseconds).ToArray())} |

Measured allocations (bytes per event, p50 / p95 / max): NoteOn cold {FmtBytes(cold.Select(x => x.PressAllocatedBytes).ToArray())}, warm {FmtBytes(warm.Select(x => x.PressAllocatedBytes).ToArray())}; NoteOff cold {FmtBytes(cold.Select(x => x.ReleaseAllocatedBytes).ToArray())}, warm {FmtBytes(warm.Select(x => x.ReleaseAllocatedBytes).ToArray())}.

Every child reported exactly two recorder callbacks, zero active notes, and zero A-key reference count afterward. The warm-up runner uses a private no-op `IKeyboardOutput`; it does not initialize native key output. This isolates managed first-event behavior. It does not measure real MIDI delivery, Windows `SendInput`, or the exact output implementation type used during warm-up versus measured events; type-specific JIT differences may remain. The reported warm-up cost includes Task.Run queueing and completion time.

The warm-up call runs on a worker thread and the measured tracker receives events on the child main thread, matching the application's thread separation. The cold case runs an empty task to equalize task scheduling setup. Child process duration includes process startup and JSON output, and is recorded per sample in `raw.json`. `raw.json` contains all individual timings and environment metadata; `summary.json` contains machine-readable percentiles.
""";
await File.WriteAllTextAsync(Path.Combine(outputDirectory, "report.md"), report);
Console.WriteLine(json);

static ChildResult RunChild(string mode)
{
    if (mode is not ("cold" or "warm")) throw new ArgumentException("Mode must be cold or warm.");
    var settings = new AppSettings { KeyboardLayout = "JIS" };
    settings.Mapping["60"] = "a";
    settings.Mapping["pedal"] = "k";
    var snapshot = ResolvedMappingSnapshot.Create(settings);

    long warmupTicks = 0;
    if (mode == "warm")
    {
        var warmupStart = Stopwatch.GetTimestamp();
        Task.Run(() => InputWarmup.Run(snapshot)).GetAwaiter().GetResult();
        warmupTicks = Stopwatch.GetTimestamp() - warmupStart;
    }
    else
    {
        Task.Run(static () => { }).GetAwaiter().GetResult();
    }

    var output = new RecordingOutput();
    var simulator = new KeySimulator(output);
    var tracker = new InputTracker(simulator, () => settings);
    tracker.PublishResolvedMapping(snapshot);
    var generation = tracker.StartConversionSession();
    var on = new MidiNoteData("bench", "bench", 0, 60, 100, true, generation);
    var off = new MidiNoteData("bench", "bench", 0, 60, 0, false, generation);

    var press = Measure(() => tracker.ProcessNoteEvent(on), output);
    var release = Measure(() => tracker.ProcessNoteEvent(off), output);
    if (output.Count != 2 || tracker.ActiveNotesCount != 0 || simulator.GetRefCount(VirtualKeyCode.VK_A) != 0)
        throw new InvalidOperationException($"Unexpected postcondition: outputs={output.Count}, active={tracker.ActiveNotesCount}");
    return new ChildResult(mode, warmupTicks * 1_000_000d / Stopwatch.Frequency,
        press.ElapsedUs, press.FirstOutputUs, press.AllocatedBytes,
        release.ElapsedUs, release.FirstOutputUs, release.AllocatedBytes,
        output.Count, tracker.ActiveNotesCount, simulator.GetRefCount(VirtualKeyCode.VK_A));
}

static EventResult Measure(Action action, RecordingOutput output)
{
    output.BeginEvent();
    var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
    var start = Stopwatch.GetTimestamp();
    output.MeasurementStart = start;
    action();
    var end = Stopwatch.GetTimestamp();
    return new EventResult((end - start) * 1_000_000d / Stopwatch.Frequency,
        (output.FirstCallback - start) * 1_000_000d / Stopwatch.Frequency,
        GC.GetAllocatedBytesForCurrentThread() - beforeAllocated);
}

static async Task<ChildResult> LaunchChild(string mode, int pair)
{
    var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process path.");
    var start = new ProcessStartInfo(processPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    {
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    }
    start.ArgumentList.Add("--child");
    start.ArgumentList.Add(mode);
    var stopwatch = Stopwatch.StartNew();
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start child process.");
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    stopwatch.Stop();
    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    if (process.ExitCode != 0) throw new InvalidOperationException($"Child {mode} failed: {stderr}\n{stdout}");
    var line = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Last();
    var parsed = JsonSerializer.Deserialize<ChildResult>(line) ?? throw new InvalidOperationException("Could not parse child output.");
    return parsed with { Pair = pair, ChildProcessMilliseconds = stopwatch.Elapsed.TotalMilliseconds };
}

static object Summary(ChildResult[] results) => new
{
    noteOnProcessMicroseconds = Stats(results.Select(x => x.PressElapsedMicroseconds).ToArray()),
    noteOnToOutputMicroseconds = Stats(results.Select(x => x.PressToOutputMicroseconds).ToArray()),
    noteOffProcessMicroseconds = Stats(results.Select(x => x.ReleaseElapsedMicroseconds).ToArray()),
    noteOffToOutputMicroseconds = Stats(results.Select(x => x.ReleaseToOutputMicroseconds).ToArray()),
    noteOnAllocatedBytes = Stats(results.Select(x => (double)x.PressAllocatedBytes).ToArray()),
    noteOffAllocatedBytes = Stats(results.Select(x => (double)x.ReleaseAllocatedBytes).ToArray()),
    childProcessMilliseconds = Stats(results.Select(x => x.ChildProcessMilliseconds).ToArray())
};

static object Stats(double[] values)
{
    var sorted = values.Order().ToArray();
    return new { mean = sorted.Average(), p50 = Percentile(sorted, .50), p95 = Percentile(sorted, .95), max = sorted[^1] };
}

static double Percentile(double[] sorted, double percentile) => sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * percentile) - 1)];
static string Fmt(double[] values) { var s = values.Order().ToArray(); return $"{Percentile(s, .50):F2} / {Percentile(s, .95):F2} / {s[^1]:F2}"; }
static string FmtBytes(long[] values) { var s = values.Order().ToArray(); return $"{Percentile(s.Select(x => (double)x).ToArray(), .50):F0} / {Percentile(s.Select(x => (double)x).ToArray(), .95):F0} / {s[^1]:F0}"; }

sealed record EventResult(double ElapsedUs, double FirstOutputUs, long AllocatedBytes);
sealed record ChildResult(string Mode, double WarmupMicroseconds, double PressElapsedMicroseconds,
    double PressToOutputMicroseconds, long PressAllocatedBytes, double ReleaseElapsedMicroseconds,
    double ReleaseToOutputMicroseconds, long ReleaseAllocatedBytes, int OutputCount,
    int ActiveNotesAfter, int KeyRefCountAfter)
{
    public int Pair { get; init; } = -1;
    public double ChildProcessMilliseconds { get; init; }
}

sealed class RecordingOutput : IKeyboardOutput
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public long MeasurementStart;
    public long FirstCallback;
    public int LastWin32Error => 0;
    public void BeginEvent() => FirstCallback = 0;
    public bool SendHardwareKey(VirtualKeyCode key, bool isDown)
    {
        FirstCallback = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _count);
        return true;
    }
}
