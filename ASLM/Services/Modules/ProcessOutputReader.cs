// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace ASLM.Services.Modules
{
    // Process output streams

    /// <summary>
    /// Redirected output stream of a process.
    /// </summary>
    internal enum ProcessOutputStream
    {
        /// <summary>
        /// The standard output stream.
        /// </summary>
        StandardOutput,

        /// <summary>
        /// The standard error stream.
        /// </summary>
        StandardError
    }


    // Output reader

    /// <summary>
    /// Turns the bytes of one redirected process stream into console lines the way a terminal shows them.
    /// A carriage return moves back to the start of the line and a backspace one character back, so a progress bar
    /// that redraws its line stays one line instead of becoming one line per redraw. Text is decoded as UTF-8; on
    /// Windows, text that is not valid UTF-8 is decoded with the OEM code page that console programs such as cmd.exe
    /// write, so the output of any runtime stays readable. The unfinished last line is reported whenever its visible
    /// text changes, so prompts and progress bars appear before their line ends.
    /// </summary>
    internal sealed class ProcessOutputReader
    {
        // Fields and constants

        /// <summary>
        /// Maximum number of characters kept for one line; characters beyond it are dropped. The console store cuts
        /// stored lines shorter and marks them as truncated, so the dropped rest would never be shown.
        /// </summary>
        internal const int MaxLineLength = 8192;

        /// <summary>
        /// Number of buffered bytes without a control character after which they are decoded, so even a very long
        /// line never needs a larger buffer.
        /// </summary>
        private const int MaxBufferedBytes = 16384;

        /// <summary>
        /// Number of bytes requested by one read of the stream.
        /// </summary>
        private const int ReadBufferSize = 4096;

        /// <summary>
        /// Byte order mark that UTF-8 output may start with.
        /// </summary>
        private const char ByteOrderMark = (char)0xFEFF;

        /// <summary>
        /// Encoding for output that is not valid UTF-8: the OEM code page on Windows, or null where console programs
        /// write UTF-8 (other systems, or Windows configured to use UTF-8 system-wide).
        /// </summary>
        private static readonly Lazy<Encoding?> LegacyConsoleEncoding = new(ResolveLegacyConsoleEncoding);

        /// <summary>
        /// Stream whose lines this reader reports.
        /// </summary>
        private readonly ProcessOutputStream _stream;

        /// <summary>
        /// Encoding for text that is not valid UTF-8, or null to decode such text as UTF-8 with replacement characters.
        /// </summary>
        private readonly Encoding? _fallbackEncoding;

        /// <summary>
        /// Called with every finished line.
        /// </summary>
        private readonly Action<ProcessOutputStream, string> _lineCompleted;

        /// <summary>
        /// Called whenever the visible text of the unfinished last line changes.
        /// </summary>
        private readonly Action<ProcessOutputStream, string> _lineUpdated;

        /// <summary>
        /// Visible text of the current line, as a terminal would show it.
        /// </summary>
        private readonly StringBuilder _line = new();

        /// <summary>
        /// Bytes received since the last control character that are not decoded yet.
        /// </summary>
        private readonly byte[] _buffered = new byte[MaxBufferedBytes];

        /// <summary>
        /// Number of valid bytes at the start of <see cref="_buffered"/>.
        /// </summary>
        private int _bufferedCount;

        /// <summary>
        /// Position in <see cref="_line"/> where the next character is written.
        /// </summary>
        private int _cursor;

        /// <summary>
        /// Text of the unfinished line that was reported last, or null when nothing was reported for the current line.
        /// </summary>
        private string? _reportedLine;

        /// <summary>
        /// Whether no text was decoded yet, so a byte order mark at the start of the stream can still be dropped.
        /// </summary>
        private bool _atStreamStart = true;


        // Initialization

        /// <summary>
        /// Creates a reader for one output stream.
        /// </summary>
        /// <param name="stream">Stream whose lines the reader reports.</param>
        /// <param name="fallbackEncoding">Encoding for text that is not valid UTF-8, or null for none.</param>
        /// <param name="lineCompleted">Called with every finished line.</param>
        /// <param name="lineUpdated">Called whenever the visible text of the unfinished last line changes.</param>
        internal ProcessOutputReader(
            ProcessOutputStream stream,
            Encoding? fallbackEncoding,
            Action<ProcessOutputStream, string> lineCompleted,
            Action<ProcessOutputStream, string> lineUpdated)
        {
            _stream = stream;
            _fallbackEncoding = fallbackEncoding;
            _lineCompleted = lineCompleted;
            _lineUpdated = lineUpdated;
        }


        // Reading

        /// <summary>
        /// Reads the redirected standard output and standard error of a started process until both streams end.
        /// Each stream is read on its own long-running thread, because reads of a process pipe block.
        /// </summary>
        /// <param name="process">Started process whose output and error streams are redirected.</param>
        /// <param name="lineCompleted">Called with every finished line of either stream.</param>
        /// <param name="lineUpdated">Called whenever the unfinished last line of a stream changes.</param>
        /// <returns>A task that completes once both streams have ended.</returns>
        internal static Task ReadAsync(
            Process process,
            Action<ProcessOutputStream, string> lineCompleted,
            Action<ProcessOutputStream, string> lineUpdated)
        {
            return Task.WhenAll(
                Start(process.StandardOutput.BaseStream, ProcessOutputStream.StandardOutput, lineCompleted, lineUpdated),
                Start(process.StandardError.BaseStream, ProcessOutputStream.StandardError, lineCompleted, lineUpdated));
        }

        /// <summary>
        /// Starts reading one stream to its end on a dedicated thread.
        /// </summary>
        /// <param name="source">Raw stream of the process pipe.</param>
        /// <param name="stream">Which output stream <paramref name="source"/> is.</param>
        /// <param name="lineCompleted">Called with every finished line.</param>
        /// <param name="lineUpdated">Called whenever the unfinished last line changes.</param>
        private static Task Start(
            Stream source,
            ProcessOutputStream stream,
            Action<ProcessOutputStream, string> lineCompleted,
            Action<ProcessOutputStream, string> lineUpdated)
        {
            var reader = new ProcessOutputReader(stream, LegacyConsoleEncoding.Value, lineCompleted, lineUpdated);
            return Task.Factory.StartNew(
                () => reader.ReadToEnd(source),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Reads the stream until it ends, then reports its unfinished last line as finished.
        /// </summary>
        /// <param name="source">Raw stream of the process pipe.</param>
        private void ReadToEnd(Stream source)
        {
            var buffer = new byte[ReadBufferSize];
            try
            {
                int count;
                while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    Write(buffer.AsSpan(0, count));
                }
            }
            catch (IOException)
            {
                // A broken pipe ends the output the same way as its regular end.
            }
            catch (ObjectDisposedException)
            {
                // The process object was disposed after an aborted command, so nothing more can be read.
            }

            Complete();
        }


        // Line building

        /// <summary>
        /// Processes a chunk of raw output: control characters move the cursor or finish the line, other bytes are
        /// buffered for decoding. Afterwards the unfinished line is reported when its visible text changed.
        /// </summary>
        /// <param name="data">Bytes read from the stream.</param>
        internal void Write(ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                switch (value)
                {
                    case (byte)'\n':
                        // A line feed finishes the line.
                        DecodeBuffered(atControlCharacter: true);
                        CompleteLine();
                        break;

                    case (byte)'\r':
                        // A carriage return lets the following text overwrite the line from its start.
                        DecodeBuffered(atControlCharacter: true);
                        _cursor = 0;
                        break;

                    case (byte)'\b':
                        // A backspace lets the following text overwrite the previous character.
                        DecodeBuffered(atControlCharacter: true);
                        _cursor = Math.Max(0, _cursor - 1);
                        break;

                    default:
                        // Text waits for the next control character, or is decoded early once the buffer is full.
                        if (_bufferedCount == _buffered.Length)
                        {
                            DecodeBuffered(atControlCharacter: false);
                        }

                        _buffered[_bufferedCount++] = value;
                        break;
                }
            }

            ReportUnfinishedLine();
        }

        /// <summary>
        /// Ends the stream: the unfinished last line, if any, is reported as finished.
        /// </summary>
        internal void Complete()
        {
            DecodeBuffered(atControlCharacter: true);
            if (_line.Length > 0)
            {
                CompleteLine();
            }
        }

        /// <summary>
        /// Reports the current line as finished and starts a new one.
        /// </summary>
        private void CompleteLine()
        {
            var text = _line.ToString();
            _line.Clear();
            _cursor = 0;
            _reportedLine = null;
            _lineCompleted(_stream, text);
        }

        /// <summary>
        /// Reports the visible text of the unfinished line, including buffered bytes that are not decoded yet, when it
        /// differs from the text reported last.
        /// </summary>
        private void ReportUnfinishedLine()
        {
            if (_line.Length == 0 && _bufferedCount == 0)
            {
                return;
            }

            // Show the buffered bytes without consuming them; a character split between reads is completed later.
            var text = _line.ToString();
            if (_bufferedCount > 0)
            {
                var preview = new StringBuilder(text);
                var cursor = _cursor;
                Overwrite(preview, ref cursor, Decode(_buffered.AsSpan(0, _bufferedCount), keepIncompleteTail: true, out _));
                text = preview.ToString();
            }

            if (text.Length == 0 || string.Equals(text, _reportedLine, StringComparison.Ordinal))
            {
                return;
            }

            _reportedLine = text;
            _lineUpdated(_stream, text);
        }


        // Decoding

        /// <summary>
        /// Decodes the buffered bytes into the line at the cursor. Bytes that end at a control character form
        /// complete text; otherwise a UTF-8 character cut off at the end of the buffer is kept for the next read.
        /// </summary>
        /// <param name="atControlCharacter">True when a control character or the stream end follows the bytes.</param>
        private void DecodeBuffered(bool atControlCharacter)
        {
            if (_bufferedCount == 0)
            {
                return;
            }

            // Decode, then move the kept bytes to the start of the buffer; the copy handles the overlap.
            var bytes = _buffered.AsSpan(0, _bufferedCount);
            var text = Decode(bytes, keepIncompleteTail: !atControlCharacter, out var keptBytes);
            bytes[(bytes.Length - keptBytes)..].CopyTo(_buffered);
            _bufferedCount = keptBytes;

            if (text.Length > 0)
            {
                _atStreamStart = false;
                Overwrite(_line, ref _cursor, text);
            }
        }

        /// <summary>
        /// Decodes bytes as UTF-8 when they are valid UTF-8, and with the fallback encoding otherwise.
        /// </summary>
        /// <param name="bytes">Bytes to decode.</param>
        /// <param name="keepIncompleteTail">True to leave a UTF-8 character cut off at the end undecoded.</param>
        /// <param name="keptBytes">Number of bytes at the end that were left undecoded.</param>
        /// <returns>The decoded text, without a byte order mark at the start of the stream.</returns>
        private string Decode(ReadOnlySpan<byte> bytes, bool keepIncompleteTail, out int keptBytes)
        {
            // A character cut off by the end of a read is completed by the next read, so it is left out.
            keptBytes = keepIncompleteTail ? GetIncompleteUtf8TailLength(bytes) : 0;
            var complete = bytes[..(bytes.Length - keptBytes)];
            string text;
            if (Utf8.IsValid(complete))
            {
                text = Encoding.UTF8.GetString(complete);
            }
            else
            {
                // Not UTF-8: a legacy code page assigns a character to every byte, so nothing is kept.
                keptBytes = 0;
                text = (_fallbackEncoding ?? Encoding.UTF8).GetString(bytes);
            }

            // A byte order mark at the start of the stream is not part of the output.
            return _atStreamStart && text.StartsWith(ByteOrderMark) ? text[1..] : text;
        }

        /// <summary>
        /// Writes text into a line at the cursor, overwriting characters that are already there as a terminal does,
        /// and moves the cursor behind it. Characters beyond <see cref="MaxLineLength"/> are dropped.
        /// </summary>
        /// <param name="line">Line that receives the text.</param>
        /// <param name="cursor">Position of the first written character; moved behind the written text.</param>
        /// <param name="text">Text to write.</param>
        private static void Overwrite(StringBuilder line, ref int cursor, string text)
        {
            foreach (var character in text)
            {
                if (cursor < line.Length)
                {
                    line[cursor] = character;
                }
                else if (line.Length < MaxLineLength)
                {
                    line.Append(character);
                }

                cursor = Math.Min(cursor + 1, MaxLineLength);
            }
        }

        /// <summary>
        /// Returns the number of bytes at the end that start a UTF-8 character without completing it.
        /// </summary>
        /// <param name="bytes">Bytes whose end is checked.</param>
        private static int GetIncompleteUtf8TailLength(ReadOnlySpan<byte> bytes)
        {
            // Walk back over continuation bytes to the lead byte of the last character, then compare the length the
            // lead byte announces with the number of bytes that are present.
            for (var length = 1; length <= Math.Min(4, bytes.Length); length++)
            {
                var value = bytes[^length];
                if ((value & 0xC0) == 0x80)
                {
                    continue;
                }

                var expected = value >= 0xF0 ? 4 : value >= 0xE0 ? 3 : value >= 0xC0 ? 2 : 1;
                return expected > length ? length : 0;
            }

            return 0;
        }


        // Legacy console encoding

        /// <summary>
        /// Returns the OEM code page encoding that Windows console programs use for redirected output, or null on
        /// other systems and when Windows uses UTF-8 for it.
        /// </summary>
        private static Encoding? ResolveLegacyConsoleEncoding()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var codePage = (int)GetOEMCP();
            return codePage == Encoding.UTF8.CodePage
                ? null
                : CodePagesEncodingProvider.Instance.GetEncoding(codePage);
        }

        /// <summary>
        /// Returns the identifier of the OEM code page of the system.
        /// </summary>
        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();
    }
}
