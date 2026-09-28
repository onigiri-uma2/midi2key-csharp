namespace MidiToKeyApp
{
    /// <summary>
    /// MIDIノート番号（0-127）から音階名（例: C4 / ド、A0 / ラ）への変換を行うヘルパークラス。
    /// 88鍵盤ピアノ（21[A0]〜108[C8]）の視覚的把握をサポートします。
    /// </summary>
    public static class MidiNoteHelper
    {
        private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        private static readonly string[] KanaNames = { "ド", "ド#", "レ", "レ#", "ミ", "ファ", "ファ#", "ソ", "ソ#", "ラ", "ラ#", "シ" };

        public static string GetNoteDisplayName(int noteNumber)
        {
            if (noteNumber < 0 || noteNumber > 127) return noteNumber.ToString();
            int octave = (noteNumber / 12) - 1; // MIDI規格: Note 60 = C4, Note 21 = A0
            string name = NoteNames[noteNumber % 12];
            string kana = KanaNames[noteNumber % 12];
            return $"{name}{octave} / {kana}";
        }
    }
}
