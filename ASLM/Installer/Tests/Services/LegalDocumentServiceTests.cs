// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text;
using ASLM.Installer;

namespace ASLM.Installer.Tests.Services
{
    /// <summary>
    /// Verifies parsing of the generated legal document bundle without packaged app assets.
    /// </summary>
    public sealed class LegalDocumentServiceTests
    {
        // Test data helpers

        /// <summary>
        /// Builds an in-memory legal bundle in the camelCase shape produced by the build task.
        /// </summary>
        /// <param name="jsons">JSON objects of the individual documents; they are wrapped into one JSON array.</param>
        private static Task<Stream> Bundle(params string[] jsons)
        {
            var json = "[" + string.Join(",", jsons) + "]";
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        }


        // Loading the bundle

        /// <summary>
        /// Verifies that the loader maps the camelCase JSON fields of every document, keeping their order.
        /// </summary>
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

        /// <summary>
        /// Verifies that a bundle without documents is rejected with the "missing or empty" error.
        /// </summary>
        [Fact]
        public async Task LoadAsync_rejects_an_empty_bundle()
        {
            var service = new LegalDocumentService(_ => Bundle());

            var load = () => service.LoadAsync();

            (await load.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("missing or empty");
        }

        /// <summary>
        /// Verifies that a missing bundle asset surfaces as a file-not-found error instead of being swallowed.
        /// </summary>
        [Fact]
        public async Task LoadAsync_propagates_a_missing_asset()
        {
            // Asset source that always reports the file as missing.
            Task<Stream> Missing(string fileName) => Task.FromException<Stream>(new FileNotFoundException(fileName));
            var service = new LegalDocumentService(Missing);

            var load = () => service.LoadAsync();

            await load.Should().ThrowAsync<FileNotFoundException>();
        }
    }
}
