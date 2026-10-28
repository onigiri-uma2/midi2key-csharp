using System;
using System.Collections.Generic;
using System.Drawing;

namespace MidiToKeyApp;

internal enum TrayIconState { Stopped, Running, Test, Busy, Warning }

/// <summary>Owned, cached icons loaded once; no image allocation on MIDI callbacks or UI ticks.</summary>
internal sealed class AppIcons : IDisposable
{
    public Icon Application { get; }
    private readonly Dictionary<TrayIconState, Icon> states = new();

    public AppIcons()
    {
        Application = Load("midi2key", new Size(32, 32));
        try
        {
            foreach (TrayIconState state in Enum.GetValues<TrayIconState>())
                states.Add(state, Load($"tray-{state.ToString().ToLowerInvariant()}", System.Windows.Forms.SystemInformation.SmallIconSize));
        }
        catch { Dispose(); throw; }
    }

    public Icon ForState(TrayIconState state) => states[state];

    private static Icon Load(string name, Size size)
    {
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream($"MidiToKeyApp.Assets.{name}.ico")
            ?? throw new InvalidOperationException($"Missing embedded icon: {name}");
        using var source = new Icon(stream, size);
        return (Icon)source.Clone();
    }

    public void Dispose()
    {
        foreach (var icon in states.Values) icon.Dispose();
        states.Clear();
        Application.Dispose();
    }
}
