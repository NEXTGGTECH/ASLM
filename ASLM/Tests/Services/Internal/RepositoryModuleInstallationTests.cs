// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using System.Text;
using System.Text.Json;
using ASLM.Localization;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies repository discovery and first installation through production services over isolated storage and HTTP.
    /// </summary>
    public sealed class RepositoryModuleInstallationTests
    {
        // Discovery and validation

        /// <summary>
        /// Verifies two discovery requests, reuse without shared mutable previews and in-memory preference editing.
        /// </summary>
        [Fact]
        public async Task Discovery_and_picker_edits_never_create_a_module_folder_or_registration()
        {
            using var graph = new TestServiceGraph();
            RegisterRepository(graph, Manifest());
            var module = await graph.Updates.FindRepositoryModuleAsync("https://github.com/owner/repo.git");
            module.RepositoryPreview.Should().NotBeNull();
            module.Source.Repo.Should().Be("owner/repo");
            module.Update.Branch.Should().Be("develop");
            module.Status.Installed.Should().BeFalse();
            module.Update.SelectedReleaseTag = "v1";
            graph.Updates.SaveModuleUpdatePreferences(module);

            var again = await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            again.Should().NotBeSameAs(module);
            again.Update.SelectedReleaseTag.Should().NotBe("v1");
            graph.GitHubHttp.Requests.Should().HaveCount(2);
            Directory.EnumerateFileSystemEntries(graph.Root.ModulesDir).Should().BeEmpty();
            graph.Installer.Registry.ReadIds().Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that credential changes do not reuse responses obtained with a different token.
        /// </summary>
        [Fact]
        public async Task Discovery_uses_the_current_token_and_separates_authenticated_caches()
        {
            using var graph = new TestServiceGraph();
            RegisterRepository(graph, Manifest());
            await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            graph.AppData.Data.GitHub.PersonalAccessToken = "private-test-token";
            await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            graph.GitHubHttp.Requests.Should().HaveCount(4);
            graph.GitHubHttp.Requests.Take(2).Should().OnlyContain(request => request.Headers.Authorization == null);
            graph.GitHubHttp.Requests.Skip(2).Should().OnlyContain(request =>
                request.Headers.Authorization!.ToString() == "Bearer private-test-token");
        }

        /// <summary>
        /// Verifies that invalid input is rejected locally and invalid root manifests never create catalog entries.
        /// </summary>
        [Theory]
        [InlineData("bad-query", "{}")]
        [InlineData("owner/repo", "not JSON")]
        [InlineData("owner/repo", "{\"id\":\"\"}")]
        [InlineData("owner/repo", "{\"id\":\"module\",\"fileVersion\":99}")]
        [InlineData("owner/repo", "{\"id\":\"module\",\"fileVersion\":2}")]
        [InlineData("owner/repo", "{\"id\":\"../outside\"}")]
        [InlineData("owner/repo", "{\"id\":\"module\",\"source\":{\"repo\":\"another/repo\"}}")]
        public async Task Invalid_input_or_manifests_do_not_leave_module_files(string query, string json)
        {
            using var graph = new TestServiceGraph();
            RegisterRepository(graph, json);
            var find = () => graph.Updates.FindRepositoryModuleAsync(query);
            await find.Should().ThrowAsync<Exception>();
            if (query == "bad-query") graph.GitHubHttp.Requests.Should().BeEmpty();
            Directory.EnumerateFileSystemEntries(graph.Root.ModulesDir).Should().BeEmpty();
            graph.Installer.Registry.ReadIds().Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that missing repositories, root manifests and authentication failures remain distinct.
        /// </summary>
        [Theory]
        [InlineData("repository", HttpStatusCode.NotFound, LocalizationKeys.ModulesDownloads_RepositoryUnavailable)]
        [InlineData("manifest", HttpStatusCode.NotFound, LocalizationKeys.ModulesDownloads_ManifestMissing)]
        [InlineData("repository", HttpStatusCode.Unauthorized, "401")]
        [InlineData("repository", HttpStatusCode.Forbidden, "403")]
        [InlineData("repository", HttpStatusCode.TooManyRequests, "429")]
        public async Task Lookup_failures_are_reported_without_registering_a_module(
            string endpoint, HttpStatusCode status, string message)
        {
            using var graph = new TestServiceGraph();
            RegisterRepository(graph, Manifest());
            graph.GitHubHttp.OnJson(endpoint == "repository" ? "https://api.github.com/repos/owner/repo"
                : "https://api.github.com/repos/owner/repo/contents/ASLM_Module.json*", "{}", status);
            var find = () => graph.Updates.FindRepositoryModuleAsync("owner/repo");
            var failure = await find.Should().ThrowAsync<Exception>();
            if (status == HttpStatusCode.NotFound)
                failure.Which.Message.Should().Be(L.Get(message));
            else
                failure.Which.Message.Should().Contain(message);
            graph.Installer.Registry.ReadIds().Should().BeEmpty();
        }

        /// <summary>
        /// Verifies v2 preview compatibility and optional artwork without reloading the root manifest.
        /// </summary>
        [Fact]
        public async Task V2_preview_reports_platform_support_and_artwork_reuses_the_manifest()
        {
            using var graph = new TestServiceGraph();
            RegisterRepository(graph, """
                {"fileVersion":2,"id":"remote","icon":"icon.png",
                 "supportedPlatforms":[{"os":"linux","arch":"arm64"}]}
                """);
            graph.GitHubHttp.OnBytes("https://api.github.com/repos/owner/repo/contents/icon.png", [1, 2, 3]);
            var module = await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            module.IsSupportedOnCurrentPlatform.Should().BeFalse();
            var artwork = await graph.Updates.GetModuleCatalogArtworkAsync(module);
            artwork!.Icon.Should().Equal(1, 2, 3);
            graph.GitHubHttp.Requests.Should().HaveCount(3);
            graph.GitHubHttp.Requests.Should().NotContain(request => request.Url.AbsolutePath.Contains("releases"));
        }

        /// <summary>
        /// Verifies that directories, invalid encodings and oversized manifest metadata are rejected without files.
        /// </summary>
        [Theory]
        [InlineData("[]")]
        [InlineData("{\"type\":\"dir\",\"path\":\"ASLM_Module.json\"}")]
        [InlineData("{\"type\":\"file\",\"path\":\"nested/ASLM_Module.json\"}")]
        [InlineData("{\"type\":\"file\",\"path\":\"ASLM_Module.json\",\"size\":1048577}")]
        [InlineData("{\"type\":\"file\",\"path\":\"ASLM_Module.json\",\"size\":1,\"encoding\":\"base64\",\"content\":\"!\"}")]
        public async Task Invalid_root_file_responses_do_not_create_module_entries(string response)
        {
            using var graph = new TestServiceGraph();
            RegisterRepository(graph, Manifest());
            graph.GitHubHttp.OnJson("https://api.github.com/repos/owner/repo/contents/ASLM_Module.json*", response);
            var find = () => graph.Updates.FindRepositoryModuleAsync("owner/repo");
            await find.Should().ThrowAsync<Exception>();
            Directory.EnumerateFileSystemEntries(graph.Root.ModulesDir).Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that the decoded file limit allows a valid manifest whose Base64 response is larger than one MiB.
        /// </summary>
        [Fact]
        public async Task Manifest_size_limit_applies_to_decoded_content_and_accepts_a_utf8_preamble()
        {
            using var graph = new TestServiceGraph();
            var json = ((char)0xFEFF) + JsonSerializer.Serialize(new { id = "remote", description = new string('x', 900000) });
            RegisterRepository(graph, json);
            var preview = await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            preview.Description.Length.Should().Be(900000);
            Directory.EnumerateFileSystemEntries(graph.Root.ModulesDir).Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that existing sources keep their identity and conflicting or occupied destinations are refused.
        /// </summary>
        [Theory]
        [InlineData("owner/repo", true)]
        [InlineData("another/repo", false)]
        [InlineData("occupied", false)]
        public async Task Discovery_respects_existing_catalog_sources(string existingSource, bool succeeds)
        {
            using var graph = new TestServiceGraph();
            RegisterRepository(graph, Manifest());
            var directory = Path.Combine(graph.Root.ModulesDir, "remote");
            Directory.CreateDirectory(directory);
            if (existingSource != "occupied")
                File.WriteAllText(Path.Combine(directory, "ASLM_Module.json"),
                    JsonSerializer.Serialize(new { id = "remote", source = new { repo = existingSource } }));
            if (succeeds)
            {
                var found = await graph.Updates.FindRepositoryModuleAsync("owner/repo");
                found.RepositoryPreview.Should().BeNull();
                found.SourcePath.Should().Be(Path.Combine(directory, "ASLM_Module.json"));
            }
            else
            {
                var find = () => graph.Updates.FindRepositoryModuleAsync("owner/repo");
                await find.Should().ThrowAsync<InvalidOperationException>();
            }
        }


        // Installation and updates

        /// <summary>
        /// Verifies installation from private branches, release zipballs and binary assets without a local seed.
        /// </summary>
        [Theory]
        [InlineData("branch")]
        [InlineData("release")]
        [InlineData("asset")]
        public async Task Private_repository_installs_through_the_existing_queue_without_a_local_manifest(string source)
        {
            using var graph = new TestServiceGraph();
            graph.AppData.Data.GitHub.PersonalAccessToken = "private-test-token";
            var json = Manifest(asset: source == "asset");
            RegisterRepository(graph, json);
            var url = RegisterDownload(graph, source, json);
            var module = await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            File.Exists(module.SourcePath).Should().BeFalse();
            var outcome = await graph.Updates.InstallCatalogModuleAsync(module, new InlineProgress<string>(_ => { }));

            outcome.Status.Should().Be(ModuleLaunchStatus.NoRunCommands);
            graph.Installer.Registry.ReadIds().Should().Contain("remote");
            var installed = (await graph.Installer.DiscoverInstalledModulesAsync()).Single();
            installed.RepositoryPreview.Should().BeNull();
            installed.Source.Repo.Should().Be("owner/repo");
            installed.Status.FirstRunCompleted.Should().BeTrue();
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(installed.SourcePath)!, "payload.txt")).Should().Be("payload");
            graph.GitHubHttp.Requests.Should().OnlyContain(request =>
                request.Headers.Authorization!.ToString() == "Bearer private-test-token");
            var download = graph.GitHubHttp.Requests.Single(request => request.Url.AbsoluteUri == url);
            if (source == "asset") download.Headers.Accept.Should().ContainSingle().Which.MediaType.Should().Be("application/octet-stream");
            if (source == "branch") installed.Update.Branch.Should().Be("develop");
        }

        /// <summary>
        /// Verifies archive identity and source checks before a repository install creates its destination.
        /// </summary>
        [Theory]
        [InlineData("{\"id\":\"other\"}")]
        [InlineData("{\"id\":\"remote\",\"source\":{\"repo\":\"another/repo\"}}")]
        public async Task Invalid_downloaded_manifests_do_not_replace_or_register_a_module(string archiveManifest)
        {
            using var graph = new TestServiceGraph();
            RegisterRepository(graph, Manifest());
            RegisterDownload(graph, "release", archiveManifest);
            var module = await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            var install = () => graph.Updates.InstallCatalogModuleAsync(module, new InlineProgress<string>(_ => { }));
            await install.Should().ThrowAsync<InvalidOperationException>();
            Directory.Exists(Path.GetDirectoryName(module.SourcePath)).Should().BeFalse();
            graph.Installer.Registry.ReadIds().Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that a catalog collision introduced after discovery is rechecked when installation is queued.
        /// </summary>
        [Fact]
        public async Task Installation_rechecks_identity_after_discovery()
        {
            using var graph = new TestServiceGraph();
            RegisterRepository(graph, Manifest());
            var module = await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            Directory.CreateDirectory(Path.GetDirectoryName(module.SourcePath)!);
            const string conflicting = "{\"id\":\"remote\",\"source\":{\"repo\":\"another/repo\"}}";
            File.WriteAllText(module.SourcePath, conflicting);
            var install = () => graph.Updates.InstallCatalogModuleAsync(module, new InlineProgress<string>(_ => { }));
            await install.Should().ThrowAsync<InvalidOperationException>();
            File.ReadAllText(module.SourcePath).Should().Be(conflicting);
            graph.Installer.Registry.ReadIds().Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that private modules keep their discovered source and binary download mode on later updates.
        /// </summary>
        [Fact]
        public async Task Installed_private_modules_keep_their_source_during_later_updates()
        {
            using var graph = new TestServiceGraph();
            graph.AppData.Data.GitHub.PersonalAccessToken = "private-test-token";
            RegisterRepository(graph, Manifest(asset: true));
            RegisterDownload(graph, "asset", Manifest(asset: true));
            var preview = await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            await graph.Updates.InstallCatalogModuleAsync(preview, new InlineProgress<string>(_ => { }));
            var installed = (await graph.Installer.DiscoverInstalledModulesAsync()).Single();
            var url = RegisterDownload(graph, "asset", Manifest(asset: true).Replace("1.0", "2.0"));

            var applied = await graph.Updates.ApplyModuleUpdateAsync(new UpdateCandidate
            {
                TargetKind = "module", TargetId = installed.Id, Name = installed.Name, Module = installed,
                Mode = "release", RemoteVersion = "v2", ReleaseTag = "v2", DownloadUrl = url
            }, isManualRequest: true);

            applied.Should().BeTrue();
            var updated = (await graph.Installer.DiscoverInstalledModulesAsync()).Single();
            updated.Source.Repo.Should().Be("owner/repo");
            updated.Update.InstalledReleaseTag.Should().Be("v2");
            updated.Version.Should().Be("2.0");
            graph.GitHubHttp.Requests.Where(request => request.Url.AbsoluteUri == url).Should().HaveCount(2)
                .And.OnlyContain(request => request.Headers.Authorization!.Parameter == "private-test-token" &&
                    request.Headers.Accept.Single().MediaType == "application/octet-stream");
        }

        /// <summary>
        /// Verifies that module dependencies and non-Python first-run commands use the ordinary installation lifecycle.
        /// </summary>
        [Fact]
        public async Task Repository_modules_install_catalog_dependencies_before_running_setup()
        {
            using var graph = new TestServiceGraph();
            var dependencyDirectory = Path.Combine(graph.Root.ModulesDir, "dependency");
            Directory.CreateDirectory(dependencyDirectory);
            File.WriteAllText(Path.Combine(dependencyDirectory, "ASLM_Module.json"),
                """{"id":"dependency","name":"Dependency","source":{"repo":"owner/dependency"},"update":{}}""");
            graph.GitHubHttp.OnJson("https://api.github.com/repos/owner/dependency/releases*",
                """[{"tag_name":"v1","zipball_url":"https://api.github.com/repos/owner/dependency/zipball/v1"}]""");
            var dependencyZip = Path.Combine(graph.Root.Root, "dependency.zip");
            TestArchives.WriteZip(dependencyZip,
                new("source/ASLM_Module.json", """{"id":"dependency","source":{"repo":"owner/dependency"}}"""),
                new("source/ready.txt", "ready"));
            graph.GitHubHttp.OnBytes("https://api.github.com/repos/owner/dependency/zipball/v1",
                File.ReadAllBytes(dependencyZip));

            // The root's setup succeeds only if the dependency payload was installed first.
            var marker = Path.Combine(graph.Root.Root, "first-run.txt");
            var json = JsonSerializer.Serialize(new
            {
                id = "remote", name = "Remote module", dependencies = new { modules = new[] { new { id = "dependency" } } },
                commands = new
                {
                    firstRun = new[] { new { exec = "powershell.exe -NoProfile -NonInteractive -Command " +
                        $"\"if (!(Test-Path '{Path.Combine(dependencyDirectory, "ready.txt")}')) {{ exit 1 }}; " +
                        $"Set-Content -Path '{marker}' -Value done\"" } }
                }
            });
            RegisterRepository(graph, json);
            RegisterDownload(graph, "release", json);
            var preview = await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            File.Exists(marker).Should().BeFalse();
            await graph.Updates.InstallCatalogModuleAsync(preview, new InlineProgress<string>(_ => { }));
            File.Exists(marker).Should().BeTrue();
            graph.Installer.Registry.ReadIds().Should().Contain(["dependency", "remote"]);
        }

        /// <summary>
        /// Verifies that missing module dependencies are reported without downloading or creating the root module.
        /// </summary>
        [Fact]
        public async Task Missing_dependencies_fail_before_downloading_the_root_module()
        {
            using var graph = new TestServiceGraph();
            RegisterRepository(graph, """{"id":"remote","dependencies":{"modules":[{"id":"unknown"}]}}""");
            var preview = await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            var install = () => graph.Updates.InstallCatalogModuleAsync(preview, new InlineProgress<string>(_ => { }));
            await install.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unknown*");
            graph.GitHubHttp.Requests.Should().HaveCount(2);
            Directory.EnumerateFileSystemEntries(graph.Root.ModulesDir).Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that cancellation while queued leaves no manifest and duplicate requests share one reservation.
        /// </summary>
        [Fact]
        public async Task Canceling_a_queued_preview_leaves_no_local_module()
        {
            using var graph = new TestServiceGraph();
            RegisterRepository(graph, Manifest());
            var preview = await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var preceding = graph.Queue.EnqueueAsync("preceding", _ => release.Task, kind: DownloadQueueKind.Modules);
            var pending = graph.Updates.InstallCatalogModuleAsync(preview, new InlineProgress<string>(_ => { }));
            graph.Updates.InstallCatalogModuleAsync(preview, new InlineProgress<string>(_ => { })).Should().BeSameAs(pending);
            try
            {
                graph.Updates.CancelCatalogInstall(preview.Id);
                release.SetResult(true);
                var observe = () => pending;
                await observe.Should().ThrowAsync<OperationCanceledException>();
                Directory.Exists(Path.GetDirectoryName(preview.SourcePath)).Should().BeFalse();
                graph.Installer.Registry.ReadIds().Should().BeEmpty();
            }
            finally
            {
                release.TrySetResult(true);
                await preceding;
            }
        }

        /// <summary>
        /// Verifies that canceling an install from its row releases the reservation and reports cancellation.
        /// </summary>
        [Fact]
        public async Task Canceling_an_active_preview_from_its_notification_leaves_no_local_module()
        {
            using var graph = new TestServiceGraph();
            RegisterRepository(graph, Manifest());
            RegisterDownload(graph, "release", Manifest());
            var preview = await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            graph.Notifications.NotificationPublished += (_, row) =>
            {
                if (row.CanCancelOperation && row.SourceId == preview.Id)
                    graph.Notifications.RequestDownloadCancellation(row);
            };

            var install = () => graph.Updates.InstallCatalogModuleAsync(preview, new InlineProgress<string>(_ => { }));
            await install.Should().ThrowAsync<OperationCanceledException>();

            graph.Updates.IsCatalogInstallPending(preview.Id).Should().BeFalse();
            graph.Installer.Registry.ReadIds().Should().BeEmpty();
            var notification = graph.Notifications.Notifications.Single(row => row.SourceId == preview.Id);
            notification.Severity.Should().Be(AppNotificationSeverity.Warning);
            notification.CanDismiss.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that preference edits after reservation cannot change the release of an already queued preview.
        /// </summary>
        [Fact]
        public async Task Queued_installation_keeps_its_own_source_selection()
        {
            using var graph = new TestServiceGraph();
            RegisterRepository(graph, Manifest());
            RegisterDownload(graph, "release", Manifest());
            var preview = await graph.Updates.FindRepositoryModuleAsync("owner/repo");
            preview.Update.SelectedReleaseTag = "v1";
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var preceding = graph.Queue.EnqueueAsync("preceding", _ => release.Task, kind: DownloadQueueKind.Modules);
            var pending = graph.Updates.InstallCatalogModuleAsync(preview, new InlineProgress<string>(_ => { }));
            try
            {
                await TestWait.UntilAsync(() => graph.Installer.IsInstalling(preview.Id),
                    because: "the root must have a reserved snapshot before its draft is edited");
                preview.Update.SelectedReleaseTag = "missing-tag";
                release.SetResult(true);
                await pending;
                var installed = (await graph.Installer.DiscoverInstalledModulesAsync()).Single();
                installed.Update.InstalledReleaseTag.Should().Be("v1");
            }
            finally
            {
                release.TrySetResult(true);
                await preceding;
                await pending;
            }
        }

        /// <summary>
        /// Verifies real HTTP redirects and streaming downloads without sending a configured GitHub token off-host.
        /// </summary>
        [Fact]
        public async Task Redirected_downloads_follow_the_response_without_authorizing_external_hosts()
        {
            using var graph = new TestServiceGraph();
            graph.AppData.Data.GitHub.PersonalAccessToken = "private-test-token";
            using var server = LoopbackHttpServer.Start();
            string? authorization = "not requested";
            server.Map("redirect", context =>
            {
                context.Response.Redirect(new Uri(server.BaseUri, "payload").AbsoluteUri);
                return Task.CompletedTask;
            });
            server.Map("payload", async context =>
            {
                authorization = context.Request.Headers["Authorization"];
                await context.Response.OutputStream.WriteAsync("redirected payload"u8.ToArray());
            });
            var client = new GitHubUpdateClient(graph.RateLimits, graph.GitHubAccounts, new HttpClient(), graph.IconCache);
            var destination = Path.Combine(graph.Root.Root, "download.zip");
            await client.DownloadFileAsync(new Uri(server.BaseUri, "redirect").AbsoluteUri, destination);
            File.ReadAllText(destination).Should().Be("redirected payload");
            authorization.Should().BeNull();
            server.Requests.Should().HaveCount(2);
        }


        // Fixtures

        /// <summary>
        /// Builds a runtime-free manifest with optional release asset preferences.
        /// </summary>
        private static string Manifest(bool asset = false) => JsonSerializer.Serialize(new
        {
            id = "remote",
            name = "Remote module",
            version = "1.0",
            update = asset ? new { assetName = "module.zip" } : null
        });

        /// <summary>
        /// Registers repository metadata and the exact default-branch root file response.
        /// </summary>
        private static void RegisterRepository(TestServiceGraph graph, string json)
        {
            graph.GitHubHttp.OnJson("https://api.github.com/repos/owner/repo",
                """{"full_name":"owner/repo","default_branch":"develop"}""");
            graph.GitHubHttp.OnJson("https://api.github.com/repos/owner/repo/contents/ASLM_Module.json?ref=develop",
                JsonSerializer.Serialize(new
                {
                    type = "file",
                    path = "ASLM_Module.json",
                    size = Encoding.UTF8.GetByteCount(json),
                    encoding = "base64",
                    content = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                }));
        }

        /// <summary>
        /// Registers actual ZIP payloads for branch, source release and binary asset installation.
        /// </summary>
        private static string RegisterDownload(TestServiceGraph graph, string source, string json)
        {
            var url = source == "asset" ? "https://api.github.com/repos/owner/repo/releases/assets/7"
                : "https://api.github.com/repos/owner/repo/zipball/" + (source == "branch" ? "develop" : "v1");
            var releases = source == "branch" ? "[]" : JsonSerializer.Serialize(new[]
            {
                new
                {
                    tag_name = "v1",
                    zipball_url = url,
                    assets = new[] { new { name = "module.zip", url, browser_download_url = "https://github.com/private.zip" } }
                }
            });
            graph.GitHubHttp.OnJson("https://api.github.com/repos/owner/repo/releases*", releases);
            graph.GitHubHttp.OnJson("https://api.github.com/repos/owner/repo/branches*",
                """[{"name":"alphabetical-first","commit":{"sha":"aaa"}},{"name":"develop","commit":{"sha":"bbb"}}]""");
            var zip = Path.Combine(graph.Root.Root, Guid.NewGuid().ToString("N") + ".zip");
            TestArchives.WriteZip(zip, new("source/ASLM_Module.json", json), new("source/payload.txt", "payload"));
            graph.GitHubHttp.OnBytes(url, File.ReadAllBytes(zip));
            return url;
        }
    }
}
