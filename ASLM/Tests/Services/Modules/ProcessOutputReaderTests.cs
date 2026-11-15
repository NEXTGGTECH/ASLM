// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text;

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies how raw process output becomes console lines: line endings and blank lines, lines redrawn with
    /// carriage returns and backspaces, the unfinished last line, and decoding of UTF-8 and of the legacy console code
    /// page.
    /// </summary>
    public sealed class ProcessOutputReaderTests
    {
        // Line endings

        /// <summary>
        /// Verifies that LF and CRLF endings both finish a line, that blank lines are kept, and that output ending with
        /// a line ending reports no unfinished line.
        /// </summary>
        [Fact]
        public void Lf_and_crlf_lines_are_completed_and_blank_lines_are_kept()
        {
            var output = new RecordedOutput();
            var reader = output.CreateReader();

            reader.Write(Utf8("first\r\n\r\nsecond\nthird\n"));
            reader.Complete();

            output.Completed.Should().Equal("first", "", "second", "third");
            output.Updates.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that a line without a line ending is reported as unfinished while it grows and is completed when
        /// the stream ends.
        /// </summary>
        [Fact]
        public void Unfinished_line_is_shown_while_it_grows_and_completed_at_the_end_of_the_stream()
        {
            var output = new RecordedOutput();
            var reader = output.CreateReader();

            reader.Write(Utf8("Loading"));
            reader.Write(Utf8(" model..."));
            output.Completed.Should().BeEmpty();
            output.Updates.Should().Equal("Loading", "Loading model...");

            reader.Complete();
            output.Completed.Should().Equal("Loading model...");
        }


        // Redrawn lines

        /// <summary>
        /// Verifies that a progress bar redrawn with carriage returns becomes one finished line with its final state,
        /// while every intermediate state is reported as the unfinished line.
        /// </summary>
        [Fact]
        public void Progress_bar_redrawn_with_carriage_returns_becomes_one_line()
        {
            var output = new RecordedOutput();
            var reader = output.CreateReader();

            reader.Write(Utf8("\r 10%|#         |"));
            reader.Write(Utf8("\r 50%|#####     |"));
            reader.Write(Utf8("\r100%|##########|\n"));
            reader.Write(Utf8("done\n"));

            output.Completed.Should().Equal("100%|##########|", "done");
            output.Updates.Should().Equal(" 10%|#         |", " 50%|#####     |");
        }

        /// <summary>
        /// Verifies that text after a carriage return overwrites only the characters it covers, and that a backspace
        /// lets the next character overwrite the previous one, as in a terminal.
        /// </summary>
        [Fact]
        public void Carriage_return_and_backspace_overwrite_characters_like_a_terminal()
        {
            var output = new RecordedOutput();
            var reader = output.CreateReader();

            reader.Write(Utf8("abcdef\rXY\n"));
            reader.Write(Utf8("Working |\b/\b-\n"));

            output.Completed.Should().Equal("XYcdef", "Working -");
        }


        // Decoding

        /// <summary>
        /// Verifies that a UTF-8 character split between two reads is decoded once it is complete, and that the
        /// unfinished line never shows the incomplete character.
        /// </summary>
        [Fact]
        public void Utf8_character_split_between_reads_is_decoded_correctly()
        {
            var output = new RecordedOutput();
            var reader = output.CreateReader();
            var bytes = Utf8("Привет\n");

            reader.Write(bytes.AsSpan(0, 3));
            reader.Write(bytes.AsSpan(3));

            output.Updates.Should().Equal("П");
            output.Completed.Should().Equal("Привет");
        }

        /// <summary>
        /// Verifies that a line that is not valid UTF-8 is decoded with the legacy console code page, while UTF-8
        /// lines of the same stream stay UTF-8.
        /// </summary>
        [Fact]
        public void Line_that_is_not_utf8_is_decoded_with_the_legacy_console_code_page()
        {
            var output = new RecordedOutput();
            var oem = CodePagesEncodingProvider.Instance.GetEncoding(866)!;
            var reader = output.CreateReader(oem);

            reader.Write(oem.GetBytes("Отказано в доступе.\r\n"));
            reader.Write(Utf8("Готово\n"));

            output.Completed.Should().Equal("Отказано в доступе.", "Готово");
        }

        /// <summary>
        /// Verifies that a UTF-8 byte order mark at the start of the stream is not part of the first line.
        /// </summary>
        [Fact]
        public void Byte_order_mark_at_the_start_of_the_stream_is_dropped()
        {
            var output = new RecordedOutput();
            var reader = output.CreateReader();

            reader.Write([0xEF, 0xBB, 0xBF, .. Utf8("hello\n")]);

            output.Completed.Should().Equal("hello");
        }

        /// <summary>
        /// Verifies that a very long line of two-byte characters is decoded without broken characters, although its
        /// bytes are decoded in several parts whose boundaries fall inside characters, and is cut to the maximum line
        /// length.
        /// </summary>
        [Fact]
        public void Long_line_is_decoded_in_parts_and_cut_to_the_maximum_length()
        {
            var output = new RecordedOutput();
            var reader = output.CreateReader();

            // The leading ASCII character shifts every two-byte character to an odd offset.
            reader.Write(Utf8("x" + new string('ж', 20000) + "\n"));

            output.Completed.Should().ContainSingle()
                .Which.Should().Be("x" + new string('ж', ProcessOutputReader.MaxLineLength - 1));
        }


        // Test helpers

        /// <summary>
        /// Returns the UTF-8 bytes of a text, without a byte order mark.
        /// </summary>
        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);


        // Test doubles

        /// <summary>
        /// Records the finished lines and the unfinished-line updates a reader reports.
        /// </summary>
        private sealed class RecordedOutput
        {
            /// <summary>
            /// Gets the finished lines in the order they were reported.
            /// </summary>
            public List<string> Completed { get; } = [];

            /// <summary>
            /// Gets the reported texts of the unfinished line in the order they were reported.
            /// </summary>
            public List<string> Updates { get; } = [];

            /// <summary>
            /// Creates a standard output reader that reports into this recorder.
            /// </summary>
            /// <param name="fallbackEncoding">Encoding for text that is not valid UTF-8, or null for none.</param>
            public ProcessOutputReader CreateReader(Encoding? fallbackEncoding = null) =>
                new(ProcessOutputStream.StandardOutput, fallbackEncoding,
                    (_, text) => Completed.Add(text), (_, text) => Updates.Add(text));
        }
    }
}
