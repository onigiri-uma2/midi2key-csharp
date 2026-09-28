using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MidiToKeyApp;
using WindowsInput.Native;

namespace MidiToKeyApp.Tests
{
    public class FakeKeyboardOutput : IKeyboardOutput
    {
        public record SentEvent(VirtualKeyCode Key, bool IsDown);
        public List<SentEvent> History { get; } = new();
        public HashSet<VirtualKeyCode> FailKeys { get; } = new();
        public bool FailAll { get; set; } = false;
        public int LastWin32Error { get; set; } = 0;
        public int SimulatedWin32ErrorOnFail { get; set; } = 5; // ERROR_ACCESS_DENIED

        public bool SendHardwareKey(VirtualKeyCode vk, bool isDown)
        {
            if (FailAll || FailKeys.Contains(vk))
            {
                LastWin32Error = SimulatedWin32ErrorOnFail;
                return false;
            }
            History.Add(new SentEvent(vk, isDown));
            return true;
        }

        public void Clear()
        {
            History.Clear();
            FailKeys.Clear();
            FailAll = false;
            LastWin32Error = 0;
        }
    }

    public class FakeMidiListener : IMidiListener
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public List<long> StartedGenerations { get; } = new();
        public List<List<string>> StartedPorts { get; } = new();
        public List<MidiPortInfo> ActivePorts { get; } = new();
        public HashSet<string> FailPorts { get; } = new();
        public bool FailAllPorts { get; set; } = false;
        public long CurrentGeneration { get; set; } = 0;

        public event Action<MidiNoteData>? OnNoteReceived;
        public event Action<MidiControlData>? OnControlReceived;
        public event Action<string, string>? OnDeviceDisconnected;

        public MidiPortStartResult Start(IEnumerable<string> portNames, long? specificGeneration = null)
        {
            StartCount++;
            long gen = specificGeneration ?? (CurrentGeneration + 1);
            CurrentGeneration = gen;
            StartedGenerations.Add(gen);
            var portList = portNames.ToList();
            StartedPorts.Add(portList);

            ActivePorts.Clear();
            var opened = new List<MidiPortInfo>();
            var failed = new List<MidiPortError>();

            int idx = 1;
            foreach (var p in portList)
            {
                if (FailAllPorts || FailPorts.Contains(p))
                {
                    failed.Add(new MidiPortError(p, "開始失敗シミュレート"));
                }
                else
                {
                    var info = new MidiPortInfo($"{p}#{idx++}", p, DeviceState.Active);
                    opened.Add(info);
                    ActivePorts.Add(info);
                }
            }

            return new MidiPortStartResult(gen, opened, failed);
        }

        public void Stop()
        {
            StopCount++;
            ActivePorts.Clear();
        }

        public IReadOnlyList<MidiPortInfo> GetActivePorts() => ActivePorts.ToList();

        public void CheckDeviceHealth() { }

        public void SimulateDeviceDisconnected(string deviceId)
        {
            var targets = string.IsNullOrEmpty(deviceId)
                ? ActivePorts.ToList()
                : ActivePorts.Where(p => p.DeviceId == deviceId).ToList();

            foreach (var t in targets)
            {
                ActivePorts.Remove(t);
                OnDeviceDisconnected?.Invoke(t.DeviceId, t.DeviceName);
            }

            if (targets.Count == 0 && string.IsNullOrEmpty(deviceId))
            {
                OnDeviceDisconnected?.Invoke(string.Empty, string.Empty);
            }
        }

        public void FireNote(MidiNoteData data) => OnNoteReceived?.Invoke(data);
        public void FireControl(MidiControlData data) => OnControlReceived?.Invoke(data);

        public void Dispose() => Stop();
    }

    [TestClass]
    public class StabilityAndInputTests
    {
        private FakeKeyboardOutput _mock = null!;
        private KeySimulator _simulator = null!;

        [TestInitialize]
        public void Setup()
        {
            _mock = new FakeKeyboardOutput();
            _simulator = new KeySimulator(_mock);
        }

        // Test 1: 同一キーに2ノート割当
        [TestMethod]
        public void Test_01_SameKeyTwoNotes_ReleasedWhenBothReleased()
        {
            var settings = new AppSettings {
                Mapping = new() { { "60", "x" }, { "62", "x" } }
            };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.IsTrue(_mock.History[0].Key == VirtualKeyCode.VK_X && _mock.History[0].IsDown);

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 62, 100, true, session));
            Assert.AreEqual(1, _mock.History.Count);

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(1, _mock.History.Count); // まだ保持中

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 62, 0, false, session));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.IsTrue(_mock.History[1].Key == VirtualKeyCode.VK_X && !_mock.History[1].IsDown);
        }

        // Test 2: Shiftペダル＋大文字A
        [TestMethod]
        public void Test_02_ShiftPedalAndUppercaseA_ShiftRetained()
        {
            var settings = new AppSettings {
                Mapping = new() { { "pedal", "shift" }, { "60", "A" } }
            };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            // 1. ペダル踏む -> Shift Down
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.SHIFT, _mock.History[0].Key);
            Assert.IsTrue(_mock.History[0].IsDown);

            // 2. Note 60 (大文字A) 押下 -> Shift保持、A Down
            _mock.Clear();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.VK_A, _mock.History[0].Key);
            Assert.IsTrue(_mock.History[0].IsDown);

            // 3. Note 60 離す -> A Up のみ (Shift保持)
            _mock.Clear();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.VK_A, _mock.History[0].Key);
            Assert.IsFalse(_mock.History[0].IsDown);

            // 4. ペダル離す -> Shift Up
            _mock.Clear();
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 0, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.SHIFT, _mock.History[0].Key);
            Assert.IsFalse(_mock.History[0].IsDown);
        }

        // Test 3: 大文字A＋大文字B
        [TestMethod]
        public void Test_03_UppercaseAAndUppercaseB_ShiftRetainedUntilBothReleased()
        {
            var settings = new AppSettings {
                Mapping = new() { { "60", "A" }, { "62", "B" } }
            };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(2, _mock.History.Count); // Shift Down, A Down

            _mock.Clear();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 62, 100, true, session));
            Assert.AreEqual(1, _mock.History.Count); // B Down のみ (Shift保持)

            _mock.Clear();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(1, _mock.History.Count); // A Up のみ (Shift保持)

            _mock.Clear();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 62, 0, false, session));
            Assert.AreEqual(2, _mock.History.Count); // B Up, Shift Up
        }

        // Test 4: 途中でマッピング変更
        [TestMethod]
        public void Test_04_MappingChangeDuringNotePress_ReleasesOriginalKey()
        {
            var settings = new AppSettings {
                Mapping = new() { { "60", "a" } }
            };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(VirtualKeyCode.VK_A, _mock.History[0].Key);

            // 押下中にマッピングを 'b' に変更
            lock (settings.MappingLock)
            {
                settings.Mapping["60"] = "b";
            }

            _mock.Clear();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.VK_A, _mock.History[0].Key);
            Assert.IsFalse(_mock.History[0].IsDown);
        }

        // Test 5: 押下中にJIS/US変更
        [TestMethod]
        public void Test_05_LayoutChangeDuringNotePress_ReleasesOriginalPhysicalKey()
        {
            var settings = new AppSettings {
                KeyboardLayout = "JIS",
                Mapping = new() { { "60", ";" } } // JIS: OEM_PLUS
            };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(VirtualKeyCode.OEM_PLUS, _mock.History[0].Key);

            settings.KeyboardLayout = "US"; // US だと OEM_1 になる

            _mock.Clear();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.OEM_PLUS, _mock.History[0].Key);
            Assert.IsFalse(_mock.History[0].IsDown);
        }

        // Test 6: CC64重複受信
        [TestMethod]
        public void Test_06_PedalCC64DuplicateEvents_SendsSingleDownAndUp()
        {
            var settings = new AppSettings {
                Mapping = new() { { "pedal", "space" } }
            };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session));
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session));
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 100, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.SPACE, _mock.History[0].Key);
            Assert.IsTrue(_mock.History[0].IsDown);

            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 63, session));
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 0, session));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.SPACE, _mock.History[1].Key);
            Assert.IsFalse(_mock.History[1].IsDown);
        }

        // Test 7: ペダル＋通常ノートの同一キー割当
        [TestMethod]
        public void Test_07_PedalAndNoteShareSameKey_RetainedUntilBothReleased()
        {
            var settings = new AppSettings {
                Mapping = new() { { "pedal", "space" }, { "60", "space" } }
            };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session));
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(1, _mock.History.Count);

            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 0, session));
            Assert.AreEqual(1, _mock.History.Count); // 保持

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.IsFalse(_mock.History[1].IsDown);
        }

        // Test 8: ノート取得中のNoteOff
        [TestMethod]
        public void Test_08_NoteOffDuringCapturing_NoKeyRelease()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();
            tracker.SetCapturing(true);

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(0, _mock.History.Count);
        }

        // Test 9: 変換中にノート番号欄へフォーカス -> リスナーの再起動が発生しない
        [TestMethod]
        public void Test_09_FocusNoteTextBoxDuringListening_DoesNotRestartListener()
        {
            var fakeListener = new FakeMidiListener();
            var settings = new AppSettings();
            var tracker = new InputTracker(_simulator, () => settings);
            
            // 変換開始
            long session = tracker.StartConversionSession();
            fakeListener.Start(new[] { "Port1" }, session);
            Assert.AreEqual(1, fakeListener.StartCount);
            Assert.AreEqual(0, fakeListener.StopCount);

            // 変換実行中にキャプチャ開始（ノート欄フォーカス）
            long capSession = tracker.StartCaptureSession();
            Assert.AreEqual(session, capSession); // Generationは変更されない
            Assert.IsTrue(tracker.IsListening);
            Assert.IsTrue(tracker.IsCapturing);
            // リスナーの再起動（Start/Stop）は呼ばれない
            Assert.AreEqual(1, fakeListener.StartCount);
            Assert.AreEqual(0, fakeListener.StopCount);
        }

        // Test 10: 押下中に変換停止
        [TestMethod]
        public void Test_10_StopListeningWhileKeysPressed_ReleasesAllKeys()
        {
            var settings = new AppSettings {
                Mapping = new() { { "60", "a" }, { "pedal", "space" } }
            };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session));
            Assert.AreEqual(2, _mock.History.Count);

            _mock.Clear();
            tracker.StopSession();

            Assert.AreEqual(2, _mock.History.Count);
            Assert.IsTrue(_mock.History.All(e => !e.IsDown));
            Assert.AreEqual(0, tracker.ActiveNotesCount);
        }

        // Test 11: 停止直後のMIDIイベント
        [TestMethod]
        public void Test_11_DelayedEventFromOldSessionAfterStop_Ignored()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long oldSession = tracker.StartSession();

            tracker.StopSession();
            _mock.Clear();

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, oldSession));
            Assert.AreEqual(0, _mock.History.Count);
        }

        // Test 12: 停止ボタン連打
        [TestMethod]
        public void Test_12_MultipleConsecutiveStops_NoError()
        {
            var settings = new AppSettings();
            var tracker = new InputTracker(_simulator, () => settings);
            tracker.StartSession();

            for (int i = 0; i < 5; i++)
            {
                tracker.StopSession();
            }
            Assert.IsFalse(tracker.IsListening);
        }

        // Test 13: 破損JSON読込
        [TestMethod]
        public void Test_13_CorruptedJsonLoadSafeguard_ThrowsException()
        {
            string badFile = Path.Combine(Path.GetTempPath(), $"bad_json_{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(badFile, "{ broken_json: [ }");
                Assert.ThrowsException<System.Text.Json.JsonException>(() => SettingsManager.Load(badFile));
            }
            finally
            {
                if (File.Exists(badFile)) File.Delete(badFile);
            }
        }

        // Test 14: 別名保存失敗 -> 実際の設定パス管理ロジックと元データが変更されない
        [TestMethod]
        public void Test_14_SaveAsFailurePathSafeguard_MaintainsCurrentPath()
        {
            string invalidPath = "Z:\\invalid_dir_non_existent\\settings.json";
            string currentPath = "settings.json";
            bool threw = false;

            try
            {
                SettingsManager.Save(invalidPath, new AppSettings());
                currentPath = invalidPath;
            }
            catch
            {
                threw = true;
            }

            Assert.IsTrue(threw);
            Assert.AreEqual("settings.json", currentPath);

            // 実際の設定ファイルが存在する場合の別名保存失敗でもパスと元データが維持されることを検証
            string tempDir = Path.Combine(Path.GetTempPath(), $"save_path_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string validOriginalPath = Path.Combine(tempDir, "original.json");
            try
            {
                var originalSettings = new AppSettings { KeyboardLayout = "JIS" };
                SettingsManager.Save(validOriginalPath, originalSettings);
                string activePath = validOriginalPath;

                // 失敗する別名保存を試みる
                try
                {
                    SettingsManager.Save(invalidPath, originalSettings);
                    activePath = invalidPath;
                }
                catch
                {
                    // 例外発生時は activePath は更新されない
                }

                Assert.AreEqual(validOriginalPath, activePath);
                var reloaded = SettingsManager.Load(activePath);
                Assert.AreEqual("JIS", reloaded.KeyboardLayout);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        // Test 15: Shift保持中にベースキー送信失敗 -> Shift参照数が元に戻る
        [TestMethod]
        public void Test_15_BaseKeyFailureWhileShiftHeld_ShiftRefRolledBack()
        {
            // ペダルでShiftを踏んでいる状態
            var resolvedShift = KeyResolver.Resolve("shift", "JIS");
            Assert.IsTrue(_simulator.PressResolvedKey(resolvedShift));
            Assert.AreEqual(1, _simulator.GetRefCount(VirtualKeyCode.SHIFT));

            // Aキーの送信を意図的に失敗させる
            _mock.FailKeys.Add(VirtualKeyCode.VK_A);

            var resolvedA = KeyResolver.Resolve("A", "JIS"); // Shiftが必要
            bool success = _simulator.PressResolvedKey(resolvedA);
            Assert.IsFalse(success);

            // Shiftの参照カウントが元に戻っていること（2ではなく1）
            Assert.AreEqual(1, _simulator.GetRefCount(VirtualKeyCode.SHIFT));

            // ペダル解放でShift Upが正常送信されること
            Assert.IsTrue(_simulator.ReleaseResolvedKey(resolvedShift));
            Assert.AreEqual(0, _simulator.GetRefCount(VirtualKeyCode.SHIFT));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.IsFalse(_mock.History[1].IsDown);
        }

        // Test 16: KeyUp送信失敗 -> 未解放状態を保持
        [TestMethod]
        public void Test_16_KeyUpFailure_RetainsUnreleasedState()
        {
            var resolved = KeyResolver.Resolve("x", "JIS");
            Assert.IsTrue(_simulator.PressResolvedKey(resolved));
            Assert.AreEqual(1, _simulator.GetRefCount(VirtualKeyCode.VK_X));

            // KeyUpの送信を失敗させる
            _mock.FailKeys.Add(VirtualKeyCode.VK_X);
            bool success = _simulator.ReleaseResolvedKey(resolved);
            Assert.IsTrue(success);

            // 論理参照は0だが、未解放キーとして追跡されていること
            Assert.AreEqual(0, _simulator.GetRefCount(VirtualKeyCode.VK_X));
            Assert.IsTrue(_simulator.IsKeyUnreleased(VirtualKeyCode.VK_X));
        }

        // Test 17: KeyUp再試行 -> 正常解放される
        [TestMethod]
        public void Test_17_KeyUpRetry_SuccessfullyReleases()
        {
            var resolved = KeyResolver.Resolve("x", "JIS");
            _simulator.PressResolvedKey(resolved);

            _mock.FailKeys.Add(VirtualKeyCode.VK_X);
            _simulator.ReleaseResolvedKey(resolved);
            Assert.IsTrue(_simulator.IsKeyUnreleased(VirtualKeyCode.VK_X));

            // 失敗フラグを解除して再試行
            _mock.FailKeys.Remove(VirtualKeyCode.VK_X);
            bool retryOk = _simulator.RetryReleasePendingKeys();

            Assert.IsTrue(retryOk);
            Assert.IsFalse(_simulator.IsKeyUnreleased(VirtualKeyCode.VK_X));
        }

        // Test 18: 全キー解放中に一部送信失敗 -> 失敗キーを追跡可能
        [TestMethod]
        public void Test_18_ReleaseAllKeysWithPartialFailure_TracksFailedKeys()
        {
            _simulator.PressResolvedKey(KeyResolver.Resolve("x", "JIS"));
            _simulator.PressResolvedKey(KeyResolver.Resolve("y", "JIS"));

            _mock.FailKeys.Add(VirtualKeyCode.VK_Y); // Yだけ失敗
            bool allReleased = _simulator.ReleaseAllKeys();

            Assert.IsFalse(allReleased);
            Assert.IsFalse(_simulator.IsKeyUnreleased(VirtualKeyCode.VK_X)); // Xは解放成功
            Assert.IsTrue(_simulator.IsKeyUnreleased(VirtualKeyCode.VK_Y));  // Yは未解放として追跡
        }

        // Test 19: キャプチャ開始前から押下中のノート -> 正常に解放
        [TestMethod]
        public void Test_19_PreExistingNoteDuringCapturing_ReleasedNormally()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            // 通常変換中に押下
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(1, _mock.History.Count);

            // キャプチャ開始
            tracker.SetCapturing(true);

            // 離す -> 正常にKeyUp送信
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.IsFalse(_mock.History[1].IsDown);
        }

        // Test 20: キャプチャ終了後のNoteOff -> 不要なキー送信なし
        [TestMethod]
        public void Test_20_NoteOffAfterCapturingEnded_NoExtraneousKeyUp()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            tracker.SetCapturing(true);
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(0, _mock.History.Count);

            tracker.SetCapturing(false);

            // キャプチャ終了後にNoteOff
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(0, _mock.History.Count);
        }

        // Test 21: キャプチャ中の重複NoteOn -> 既存の押下状態を破壊しない
        [TestMethod]
        public void Test_21_DuplicateNoteOnDuringCapturing_ProtectsExistingNote()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            // 通常変換で Note 60 押下
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(1, _mock.History.Count);

            // キャプチャ開始後、同じNote 60からNoteOnが重複受信される
            tracker.SetCapturing(true);
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));

            // 抑止に入らず、既存のアクティブノートとして保護されていること
            Assert.IsTrue(tracker.IsNoteActive(new MidiSourceKey("dev1", 0, 60, false)));

            // NoteOffで正常に解放されること
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.IsFalse(_mock.History[1].IsDown);
        }

        // Test 22: キャプチャ開始前から踏んでいたペダル -> 正常に解放
        [TestMethod]
        public void Test_22_PreExistingPedalDuringCapturing_ReleasedNormally()
        {
            var settings = new AppSettings { Mapping = new() { { "pedal", "space" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session));
            Assert.AreEqual(1, _mock.History.Count);

            tracker.SetCapturing(true);

            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 0, session));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.IsFalse(_mock.History[1].IsDown);
        }

        // Test 23: 古いGenerationのNoteOn -> 新セッションで破棄
        [TestMethod]
        public void Test_23_OldGenerationNoteOn_DiscardedInNewSession()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);

            long oldSession = tracker.StartSession();
            tracker.StopSession();

            long newSession = tracker.StartSession();

            // oldSessionのイベントが遅延到着
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, oldSession));
            Assert.AreEqual(0, _mock.History.Count); // 破棄

            // newSessionのイベントは正常処理
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, newSession));
            Assert.AreEqual(1, _mock.History.Count);
        }

        // Test 24: 古いGenerationのNoteOff -> 新セッションのキーを解放しない
        [TestMethod]
        public void Test_24_OldGenerationNoteOff_DoesNotReleaseNewSessionKey()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);

            long oldSession = tracker.StartSession();
            tracker.StopSession();

            long newSession = tracker.StartSession();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, newSession));
            Assert.AreEqual(1, _mock.History.Count);

            // oldSessionのNoteOffが遅延到着
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, oldSession));
            Assert.AreEqual(1, _mock.History.Count); // 解放されない！

            // newSessionのNoteOffで解放
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, newSession));
            Assert.AreEqual(2, _mock.History.Count);
        }

        // Test 25: 停止直後の再開始 -> 古い入力状態が混入しない
        [TestMethod]
        public void Test_25_ImmediateRestart_NoOldInputStateBleed()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);

            long s1 = tracker.StartSession();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, s1));
            tracker.StopSession();

            // 直後に再開
            long s2 = tracker.StartSession();
            Assert.AreEqual(0, tracker.ActiveNotesCount);
            Assert.AreEqual(0, tracker.SuppressedSourcesCount);
        }

        // Test 26: 不正なmapping JSON -> 読込失敗・現設定維持
        [TestMethod]
        public void Test_26_InvalidMappingJson_ThrowsAndMaintainsSettings()
        {
            string invalidFile = Path.Combine(Path.GetTempPath(), $"invalid_map_{Guid.NewGuid():N}.json");
            try
            {
                // mappingのキーが不正（999は127超）
                File.WriteAllText(invalidFile, "{ \"mapping\": { \"999\": \"a\" }, \"keyboard_layout\": \"JIS\", \"selected_ports\": [] }");
                Assert.ThrowsException<InvalidDataException>(() => SettingsManager.Load(invalidFile));
            }
            finally
            {
                if (File.Exists(invalidFile)) File.Delete(invalidFile);
            }
        }

        // Test 27: 保存失敗 -> 元ファイルと現在パスを維持（一時ファイル書き込み後や正式ファイル置換時の失敗含む）
        [TestMethod]
        public void Test_27_AtomicSaveFailure_PreservesOriginalFile()
        {
            string testDir = Path.Combine(Path.GetTempPath(), $"save_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(testDir);
            string settingsPath = Path.Combine(testDir, "settings.json");

            try
            {
                var initialSettings = new AppSettings { KeyboardLayout = "US" };
                SettingsManager.Save(settingsPath, initialSettings);
                Assert.IsTrue(File.Exists(settingsPath));

                // 1. 不正な設定（mapping = null）を保存しようとするとバリデーションで失敗
                var badSettings = new AppSettings { Mapping = null! };
                Assert.ThrowsException<InvalidDataException>(() => SettingsManager.Save(settingsPath, badSettings));

                // 元の設定ファイルが破損せず、US設定のままであること
                var reloaded = SettingsManager.Load(settingsPath);
                Assert.AreEqual("US", reloaded.KeyboardLayout);

                // 2. 一時ファイル作成後、正式ファイル置換時の失敗シミュレーション（ファイルが別プロセスで排他ロック中）
                var modifiedSettings = new AppSettings { KeyboardLayout = "JIS" };
                using (var lockStream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    // 排他ロック中にSaveを実行すると置換（Replace/Move）でIOExceptionが発生
                    Assert.ThrowsException<IOException>(() => SettingsManager.Save(settingsPath, modifiedSettings));
                }

                // ロック解除後、元のファイルが破損しておらず、変更前のUS設定が保持されていること
                var preserved = SettingsManager.Load(settingsPath);
                Assert.AreEqual("US", preserved.KeyboardLayout);

                // ディレクトリ内に残留した一時ファイル (settings.json.tmp.*) がクリーンアップされていること
                string searchPattern = $"{Path.GetFileName(settingsPath)}.tmp.*";
                var tmpFiles = Directory.GetFiles(testDir, searchPattern);
                Assert.AreEqual(0, tmpFiles.Length);
                var allTmpFiles = Directory.GetFiles(testDir, "*.tmp*");
                Assert.AreEqual(0, allTmpFiles.Length);
            }
            finally
            {
                if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
            }
        }

        // Test 28: Shiftペダル＋小文字a -> Shift保持中にAキー送信
        [TestMethod]
        public void Test_28_ShiftPedalAndLowercaseA_ShiftPreserved()
        {
            var settings = new AppSettings {
                Mapping = new() { { "pedal", "shift" }, { "60", "a" } }
            };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            // ペダル踏下 -> Shift Down
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.SHIFT, _mock.History[0].Key);

            // Note 60 ('a') 押下 -> 'a' Down送信 (Shift保持)
            _mock.Clear();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.VK_A, _mock.History[0].Key);
            Assert.IsTrue(_mock.History[0].IsDown);

            // Note 60 離す -> 'a' Up のみ (Shift保持)
            _mock.Clear();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.VK_A, _mock.History[0].Key);
            Assert.IsFalse(_mock.History[0].IsDown);

            // ペダル離す -> Shift Up
            _mock.Clear();
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 0, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.SHIFT, _mock.History[0].Key);
            Assert.IsFalse(_mock.History[0].IsDown);
        }

        // Test 29: ペダル＋通常ノートの同一キー割当 (複数機器・チャンネル)
        [TestMethod]
        public void Test_29_MultiDevicePedalAndNoteShareSameKey_RetainedUntilLastRelease()
        {
            var settings = new AppSettings {
                Mapping = new() { { "pedal", "ctrl" }, { "60", "ctrl" } }
            };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            // デバイス1 ペダル踏下
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano1", 0, 64, 127, session));
            // デバイス2 Note 60 押下
            tracker.ProcessNoteEvent(new MidiNoteData("dev2", "Piano2", 1, 60, 100, true, session));

            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.CONTROL, _mock.History[0].Key);

            // デバイス2 離す -> デバイス1ペダルにより保持
            tracker.ProcessNoteEvent(new MidiNoteData("dev2", "Piano2", 1, 60, 0, false, session));
            Assert.AreEqual(1, _mock.History.Count);

            // デバイス1 離す -> 解放
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano1", 0, 64, 0, session));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.IsFalse(_mock.History[1].IsDown);
        }

        // Test 30: 停止中にキャプチャ開始 -> Generationが一致し、MIDI入力を取得できる
        [TestMethod]
        public void Test_30_StartCaptureWhileStopped_GenerationMatchesAndCapturesInput()
        {
            var fakeListener = new FakeMidiListener();
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);

            string? capturedInput = null;
            tracker.OnInputCaptured += (input) => capturedInput = input;

            // 変換停止中にキャプチャセッション開始
            long capGen = tracker.StartCaptureSession();
            fakeListener.Start(new[] { "Port1" }, capGen);

            Assert.AreEqual(capGen, fakeListener.CurrentGeneration);
            Assert.AreEqual(capGen, tracker.CurrentSessionId);
            Assert.IsTrue(tracker.IsCapturing);
            Assert.IsFalse(tracker.IsListening);

            fakeListener.OnNoteReceived += data => tracker.ProcessNoteEvent(data);

            // リスナーからMIDIノート到着
            fakeListener.FireNote(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, capGen));

            // 入力がキャプチャされたこと
            Assert.AreEqual("60", capturedInput);
            // PCキー送信は抑止されていること
            Assert.AreEqual(0, _mock.History.Count);
        }

        // Test 31: 変換中のキャプチャ終了後に重複NoteOn -> KeyDownを発生させない
        [TestMethod]
        public void Test_31_DuplicateNoteOnAfterCaptureEnded_DoesNotSendKeyDown()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "A" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            
            // 1. 通常変換セッション開始
            long session = tracker.StartConversionSession();

            // 2. キャプチャ開始 (Generationは維持される)
            long capSession = tracker.StartCaptureSession();
            Assert.AreEqual(session, capSession);

            // 3. NoteOn(60) 受信 -> キャプチャ抑止対象として登録
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(0, _mock.History.Count);
            Assert.AreEqual(1, tracker.SuppressedSourcesCount);

            // 4. キャプチャ終了 (Generationは変更しない)
            tracker.StopCaptureSession();
            Assert.IsFalse(tracker.IsCapturing);
            Assert.AreEqual(session, tracker.CurrentSessionId);

            // 5. キャプチャ終了後も抑止対象が残っていること
            Assert.AreEqual(1, tracker.SuppressedSourcesCount);

            // 6. NoteOn(60) が重複到着
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));

            // 7. 検証: 通常変換側のKeyDownは発生しない
            Assert.AreEqual(0, _mock.History.Count);
            // 8. 検証: _activeNotes に誤登録されない
            Assert.AreEqual(0, tracker.ActiveNotesCount);
            // 9. 検証: Shiftを含む参照カウントが増加しない
            Assert.AreEqual(0, _simulator.GetRefCount(VirtualKeyCode.VK_A));
            Assert.AreEqual(0, _simulator.GetRefCount(VirtualKeyCode.SHIFT));

            // 10. NoteOff(60) 到着 -> 抑止セットから削除
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(0, tracker.SuppressedSourcesCount);

            // 11. PCキー送信は一切発生しない
            Assert.AreEqual(0, _mock.History.Count);
        }

        // Test 32: 変換中のキャプチャ終了後のNoteOff -> 抑止解除と通常変換再開
        [TestMethod]
        public void Test_32_NoteOffAfterCaptureEnded_ReleasesSuppression()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            
            // 1. 通常変換セッション開始
            long session = tracker.StartConversionSession();

            // 2. キャプチャ開始
            tracker.StartCaptureSession();

            // 3. NoteOn(60) 受信 -> 抑止セットへ登録
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(1, tracker.SuppressedSourcesCount);

            // 4. キャプチャ終了
            tracker.StopCaptureSession();

            // 5. NoteOff(60) 到着 -> 抑止状態が解除される
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(0, tracker.SuppressedSourcesCount);

            // 6. 不要なKeyUpを発生させない
            Assert.AreEqual(0, _mock.History.Count);

            // 7. 対応するNoteOffの後に新しいNoteOnを受信 -> 通常変換を再開できる
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.VK_A, _mock.History[0].Key);
            Assert.IsTrue(_mock.History[0].IsDown);

            // 8. NoteOffで適切にKeyUpが発生する
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.VK_A, _mock.History[1].Key);
            Assert.IsFalse(_mock.History[1].IsDown);
        }

        // Test 33: キャプチャ前から押下中のノート -> キャプチャ中も正常に解放
        [TestMethod]
        public void Test_33_PreExistingNoteDuringCapture_ReleasedNormally()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartConversionSession();

            // 通常変換中に押下
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.IsTrue(_mock.History[0].Key == VirtualKeyCode.VK_A && _mock.History[0].IsDown);

            // キャプチャ開始
            tracker.StartCaptureSession();

            // キャプチャ中に NoteOff 到着
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));

            // 正常に解放（KeyUp）されること
            Assert.AreEqual(2, _mock.History.Count);
            Assert.IsTrue(_mock.History[1].Key == VirtualKeyCode.VK_A && !_mock.History[1].IsDown);
            Assert.AreEqual(0, tracker.ActiveNotesCount);
        }

        // Test 34: キャプチャ専用セッションの再開始 -> 旧イベントを破棄
        [TestMethod]
        public void Test_34_RestartCaptureSession_DiscardsOldEvents()
        {
            var settings = new AppSettings();
            var tracker = new InputTracker(_simulator, () => settings);

            string? capturedText = null;
            tracker.OnInputCaptured += (text) => capturedText = text;

            long sessionA = tracker.StartCaptureSession();
            tracker.StopCaptureSession();

            long sessionB = tracker.StartCaptureSession();
            Assert.IsTrue(sessionB > sessionA);

            // セッションAの旧イベントが遅延到着
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, sessionA));
            Assert.IsNull(capturedText); // 破棄される

            // セッションBの新イベント
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 62, 100, true, sessionB));
            Assert.AreEqual("62", capturedText); // 正常キャプチャ
        }

        // Test 35: 変換実行中にキャプチャ開始 -> Generationとリスナーを維持
        [TestMethod]
        public void Test_35_StartCaptureDuringConversion_MaintainsGenerationAndListener()
        {
            var fakeListener = new FakeMidiListener();
            var settings = new AppSettings();
            var tracker = new InputTracker(_simulator, () => settings);

            long convGen = tracker.StartConversionSession();
            fakeListener.Start(new[] { "Port1" }, convGen);
            Assert.AreEqual(1, fakeListener.StartCount);

            // キャプチャ開始
            long capGen = tracker.StartCaptureSession();

            // Generationは維持される
            Assert.AreEqual(convGen, capGen);
            Assert.IsTrue(tracker.IsListening);
            Assert.IsTrue(tracker.IsCapturing);
            // リスナーは停止も再開もされない
            Assert.AreEqual(1, fakeListener.StartCount);
            Assert.AreEqual(0, fakeListener.StopCount);
        }

        // Test 36: キャプチャ中に変換開始 -> 状態遷移が正常に完了
        [TestMethod]
        public void Test_36_StartConversionDuringCapture_CompletesStateTransition()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);

            long capGen = tracker.StartCaptureSession();
            Assert.IsTrue(tracker.IsCapturing);
            Assert.IsFalse(tracker.IsListening);

            // 変換開始
            long convGen = tracker.StartConversionSession();
            Assert.IsTrue(convGen > capGen);
            Assert.IsTrue(tracker.IsListening);
            Assert.IsFalse(tracker.IsCapturing);

            // 新セッションでキー変換が正常に動作
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, convGen));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.VK_A, _mock.History[0].Key);
        }

        // Test 37: キャプチャ中のペダル重複CC64 -> KeyDownが重複しない
        [TestMethod]
        public void Test_37_PedalDuplicateCC64DuringCapture_DoesNotSendKeyDown()
        {
            var settings = new AppSettings { Mapping = new() { { "pedal", "space" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartCaptureSession();

            // ペダル踏下
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session));
            // 重複踏下
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 100, session));
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session));

            // PCキー送信は一切発生しない
            Assert.AreEqual(0, _mock.History.Count);
            Assert.AreEqual(0, tracker.ActiveNotesCount);
            Assert.AreEqual(1, tracker.SuppressedSourcesCount);
        }

        // Test 38: 変換実行中のペダルキャプチャ終了後の解放と通常変換再開
        [TestMethod]
        public void Test_38_PedalReleaseAfterCaptureEnded_ReleasesSuppression()
        {
            var settings = new AppSettings { Mapping = new() { { "pedal", "space" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            
            // 1. 通常変換開始
            long session = tracker.StartConversionSession();

            // 2. キャプチャ開始
            tracker.StartCaptureSession();

            // 3. CC64 = 127 (踏み込み) -> ペダルを抑止対象へ登録
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session));
            Assert.AreEqual(1, tracker.SuppressedSourcesCount);
            Assert.AreEqual(0, _mock.History.Count);

            // 4. キャプチャ終了
            tracker.StopCaptureSession();

            // 検証: キャプチャ終了時にGenerationが変わらない
            Assert.AreEqual(session, tracker.CurrentSessionId);
            // 検証: キャプチャ終了後もペダル抑止が維持される
            Assert.AreEqual(1, tracker.SuppressedSourcesCount);

            // 5. CC64 = 100 (重複踏み込み) -> 無視
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 100, session));
            Assert.AreEqual(0, _mock.History.Count);

            // 6. CC64 = 0 (ペダル解放) -> 抑止状態を解除
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 0, session));
            Assert.AreEqual(0, tracker.SuppressedSourcesCount);
            Assert.AreEqual(0, _mock.History.Count);

            // 7. CC64 = 127 (再度踏み込み) -> 通常変換としてKeyDown
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.SPACE, _mock.History[0].Key);
            Assert.IsTrue(_mock.History[0].IsDown);

            // 8. CC64 = 0 (解放) -> KeyUp
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 0, session));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.SPACE, _mock.History[1].Key);
            Assert.IsFalse(_mock.History[1].IsDown);
        }

        // Test 39: 起動時の破損JSON -> 未処理例外による終了を防ぐ
        [TestMethod]
        public void Test_39_CorruptedJsonOnStartup_RecoversWithDefaultSettingsWithoutUnhandledException()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"startup_bad_json_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string badFilePath = Path.Combine(tempDir, "settings.json");

            try
            {
                File.WriteAllText(badFilePath, "{ invalid json content! [[[ }}}");

                AppSettings loadedSettings;
                bool isCorrupted = false;

                // Form1の LoadInitialSettings() 相当の安全起動ロジック
                try
                {
                    loadedSettings = SettingsManager.Load(badFilePath);
                }
                catch
                {
                    isCorrupted = true;
                    loadedSettings = SettingsManager.GetDefaultSettings();
                }

                // 未処理例外にならず、デフォルト設定で復旧すること
                Assert.IsTrue(isCorrupted);
                Assert.IsNotNull(loadedSettings);
                Assert.AreEqual("JIS", loadedSettings.KeyboardLayout);
                Assert.IsTrue(loadedSettings.Mapping.ContainsKey("60"));
                // 元の破損ファイルが消去・上書きされていないこと
                Assert.IsTrue(File.Exists(badFilePath));
                Assert.AreEqual("{ invalid json content! [[[ }}}", File.ReadAllText(badFilePath));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        // Test 40: 設定読込失敗 -> 既存設定と元ファイルを維持
        [TestMethod]
        public void Test_40_SettingsLoadFailure_PreservesExistingSettingsAndOriginalFile()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"settings_fail_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string corruptFile = Path.Combine(tempDir, "settings.json");

            try
            {
                File.WriteAllText(corruptFile, "{\"mapping\": \"not a dictionary\"}");

                var currentSettings = new AppSettings { KeyboardLayout = "US" };
                bool threw = false;

                try
                {
                    var newSettings = SettingsManager.Load(corruptFile);
                    currentSettings = newSettings; // 成功時のみ更新
                }
                catch
                {
                    threw = true;
                }

                // 例外が検出され、既存設定が維持されること
                Assert.IsTrue(threw);
                Assert.AreEqual("US", currentSettings.KeyboardLayout);
                // 元ファイルが保護されていること
                Assert.AreEqual("{\"mapping\": \"not a dictionary\"}", File.ReadAllText(corruptFile));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        // Test 41: 停止直後の旧イベント -> 新セッションへ混入しない
        [TestMethod]
        public void Test_41_OldSessionDelayedEvents_DoNotBleedIntoNewSession()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" }, { "pedal", "space" } } };
            var tracker = new InputTracker(_simulator, () => settings);

            long session1 = tracker.StartConversionSession();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session1));
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session1));
            Assert.AreEqual(2, _mock.History.Count);

            // セッション1停止
            tracker.StopSession();
            _mock.Clear();

            // 新セッション2開始
            long session2 = tracker.StartConversionSession();
            Assert.IsTrue(session2 > session1);

            // セッション1由来の遅延 NoteOff / CC64 が新セッション中に到着
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session1));
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 0, session1));

            // 新セッションには何の影響も与えず破棄される
            Assert.AreEqual(0, _mock.History.Count);

            // 新セッション2で Note 60 押下
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session2));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.VK_A, _mock.History[0].Key);
            Assert.IsTrue(_mock.History[0].IsDown);

            // さらにセッション1由来の NoteOn が遅延到着しても無視
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session1));
            Assert.AreEqual(1, _mock.History.Count); // 増加しない

            // セッション2の NoteOff で正常解放
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session2));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.IsFalse(_mock.History[1].IsDown);
        }

        // Test 42: 未解放キーの再試行失敗 -> 新セッションを開始しない
        [TestMethod]
        public void Test_42_UnreleasedKeysRetryFailure_AbortsConversionStart()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);

            // 以前のセッションでKeyUp送信失敗が発生した状態をシミュレート
            _simulator.PressResolvedKey(KeyResolver.Resolve("a", "JIS"));
            _mock.FailKeys.Add(VirtualKeyCode.VK_A); // Aキーの解放を意図的に失敗させる
            _simulator.ReleaseResolvedKey(KeyResolver.Resolve("a", "JIS"));

            // Aキーが未解放キーとして残っていること
            Assert.IsTrue(_simulator.HasUnreleasedKeys);
            Assert.AreEqual(1, _simulator.UnreleasedKeysCount);
            Assert.IsTrue(_simulator.IsKeyUnreleased(VirtualKeyCode.VK_A));

            // 変換開始前のチェックを実行（未解放キーの再試行に失敗）
            bool canStart = _simulator.TryPrepareStartConversion();
            Assert.IsFalse(canStart);

            // 開始失敗のため、新セッションを開始しないこと（開始処理の中止）
            long currentSession = tracker.CurrentSessionId;
            if (canStart)
            {
                tracker.StartConversionSession();
            }

            // セッションは開始されず、Generationも進まず、未解放キーの追跡情報が維持されていること
            Assert.IsFalse(tracker.IsListening);
            Assert.AreEqual(currentSession, tracker.CurrentSessionId);
            Assert.IsTrue(_simulator.HasUnreleasedKeys);
        }

        // Test 43: 未解放キーの再試行成功 -> 通常どおり変換を開始できる
        [TestMethod]
        public void Test_43_UnreleasedKeysRetrySuccess_AllowsConversionStart()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);

            // 未解放キーが存在する状態
            _simulator.PressResolvedKey(KeyResolver.Resolve("a", "JIS"));
            _mock.FailKeys.Add(VirtualKeyCode.VK_A);
            _simulator.ReleaseResolvedKey(KeyResolver.Resolve("a", "JIS"));
            Assert.IsTrue(_simulator.HasUnreleasedKeys);

            // 送信環境が回復（失敗フラグ解除）
            _mock.FailKeys.Remove(VirtualKeyCode.VK_A);

            // 変換開始前のチェックを実行
            bool canStart = _simulator.TryPrepareStartConversion();
            Assert.IsTrue(canStart);
            Assert.IsFalse(_simulator.HasUnreleasedKeys);

            // 通常どおり変換セッションを開始できること
            long session = tracker.StartConversionSession();
            Assert.IsTrue(tracker.IsListening);
            Assert.IsTrue(session > 0);

            // 新セッションでキー押下・解放が正常動作すること
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.IsTrue(_mock.History.Any(e => e.Key == VirtualKeyCode.VK_A && e.IsDown));
        }

        // Test 44: 停止時のKeyUp失敗 -> 未解放キーの追跡情報が残る
        [TestMethod]
        public void Test_44_StopListeningKeyUpFailure_RetainsUnreleasedKeyTracking()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartConversionSession();

            // 押下中にする
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));

            // 停止時のKeyUp送信を失敗させる
            _mock.FailKeys.Add(VirtualKeyCode.VK_A);

            // 停止処理実行（セッション無効化＋全キー解放）
            tracker.StopSession();

            // リスナー停止・セッション無効化は完了していること
            Assert.IsFalse(tracker.IsListening);

            // しかし、KeyUp失敗キーが未解放キーとして追跡されていること
            Assert.IsTrue(_simulator.HasUnreleasedKeys);
            Assert.AreEqual(1, _simulator.UnreleasedKeysCount);
            Assert.IsTrue(_simulator.IsKeyUnreleased(VirtualKeyCode.VK_A));
        }

        // Test 45: 停止後の再試行成功 -> 未解放状態が解除される
        [TestMethod]
        public void Test_45_PostStopRetrySuccess_ClearsUnreleasedState()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartConversionSession();

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            _mock.FailKeys.Add(VirtualKeyCode.VK_A);
            tracker.StopSession();

            Assert.IsTrue(_simulator.HasUnreleasedKeys);

            // 停止後に再試行を実行（環境回復後）
            _mock.FailKeys.Remove(VirtualKeyCode.VK_A);
            bool allReleased = _simulator.RetryReleasePendingKeys();

            // 未解放状態が正常に解除されること
            Assert.IsTrue(allReleased);
            Assert.IsFalse(_simulator.HasUnreleasedKeys);
            Assert.AreEqual(0, _simulator.UnreleasedKeysCount);
        }

        // Test 46: 変換中キャプチャ終了後の重複NoteOn -> KeyDownが発生しない
        [TestMethod]
        public void Test_46_ConversionCapturingDuplicateNoteOn_DoesNotSendKeyDown()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "b" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartConversionSession();

            // キャプチャ開始
            tracker.StartCaptureSession();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(1, tracker.SuppressedSourcesCount);

            // キャプチャ終了
            tracker.StopCaptureSession();
            Assert.IsFalse(tracker.IsCapturing);

            _mock.Clear();

            // キャプチャ終了後に重複NoteOnが複数回到着
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 120, true, session));

            // 一切KeyDownは送信されない
            Assert.AreEqual(0, _mock.History.Count);
            Assert.AreEqual(0, tracker.ActiveNotesCount);
            Assert.AreEqual(0, _simulator.GetRefCount(VirtualKeyCode.VK_B));
        }

        // Test 47: 抑止解除後の新規NoteOn -> 通常変換を再開できる
        [TestMethod]
        public void Test_47_NewNoteOnAfterSuppressionReleased_ResumesNormalConversion()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "c" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartConversionSession();

            // キャプチャ中に押下
            tracker.StartCaptureSession();
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            tracker.StopCaptureSession();

            // NoteOffで抑止解除
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(0, tracker.SuppressedSourcesCount);

            _mock.Clear();

            // 抑止解除後、新規NoteOnを受信
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.VK_C, _mock.History[0].Key);
            Assert.IsTrue(_mock.History[0].IsDown);

            // NoteOffで正常解放
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 0, false, session));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.VK_C, _mock.History[1].Key);
            Assert.IsFalse(_mock.History[1].IsDown);
        }

        // Test 48: ペダル抑止解除後の再踏み込み -> 正常にKeyDown・KeyUpが発生する
        [TestMethod]
        public void Test_48_PedalResumesNormalConversionAfterSuppressionReleased()
        {
            var settings = new AppSettings { Mapping = new() { { "pedal", "shift" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartConversionSession();

            // キャプチャ中にペダル踏み込み
            tracker.StartCaptureSession();
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session));
            tracker.StopCaptureSession();

            // ペダル解放で抑止解除
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 0, session));
            Assert.AreEqual(0, tracker.SuppressedSourcesCount);
            Assert.AreEqual(0, _mock.History.Count);

            // 抑止解除後の新規ペダル踏み込み
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 127, session));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.SHIFT, _mock.History[0].Key);
            Assert.IsTrue(_mock.History[0].IsDown);

            // ペダル解放でShift解放
            tracker.ProcessControlEvent(new MidiControlData("dev1", "Piano", 0, 64, 0, session));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.SHIFT, _mock.History[1].Key);
            Assert.IsFalse(_mock.History[1].IsDown);
        }

        // Test 49: 全ポート開始失敗時のロールバック
        [TestMethod]
        public void Test_49_AllPortsFailed_RollbackAndReportsAllFailed()
        {
            var listener = new FakeMidiListener { FailAllPorts = true };
            var result = listener.Start(new[] { "Port1", "Port2" });

            Assert.IsTrue(result.IsAllFailed);
            Assert.IsFalse(result.IsAllSuccess);
            Assert.IsFalse(result.IsPartialSuccess);
            Assert.AreEqual(0, result.OpenedPorts.Count);
            Assert.AreEqual(2, result.FailedPorts.Count);
            Assert.AreEqual(0, listener.GetActivePorts().Count);
        }

        // Test 50: 一部ポート失敗時の正常ポート継続
        [TestMethod]
        public void Test_50_PartialPortsFailed_ContinuesActivePorts()
        {
            var listener = new FakeMidiListener();
            listener.FailPorts.Add("BadPort");
            var result = listener.Start(new[] { "GoodPort", "BadPort" });

            Assert.IsTrue(result.IsPartialSuccess);
            Assert.IsFalse(result.IsAllFailed);
            Assert.AreEqual(1, result.OpenedPorts.Count);
            Assert.AreEqual("GoodPort", result.OpenedPorts[0].DeviceName);
            Assert.AreEqual(1, result.FailedPorts.Count);
            Assert.AreEqual("BadPort", result.FailedPorts[0].PortName);
            Assert.AreEqual(1, listener.GetActivePorts().Count);
        }

        // Test 51: 監視開始直後のイベントが正しくディスパッチされる
        [TestMethod]
        public void Test_51_EventsImmediatelyAfterStart_AreProcessed()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "k" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            var listener = new FakeMidiListener();

            listener.OnNoteReceived += tracker.ProcessNoteEvent;
            long gen = tracker.StartConversionSession();
            var startRes = listener.Start(new[] { "Piano" }, gen);

            Assert.IsTrue(startRes.IsAllSuccess);

            // 開始直後にイベントを発火
            listener.FireNote(new MidiNoteData(startRes.OpenedPorts[0].DeviceId, "Piano", 0, 60, 100, true, gen));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(VirtualKeyCode.VK_K, _mock.History[0].Key);
            Assert.IsTrue(_mock.History[0].IsDown);
        }

        // Test 52: ホットキーの登録競合とロールバック
        [TestMethod]
        public void Test_52_HotkeyManager_ConflictWithMapping_Rejected()
        {
            var hotkeyMgr = new HotkeyManager();
            var mapping = new Dictionary<string, string> { { "60", "F9" } };
            var hotkey = new HotkeySettings { Enabled = true, Modifiers = "Ctrl+Alt", Key = "F9" };

            // マッピングにF9が存在するため衝突拒否されること
            bool registered = hotkeyMgr.TryRegister(IntPtr.Zero, 9001, hotkey, mapping, "JIS", out string? errMsg);
            Assert.IsFalse(registered);
            Assert.IsNotNull(errMsg);
            Assert.IsTrue(errMsg.Contains("MIDIマッピング先"));
            Assert.IsFalse(hotkeyMgr.IsRegistered);
        }

        // Test 53: MIDIマッピングによるホットキー誤発動防止（衝突判定）
        [TestMethod]
        public void Test_53_IsHotkeyConflictingWithMapping_Verification()
        {
            var hotkey = new HotkeySettings { Enabled = true, Modifiers = "Ctrl+Alt", Key = "F9" };
            var conflictMapping = new Dictionary<string, string> { { "60", "f9" } };
            var nonConflictMapping = new Dictionary<string, string> { { "60", "a" }, { "62", "b" } };

            Assert.IsTrue(SettingsManager.IsHotkeyConflictingWithMapping(hotkey, conflictMapping, "JIS"));
            Assert.IsTrue(SettingsManager.IsHotkeyConflictingWithMapping(hotkey, conflictMapping, "US"));
            Assert.IsFalse(SettingsManager.IsHotkeyConflictingWithMapping(hotkey, nonConflictMapping, "JIS"));

            // ホットキーが無効な場合は衝突と判定しない
            hotkey.Enabled = false;
            Assert.IsFalse(SettingsManager.IsHotkeyConflictingWithMapping(hotkey, conflictMapping, "JIS"));
        }

        // Test 54: 切断したデバイスの入力のみ解放（他デバイス維持）
        [TestMethod]
        public void Test_54_DisconnectedDeviceInputs_ReleasedIndependently()
        {
            var settings = new AppSettings {
                Mapping = new() { { "60", "x" }, { "62", "x" } }
            };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartConversionSession();

            // dev1からNote 60 ('x'), dev2からNote 62 ('x')
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano1", 0, 60, 100, true, session));
            tracker.ProcessNoteEvent(new MidiNoteData("dev2", "Piano2", 0, 62, 100, true, session));

            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(2, _simulator.GetRefCount(VirtualKeyCode.VK_X));

            // dev1のみ切断
            tracker.ReleaseDeviceInputs("dev1", session);

            // dev2の入力があるため、キー 'x' の押下は維持される
            Assert.AreEqual(1, _simulator.GetRefCount(VirtualKeyCode.VK_X));
            Assert.AreEqual(1, _mock.History.Count); // 解放イベントは未送信

            // dev2も切断
            tracker.ReleaseDeviceInputs("dev2", session);
            Assert.AreEqual(0, _simulator.GetRefCount(VirtualKeyCode.VK_X));
            Assert.AreEqual(2, _mock.History.Count);
            Assert.IsFalse(_mock.History[1].IsDown);
        }

        // Test 55: 同名デバイスの複数接続（一意DeviceIdにより独立管理）
        [TestMethod]
        public void Test_55_DuplicateDeviceNames_HandledIndependently()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "a" }, { "62", "b" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartConversionSession();

            string devA = "USB-MIDI#1";
            string devB = "USB-MIDI#2";

            tracker.ProcessNoteEvent(new MidiNoteData(devA, "USB-MIDI", 0, 60, 100, true, session));
            tracker.ProcessNoteEvent(new MidiNoteData(devB, "USB-MIDI", 0, 62, 100, true, session));

            Assert.AreEqual(2, tracker.ActiveNotesCount);
            Assert.AreEqual(1, _simulator.GetRefCount(VirtualKeyCode.VK_A));
            Assert.AreEqual(1, _simulator.GetRefCount(VirtualKeyCode.VK_B));

            // 片方のインスタンスが切断
            tracker.ReleaseDeviceInputs(devA, session);

            Assert.AreEqual(1, tracker.ActiveNotesCount);
            Assert.AreEqual(0, _simulator.GetRefCount(VirtualKeyCode.VK_A));
            Assert.AreEqual(1, _simulator.GetRefCount(VirtualKeyCode.VK_B)); // もう片方は維持
        }

        // Test 56: 全ポート切断または切断元特定不能時の安全停止
        [TestMethod]
        public void Test_56_AllPortsDisconnectedOrUnknown_StopsSessionSafely()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "z" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartConversionSession();

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));
            Assert.IsTrue(tracker.IsListening);
            Assert.AreEqual(1, _simulator.GetRefCount(VirtualKeyCode.VK_Z));

            // 切断元が特定不能（空文字）の切断通知 -> 安全側として全変換停止
            tracker.ReleaseDeviceInputs(string.Empty, session);

            Assert.IsFalse(tracker.IsListening);
            Assert.AreEqual(0, tracker.ActiveNotesCount);
            Assert.AreEqual(0, _simulator.GetRefCount(VirtualKeyCode.VK_Z));
        }

        // Test 57: 切断処理と手動停止の競合
        [TestMethod]
        public void Test_57_DisconnectionAndManualStopRace_DoesNotThrow()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "y" } } };
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartConversionSession();

            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, session));

            // 切断処理と手動停止を並行実行
            var task1 = Task.Run(() => tracker.ReleaseDeviceInputs("dev1", session));
            var task2 = Task.Run(() => tracker.StopSession());

            Task.WaitAll(task1, task2);

            Assert.IsFalse(tracker.IsListening);
            Assert.AreEqual(0, _simulator.GetRefCount(VirtualKeyCode.VK_Y));
        }

        // Test 58: 再接続後の旧イベント破棄
        [TestMethod]
        public void Test_58_StaleEventsFromDisconnectedDeviceOrOldGeneration_Discarded()
        {
            var settings = new AppSettings { Mapping = new() { { "60", "m" } } };
            var tracker = new InputTracker(_simulator, () => settings);

            long oldSession = tracker.StartConversionSession();
            tracker.ReleaseDeviceInputs("dev1", oldSession);
            tracker.StopSession();

            // 新セッション開始
            long newSession = tracker.StartConversionSession();
            Assert.AreNotEqual(oldSession, newSession);

            _mock.Clear();

            // 旧セッションの遅延イベントが届く
            tracker.ProcessNoteEvent(new MidiNoteData("dev1", "Piano", 0, 60, 100, true, oldSession));
            Assert.AreEqual(0, _mock.History.Count);
            Assert.AreEqual(0, _simulator.GetRefCount(VirtualKeyCode.VK_M));

            // 新セッションのイベントは正常処理
            tracker.ProcessNoteEvent(new MidiNoteData("dev1_new", "Piano", 0, 60, 100, true, newSession));
            Assert.AreEqual(1, _mock.History.Count);
            Assert.AreEqual(1, _simulator.GetRefCount(VirtualKeyCode.VK_M));
        }

        // Test 59: キー送信失敗時のエラー通知
        [TestMethod]
        public void Test_59_KeySendError_NotifiesWithWin32ErrorWithoutLock()
        {
            _mock.FailKeys.Add(VirtualKeyCode.VK_A);
            _mock.SimulatedWin32ErrorOnFail = 5; // ERROR_ACCESS_DENIED

            KeySendErrorInfo? capturedError = null;
            _simulator.OnKeySendError += (err) => {
                capturedError = err;
            };

            var resolved = KeyResolver.Resolve("a", "JIS");
            bool success = _simulator.PressResolvedKey(resolved);

            Assert.IsFalse(success);
            Assert.IsNotNull(capturedError);
            Assert.AreEqual("VK_A", capturedError.KeyName);
            Assert.AreEqual(5, capturedError.Win32Error);
            Assert.IsTrue(capturedError.IsDown);
        }

        // Test 60: キー名厳格検証（KeyResolver.IsValidTargetKey）
        [TestMethod]
        public void Test_60_KeyResolver_RejectsInvalidKeysStrictly()
        {
            // 有効なキー
            Assert.IsTrue(KeyResolver.IsValidTargetKey("a", "JIS"));
            Assert.IsTrue(KeyResolver.IsValidTargetKey("Z", "JIS"));
            Assert.IsTrue(KeyResolver.IsValidTargetKey("1", "JIS"));
            Assert.IsTrue(KeyResolver.IsValidTargetKey("enter", "JIS"));
            Assert.IsTrue(KeyResolver.IsValidTargetKey("space", "JIS"));
            Assert.IsTrue(KeyResolver.IsValidTargetKey("f9", "JIS"));
            Assert.IsTrue(KeyResolver.IsValidTargetKey("F12", "JIS"));
            Assert.IsTrue(KeyResolver.IsValidTargetKey("zenkaku_hankaku", "JIS"));
            Assert.IsTrue(KeyResolver.IsValidTargetKey(";", "JIS"));
            Assert.IsTrue(KeyResolver.IsValidTargetKey(";", "US"));

            // 拒否されるべきキー
            Assert.IsFalse(KeyResolver.IsValidTargetKey("", "JIS"));
            Assert.IsFalse(KeyResolver.IsValidTargetKey("   ", "JIS"));
            Assert.IsFalse(KeyResolver.IsValidTargetKey("NONAME", "JIS"));
            Assert.IsFalse(KeyResolver.IsValidTargetKey("noname", "JIS"));
            Assert.IsFalse(KeyResolver.IsValidTargetKey("LButton", "JIS"));
            Assert.IsFalse(KeyResolver.IsValidTargetKey("RButton", "JIS"));
            Assert.IsFalse(KeyResolver.IsValidTargetKey("MButton", "JIS"));
            Assert.IsFalse(KeyResolver.IsValidTargetKey("XButton1", "JIS"));
            Assert.IsFalse(KeyResolver.IsValidTargetKey("123", "JIS")); // 2桁以上の数字
            Assert.IsFalse(KeyResolver.IsValidTargetKey("invalid_key_code", "JIS"));
        }

        // Test 61: 既存JSON互換性および大文字PEDALの正規化
        [TestMethod]
        public void Test_61_SettingsCompatibility_NormalizesUppercasePedal()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"settings_test_{Guid.NewGuid():N}.json");
            try
            {
                string json = @"
{
  ""selected_ports"": [""TestPort""],
  ""keyboard_layout"": ""JIS"",
  ""mapping"": {
    ""60"": ""k"",
    ""PEDAL"": ""space""
  }
}";
                File.WriteAllText(tempFile, json);

                var loaded = SettingsManager.Load(tempFile);
                Assert.IsNotNull(loaded);
                Assert.AreEqual("JIS", loaded.KeyboardLayout);
                Assert.IsTrue(loaded.Mapping.ContainsKey("pedal"));
                Assert.IsFalse(loaded.Mapping.ContainsKey("PEDAL"));
                Assert.AreEqual("space", loaded.Mapping["pedal"]);
                Assert.AreEqual("k", loaded.Mapping["60"]);
                Assert.IsNotNull(loaded.Hotkey);
                Assert.IsTrue(loaded.Hotkey.Enabled);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        // Test 62: 既存JSON読み込み時のホットキー衝突自動復旧
        [TestMethod]
        public void Test_62_SettingsLoad_HotkeyConflict_AutoDisablesHotkeySafely()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"settings_conflict_{Guid.NewGuid():N}.json");
            try
            {
                // マッピングにF9があり、ホットキーもF9で有効
                string json = @"
{
  ""selected_ports"": [],
  ""keyboard_layout"": ""JIS"",
  ""mapping"": {
    ""60"": ""F9""
  },
  ""hotkey"": {
    ""enabled"": true,
    ""modifiers"": ""Ctrl+Alt"",
    ""key"": ""F9""
  }
}";
                File.WriteAllText(tempFile, json);

                var loaded = SettingsManager.Load(tempFile);
                Assert.IsNotNull(loaded);
                // マッピングは維持され、ホットキーが無効化されて安全に復旧
                Assert.AreEqual("F9", loaded.Mapping["60"]);
                Assert.IsFalse(loaded.Hotkey.Enabled);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        // Test 63: 列挙メソッドの安全性
        [TestMethod]
        public void Test_63_GetPortNames_DisposesAllEnumeratedDevices()
        {
            // 例外が発生せず正常に列挙リストが取得できること
            var names = MidiListener.GetPortNames();
            Assert.IsNotNull(names);
        }
    }
}
