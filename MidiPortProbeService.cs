namespace MidiToKeyApp
{
    /// <summary>
    /// Gathers the two independent views of MIDI ports. Call from a worker thread;
    /// both DryWetMIDI enumeration and the diagnostic child process may block.
    /// </summary>
    internal static class MidiPortProbeService
    {
        internal readonly record struct ProbeResult(PortEnumerationResult Backend, OutOfProcessWinMmResult WinMm);

        internal static ProbeResult Probe(int childTimeoutMs) => new(
            MidiListener.GetPortNames(),
            MidiDeviceDiagnostics.GetOutOfProcessWinMmResult(childTimeoutMs));
    }
}
