namespace MidiToKeyApp
{
    /// <summary>
    /// Bounds each caller's wait on native port enumeration. Native enumeration cannot
    /// be interrupted, so all callers share its one outstanding task until it finishes.
    /// </summary>
    internal static class MidiPortProbeService
    {
        internal readonly record struct ProbeResult(PortEnumerationResult Backend, OutOfProcessWinMmResult WinMm);

        private static readonly MidiPortProbeRunner Runner = new(
            MidiListener.GetPortNames,
            MidiDeviceDiagnostics.GetOutOfProcessWinMmResult,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(1500));

        internal static Task<ProbeResult> ProbeAsync(int childTimeoutMs, CancellationToken cancellationToken = default) =>
            Runner.ProbeAsync(childTimeoutMs, cancellationToken);
    }

    internal sealed class MidiPortProbeRunner
    {
        private readonly object _sync = new();
        private readonly Func<PortEnumerationResult> _backendProbe;
        private readonly Func<int, OutOfProcessWinMmResult> _winMmProbe;
        private readonly TimeSpan _backendTimeout;
        private readonly TimeSpan _childGrace;
        private Task<PortEnumerationResult>? _backendTask;
        private Task<OutOfProcessWinMmResult>? _winMmTask;

        internal MidiPortProbeRunner(
            Func<PortEnumerationResult> backendProbe,
            Func<int, OutOfProcessWinMmResult> winMmProbe,
            TimeSpan backendTimeout,
            TimeSpan childGrace)
        {
            _backendProbe = backendProbe;
            _winMmProbe = winMmProbe;
            _backendTimeout = backendTimeout;
            _childGrace = childGrace;
        }

        internal async Task<MidiPortProbeService.ProbeResult> ProbeAsync(int childTimeoutMs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<PortEnumerationResult> backendTask;
            Task<OutOfProcessWinMmResult> winMmTask;
            lock (_sync)
            {
                // A timed-out caller must never start another native enumeration
                // while the original call remains blocked.
                if (_backendTask == null || _backendTask.IsCompleted)
                    _backendTask = Task.Run(() =>
                    {
                        try { return _backendProbe(); }
                        catch (Exception ex)
                        {
                            DiagnosticLogger.Log("MidiPortProbe", $"DryWetMIDI port enumeration failed: {ex}");
                            return PortEnumerationResult.Failed($"MIDIポート列挙に失敗しました: {ex.Message}", ex);
                        }
                    });
                backendTask = _backendTask;

                if (_winMmTask == null || _winMmTask.IsCompleted)
                    _winMmTask = Task.Run(() =>
                    {
                        try { return _winMmProbe(childTimeoutMs); }
                        catch (Exception ex)
                        {
                            DiagnosticLogger.Log("MidiPortProbe", $"WinMM subprocess query failed: {ex}");
                            return OutOfProcessWinMmResult.Failed($"WinMMポート確認に失敗しました: {ex.Message}");
                        }
                    });
                winMmTask = _winMmTask;
            }

            var backendWait = WaitForBackendAsync(backendTask, cancellationToken);
            var winMmWait = WaitForWinMmAsync(winMmTask, childTimeoutMs, cancellationToken);
            await Task.WhenAll(backendWait, winMmWait);
            return new(backendWait.Result, winMmWait.Result);
        }

        private async Task<PortEnumerationResult> WaitForBackendAsync(Task<PortEnumerationResult> task, CancellationToken cancellationToken)
        {
            try { return await task.WaitAsync(_backendTimeout, cancellationToken); }
            catch (TimeoutException)
            {
                DiagnosticLogger.Log("MidiPortProbe", $"DryWetMIDI port enumeration timed out after {_backendTimeout.TotalMilliseconds:0}ms.");
                return PortEnumerationResult.Failed("MIDIポート列挙がタイムアウトしました。機器を確認してアプリを再起動してください。");
            }
        }

        private async Task<OutOfProcessWinMmResult> WaitForWinMmAsync(Task<OutOfProcessWinMmResult> task, int childTimeoutMs, CancellationToken cancellationToken)
        {
            try { return await task.WaitAsync(TimeSpan.FromMilliseconds(childTimeoutMs) + _childGrace, cancellationToken); }
            catch (TimeoutException)
            {
                DiagnosticLogger.Log("MidiPortProbe", $"WinMM subprocess query did not return after {childTimeoutMs}ms plus grace period.");
                return OutOfProcessWinMmResult.Failed("WinMMポート確認がタイムアウトしました。", timedOut: true);
            }
        }
    }
}
