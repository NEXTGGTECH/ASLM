// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal;

/// <summary>
/// Verifies the persisted GitHub account lifecycle: connect, verify, retain, and clear.
/// </summary>
public sealed class GitHubAccountStoreTests
{
    private const string UserEndpoint = "https://api.github.com/user";

    [Fact]
    public void BuildTokenCreationUrl_prefills_aslm_and_one_year_expiration()
    {
        var url = GitHubAccountStore.BuildTokenCreationUrl();

        url.Should().StartWith("https://github.com/settings/personal-access-tokens/new?");
        url.Should().Contain("name=ASLM");
        url.Should().Contain("expires_in=365");
    }

    [Fact]
    public async Task ConnectAsync_verifies_token_persists_it_and_exposes_the_profile()
    {
        using var graph = new TestServiceGraph();
        graph.GitHubHttp.OnJson(UserEndpoint, """{"login":"octocat","name":"The Octocat"}""");

        var result = await graph.GitHubAccounts.ConnectAsync("ghp_test-token");

        result.Success.Should().BeTrue();
        result.State.IsConnected.Should().BeTrue();
        result.State.UserName.Should().Be("octocat");
        result.Message.Should().Be("octocat");
        graph.GitHubAccounts.GetPersonalAccessToken().Should().Be("ghp_test-token");
        graph.AppData.Data.GitHub.UserName.Should().Be("octocat");
        graph.GitHubHttp.Requests.Should().ContainSingle()
            .Which.Headers.Authorization!.Parameter.Should().Be("ghp_test-token");
    }

    [Fact]
    public async Task ConnectAsync_with_rejected_token_reports_failure_and_persists_nothing()
    {
        using var graph = new TestServiceGraph();
        graph.GitHubHttp.On(UserEndpoint, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Bad credentials")
        });

        var result = await graph.GitHubAccounts.ConnectAsync("ghp_invalid");

        result.Success.Should().BeFalse();
        result.State.IsConnected.Should().BeFalse();
        result.Message.Should().Contain("Bad credentials");
        graph.GitHubAccounts.GetPersonalAccessToken().Should().BeNull();
    }

    [Fact]
    public async Task InitializeAsync_with_valid_persisted_token_restores_the_account()
    {
        using var graph = new TestServiceGraph();
        graph.AppData.Data.GitHub.PersonalAccessToken = "ghp_persisted";
        graph.AppData.Data.GitHub.UserName = "octocat";
        graph.GitHubHttp.OnJson(UserEndpoint, """{"login":"octocat"}""");

        await graph.GitHubAccounts.InitializeAsync();

        graph.GitHubAccounts.GetState().IsConnected.Should().BeTrue();
        graph.GitHubAccounts.GetPersonalAccessToken().Should().Be("ghp_persisted");
    }

    [Fact]
    public async Task InitializeAsync_with_unauthorized_persisted_token_clears_stored_credentials()
    {
        using var graph = new TestServiceGraph();
        graph.AppData.Data.GitHub.PersonalAccessToken = "ghp_revoked";
        graph.AppData.Data.GitHub.UserName = "octocat";
        graph.AppData.Data.Updates.CheckEnabled = true;
        await graph.AppData.SaveAsync();
        graph.GitHubHttp.On(UserEndpoint, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Bad credentials")
        });

        await graph.GitHubAccounts.InitializeAsync();

        graph.GitHubAccounts.GetPersonalAccessToken().Should().BeNull();
        graph.AppData.Data.GitHub.UserName.Should().BeNull();
        graph.GitHubAccounts.GetState().IsConnected.Should().BeFalse();
        // The cleared credentials must survive on disk, not only in memory.
        File.ReadAllText(graph.Root.AppDataFilePath).Should().NotContain("ghp_revoked");
    }

    [Fact]
    public async Task InitializeAsync_with_network_outage_keeps_stored_credentials_connected()
    {
        using var graph = new TestServiceGraph();
        graph.AppData.Data.GitHub.PersonalAccessToken = "ghp_kept";
        graph.AppData.Data.GitHub.UserName = "octocat";
        graph.GitHubHttp.OnNetworkError(UserEndpoint, () => new HttpRequestException("socket offline"));

        await graph.GitHubAccounts.InitializeAsync();

        graph.GitHubAccounts.GetPersonalAccessToken().Should().Be("ghp_kept");
        var state = graph.GitHubAccounts.GetState();
        state.IsConnected.Should().BeTrue("an outage must not look like a logout");
        state.UserName.Should().Be("octocat");
        state.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DisconnectAsync_clears_token_and_state()
    {
        using var graph = new TestServiceGraph();
        graph.GitHubHttp.OnJson(UserEndpoint, """{"login":"octocat"}""");
        await graph.GitHubAccounts.ConnectAsync("ghp_temp");

        var result = await graph.GitHubAccounts.DisconnectAsync();

        result.Success.Should().BeTrue();
        graph.GitHubAccounts.GetPersonalAccessToken().Should().BeNull();
        graph.GitHubAccounts.GetState().IsConnected.Should().BeFalse();
    }
}
