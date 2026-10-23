using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MidiToKeyApp;

namespace MidiToKeyApp.Tests;

[TestClass]
public sealed class LatencyMeasurementExportTests
{
    [TestMethod]
    public void FailedExportRetainsTraceForRetry_AndNeverOverwritesExistingFile()
    {
        ResetLatencyMeasurement();
        string tempDir = Path.Combine(AppContext.BaseDirectory, $"midi2key-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string invalidPath = tempDir; // A directory cannot be opened as the JSON file.
        string existingPath = Path.Combine(tempDir, "existing.json");
        string retryPath = Path.Combine(tempDir, "retry.json");
        File.WriteAllText(existingPath, "keep this file");

        try
        {
            LatencyMeasurement.Enable(invalidPath);
            long traceId = LatencyMeasurement.RecordMidiCallback();
            LatencyMeasurement.RecordMidiIdentity(traceId, "fake-device", 42, "note_on");

            var failed = LatencyMeasurement.ExportAfterStop();
            Assert.IsFalse(failed.Success);
            Assert.IsTrue(LatencyMeasurement.ExportPending, "The failed export must remain available for retry.");

            var overwriteAttempt = LatencyMeasurement.ExportAfterStop(existingPath);
            Assert.IsFalse(overwriteAttempt.Success, "An existing destination must be rejected.");
            Assert.AreEqual("keep this file", File.ReadAllText(existingPath));
            Assert.IsTrue(LatencyMeasurement.ExportPending, "A rejected destination must not discard the pending trace.");

            var succeeded = LatencyMeasurement.ExportAfterStop(retryPath);
            Assert.IsTrue(succeeded.Success, succeeded.PathOrError);
            Assert.IsFalse(LatencyMeasurement.ExportPending);
            using var json = JsonDocument.Parse(File.ReadAllText(retryPath));
            Assert.IsTrue(json.RootElement.GetProperty("points").EnumerateArray().Any(point =>
                point.GetProperty("TraceId").GetInt64() == traceId &&
                point.GetProperty("Stage").GetString() == "midi_identity"));
        }
        finally
        {
            ResetLatencyMeasurement();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void ResetLatencyMeasurement() =>
        typeof(LatencyMeasurement).GetMethod("ResetForTesting", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null);
}
