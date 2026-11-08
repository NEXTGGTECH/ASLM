// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Internal;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies platform key resolution and the Windows argument-quoting rules used by
    /// detached process starts.
    /// </summary>
    public sealed class PlatformInfoTests
    {
        // Platform keys

        /// <summary>
        /// Verifies that the platform key joins the OS key and the architecture key with a hyphen, that the
        /// architecture is one of the known keys, and that the runtime identifier contains a hyphen.
        /// </summary>
        [Fact]
        public void Platform_key_combines_the_current_os_and_architecture()
        {
            PlatformInfo.PlatformKey.Should().Be($"{PlatformInfo.OsKey}-{PlatformInfo.ArchKey}");
            PlatformInfo.ArchKey.Should().BeOneOf("amd64", "arm64", "x86");
            PlatformInfo.RuntimeIdentifier.Should().Contain("-");
        }

        /// <summary>
        /// Verifies that an amd64 platform (in any letter case) is matched by its own key and an x64 alias, while
        /// other architectures yield only their own key.
        /// </summary>
        [Fact]
        public void Amd64_keys_have_an_x64_alias_and_other_architectures_do_not()
        {
            PlatformInfo.PlatformKeyCandidates("windows", "amd64")
                .Should().Equal("windows-amd64", "windows-x64");
            PlatformInfo.PlatformKeyCandidates("windows", "AMD64")
                .Should().Equal("windows-amd64", "windows-x64");

            PlatformInfo.PlatformKeyCandidates("macos", "arm64")
                .Should().ContainSingle().Which.Should().Be("macos-arm64");
        }

        /// <summary>
        /// Verifies that a value map is resolved through the key candidates of the current platform (the primary key
        /// and, when present, the alias), that blank values are ignored, and that a missing map yields no value.
        /// </summary>
        [Fact]
        public void ResolveFromMap_uses_the_candidates_of_the_current_platform_and_skips_blank_values()
        {
            var candidates = PlatformInfo.PlatformKeyCandidates();
            candidates.Should().NotBeEmpty();
            var primary = new Dictionary<string, string> { [candidates[0]] = "primary-value" };
            PlatformInfo.ResolveFromMap(primary).Should().Be("primary-value");

            if (candidates.Count > 1)
            {
                var aliasOnly = new Dictionary<string, string> { [candidates[1]] = "alias-value" };
                PlatformInfo.ResolveFromMap(aliasOnly).Should().Be("alias-value");
            }

            PlatformInfo.ResolveFromMap(new Dictionary<string, string> { [candidates[0]] = " " })
                .Should().BeNull("blank entries are ignored");
            PlatformInfo.ResolveFromMap(null).Should().BeNull();
        }


        // Argument quoting

        /// <summary>
        /// Verifies that a process argument is quoted as the Windows command line expects: only text with whitespace or
        /// quotes is quoted, inner quotes are escaped, and backslashes are doubled where they precede a quote or the
        /// closing quote.
        /// </summary>
        /// <param name="input">Raw argument text.</param>
        /// <param name="expected">Expected quoted form.</param>
        /// <param name="because">Description of the rule that the case demonstrates.</param>
        [Theory]
        [InlineData("plain", "plain", "no quoting needed")]
        [InlineData("", "\"\"", "empty becomes an empty quoted argument")]
        [InlineData("with space", "\"with space\"", "whitespace forces quoting")]
        [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"", "inner quotes are escaped")]
        [InlineData("trail\\", "trail\\", "a lone trailing backslash without whitespace stays unquoted")]
        [InlineData("dir one\\", "\"dir one\\\\\"", "trailing backslashes are doubled inside quotes")]
        [InlineData("a\\\"b", "\"a\\\\\\\"b\"", "backslashes before a quote double up first")]
        public void QuoteArgument_follows_windows_command_line_rules(string input, string expected, string because)
        {
            DetachedProcessStarter.QuoteArgument(input).Should().Be(expected, because);
        }
    }
}
