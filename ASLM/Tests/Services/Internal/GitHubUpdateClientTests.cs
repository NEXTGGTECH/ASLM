// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ASLM.Models;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies how module update sources (release, pre-release and branch channels) are resolved from GitHub
    /// repository metadata through <see cref="UpdateManager"/> and the reinstall dialog, how catalog versions and
    /// artwork are loaded, how the module icon cache stores and prunes icons, and how <see cref="GitHubUpdateClient"/>
    /// shares requests and handles blank repositories.
    /// </summary>
    public sealed class GitHubUpdateClientTests : IDisposable
    {
        // Fixture state

        /// <summary>
        /// Temporary directory that serves as the icon cache directory of the test and is deleted afterwards.
        /// </summary>
        private readonly DirectoryInfo _iconDirectory = Directory.CreateTempSubdirectory("ASLM-IconCache-");

        /// <summary>
        /// Deletes the temporary icon cache directory together with its content.
        /// </summary>
        public void Dispose() => _iconDirectory.Delete(recursive: true);


        // Module source resolution

        /// <summary>
        /// Verifies that returning to a previously installed source compares the current installation files, not the
        /// old source history: after every switch between release and branch the other source counts as new, the
        /// merged manifest keeps only the fingerprint of the applied source, and that source is then up to date.
        /// </summary>
        [Theory]
        [InlineData("release", false)]
        [InlineData("branch", false)]
        [InlineData("release", true)]
        [InlineData("branch", true)]
        public async Task Source_switches_compare_only_the_current_installation(string initialMode, bool legacyFingerprints)
        {
            // Arrange: both sources deliberately keep the same release tag and commit across the entire round trip.
            using var http = new HttpClient(new RepositoryHandler(branchJson:
                """[{"name":"main","commit":{"sha":"abcdef123456"}}]"""));
            var manager = CreateSourceManager(http);
            var module = CreateModule("module", "owner/repo");
            module.SourcePath = Path.Combine(_iconDirectory.FullName, "ASLM_Module.json");
            module.Update.Mode = initialMode;
            module.Update.Branch = "main";
            module.Update.InstalledReleaseTag = initialMode == "release" || legacyFingerprints ? "v1.0" : null;
            module.Update.InstalledCommitSha = initialMode == "branch" || legacyFingerprints ? "abcdef123456" : null;
            var otherMode = initialMode == "branch" ? "release" : "branch";

            foreach (var mode in new[] { otherMode, initialMode, otherMode })
            {
                // Switching the source must offer a candidate that is not yet the current installation.
                module.Update.Mode = mode;
                var candidate = await manager.CheckModuleUpdateAsync(module, publishUpdateNotification: false);
                candidate.Should().NotBeNull("a previously installed target is not the current installation");
                candidate!.Mode.Should().Be(mode);
                UpdateManager.IsModuleAlreadyAtInstallTarget(module, candidate).Should().BeFalse();
                (await manager.ResolveModuleInstallCandidateAsync(module)).Should().NotBeNull();

                // The pending update stored for the candidate can be restored.
                module.Update.PendingUpdate = ModulePendingUpdate.FromCandidate(module, candidate);
                manager.TryRestorePendingUpdateCandidate(module, out _).Should().BeTrue();

                // Exercise the actual manifest merge used after a successful archive replacement.
                var installed = CreateModule("module", "owner/repo");
                installed.SourcePath = module.SourcePath;
                installed.Update.InstalledReleaseTag = "untrusted-package-tag";
                installed.Update.InstalledCommitSha = "untrusted-package-commit";
                UpdateManager.MergeModuleState(module, installed, candidate);
                installed.Update.InstalledReleaseTag.Should().Be(mode == "branch" ? null : "v1.0");
                installed.Update.InstalledCommitSha.Should().Be(mode == "branch" ? "abcdef123456" : null);

                // After the merge the applied source is up to date and needs neither an update nor a pending restore.
                UpdateManager.IsModuleAlreadyAtInstallTarget(installed, candidate).Should().BeTrue();
                (await manager.CheckModuleUpdateAsync(installed, publishUpdateNotification: false)).Should().BeNull();
                installed.Update.PendingUpdate = ModulePendingUpdate.FromCandidate(installed, candidate);
                manager.TryRestorePendingUpdateCandidate(installed, out _).Should().BeFalse();
                module = installed;
            }
        }

        /// <summary>
        /// Verifies that stable and prerelease selectors show releases in one tag and date format
        /// without an extra channel suffix, both in the download options and in the release picker.
        /// </summary>
        [Theory]
        [InlineData(false, "release")]
        [InlineData(true, "pre-release")]
        public async Task Release_labels_have_the_same_format_in_every_channel(bool prerelease, string channel)
        {
            var json = JsonSerializer.Serialize(new[] { new
            {
                tag_name = "v1.0.0.0", prerelease, published_at = "2026-10-11T12:00:00Z",
                zipball_url = "https://example.test/module.zip"
            } });
            using var http = new HttpClient(new RepositoryHandler(releaseJson: json));
            var module = CreateModule("module", "owner/repo");
            module.Update.Mode = channel;

            var options = await CreateSourceManager(http).GetModuleDownloadOptionsAsync(module);

            options.Releases.Should().ContainSingle().Which.DisplayName.Should().Be("v1.0.0.0 - 2026-10-11");
            ASLM.Pages.ModuleInfo.BuildReleaseOptions(options.Releases, "latest")[0].DisplayName.Should()
                .Be("latest (v1.0.0.0 - 2026-10-11)");
        }

        /// <summary>
        /// Verifies that catalog display, setup and update resolution agree on the same fallback channel
        /// (release, pre-release, then branch) when the preferred stream has no releases, that drafts never make a
        /// channel selectable, and that the saved preference is not modified by metadata queries.
        /// </summary>
        [Theory]
        [InlineData(true, true, "release", "release", "v1.0")]
        [InlineData(true, true, "pre-release", "pre-release", "v2.0-beta")]
        [InlineData(false, true, "release", "pre-release", "v2.0-beta")]
        [InlineData(false, true, "pre-release", "pre-release", "v2.0-beta")]
        [InlineData(true, false, "pre-release", "release", "v1.0")]
        [InlineData(false, false, "release", "branch", "develop@abcdef1")]
        [InlineData(false, false, "pre-release", "branch", "develop@abcdef1")]
        public async Task Missing_channels_fall_back_consistently(bool stable, bool preview, string preferred,
            string expectedMode, string expectedVersion)
        {
            // Arrange: a repository with the requested release streams plus a draft that must be ignored.
            var releases = new List<object>();
            if (stable) releases.Add(new { tag_name = "v1.0", prerelease = false, zipball_url = "https://example.test/stable.zip" });
            if (preview) releases.Add(new { tag_name = "v2.0-beta", prerelease = true, zipball_url = "https://example.test/preview.zip" });

            // Drafts must not make a channel selectable.
            releases.Add(new { tag_name = "v3.0", draft = true, prerelease = false, zipball_url = "https://example.test/draft.zip" });
            var handler = new RepositoryHandler(releaseJson: JsonSerializer.Serialize(releases),
                branchJson: """[{"name":"develop","commit":{"sha":"abcdef123456"}}]""");
            using var http = new HttpClient(handler);
            var manager = CreateSourceManager(http);
            var module = CreateModule("module", "owner/repo");
            module.SourcePath = Path.Combine(_iconDirectory.FullName, "ASLM_Module.json");
            UpdateManager.ApplyCatalogDefaults(module, false, preferred);

            // The download options expose only existing channels, always offer the branch, and use the fallback mode.
            var options = await manager.GetModuleDownloadOptionsAsync(module);
            options.Mode.Should().Be(expectedMode);
            options.Channels.Contains("release").Should().Be(stable);
            options.Channels.Contains("pre-release").Should().Be(preview);
            options.Channels.Should().Contain("branch");
            module.Update.Mode.Should().Be(preferred, "a metadata query alone must not edit preferences");

            // The catalog version shows the fallback target without touching the stored preference.
            var displayed = await manager.GetModuleCatalogVersionAsync(module, false);
            displayed.Should().Be(expectedMode == "branch" ? module.Version : expectedVersion);
            module.Update.Mode.Should().Be(preferred, "presentation must not race the editable configuration");

            // Setup and catalog installation both call this entry point, not a view-specific fallback.
            var candidate = await manager.ResolveModuleInstallCandidateAsync(module);
            candidate.Should().NotBeNull();
            candidate!.Mode.Should().Be(expectedMode);
            candidate.RemoteVersion.Should().Be(expectedVersion);
            module.Update.Mode.Should().Be(expectedMode);

            // Update checking resolves the same target, and all of the above needed only one releases request.
            var update = await manager.CheckModuleUpdateAsync(module, publishUpdateNotification: false);
            update!.RemoteVersion.Should().Be(expectedVersion);
            handler.Requests.Count(uri => uri.Contains("/releases")).Should().Be(1);
        }

        /// <summary>
        /// Verifies that a pinned release tag or branch is never silently replaced with the latest one: a missing tag
        /// or a deleted branch resolves to no candidate and keeps the saved selection, while an existing branch
        /// resolves to itself.
        /// </summary>
        [Fact]
        public async Task Available_channels_preserve_explicit_missing_targets_and_branches()
        {
            using var http = new HttpClient(new RepositoryHandler(branchJson:
                """[{"name":"main","commit":{"sha":"abcdef123456"}},{"name":"preview","commit":{"sha":"111111122222"}}]"""));
            var manager = CreateSourceManager(http);
            var module = CreateModule("module", "owner/repo");
            module.Update.SelectedReleaseTag = "v-missing";
            (await manager.ResolveModuleInstallCandidateAsync(module)).Should().BeNull();
            module.Update.SelectedReleaseTag.Should().Be("v-missing");
            module.Update.Mode = "branch";
            module.Update.Branch = "preview";
            (await manager.ResolveModuleInstallCandidateAsync(module))!.ReferenceName.Should().Be("preview");
            module.Update.Branch = "deleted-branch";
            (await manager.ResolveModuleInstallCandidateAsync(module)).Should().BeNull();
            module.Update.Branch.Should().Be("deleted-branch");
        }

        /// <summary>
        /// Verifies that a failed repository request surfaces as an error and leaves the saved update preferences
        /// unchanged, because a failed request is not proof that a repository has no releases.
        /// </summary>
        [Fact]
        public async Task Failed_catalog_queries_do_not_change_saved_source_preferences()
        {
            using var http = new HttpClient(new RepositoryHandler(beforeResponse: (_, _) =>
                throw new HttpRequestException("offline")));
            var manager = CreateSourceManager(http);
            var module = CreateModule("module", "owner/repo");
            module.Update.SelectedReleaseTag = "v1.0";
            module.Normalize();
            var before = JsonSerializer.Serialize(module.Update);
            await FluentActions.Awaiting(() => manager.ResolveModuleInstallCandidateAsync(module))
                .Should().ThrowAsync<HttpRequestException>();
            JsonSerializer.Serialize(module.Update).Should().Be(before);
        }


        // Reinstall dialog

        /// <summary>
        /// Verifies that the reinstall dialog offers only the channels and targets the repository really has (here only
        /// a branch), follows the shared fallback, and leaves the saved preferences untouched.
        /// </summary>
        [Fact]
        public async Task Reinstall_selectors_only_offer_available_repository_channels()
        {
            using var http = new HttpClient(new RepositoryHandler(releaseJson: "[]", branchJson:
                """[{"name":"develop","commit":{"sha":"abcdef123456"}}]"""));
            var module = CreateModule("module", "owner/repo");
            using var form = new ASLM.Services.Dialogs.ModuleReinstallForm(module, CreateSourceManager(http));
            await form.LoadOptionsAsync();
            form.Channel.Choices.Select(choice => choice.Id).Should().Equal("branch");
            form.Channel.Value.Should().Be("branch");
            form.Target.Choices.Select(choice => choice.Id).Should().Equal("develop");
            form.Target.Value.Should().Be("develop");
            module.Update.Mode.Should().Be("release", "canceling the dialog must leave saved preferences untouched");
        }

        /// <summary>
        /// Verifies that the reinstall dialog displays the saved channel and offers the branch option while the
        /// repository metadata is still pending, keeps the target selection disabled until it is validated, and after
        /// the response removes the unavailable release channels without modifying the saved preferences.
        /// </summary>
        [Theory]
        [InlineData("release")]
        [InlineData("pre-release")]
        [InlineData("branch")]
        public async Task Configuration_shows_selected_channel_and_branch_option_while_metadata_is_pending(string mode)
        {
            // Arrange: a repository without releases whose responses are held back until the test releases them.
            var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new RepositoryHandler(releaseJson: "[]", branchJson:
                """[{"name":"develop","commit":{"sha":"abcdef123456"}}]""",
                beforeResponse: (_, ct) => response.Task.WaitAsync(ct)));
            var module = CreateModule("module", "owner/repo");
            module.Update.Mode = module.Update.Channel = mode;
            module.Update.Branch = "develop";
            using var form = new ASLM.Services.Dialogs.ModuleReinstallForm(module, CreateSourceManager(http));

            // Before loading: the saved channel and the always-available branch option are displayed.
            form.Channel.IsEnabled.Should().BeTrue();
            form.Channel.Value.Should().Be(mode);
            form.Channel.Choices.Should().Contain(choice => choice.Id == mode);
            form.Channel.Choices.Should().Contain(choice => choice.Id == "branch");
            var loading = form.LoadOptionsAsync();
            try
            {
                // While the response is pending: nothing changes and the target cannot be changed yet.
                loading.IsCompleted.Should().BeFalse();
                form.Channel.Value.Should().Be(mode);
                form.Channel.Choices.Should().Contain(choice => choice.Id == mode);
                form.Target.Value.Should().Be(mode == "branch" ? "develop" : "latest");
                form.Target.IsEnabled.Should().BeFalse("destructive confirmation still requires target validation");
            }
            finally
            {
                response.TrySetResult();
                await loading;
            }

            // After loading: only the branch channel remains, because the repository has no releases.
            form.Channel.Choices.Should().ContainSingle(choice => choice.Id == "branch");
            form.Channel.Value.Should().Be("branch");
            form.Target.Value.Should().Be("develop");
            module.Update.Mode.Should().Be(mode, "the dialog only edits its detached draft");
        }


        // Request scheduling

        /// <summary>
        /// Verifies that a slow release response does not postpone the branch request for an explicitly selected
        /// branch, and that each endpoint is requested exactly once.
        /// </summary>
        [Fact]
        public async Task Selected_branch_metadata_is_requested_without_waiting_for_releases()
        {
            // Arrange: the releases response is held back while any other request is answered immediately.
            var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var branchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new RepositoryHandler(branchJson:
                """[{"name":"preview","commit":{"sha":"abcdef123456"}}]""", beforeResponse: (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases")) return releaseResponse.Task.WaitAsync(ct);
                branchRequested.TrySetResult();
                return Task.CompletedTask;
            });
            using var http = new HttpClient(handler);
            var module = CreateModule("module", "owner/repo");
            module.Update.Mode = "branch";
            module.Update.Branch = "preview";
            var loading = CreateSourceManager(http).GetModuleDownloadOptionsAsync(module);
            try
            {
                // Act: the branch request is made while the releases request is still blocked.
                await branchRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                loading.IsCompleted.Should().BeFalse();
            }
            finally { releaseResponse.TrySetResult(); }

            // Assert: after the release response arrives the options are complete.
            var options = await loading;
            options.Mode.Should().Be("branch");
            options.Branches.Should().Equal("preview");
            handler.Requests.Count(uri => uri.Contains("/branches")).Should().Be(1);
            handler.Requests.Count(uri => uri.Contains("/releases")).Should().Be(1);
        }


        // Update manager factory

        /// <summary>
        /// Creates an update manager that has only the services required for metadata and candidate resolution.
        /// </summary>
        /// <param name="http">HTTP client the GitHub client sends all repository requests through.</param>
        private UpdateManager CreateSourceManager(HttpClient http) => new(null!, null!, null!, null!, null!, null!,
            null!, null!, CreateClient(http), null!, NullLogger<UpdateManager>.Instance, new DownloadQueue());


        // Catalog version and artwork

        /// <summary>
        /// Verifies that the catalog version, the release picker and the install target all use the default channel
        /// (the latest release or pre-release), that the catalog presentation leaves the manifest version unchanged,
        /// and that a selected release tag changes the displayed version only for modules that are not installed.
        /// </summary>
        /// <param name="channel">Default channel assigned to the catalog module.</param>
        /// <param name="expected">Version expected from the catalog, picker and install target.</param>
        /// <param name="count">Number of releases expected in the picker for that channel.</param>
        [Theory]
        [InlineData("release", "v1.0", 1)]
        [InlineData("pre-release", "v2.0-beta", 2)]
        public async Task Catalog_display_picker_and_install_target_share_the_default_channel(string channel, string expected, int count)
        {
            using var http = new HttpClient(new RepositoryHandler());
            var client = CreateClient(http);
            var manager = new UpdateManager(null!, null!, null!, null!, null!, null!, null!, null!, client, null!, NullLogger<UpdateManager>.Instance, new DownloadQueue());
            var module = new ModuleConfig { Id = "module", Version = "0.1", Source = new() { Type = "github", Repo = "owner/repo" } };
            UpdateManager.ApplyCatalogDefaults(module, false, channel);
            var version = await manager.GetModuleCatalogVersionAsync(module, false);
            var options = await manager.GetModuleReleaseCandidatesAsync(module);
            var install = await manager.ResolveModuleInstallCandidateAsync(module);
            version.Should().Be(expected);
            options.Should().HaveCount(count);
            install!.RemoteVersion.Should().Be(version);
            module.Version.Should().Be("0.1", "catalog presentation must not overwrite manifest version");
            module.Update.SelectedReleaseTag = "v1.0";
            (await manager.GetModuleCatalogVersionAsync(module, false)).Should().Be("v1.0");
            (await manager.GetModuleCatalogVersionAsync(module, true)).Should().Be("0.1");
        }

        /// <summary>
        /// Verifies that artwork loading reads the repository's module manifest first and then the icon file that it
        /// names, and that a second lookup for the same module is served from the cache without further requests.
        /// </summary>
        [Fact]
        public async Task Artwork_reads_the_current_manifest_then_its_icon_from_the_default_branch_and_reuses_cache()
        {
            var handler = new RepositoryHandler();
            using var http = new HttpClient(handler);
            var client = CreateClient(http);
            var first = await client.GetModuleArtworkAsync("owner/repo", "module");
            var second = await client.GetModuleArtworkAsync("owner/repo", "module");
            first.Icon.Should().Equal(RepositoryHandler.Icon);
            second.Icon.Should().Equal(first.Icon!);
            handler.Requests.Should().Equal(
                "https://api.github.com/repos/owner/repo/contents/ASLM_Module.json",
                "https://api.github.com/repos/owner/repo/contents/assets/new%20icon.png");
        }

        /// <summary>
        /// Verifies that cached artwork survives a new client without any network request, that the cache index holds
        /// only the module id, a UTC download time and a generated file name, and that a cached entry whose icon file
        /// was deleted is downloaded again.
        /// </summary>
        [Fact]
        public async Task Artwork_cache_survives_a_new_client_and_checks_that_the_file_still_exists()
        {
            // Arrange: a first client downloads and caches the artwork.
            using var firstHttp = new HttpClient(new RepositoryHandler());
            await CreateClient(firstHttp).GetModuleArtworkAsync("owner/repo", "module");

            // A second client with a fresh handler answers from the cache and sends no request.
            var handler = new RepositoryHandler();
            using var nextHttp = new HttpClient(handler);
            var nextClient = CreateClient(nextHttp);
            (await nextClient.GetModuleArtworkAsync("owner/repo", "module")).Icon.Should().Equal(RepositoryHandler.Icon);
            handler.Requests.Should().BeEmpty();

            // The index file has the documented shape.
            using var index = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_iconDirectory.FullName, "index.json")));
            index.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
            var entry = index.RootElement[0];
            entry.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("moduleId", "downloadedAt", "fileName");
            entry.GetProperty("moduleId").GetString().Should().Be("module");
            entry.GetProperty("downloadedAt").GetDateTimeOffset().Offset.Should().Be(TimeSpan.Zero);
            var fileName = entry.GetProperty("fileName").GetString()!;
            Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "N", out _).Should().BeTrue();

            // Deleting the icon file invalidates the entry: the next lookup downloads the manifest and the icon again.
            File.Delete(Path.Combine(_iconDirectory.FullName, fileName));
            (await nextClient.GetModuleArtworkAsync("owner/repo", "module")).Icon.Should().Equal(RepositoryHandler.Icon);
            handler.Requests.Should().HaveCount(2);
        }


        // Icon cache

        /// <summary>
        /// Verifies that a cached icon is served unchanged for six days without its download time being refreshed by
        /// reads, and is downloaded again exactly after seven days, replacing the old file.
        /// </summary>
        [Fact]
        public async Task Icon_expires_after_exactly_seven_days_without_refreshing_its_age_on_reads()
        {
            // Arrange: a cache with a controllable clock whose download callback returns a new icon on every call.
            var clock = new CacheClock();
            var cache = new ModuleIconCache(_iconDirectory.FullName, clock);
            var downloads = 0;

            // Returns a one-byte icon that numbers the download.
            Task<ModuleIconCache.DownloadedIcon?> Download(CancellationToken _) =>
                Task.FromResult<ModuleIconCache.DownloadedIcon?>(new([(byte)++downloads], ".png"));
            await cache.GetOrDownloadAsync("module", Download);
            var original = ReadIconIndex().Single();

            // After six days the first icon is still served and its age is not refreshed.
            clock.Now += TimeSpan.FromDays(6);
            (await cache.GetOrDownloadAsync("module", Download)).Should().Equal([1]);
            ReadIconIndex().Single().DownloadedAt.Should().Be(original.DownloadedAt);

            // After seven days the icon has expired and the second download replaces it.
            clock.Now += TimeSpan.FromDays(1);
            (await cache.GetOrDownloadAsync("module", Download)).Should().Equal([2]);
            File.Exists(Path.Combine(_iconDirectory.FullName, original.FileName)).Should().BeFalse();
            ReadIconIndex().Single().FileName.Should().NotBe(original.FileName);
        }

        /// <summary>
        /// Verifies that both pruning and storing a new icon keep at most 256 cached icons by dropping the entry with
        /// the oldest download time, together with its file.
        /// </summary>
        [Fact]
        public async Task Cleanup_and_writes_enforce_the_256_icon_limit_by_download_time()
        {
            // Arrange: an index and icon files for 257 modules, the oldest download first.
            var clock = new CacheClock();
            var entries = new List<ModuleIconCache.Entry>();
            for (var i = 0; i < 257; i++)
            {
                var name = Guid.NewGuid().ToString("N") + ".png";
                entries.Add(new("module-" + i, clock.Now.AddMinutes(i - 257), name));
                await File.WriteAllBytesAsync(Path.Combine(_iconDirectory.FullName, name), [1]);
            }

            await WriteIconIndex(entries);
            var cache = new ModuleIconCache(_iconDirectory.FullName, clock);

            // Pruning removes the single oldest entry and its file.
            await cache.PruneAsync();
            ReadIconIndex().Should().HaveCount(256).And.NotContain(entry => entry.ModuleId == "module-0");
            File.Exists(Path.Combine(_iconDirectory.FullName, entries[0].FileName)).Should().BeFalse();

            // Storing a new icon at full capacity evicts the next oldest entry, so the limit also holds for writes.
            await cache.GetOrDownloadAsync("new", _ => Task.FromResult<ModuleIconCache.DownloadedIcon?>(new([2], ".png")));
            ReadIconIndex().Should().HaveCount(256).And.NotContain(entry => entry.ModuleId == "module-1");
            _iconDirectory.GetFiles("*.png").Should().HaveCount(256);
            ReadIconIndex().Select(entry => entry.FileName).Should().OnlyHaveUniqueItems();
        }

        /// <summary>
        /// Verifies that pruning removes expired entries, entries whose file is missing and files that no entry
        /// references, and never deletes a file outside the cache directory even when an index entry points to it
        /// with a relative path.
        /// </summary>
        [Fact]
        public async Task Cleanup_removes_expired_missing_and_orphan_icons_but_never_follows_index_paths()
        {
            // Arrange: a cache directory with an expired icon, an orphan file and a protected file outside the cache.
            var clock = new CacheClock();
            var cacheDirectory = Path.Combine(_iconDirectory.FullName, "cache");
            Directory.CreateDirectory(cacheDirectory);
            var protectedFile = Path.Combine(_iconDirectory.FullName, "protected.png");
            await File.WriteAllBytesAsync(protectedFile, [9]);
            var expired = Guid.NewGuid().ToString("N") + ".png";
            var orphan = Guid.NewGuid().ToString("N") + ".png";
            await File.WriteAllBytesAsync(Path.Combine(cacheDirectory, expired), [1]);
            await File.WriteAllBytesAsync(Path.Combine(cacheDirectory, orphan), [1]);

            // The index lists the expired icon, an icon whose file is missing and an unsafe path to the protected file.
            await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "index.json"), JsonSerializer.Serialize(new ModuleIconCache.Entry[]
            {
                new("expired", clock.Now - TimeSpan.FromDays(7), expired),
                new("missing", clock.Now, Guid.NewGuid().ToString("N") + ".png"),
                new("unsafe", clock.Now, "../protected.png")
            }));

            // Act and assert: only an empty index remains in the cache directory and the protected file is untouched.
            await new ModuleIconCache(cacheDirectory, clock).PruneAsync();
            Directory.GetFiles(cacheDirectory).Select(Path.GetFileName).Should().Equal("index.json");
            (await File.ReadAllTextAsync(Path.Combine(cacheDirectory, "index.json"))).Trim().Should().Be("[]");
            (await File.ReadAllBytesAsync(protectedFile)).Should().Equal([9]);
        }

        /// <summary>
        /// Verifies that concurrent requests for the same module id share one download, and that a download which is
        /// canceled throws an <see cref="OperationCanceledException"/> and leaves no cache entry.
        /// </summary>
        [Fact]
        public async Task Concurrent_requests_for_one_id_download_once_and_canceled_downloads_leave_no_record()
        {
            // Arrange: a download that blocks until released and counts how often it is called.
            var cache = new ModuleIconCache(_iconDirectory.FullName);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;

            // Blocks until the test releases it.
            async Task<ModuleIconCache.DownloadedIcon?> Download(CancellationToken ct)
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new([1], ".png");
            }

            // Two overlapping requests for the same module call the download only once.
            var first = cache.GetOrDownloadAsync("module", Download);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = cache.GetOrDownloadAsync("module", Download);
            release.SetResult();
            await Task.WhenAll(first, second);
            calls.Should().Be(1);

            // A download canceled from within leaves the index with only the first module.
            using var canceled = new CancellationTokenSource();
            Func<Task> canceledDownload = () => cache.GetOrDownloadAsync("canceled", ct =>
            {
                canceled.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ModuleIconCache.DownloadedIcon?>(null);
            }, canceled.Token);
            await canceledDownload.Should().ThrowAsync<OperationCanceledException>();
            ReadIconIndex().Should().ContainSingle().Which.ModuleId.Should().Be("module");
        }

        /// <summary>
        /// Verifies that a corrupt index file is replaced by a valid one, and that an icon is still returned (and an
        /// existing file left alone) when the cache directory cannot be created because a file occupies its path.
        /// </summary>
        [Fact]
        public async Task Corrupt_index_is_rebuilt_and_unwritable_cache_does_not_discard_downloaded_artwork()
        {
            await File.WriteAllTextAsync(Path.Combine(_iconDirectory.FullName, "index.json"), "[broken");
            var cache = new ModuleIconCache(_iconDirectory.FullName);

            // Returns an icon consisting of the single byte 3.
            Task<ModuleIconCache.DownloadedIcon?> Download(CancellationToken _) =>
                Task.FromResult<ModuleIconCache.DownloadedIcon?>(new([3], ".png"));

            // A corrupt index counts as empty: the icon is downloaded and a valid index is written.
            (await cache.GetOrDownloadAsync("module", Download)).Should().Equal([3]);
            ReadIconIndex().Should().ContainSingle().Which.ModuleId.Should().Be("module");

            // A file occupying the cache path cannot be used as a directory: the icon is still returned.
            var blockedDirectory = Path.Combine(_iconDirectory.FullName, "not-a-directory");
            await File.WriteAllTextAsync(blockedDirectory, "keep");
            (await new ModuleIconCache(blockedDirectory).GetOrDownloadAsync("module", Download)).Should().Equal([3]);
            (await File.ReadAllTextAsync(blockedDirectory)).Should().Be("keep");
        }


        // Icon cache helpers

        /// <summary>
        /// Reads the entries of the icon cache index file from the test cache directory.
        /// </summary>
        private List<ModuleIconCache.Entry> ReadIconIndex() => JsonSerializer.Deserialize<List<ModuleIconCache.Entry>>(
            File.ReadAllText(Path.Combine(_iconDirectory.FullName, "index.json")))!;

        /// <summary>
        /// Replaces the icon cache index file in the test cache directory with the given entries.
        /// </summary>
        private Task WriteIconIndex(List<ModuleIconCache.Entry> entries) =>
            File.WriteAllTextAsync(Path.Combine(_iconDirectory.FullName, "index.json"), JsonSerializer.Serialize(entries));

        /// <summary>
        /// Time provider whose current time is set by the test, so icon expiry can be checked without waiting.
        /// </summary>
        private sealed class CacheClock : TimeProvider
        {
            /// <summary>
            /// Current UTC time reported by the clock; tests advance it directly.
            /// </summary>
            public DateTimeOffset Now { get; set; } = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

            /// <summary>
            /// Returns <see cref="Now"/> as the current UTC time.
            /// </summary>
            public override DateTimeOffset GetUtcNow() => Now;
        }


        // Artwork lookup

        /// <summary>
        /// Verifies that remote icon paths which are parent-relative, rooted, absolute URLs or drive paths are rejected
        /// when the module manifest is read, so an icon can never point outside the repository.
        /// </summary>
        [Theory]
        [InlineData("../icon.png")]
        [InlineData("/icon.png")]
        [InlineData("https://elsewhere/icon.png")]
        [InlineData("C:/icon.png")]
        public void Remote_icon_paths_cannot_escape_the_repository(string path)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { id = "module", icon = path }));
            var read = () => GitHubUpdateClient.ReadModuleIconPath(json.RootElement, "module");
            read.Should().Throw<JsonException>();
        }

        /// <summary>
        /// Verifies that a module whose icon file cannot be fetched (not found) still resolves its catalog version,
        /// while the artwork lookup reports no result.
        /// </summary>
        [Fact]
        public async Task Missing_remote_icon_does_not_prevent_resolving_the_selected_release()
        {
            using var http = new HttpClient(new RepositoryHandler(failIcon: true));
            var client = CreateClient(http);
            var manager = new UpdateManager(null!, null!, null!, null!, null!, null!, null!, null!, client, null!, NullLogger<UpdateManager>.Instance, new DownloadQueue());
            var module = new ModuleConfig { Id = "module", Source = new() { Type = "github", Repo = "owner/repo" } };
            (await manager.GetModuleCatalogVersionAsync(module, false)).Should().Be("v1.0");
            (await manager.GetModuleCatalogArtworkAsync(module)).Should().BeNull();
        }

        /// <summary>
        /// Verifies that icons already in the cache are available while the release requests of the same modules are
        /// still pending, without contacting the repository for the icons, and that the versions resolve once the
        /// release responses arrive.
        /// </summary>
        [Fact]
        public async Task Cached_artwork_is_available_while_release_requests_are_still_pending()
        {
            // Arrange: both module icons are cached; the releases responses are held back, and the handler fails
            // the test for any request that is not a releases request.
            var cache = new ModuleIconCache(_iconDirectory.FullName);
            foreach (var id in new[] { "module", "other" })
                await cache.GetOrDownloadAsync(id, _ => Task.FromResult<ModuleIconCache.DownloadedIcon?>(new(RepositoryHandler.Icon, ".png")));

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var handler = new RepositoryHandler(beforeResponse: async (request, ct) =>
            {
                request.RequestUri!.AbsolutePath.Should().EndWith("/releases", "cached icons must not contact the repository");
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            });
            using var http = new HttpClient(handler);
            var manager = new UpdateManager(null!, null!, null!, null!, null!, null!, null!, null!, CreateClient(http), null!, NullLogger<UpdateManager>.Instance, new DownloadQueue());
            var modules = new[] { CreateModule("module", "owner/repo"), CreateModule("other", "owner/other") };
            var versions = modules.Select(module => manager.GetModuleCatalogVersionAsync(module, false, timeout.Token)).ToArray();
            try
            {
                // While the release requests are blocked, the cached icons are returned and no version is ready yet.
                await entered.Task.WaitAsync(timeout.Token);
                var icons = await Task.WhenAll(modules.Select(module => manager.GetModuleCatalogArtworkAsync(module, timeout.Token)))
                    .WaitAsync(timeout.Token);
                foreach (var icon in icons) icon!.Icon.Should().Equal(RepositoryHandler.Icon);
                versions.Should().OnlyContain(version => !version.IsCompleted);
            }
            finally { release.TrySetResult(); await Task.WhenAll(versions); }

            // After the release: every version resolves and exactly one request per repository was made.
            (await Task.WhenAll(versions)).Should().OnlyContain(version => version == "v1.0");
            handler.Requests.Should().HaveCount(2);
        }

        /// <summary>
        /// Verifies that a slow icon download blocks neither the version lookup of the same module nor the cached icon
        /// of another module, and that canceling the slow download throws and does not cache the icon.
        /// </summary>
        [Fact]
        public async Task A_slow_icon_does_not_block_versions_or_other_cached_icons_and_cancellation_does_not_cache_it()
        {
            // Arrange: another module's icon is cached; any request for an icon file waits forever for cancellation.
            await new ModuleIconCache(_iconDirectory.FullName).GetOrDownloadAsync("other",
                _ => Task.FromResult<ModuleIconCache.DownloadedIcon?>(new(RepositoryHandler.Icon, ".png")));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var handler = new RepositoryHandler(beforeResponse: async (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(".png"))
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
            });
            using var http = new HttpClient(handler);
            var manager = new UpdateManager(null!, null!, null!, null!, null!, null!, null!, null!, CreateClient(http), null!, NullLogger<UpdateManager>.Instance, new DownloadQueue());
            var slow = CreateModule("module", "owner/repo");
            var cached = CreateModule("other", "owner/other");
            var icon = manager.GetModuleCatalogArtworkAsync(slow, canceled.Token);
            try
            {
                // While the icon download hangs, the version and the other module's cached icon are still available.
                await entered.Task.WaitAsync(timeout.Token);
                (await manager.GetModuleCatalogVersionAsync(slow, false, timeout.Token)).Should().Be("v1.0");
                (await manager.GetModuleCatalogArtworkAsync(cached, timeout.Token))!.Icon.Should().Equal(RepositoryHandler.Icon);
                icon.IsCompleted.Should().BeFalse();
            }
            finally
            {
                // Canceling the hanging download throws and must not cache anything for the slow module.
                canceled.Cancel();
                var wait = async () => await icon;
                await wait.Should().ThrowAsync<OperationCanceledException>();
            }

            ReadIconIndex().Should().ContainSingle().Which.ModuleId.Should().Be("other");
        }


        // Metadata sharing and cache hits

        /// <summary>
        /// Verifies that a pending releases request is shared only with requests for the same repository (ignoring
        /// case) and the same endpoint, that other repositories and endpoints are not blocked by it, and that
        /// canceling one waiting caller throws for that caller only.
        /// </summary>
        [Fact]
        public async Task Metadata_requests_only_wait_for_the_same_repository_and_endpoint()
        {
            // Arrange: the releases request of the first repository is held back by the handler.
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var handler = new RepositoryHandler(beforeResponse: async (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath == "/repos/owner/repo/releases")
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
            });
            using var http = new HttpClient(handler);
            var client = CreateClient(http);
            var first = client.GetReleasesAsync("owner/repo", false, ct: timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            var duplicate = client.GetReleasesAsync("OWNER/REPO", true, ct: timeout.Token);
            try
            {
                // A different repository and a different endpoint of the same repository complete while it is held.
                (await client.GetReleasesAsync("owner/other", false, ct: timeout.Token)).Should().ContainSingle();
                (await client.GetBranchesAsync("owner/repo", ct: timeout.Token)).Should().BeEmpty();
                first.IsCompleted.Should().BeFalse();
                duplicate.IsCompleted.Should().BeFalse();

                // A caller that waits on the held request can be canceled on its own.
                using var canceled = new CancellationTokenSource();
                var waiting = client.GetReleasesAsync("owner/repo", false, ct: canceled.Token);
                canceled.Cancel();
                var wait = async () => await waiting;
                await wait.Should().ThrowAsync<OperationCanceledException>();
            }
            finally { release.TrySetResult(); await Task.WhenAll(first, duplicate); }

            // After the release both callers of the shared request get their own filtered view of the same fetch.
            (await first).Should().ContainSingle();
            (await duplicate).Should().HaveCount(2);
            handler.Requests.Should().HaveCount(3, "same-repository requests must still share one fetch");
        }

        /// <summary>
        /// Verifies that a cache hit only reads the icon: it works even when the index cannot be rewritten, leaves the
        /// index unchanged and does not delete expired files, which only an explicit prune removes.
        /// </summary>
        [Fact]
        public async Task A_cache_hit_only_reads_its_icon_without_rewriting_the_index_or_pruning_other_files()
        {
            // Arrange: an index with a valid entry and an expired entry, each with its icon file.
            var clock = new CacheClock();
            var valid = Guid.NewGuid().ToString("N") + ".png";
            var expired = Guid.NewGuid().ToString("N") + ".png";
            await File.WriteAllBytesAsync(Path.Combine(_iconDirectory.FullName, valid), [1]);
            await File.WriteAllBytesAsync(Path.Combine(_iconDirectory.FullName, expired), [2]);
            await WriteIconIndex([new("module", clock.Now, valid), new("old", clock.Now - TimeSpan.FromDays(8), expired)]);
            var index = Path.Combine(_iconDirectory.FullName, "index.json");
            var before = await File.ReadAllTextAsync(index);

            // Reading should still work when the index cannot be replaced or written.
            using (var readOnly = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var bytes = await new ModuleIconCache(_iconDirectory.FullName, clock).GetOrDownloadAsync("module",
                    _ => throw new InvalidOperationException("A cache hit must not download anything."));
                bytes.Should().Equal([1]);
            }

            // The hit left the index and the expired file as they were; an explicit prune then removes the expired file.
            (await File.ReadAllTextAsync(index)).Should().Be(before);
            File.Exists(Path.Combine(_iconDirectory.FullName, expired)).Should().BeTrue();
            await new ModuleIconCache(_iconDirectory.FullName, clock).PruneAsync();
            File.Exists(Path.Combine(_iconDirectory.FullName, expired)).Should().BeFalse();
        }


        // Test doubles and helpers

        /// <summary>
        /// Creates a minimal GitHub module configuration for the given module id and repository.
        /// </summary>
        private static ModuleConfig CreateModule(string id, string repo) =>
            new() { Id = id, Version = "0.1", Source = new() { Type = "github", Repo = repo } };

        /// <summary>
        /// HTTP handler that plays the GitHub repository API for <c>owner/repo</c> and <c>owner/other</c>: it records
        /// every request URL, can delay responses through a hook, and answers releases, branches, the module manifest
        /// and its icon. Any other request fails the test with an exception.
        /// </summary>
        /// <param name="failIcon">True to answer the icon request with "not found".</param>
        /// <param name="beforeResponse">Optional hook awaited for every request before it is answered.</param>
        /// <param name="releaseJson">Releases JSON to serve, or null for a stable v1.0 and a pre-release v2.0-beta.</param>
        /// <param name="branchJson">Branches JSON to serve; an empty list by default.</param>
        private sealed class RepositoryHandler(bool failIcon = false,
            Func<HttpRequestMessage, CancellationToken, Task>? beforeResponse = null,
            string? releaseJson = null, string branchJson = "[]") : HttpMessageHandler
        {
            /// <summary>
            /// Bytes of the icon file that the handler serves (the start of a PNG signature).
            /// </summary>
            public static readonly byte[] Icon = [137, 80, 78, 71];

            /// <summary>
            /// Absolute URLs of all requests that reached the handler, in arrival order.
            /// </summary>
            public ConcurrentQueue<string> Requests { get; } = [];

            /// <summary>
            /// Records the request, awaits the optional hook, and returns the canned response for the request path.
            /// </summary>
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Enqueue(request.RequestUri!.AbsoluteUri);
                if (beforeResponse != null) await beforeResponse(request, cancellationToken);

                // Choose the response body by API path; the manifest request must ask for the raw media type.
                HttpContent content;
                switch (request.RequestUri.AbsolutePath)
                {
                    case "/repos/owner/repo/releases":
                    case "/repos/owner/other/releases":
                        content = new StringContent(releaseJson ?? """
                            [{"tag_name":"v1.0","prerelease":false,"zipball_url":"https://example.test/v1.zip"},
                             {"tag_name":"v2.0-beta","prerelease":true,"zipball_url":"https://example.test/v2.zip"}]
                            """);
                        break;
                    case "/repos/owner/repo/branches":
                        content = new StringContent(branchJson);
                        break;
                    case "/repos/owner/repo/contents/ASLM_Module.json":
                        request.Headers.Accept.Should().Contain(value => value.MediaType == "application/vnd.github.raw+json");
                        content = new StringContent("""{"id":"module","icon":"assets/new icon.png"}""");
                        break;
                    case "/repos/owner/repo/contents/assets/new%20icon.png":
                        if (failIcon) return new HttpResponseMessage(HttpStatusCode.NotFound);
                        content = new ByteArrayContent(Icon);
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected test request: " + request.RequestUri.AbsoluteUri);
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
        }


        // Blank repository handling

        /// <summary>
        /// Verifies that requesting releases for a blank repository name returns an empty list.
        /// </summary>
        [Fact]
        public async Task GetReleasesAsync_returns_empty_for_blank_repo()
        {
            var client = CreateClient();

            var releases = await client.GetReleasesAsync("  ", includePrerelease: true);

            releases.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that requesting branches for an empty repository name returns an empty list.
        /// </summary>
        [Fact]
        public async Task GetBranchesAsync_returns_empty_for_blank_repo()
        {
            var client = CreateClient();

            var branches = await client.GetBranchesAsync(string.Empty);

            branches.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that requesting the latest release for an empty repository name returns no release.
        /// </summary>
        [Fact]
        public async Task GetLatestReleaseAsync_returns_null_for_blank_repo()
        {
            var client = CreateClient();

            var release = await client.GetLatestReleaseAsync(string.Empty, includePrerelease: false);

            release.Should().BeNull();
        }


        // Client factory

        /// <summary>
        /// Creates a GitHub client over the default rate-limit and account stores and the test icon cache; it uses the
        /// given HTTP client, or the client's own default one when none is passed.
        /// </summary>
        /// <param name="http">HTTP client for repository requests, or null to use the default.</param>
        private GitHubUpdateClient CreateClient(HttpClient? http = null)
        {
            var appData = new AppDataStore(NullLogger<AppDataStore>.Instance);
            var rateLimitStore = new GitHubRateLimitStore(NullLogger<GitHubRateLimitStore>.Instance);
            var accountStore = new GitHubAccountStore(
                appData,
                rateLimitStore,
                NullLogger<GitHubAccountStore>.Instance);
            var icons = new ModuleIconCache(_iconDirectory.FullName);
            return http == null ? new GitHubUpdateClient(rateLimitStore, accountStore, icons)
                : new GitHubUpdateClient(rateLimitStore, accountStore, http, icons);
        }
    }
}
