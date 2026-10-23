using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MidiToKeyApp;

/// <summary>Serializes native MIDI operations away from the UI thread.</summary>
internal sealed class MidiListenerLifecycle
{
    private readonly IMidiListener _listener;
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    private Task? _pendingStop;
    private Task? _pendingDispose;
    private int _pendingCount;
    private bool _disposed;

    internal MidiListenerLifecycle(IMidiListener listener) => _listener = listener;

    internal bool IsBusy => Volatile.Read(ref _pendingCount) != 0;

    // A caller may await the work already queued without adding another native operation.
    // Preserve stop failures so a new session cannot start after a failed close.
    internal Task WaitForIdleAsync()
    {
        lock (_gate)
            return _pendingStop ?? _tail;
    }

    internal Task<MidiPortStartResult> StartAsync(IReadOnlyList<string> ports, long generation,
        InitialPortSnapshot? snapshot, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed || _pendingCount != 0)
                throw new InvalidOperationException("MIDIデバイスの前の処理が完了していません。");
            _pendingStop = null;
            _pendingCount++;
            var previous = _tail;
            var task = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    // DryWetMIDI has synchronous WinMM calls. This worker must be the only owner.
                    var result = _listener.Start(ports, generation, snapshot);
                    cancellationToken.ThrowIfCancellationRequested();
                    return result;
                }
                finally { Interlocked.Decrement(ref _pendingCount); }
            });
            _tail = Observe(task);
            return task;
        }
    }

    internal Task StopAsync()
    {
        lock (_gate)
        {
            if (_disposed) return _pendingDispose ?? Task.CompletedTask;
            if (_pendingStop != null) return _pendingStop;
            _pendingCount++;
            var previous = _tail;
            var task = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    _listener.Stop();
                }
                finally { Interlocked.Decrement(ref _pendingCount); }
            });
            _pendingStop = task;
            _tail = Observe(task);
            return task;
        }
    }

    internal Task DisposeAsync()
    {
        lock (_gate)
        {
            if (_pendingDispose != null) return _pendingDispose;
            _disposed = true;
            _pendingCount++;
            var previous = _tail;
            var task = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    _listener.Dispose();
                }
                finally { Interlocked.Decrement(ref _pendingCount); }
            });
            _pendingDispose = task;
            _tail = Observe(task);
            return task;
        }
    }

    internal Task<(IReadOnlyList<MidiPortInfo> Before, IReadOnlyList<MidiPortInfo> After)> CheckHealthAsync(
        IEnumerable<string>? osNames, OutOfProcessWinMmResult outOfProcess,
        long generation, IReadOnlyList<string> backendPorts)
    {
        lock (_gate)
        {
            if (_disposed || _pendingCount != 0)
                throw new InvalidOperationException("MIDIデバイスの処理中です。");
            _pendingStop = null;
            _pendingCount++;
            var previous = _tail;
            var task = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    var before = _listener.GetActivePorts();
                    if (_listener is MidiListener concrete)
                        concrete.CheckDeviceHealth(osNames, outOfProcess, generation, backendPorts);
                    else
                        _listener.CheckDeviceHealth(osNames, outOfProcess, generation);
                    return (before, _listener.GetActivePorts());
                }
                finally { Interlocked.Decrement(ref _pendingCount); }
            });
            _tail = Observe(task);
            return task;
        }
    }

    // A failed operation must not poison subsequent cleanup.
    private static async Task Observe(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { DiagnosticLogger.Log("MidiLifecycle", $"MIDI処理失敗: {ex}"); }
    }
}
