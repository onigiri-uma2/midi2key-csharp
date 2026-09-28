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

        public bool SendHardwareKey(VirtualKeyCode vk, bool isDown)
        {
            if (FailAll || FailKeys.Contains(vk))
            {
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
        }
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

        // Test 9: 変換中にノート番号欄へフォーカス
        [TestMethod]
        public void Test_09_FocusNoteTextBoxDuringListening_DoesNotRestartListener()
        {
            var settings = new AppSettings();
            var tracker = new InputTracker(_simulator, () => settings);
            long session = tracker.StartSession();

            tracker.SetCapturing(true);
            Assert.IsTrue(tracker.IsListening);
            Assert.AreEqual(session, tracker.CurrentSessionId);
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

        // Test 14: 別名保存失敗
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

        // Test 27: 保存失敗 -> 元ファイルと現在パスを維持
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

                // 不正な設定（mapping = null）を保存しようとすると失敗
                var badSettings = new AppSettings { Mapping = null! };
                Assert.ThrowsException<InvalidDataException>(() => SettingsManager.Save(settingsPath, badSettings));

                // 元の設定ファイルが破損せず、US設定のままであること
                var reloaded = SettingsManager.Load(settingsPath);
                Assert.AreEqual("US", reloaded.KeyboardLayout);
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
    }
}
