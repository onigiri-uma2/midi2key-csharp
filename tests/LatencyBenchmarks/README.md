# Input processing latency benchmark

Run from the repository root in PowerShell on Windows with .NET 10. NuGet packages are the same versions as the application. The baseline source is extracted from Git `HEAD` into ignored `tests/LatencyBenchmarks/obj/baseline`; local edits are untouched.

```powershell
dotnet build tests/LatencyBenchmarks/LatencyBenchmarks.csproj -c Release -p:NuGetAudit=false
dotnet tests/LatencyBenchmarks/bin/Release/net10.0-windows10.0.19041.0/LatencyBenchmarks.dll --label current --iterations 3000 --warmup 1000 --rounds 5 > current.json

pwsh -File tests/LatencyBenchmarks/prepare-baseline.ps1
dotnet build tests/LatencyBenchmarks/LatencyBenchmarks.csproj -c Release -p:Baseline=true -p:NuGetAudit=false
dotnet tests/LatencyBenchmarks/bin/baseline/Release/net10.0-windows10.0.19041.0/LatencyBenchmarks.dll --label HEAD --iterations 3000 --warmup 1000 --rounds 5 > baseline.json
```

Run the two executables again in reverse order to check for CPU frequency, thermal, and background load effects. JSON contains every round, without outlier removal. Compare each scenario's per-round mean, p50, p95, p99 and max; a tiny sub-microsecond difference should be treated cautiously.

The harness compiles the production `InputTracker`, `KeySimulator`, `KeyResolver` and their dependencies unchanged. The current build publishes a resolved settings snapshot before the timed run, so it measures the lock-free mapping lookup mode; the `Baseline=true` build uses the original mutable-provider path from `HEAD`. Each event is prebuilt outside its timed call. It measures from immediately before `InputTracker.ProcessNoteEvent` or `ProcessControlEvent` to the first recording `IKeyboardOutput` callback and to method return. Chord results include individual event latencies and a separate first-down-to-last-down-output burst latency. Two device threads use one tracker and begin from a common gate, creating real lock contention but without a claim of perfectly simultaneous events. Output counts and final key state are checked. `allocatedBytesPerEvent` comes from the current thread and includes measurement framework overhead. The JSON's empty timestamp measurement quantifies a lower bound on timer overhead; no subtraction is applied to results.

The recorder never sends keys to the OS. MIDI hardware, driver and `MidiListener` receive/queue behavior, Win32 `SendInput`, UI, and the receiving application's response are outside the measurement. Absolute end-to-end key response cannot be inferred from these results.
