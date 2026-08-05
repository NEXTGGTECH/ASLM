// Copyright NEXTGGTECH. Apache License 2.0.


namespace ASLM.Tests.Services;

public sealed class GitHubAccountStoreTests
{
    [Fact]
    public void BuildTokenCreationUrl_prefills_aslm_and_one_year_expiration()
    {
        var url = GitHubAccountStore.BuildTokenCreationUrl();

        url.Should().StartWith("https://github.com/settings/personal-access-tokens/new?");
        url.Should().Contain("name=ASLM");
        url.Should().Contain("expires_in=365");
    }
}
