using Microsoft.VisualStudio.TestTools.UnitTesting;
using MidiToKeyApp;

namespace MidiToKeyApp.Tests;

[TestClass]
public sealed class InputWarmupTests
{
    [TestMethod]
    public void Run_Repeatedly_DoesNotChangeUserSettingsOrThrow()
    {
        var settings = new AppSettings { KeyboardLayout = "JIS" };
        settings.Mapping["60"] = "a";
        settings.Mapping["pedal"] = "space";
        var originalLayout = settings.KeyboardLayout;
        var originalMapping = new Dictionary<string, string>(settings.Mapping);
        var snapshot = ResolvedMappingSnapshot.Create(settings);

        InputWarmup.Run(snapshot);
        InputWarmup.Run(snapshot);

        Assert.AreEqual(originalLayout, settings.KeyboardLayout);
        CollectionAssert.AreEquivalent(originalMapping, settings.Mapping);
        Assert.IsTrue(snapshot.GetNote(60).IsValid);
        Assert.IsTrue(snapshot.Pedal.IsValid);
    }

    [TestMethod]
    public void Run_Cancellation_PropagatesAndLeavesSettingsUntouched()
    {
        var settings = new AppSettings { KeyboardLayout = "US" };
        settings.Mapping["60"] = "b";
        settings.Mapping["pedal"] = "k";
        var originalLayout = settings.KeyboardLayout;
        var originalMapping = new Dictionary<string, string>(settings.Mapping);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsException<OperationCanceledException>(() =>
            InputWarmup.Run(ResolvedMappingSnapshot.Create(settings), cancellation.Token));

        Assert.AreEqual(originalLayout, settings.KeyboardLayout);
        CollectionAssert.AreEquivalent(originalMapping, settings.Mapping);
    }
}
