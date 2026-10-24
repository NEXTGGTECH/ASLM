// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text;
using ASLM.Installer;

namespace ASLM.Installer.Tests.Services;

/// <summary>
/// Verifies parsing of the generated legal document bundle without packaged app assets.
/// </summary>
public sealed class LegalDocumentServiceTests
{
    /// <summary>
    /// Builds an in-memory legal bundle in the camelCase shape produced by the build task.
    /// </summary>
    private static Task<Stream> Bundle(params string[] jsons)
    {
        var json = "[" + string.Join(",", jsons) + "]";
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task LoadAsync_parses_ids_titles_and_hashes()
    {
        var service = new LegalDocumentService(fileName => Bundle(
            """{"id":"eula","title":"EULA","fileName":"eula.md","markdown":"# EULA","sha256":"aa"}""",
            """{"id":"privacy","title":"Privacy","fileName":"privacy.md","markdown":"# Privacy","sha256":"bb"}"""));

        var documents = await service.LoadAsync();

        documents.Select(document => document.Id).Should().Equal("eula", "privacy");
        documents[0].Title.Should().Be("EULA");
        documents[1].Sha256.Should().Be("bb");
        documents[0].Markdown.Should().Contain("EULA");
    }

    [Fact]
    public async Task LoadAsync_rejects_an_empty_bundle()
    {
        var service = new LegalDocumentService(_ => Bundle());

        var load = () => service.LoadAsync();

        (await load.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("missing or empty");
    }

    [Fact]
    public async Task LoadAsync_propagates_a_missing_asset()
    {
        Task<Stream> Missing(string fileName) => Task.FromException<Stream>(new FileNotFoundException(fileName));
        var service = new LegalDocumentService(Missing);

        var load = () => service.LoadAsync();

        await load.Should().ThrowAsync<FileNotFoundException>();
    }
}
