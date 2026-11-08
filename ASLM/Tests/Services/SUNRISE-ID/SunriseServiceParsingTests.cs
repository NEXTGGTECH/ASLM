// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text;
using ASLM.Services.Sunrise;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Sunrise
{
    /// <summary>
    /// Verifies the strict parsing and validation rules of the SUNRISE authentication callback.
    /// </summary>
    public sealed class SunriseServiceParsingTests : IDisposable
    {
        // Fields and constants

        /// <summary>
        /// Isolated ASLM root, so the services created by the tests never touch real application data.
        /// </summary>
        private readonly UniqueAslmRoot _root = new("sunrise");

        /// <summary>
        /// Deletes the isolated root and restores the previous root setting.
        /// </summary>
        public void Dispose() => _root.Dispose();

        /// <summary>
        /// Branded loopback host name that the callback requests are expected to carry.
        /// </summary>
        private const string Host = "aslm.nextggtech.localhost";

        /// <summary>
        /// A valid callback state value: 32 URL-safe characters.
        /// </summary>
        private static readonly string State = new('s', 32);

        /// <summary>
        /// Creates a service over a fresh application data store in the isolated root.
        /// </summary>
        private SunriseService CreateService() => new(
            TestLoggerFactory.Create<SunriseService>(),
            new AppDataStore(TestLoggerFactory.Create<AppDataStore>()));


        // Callback request parsing

        /// <summary>
        /// Verifies that a POST to the callback path with a form content type and the expected host yields the state
        /// and the access and refresh tokens from the form body.
        /// </summary>
        [Fact]
        public void ParseCallbackRequest_accepts_a_complete_form_post_to_the_branded_host()
        {
            var payload = SunriseService.ParseCallbackRequest(
                "POST",
                "/sunrise-auth/",
                new Dictionary<string, string> { ["Host"] = Host, ["Content-Type"] = "application/x-www-form-urlencoded" },
                Encoding.UTF8.GetBytes($"state={State}&access=token-a&refresh=token-r"),
                Host);

            payload.State.Should().Be(State);
            payload.Access.Should().Be("token-a");
            payload.Refresh.Should().Be("token-r");
        }

        /// <summary>
        /// Verifies that a GET request, or a POST to a path other than the callback path, is rejected with
        /// <see cref="InvalidDataException"/>.
        /// </summary>
        [Theory]
        [InlineData("GET", "/sunrise-auth/")]
        [InlineData("POST", "/other/")]
        public void ParseCallbackRequest_rejects_wrong_method_or_path(string method, string target)
        {
            var act = () => SunriseService.ParseCallbackRequest(method, target,
                new Dictionary<string, string> { ["Host"] = Host, ["Content-Type"] = "application/x-www-form-urlencoded" },
                Encoding.UTF8.GetBytes($"state={State}&access=a&refresh=r"), Host);

            act.Should().Throw<InvalidDataException>();
        }

        /// <summary>
        /// Verifies that a request whose Host header differs from the expected branded host is rejected with an
        /// error that mentions the host.
        /// </summary>
        [Fact]
        public void ParseCallbackRequest_rejects_a_foreign_host_header()
        {
            var act = () => SunriseService.ParseCallbackRequest("POST", "/sunrise-auth/",
                new Dictionary<string, string> { ["Host"] = "evil.example", ["Content-Type"] = "application/x-www-form-urlencoded" },
                Encoding.UTF8.GetBytes($"state={State}&access=a&refresh=r"), Host);

            act.Should().Throw<InvalidDataException>().WithMessage("*Host*");
        }

        /// <summary>
        /// Verifies that a form without a refresh token, or with a state value that is too short, is rejected as an
        /// incomplete token payload.
        /// </summary>
        [Fact]
        public void ParseCallbackRequest_rejects_incomplete_or_weak_token_payloads()
        {
            var headers = new Dictionary<string, string>
            {
                ["Host"] = Host, ["Content-Type"] = "application/x-www-form-urlencoded"
            };

            var missingRefresh = () => SunriseService.ParseCallbackRequest("POST", "/sunrise-auth/", headers,
                Encoding.UTF8.GetBytes($"state={State}&access=a"), Host);
            missingRefresh.Should().Throw<InvalidDataException>().WithMessage("*complete token payload*");

            var weakState = () => SunriseService.ParseCallbackRequest("POST", "/sunrise-auth/", headers,
                Encoding.UTF8.GetBytes("state=short&access=a&refresh=r"), Host);
            weakState.Should().Throw<InvalidDataException>().WithMessage("*complete token payload*");
        }


        // Form and address validation

        /// <summary>
        /// Verifies that form parsing decodes plus signs and percent escapes, rejects a repeated field name as
        /// duplicated, and rejects a pair without an equals sign as malformed.
        /// </summary>
        [Fact]
        public void ParseFormBody_decodes_url_encoding_and_rejects_duplicates()
        {
            var form = SunriseService.ParseFormBody("name=octo+cat&note=hello%20world");

            form["name"].Should().Be("octo cat");
            form["note"].Should().Be("hello world");

            var duplicate = () => SunriseService.ParseFormBody("a=1&a=2");
            duplicate.Should().Throw<InvalidDataException>().WithMessage("*duplicated*");

            var malformed = () => SunriseService.ParseFormBody("novalue");
            malformed.Should().Throw<InvalidDataException>().WithMessage("*malformed*");
        }

        /// <summary>
        /// Verifies that a callback state is accepted only when it is a long token of URL-safe characters, and that
        /// missing, empty, short, or space and symbol containing values are refused.
        /// </summary>
        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("short-state-1234", false)]
        [InlineData("has spaces and symbols!!", false)]
        [InlineData("valid-state_0123456789-ABC", true)]
        public void IsValidCallbackState_requires_long_alphanumeric_tokens(string? state, bool expected)
        {
            SunriseService.IsValidCallbackState(state).Should().Be(expected);
        }

        /// <summary>
        /// Verifies that only <c>http</c> on the branded host with an explicit port and the exact callback path is
        /// valid as redirect address, and that a missing port, <c>https</c>, a foreign host, a query string, or user
        /// information is refused.
        /// </summary>
        [Theory]
        [InlineData("http://aslm.nextggtech.localhost:8443/sunrise-auth/", true)]
        [InlineData("http://aslm.nextggtech.localhost/sunrise-auth/", false)]
        [InlineData("https://aslm.nextggtech.localhost:8443/sunrise-auth/", false)]
        [InlineData("http://evil.example:8443/sunrise-auth/", false)]
        [InlineData("http://aslm.nextggtech.localhost:8443/sunrise-auth/?x=1", false)]
        [InlineData("http://user@aslm.nextggtech.localhost:8443/sunrise-auth/", false)]
        public void IsValidLoopbackRedirectUri_accepts_only_the_exact_callback_shape(string uri, bool expected)
        {
            SunriseService.IsValidLoopbackRedirectUri(new Uri(uri)).Should().Be(expected);
        }


        // Account data checks

        /// <summary>
        /// Verifies that user data is classified by its first failed check: unreadable or missing data, inactive
        /// account, missing ASLM profile, and banned profile each yield their own error code, and a healthy
        /// account yields success with no error.
        /// </summary>
        [Fact]
        public void CheckUserData_classifies_account_and_profile_states()
        {
            var service = CreateService();

            service.CheckUserData("not json", out var malformed).Should().BeFalse();
            malformed.Should().Be(SunriseService.ErrorUserData);

            service.CheckUserData("""{"user_data": null}""", out var missing).Should().BeFalse();
            missing.Should().Be(SunriseService.ErrorUserData);

            service.CheckUserData("""{"user_data": {"is_active": false}}""", out var inactive).Should().BeFalse();
            inactive.Should().Be(SunriseService.ErrorUserIsActive);

            service.CheckUserData("""{"user_data": {"is_active": true}}""", out var noProfile).Should().BeFalse();
            noProfile.Should().Be(SunriseService.ErrorProfileAslm);

            service.CheckUserData(
                """{"user_data": {"is_active": true, "aslm": {"is_active": true, "is_banned": true}}}""",
                out var banned).Should().BeFalse();
            banned.Should().Be(SunriseService.ErrorAslmIsBanned);

            service.CheckUserData(
                """{"user_data": {"is_active": true, "aslm": {"is_active": true, "is_banned": false}}}""",
                out var healthy).Should().BeTrue();
            healthy.Should().BeEmpty();
        }
    }
}
