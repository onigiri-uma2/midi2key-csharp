using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MidiToKeyApp;

namespace MidiToKeyApp.Tests;

[TestClass]
public sealed class SingleInstanceTests
{
    private static string UniqueName() => @"Local\midi2key-test-" + Guid.NewGuid().ToString("N");

    private static void OnOtherThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "Instance worker did not finish.");
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    [TestMethod]
    public void Duplicate_SignalsOwner_EvenBeforeUiStarts()
    {
        string name = UniqueName();
        using var first = new SingleInstance(name);
        Assert.IsTrue(first.IsPrimary);
        OnOtherThread(() => {
            using var second = new SingleInstance(name);
            Assert.IsFalse(second.IsPrimary);
            second.RequestActivation();
        });
        Assert.IsTrue(first.ConsumeActivation());
        Assert.IsFalse(first.ConsumeActivation(), "An activation must only be consumed once.");
    }

    [TestMethod]
    public void NormalExit_AllowsNextLaunch()
    {
        string name = UniqueName();
        using (var first = new SingleInstance(name)) Assert.IsTrue(first.IsPrimary);
        OnOtherThread(() => {
            using var next = new SingleInstance(name);
            Assert.IsTrue(next.IsPrimary);
        });
    }

    [TestMethod]
    public void AbandonedMutex_AllowsRecovery()
    {
        string name = UniqueName();
        using var keepAlive = new Mutex(false, name + ".mutex");
        OnOtherThread(() => { Assert.IsTrue(keepAlive.WaitOne(0)); });
        using var recovered = new SingleInstance(name);
        Assert.IsTrue(recovered.IsPrimary);
    }

    [TestMethod]
    public void SeparateProcess_CannotAcquireWhileOwnerIsRunning()
    {
        string name = UniqueName();
        int Probe()
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            // name is generated locally from a GUID, never from external input.
            start.ArgumentList.Add($"$m=[Threading.Mutex]::new($false,'{name}.mutex'); if($m.WaitOne(0)){{$m.ReleaseMutex();$m.Dispose();exit 0}};$m.Dispose();exit 1");
            using var process = Process.Start(start)!;
            Assert.IsTrue(process.WaitForExit(10000));
            return process.ExitCode;
        }
        using (var first = new SingleInstance(name)) Assert.AreEqual(1, Probe());
        Assert.AreEqual(0, Probe());
    }
}
