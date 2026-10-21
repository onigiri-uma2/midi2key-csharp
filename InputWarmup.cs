using System;
using System.Collections.Generic;
using System.Threading;
using WindowsInput.Native;

namespace MidiToKeyApp
{
    /// <summary>
    /// Warms the managed input path on disposable state. This class can never send an OS key:
    /// its KeySimulator is explicitly constructed with a private no-op output.
    /// </summary>
    public static class InputWarmup
    {
        private const int Passes = 4;
        private const string DeviceId = "warmup-only";

        public static bool Enabled { get; internal set; } = true;

        private sealed class NoopKeyboardOutput : IKeyboardOutput
        {
            public bool SendHardwareKey(VirtualKeyCode vk, bool isDown) => true;
            public int LastWin32Error => 0;
        }

        public static void Run(ResolvedMappingSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            // Use the same resolved mapping as the upcoming conversion where possible.
            int mappedNote = -1;
            for (int note = 0; note < 128; note++)
            {
                if (snapshot.GetNote(note).IsValid)
                {
                    mappedNote = note;
                    break;
                }
            }
            if (mappedNote >= 0 || snapshot.Pedal.IsValid)
                Exercise(snapshot, mappedNote, snapshot.Pedal.IsValid, cancellationToken);

            // The user's layout may have no shifted symbol or pedal mapping. Exercise
            // these branches with a separate synthetic snapshot in that case.
            var syntheticSettings = new AppSettings
            {
                KeyboardLayout = "US",
                Mapping = new Dictionary<string, string>
                {
                    ["60"] = "!",
                    ["pedal"] = "space"
                }
            };
            Exercise(ResolvedMappingSnapshot.Create(syntheticSettings), 60, true, cancellationToken);
        }

        private static void Exercise(ResolvedMappingSnapshot snapshot, int mappedNote, bool mappedPedal,
            CancellationToken cancellationToken)
        {
            var simulator = new KeySimulator(new NoopKeyboardOutput());
            var tracker = new InputTracker(simulator, () => throw new InvalidOperationException("Warmup mapping was not published."));
            tracker.PublishResolvedMapping(snapshot);
            long generation = tracker.StartConversionSession();
            try
            {
                for (int i = 0; i < Passes; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (mappedNote >= 0)
                    {
                        tracker.ProcessNoteEvent(new MidiNoteData(DeviceId, DeviceId, 0, mappedNote, 100, true, generation));
                        tracker.ProcessNoteEvent(new MidiNoteData(DeviceId, DeviceId, 0, mappedNote, 0, false, generation));
                    }
                    if (mappedPedal)
                    {
                        tracker.ProcessControlEvent(new MidiControlData(DeviceId, DeviceId, 0, 64, 127, generation));
                        tracker.ProcessControlEvent(new MidiControlData(DeviceId, DeviceId, 0, 64, 0, generation));
                    }
                }
            }
            finally
            {
                tracker.StopSession();
            }
        }
    }
}
