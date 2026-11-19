// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Carries the text and identity of an output snapshot together so a session switch is applied in one update.
    /// </summary>
    internal sealed class ConsoleOutput(string sessionKey, string text)
    {
        /// <summary>
        /// Empty output before a console is opened.
        /// </summary>
        internal static readonly ConsoleOutput Empty = new(string.Empty, string.Empty);

        /// <summary>
        /// Identifies the session that produced this text.
        /// </summary>
        internal string SessionKey { get; } = sessionKey;

        /// <summary>
        /// Text displayed by the native console.
        /// </summary>
        internal string Text { get; } = text;

        /// <summary>
        /// Locations of real log lines, excluding display-only banners and placeholders.
        /// </summary>
        private IReadOnlyList<LinePosition> Positions { get; init; } = [];

        /// <summary>
        /// Builds native text and records the original positions of its lines for later scroll restoration.
        /// </summary>
        internal static ConsoleOutput FromLines(string sessionKey, IReadOnlyList<ConsoleOutputLine> lines)
        {
            if (lines.Count == 0) return new ConsoleOutput(sessionKey, string.Empty);

            var text = new StringBuilder();
            var positions = new List<LinePosition>(lines.Count);
            foreach (var line in lines)
            {
                if (line.Sequence > 0) positions.Add(new LinePosition(line.Sequence, text.Length));
                text.Append(line.Text);
                text.AppendLine();
            }

            text.Length -= Environment.NewLine.Length;
            return new ConsoleOutput(sessionKey, text.ToString()) { Positions = positions };
        }

        /// <summary>
        /// Builds edits in descending order so each action can use offsets from the previous snapshot.
        /// </summary>
        internal IReadOnlyList<ConsoleTextEdit> GetEdits(ConsoleOutput previous)
        {
            var oldText = previous.Text;
            if (Text.StartsWith(oldText, StringComparison.Ordinal) && !SplitsCharacter(Text, oldText.Length))
            {
                return Text.Length == oldText.Length ? [] : [new(oldText.Length, oldText.Length, Text[oldText.Length..])];
            }

            // Match source lines by sequence so trimming or replacing one line need not touch the retained text.
            var edits = new List<ConsoleTextEdit>();
            var oldOffset = 0;
            var newOffset = 0;
            var oldIndex = 0;
            var newIndex = 0;
            while (oldIndex < previous.Positions.Count && newIndex < Positions.Count)
            {
                var oldLine = previous.Positions[oldIndex];
                var newLine = Positions[newIndex];
                if (oldLine.Sequence < newLine.Sequence) { oldIndex++; continue; }
                if (newLine.Sequence < oldLine.Sequence) { newIndex++; continue; }

                AddEdit(edits, oldText, oldOffset, oldLine.Offset, newOffset, newLine.Offset);
                oldOffset = ++oldIndex < previous.Positions.Count ? previous.Positions[oldIndex].Offset : oldText.Length;
                newOffset = ++newIndex < Positions.Count ? Positions[newIndex].Offset : Text.Length;
                AddEdit(edits, oldText, oldLine.Offset, oldOffset, newLine.Offset, newOffset);
            }

            AddEdit(edits, oldText, oldOffset, oldText.Length, newOffset, Text.Length);
            edits.Reverse();
            return edits;
        }

        /// <summary>
        /// Records only the changed part of a matched line or a gap between retained lines.
        /// </summary>
        private void AddEdit(List<ConsoleTextEdit> edits, string oldText, int oldStart, int oldEnd, int newStart, int newEnd)
        {
            var oldPart = oldText.AsSpan(oldStart, oldEnd - oldStart);
            var newPart = Text.AsSpan(newStart, newEnd - newStart);
            if (oldPart.SequenceEqual(newPart)) return;

            // Keep common text on each side, without splitting a Unicode surrogate pair or a CRLF.
            var prefix = oldPart.CommonPrefixLength(newPart);
            if (SplitsCharacter(oldPart, prefix) || SplitsCharacter(newPart, prefix)) prefix--;

            var suffix = 0;
            while (suffix < oldPart.Length - prefix && suffix < newPart.Length - prefix &&
                   oldPart[^(suffix + 1)] == newPart[^(suffix + 1)])
            {
                suffix++;
            }

            if (SplitsCharacter(oldPart, oldPart.Length - suffix) ||
                SplitsCharacter(newPart, newPart.Length - suffix))
            {
                suffix--;
            }

            edits.Add(new ConsoleTextEdit(oldStart + prefix, oldEnd - suffix,
                Text[(newStart + prefix)..(newEnd - suffix)]));
        }

        /// <summary>
        /// Reports whether an edit index lies inside a two-code-unit character or line break.
        /// </summary>
        private static bool SplitsCharacter(ReadOnlySpan<char> text, int index) =>
            index > 0 && index < text.Length &&
            ((char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index])) ||
              (text[index - 1] == '\r' && text[index] == '\n'));

        /// <summary>
        /// Captures the line at the viewport's top and finds its new character index after a rewrite or trim.
        /// </summary>
        internal ConsoleScrollPosition PreservePosition(ConsoleOutput? previous, double offset, Func<int, double?> lineTop)
        {
            if (previous is null || previous.Positions.Count == 0 || Positions.Count == 0 ||
                Text.StartsWith(previous.Text, StringComparison.Ordinal))
            {
                return new ConsoleScrollPosition(offset);
            }

            // Find the last line at or before the viewport's top, without measuring every line.
            var low = 0;
            var high = previous.Positions.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (lineTop(previous.Positions[middle].Offset) is not { } top) return new ConsoleScrollPosition(offset);
                if (top <= offset) low = middle + 1;
                else high = middle;
            }

            var oldLine = previous.Positions[Math.Max(0, low - 1)];
            var newIndex = 0;
            while (newIndex < Positions.Count - 1 && Positions[newIndex].Sequence < oldLine.Sequence)
            {
                newIndex++;
            }

            // If the visible line was trimmed, read from the nearest remaining line instead.
            var newLine = Positions[newIndex];
            var withinLine = newLine.Sequence == oldLine.Sequence && lineTop(oldLine.Offset) is { } oldTop
                ? offset - oldTop
                : 0;
            return new ConsoleScrollPosition(offset, newLine.Offset, withinLine);
        }

        /// <summary>
        /// Stores a source line identifier and its UTF-16 offset in the rendered text.
        /// </summary>
        private readonly record struct LinePosition(long Sequence, int Offset);
    }

    /// <summary>
    /// Holds one displayed line and its stable sequence number, or zero for synthetic lines.
    /// </summary>
    internal readonly record struct ConsoleOutputLine(long Sequence, string Text);

    /// <summary>
    /// Replaces a UTF-16 range without assigning or rebuilding the native document.
    /// </summary>
    internal readonly record struct ConsoleTextEdit(int Start, int End, string Text);

    /// <summary>
    /// Stores a viewport offset and, when needed, its position relative to a retained line.
    /// </summary>
    internal readonly record struct ConsoleScrollPosition(double Offset, int CharacterIndex = -1, double OffsetInLine = 0)
    {
        /// <summary>
        /// Resolves the requested offset against the new native text layout.
        /// </summary>
        internal double Resolve(Func<int, double?> lineTop) =>
            CharacterIndex >= 0 && lineTop(CharacterIndex) is { } top ? top + OffsetInLine : Offset;
    }
}
