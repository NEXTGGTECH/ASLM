// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text;
using ASLM.Services.Sunrise;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Sunrise;

/// <summary>
/// Verifies the strict parsing and validation rules of the SUNRISE authentication callback.
/// </summary>
public sealed class SunriseServiceParsingTests : IDisposable
{
    private readonly UniqueAslmRoot _root = new("sunrise");

    public void Dispose() => _root.Dispose();

    private const string Host = "aslm.nextggtech.localhost";
    private static readonly string State = new('s', 32);

    private SunriseService CreateService() => new(
        TestLoggerFactory.Create<SunriseService>(),
        new AppDataStore(TestLoggerFactory.Create<AppDataStore>()));

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

    [Fact]
    public void ParseCallbackRequest_rejects_a_foreign_host_header()
    {
        var act = () => SunriseService.ParseCallbackRequest("POST", "/sunrise-auth/",
            new Dictionary<string, string> { ["Host"] = "evil.example", ["Content-Type"] = "application/x-www-form-urlencoded" },
            Encoding.UTF8.GetBytes($"state={State}&access=a&refresh=r"), Host);

        act.Should().Throw<InvalidDataException>().WithMessage("*Host*");
    }

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
