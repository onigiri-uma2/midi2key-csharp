using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace MidiToKeyApp;

/// <summary>One UI process per Windows user/session; diagnostic child processes bypass this guard.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle activation;
    private readonly MemoryMappedFile owner;
    private bool disposed;
    public bool IsPrimary { get; }

    public SingleInstance(string? name = null)
    {
        name ??= @"Local\midi2key." + WindowsIdentity.GetCurrent().User!.Value;
        mutex = new Mutex(false, name + ".mutex");
        try
        {
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".activate");
            try { owner = MemoryMappedFile.CreateOrOpen(name + ".owner", sizeof(int)); }
            catch { activation.Dispose(); throw; }
            try
            {
                try { IsPrimary = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { IsPrimary = true; }
                if (IsPrimary)
                {
                    using var view = owner.CreateViewAccessor();
                    view.Write(0, Environment.ProcessId);
                }
            }
            catch
            {
                if (IsPrimary) mutex.ReleaseMutex();
                owner.Dispose();
                activation.Dispose();
                throw;
            }
        }
        catch { mutex.Dispose(); throw; }
    }

    public void RequestActivation()
    {
        if (IsPrimary) return;
        // Transfer foreground permission from the newly launched process to the owner.
        // The event remains signalled if the first process is still constructing its UI.
        using var view = owner.CreateViewAccessor();
        int processId = view.ReadInt32(0);
        if (processId > 0) AllowSetForegroundWindow((uint)processId);
        activation.Set();
    }

    public bool ConsumeActivation() => IsPrimary && activation.WaitOne(0);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (IsPrimary) mutex.ReleaseMutex();
        owner.Dispose();
        activation.Dispose();
        mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
