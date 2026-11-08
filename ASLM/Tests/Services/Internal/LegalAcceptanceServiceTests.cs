// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Internal;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies the legal acceptance gate: bundled documents, acceptance persistence keyed by
    /// content hash, and the startup resolution flow.
    /// </summary>
    public sealed class LegalAcceptanceServiceTests : IDisposable
    {
        // Fixture state and factories

        /// <summary>
        /// Isolated ASLM root whose data folder receives the persisted legal acceptance file.
        /// </summary>
        private readonly UniqueAslmRoot _root = new("legal");

        /// <summary>
        /// Removes the isolated root together with the acceptance file.
        /// </summary>
        public void Dispose() => _root.Dispose();

        /// <summary>
        /// Creates a legal acceptance service; services created in one test share the persisted acceptance file.
        /// </summary>
        private LegalAcceptanceService CreateService() => new(TestLoggerFactory.Create<LegalAcceptanceService>());

        /// <summary>
        /// Creates an application data store whose legal preferences the test can adjust.
        /// </summary>
        private AppDataStore CreateAppData() => new(TestLoggerFactory.Create<AppDataStore>());


        // Documents

        /// <summary>
        /// Verifies that the bundled legal documents load from the embedded bundle with a title, a Markdown text and a
        /// SHA-256 hash each.
        /// </summary>
        [Fact]
        public async Task Bundled_documents_load_from_the_embedded_bundle()
        {
            var service = CreateService();

            var documents = await service.LoadDocumentsAsync();

            documents.Should().NotBeEmpty();
            documents.Should().OnlyContain(d => !string.IsNullOrWhiteSpace(d.Title)
                && !string.IsNullOrWhiteSpace(d.Markdown) && !string.IsNullOrWhiteSpace(d.Sha256));
        }


        // Startup acceptance

        /// <summary>
        /// Verifies that the startup check asks for manual acceptance while no document has been accepted.
        /// </summary>
        [Fact]
        public async Task Startup_requires_manual_acceptance_until_documents_are_accepted()
        {
            var appData = CreateAppData();
            var service = CreateService();
            await service.InitializeAsync();

            var resolution = await service.ResolveStartupAcceptanceAsync(appData);

            resolution.Should().Be(LegalAcceptanceResolution.ManualRequired);
            service.ManualAcceptanceRequired.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that after the current documents are accepted, a newly created service finds the acceptance
        /// up to date, because it was written to the acceptance file.
        /// </summary>
        [Fact]
        public async Task Accepting_the_current_documents_makes_the_next_startup_up_to_date()
        {
            var appData = CreateAppData();
            var service = CreateService();
            await service.InitializeAsync();
            var documents = await service.LoadDocumentsAsync();

            await service.SaveDocumentsAsAcceptedAsync(documents);
            var next = CreateService();
            await next.InitializeAsync();

            (await next.ResolveStartupAcceptanceAsync(appData)).Should().Be(LegalAcceptanceResolution.UpToDate);
            next.ManualAcceptanceRequired.Should().BeFalse();

            // The acceptance is keyed per document content and survives a reload.
            File.Exists(Path.Combine(_root.DataAppDir, "ASLM_LegalAcceptance.json")).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that when the content hash of an accepted document no longer matches, the startup check accepts
        /// the new content automatically (and refreshes the stored hashes) if automatic acceptance is enabled, and
        /// otherwise requires manual acceptance.
        /// </summary>
        /// <param name="autoAccept">Value of the automatic acceptance preference for changed documents.</param>
        /// <param name="expected">Resolution that the startup check is expected to return.</param>
        [Theory]
        [InlineData(true, LegalAcceptanceResolution.AutoAccepted)]
        [InlineData(false, LegalAcceptanceResolution.ManualRequired)]
        public async Task Changed_documents_follow_the_auto_accept_preference(bool autoAccept, LegalAcceptanceResolution expected)
        {
            // Arrange: accept the current documents under the chosen preference.
            var appData = CreateAppData();
            appData.Data.Legal.AutoAcceptUpdates = autoAccept;
            var service = CreateService();
            await service.InitializeAsync();
            await service.SaveDocumentsAsAcceptedAsync(await service.LoadDocumentsAsync());

            // Rewrite one stored hash so the acceptance no longer matches the shipped content.
            var acceptancePath = Path.Combine(_root.DataAppDir, "ASLM_LegalAcceptance.json");
            var json = await File.ReadAllTextAsync(acceptancePath);
            await File.WriteAllTextAsync(acceptancePath,
                json.Replace("\"sha256\": \"", "\"sha256\": \"deadbeef", StringComparison.OrdinalIgnoreCase));

            // Act and assert: a service created afterwards sees changed content and resolves per the preference.
            var tampered = CreateService();
            await tampered.InitializeAsync();

            (await tampered.ResolveStartupAcceptanceAsync(appData)).Should().Be(expected);
            if (autoAccept)
            {
                // Auto-acceptance refreshes the stored hashes so the next startup is up to date.
                var refreshed = CreateService();
                await refreshed.InitializeAsync();
                (await refreshed.ResolveStartupAcceptanceAsync(appData))
                    .Should().Be(LegalAcceptanceResolution.UpToDate);
            }
        }
    }
}
