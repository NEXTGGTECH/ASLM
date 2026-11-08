// Copyright NEXTGGTECH. Apache License 2.0.

using System.Security.Cryptography;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies execution of whitelisted bridge install actions against their managed target
    /// buckets, including checksum enforcement and the path sandbox.
    /// </summary>
    public sealed class DownloadInstallerTests
    {
        // Download action

        /// <summary>
        /// Verifies that a <c>download_file</c> action with a target reference downloads the payload, passes the
        /// checksum check, saves the file below the declared managed target and logs the download and the saved path.
        /// </summary>
        [Fact]
        public async Task Download_file_with_targetRef_saves_verified_payload_into_managed_bucket()
        {
            // Arrange: a bridge module, a fake download for the payload and a manifest with the payload's checksum.
            using var graph = new TestServiceGraph();
            var module = CreateBridgeModule(graph);
            var payload = "model data"u8.ToArray();
            graph.DownloadHttp.OnBytes("https://cdn.test/model.bin", payload);
            var manifest = Manifest("model", new ModuleDownloadInstallAction
            {
                Type = "download_file",
                Title = "Model",
                Url = "https://cdn.test/model.bin",
                TargetRef = "models-target",
                RelativePath = "vendor/model.bin",
                Sha256 = await Sha256OfAsync(payload)
            });
            var log = new List<string>();

            // Act: execute the manifest.
            await graph.Downloads.ExecuteManifestAsync(
                module, manifest, "resource:model", new Progress<string>(log.Add), CancellationToken.None);

            // Assert: the file lies in the models bucket under the relative path and the log names both steps.
            var saved = Path.Combine(graph.Root.Root, "Models", "downloads", "bridge", "vendor", "model.bin");
            File.ReadAllText(saved).Should().Be("model data");
            log.Should().Contain(s => s.Contains("Downloading https://cdn.test/model.bin"));
            log.Should().Contain($"Saved to {saved}");
        }

        /// <summary>
        /// Verifies that a downloaded payload whose SHA-256 differs from the declared checksum fails the manifest.
        /// </summary>
        [Fact]
        public async Task Download_file_with_wrong_checksum_fails_the_manifest()
        {
            using var graph = new TestServiceGraph();
            var module = CreateBridgeModule(graph);
            graph.DownloadHttp.OnBytes("https://cdn.test/bad.bin", "actual"u8.ToArray());
            var manifest = Manifest("bad", new ModuleDownloadInstallAction
            {
                Type = "download_file",
                Url = "https://cdn.test/bad.bin",
                TargetRef = "models-target",
                Sha256 = await Sha256OfAsync("expected"u8.ToArray())
            });

            var act = async () => await graph.Downloads.ExecuteManifestAsync(
                module, manifest, "resource:bad", null, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Checksum mismatch*");
        }


        // Archive extraction

        /// <summary>
        /// Verifies that a zip downloaded as an artifact is extracted by an <c>extract_zip</c> action into the
        /// declared target directory, keeping the nested folder structure.
        /// </summary>
        [Fact]
        public async Task Downloaded_artifact_extracts_into_declared_target_with_traversal_guard()
        {
            // Arrange: a zip served as a download, a download action that stores it as an artifact and an extract action.
            using var graph = new TestServiceGraph();
            var module = CreateBridgeModule(graph);
            var zipPath = Path.Combine(graph.Root.Root, "payload.zip");
            TestArchives.WriteZip(zipPath,
                new TestArchives.Entry("tool/run.sh", "#!/bin/sh"),
                new TestArchives.Entry("tool/data/config.ini", "[x]"));
            graph.DownloadHttp.OnBytes("https://cdn.test/tool.zip", await File.ReadAllBytesAsync(zipPath));
            var manifest = Manifest("tool", new ModuleDownloadInstallAction
            {
                Type = "download_file",
                Url = "https://cdn.test/tool.zip",
                ArtifactId = "tool-zip"
            }, new ModuleDownloadInstallAction
            {
                Type = "extract_zip",
                SourceArtifactId = "tool-zip",
                TargetRef = "tools-target"
            });

            // Act.
            await graph.Downloads.ExecuteManifestAsync(module, manifest, "resource:tool", null, CancellationToken.None);

            // Assert: both archive files exist below the tools bucket.
            File.Exists(Path.Combine(graph.Root.Root, "Tools", "tool", "run.sh")).Should().BeTrue();
            File.Exists(Path.Combine(graph.Root.Root, "Tools", "tool", "data", "config.ini")).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that an <c>extract_zip</c> action fails with the "escapes the destination directory" error when an
        /// archive entry would be written outside the destination directory, and that no <c>escape.txt</c> file
        /// exists inside the tools bucket afterwards.
        /// </summary>
        [Fact]
        public async Task Extract_zip_rejects_entries_escaping_the_destination()
        {
            // Arrange: a zip with a path-traversal entry, served as an artifact that the extract action unpacks.
            using var graph = new TestServiceGraph();
            var module = CreateBridgeModule(graph);
            var zipPath = Path.Combine(graph.Root.Root, "evil.zip");
            TestArchives.WriteZip(zipPath, new TestArchives.Entry("../escape.txt", "stolen"));
            graph.DownloadHttp.OnBytes("https://cdn.test/evil.zip", await File.ReadAllBytesAsync(zipPath));
            var manifest = Manifest("evil", new ModuleDownloadInstallAction
            {
                Type = "download_file",
                Url = "https://cdn.test/evil.zip",
                ArtifactId = "evil-zip"
            }, new ModuleDownloadInstallAction
            {
                Type = "extract_zip",
                SourceArtifactId = "evil-zip",
                TargetRef = "tools-target"
            });

            // Act and assert: extraction throws the escape error and no escape.txt exists in the tools bucket.
            var act = async () => await graph.Downloads.ExecuteManifestAsync(
                module, manifest, "resource:evil", null, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*escapes the destination directory*");
            File.Exists(Path.Combine(graph.Root.Root, "Tools", "escape.txt")).Should().BeFalse();
        }


        // Manifest validation

        /// <summary>
        /// Verifies that an action type outside the supported whitelist fails the whole manifest.
        /// </summary>
        [Fact]
        public async Task Unknown_action_type_fails_the_whole_manifest()
        {
            using var graph = new TestServiceGraph();
            var module = CreateBridgeModule(graph);
            var manifest = Manifest("weird", new ModuleDownloadInstallAction
            {
                Type = "run_command",
                Url = "https://cdn.test/x"
            });

            var act = async () => await graph.Downloads.ExecuteManifestAsync(
                module, manifest, "resource:weird", null, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Unsupported download install action*");
        }

        /// <summary>
        /// Verifies that a <c>download_file</c> action fails when it declares no URL and, separately, when it declares
        /// neither a target reference nor an artifact id to store the file.
        /// </summary>
        [Fact]
        public async Task Download_file_without_url_or_artifact_destination_fails()
        {
            using var graph = new TestServiceGraph();
            var module = CreateBridgeModule(graph);

            var noUrl = async () => await graph.Downloads.ExecuteManifestAsync(
                module, Manifest("a", new ModuleDownloadInstallAction { Type = "download_file" }),
                "resource:a", null, CancellationToken.None);
            await noUrl.Should().ThrowAsync<InvalidOperationException>().WithMessage("*missing 'url'*");

            var noDestination = async () => await graph.Downloads.ExecuteManifestAsync(
                module, Manifest("b", new ModuleDownloadInstallAction { Type = "download_file", Url = "https://cdn.test/b" }),
                "resource:b", null, CancellationToken.None);
            await noDestination.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*targetRef or artifactId*");
        }


        // Path sandbox

        /// <summary>
        /// Verifies that a target reference resolves only when the module declares it and its root is one of the
        /// supported managed buckets.
        /// </summary>
        [Fact]
        public void ResolveTargetDirectory_only_allows_declared_targets_and_supported_buckets()
        {
            using var graph = new TestServiceGraph();
            var module = CreateBridgeModule(graph);

            graph.Downloads.ResolveTargetDirectory(module, "models-target")
                .Should().Be(Path.Combine(graph.Root.Root, "Models", "downloads", "bridge"));

            var undeclared = () => graph.Downloads.ResolveTargetDirectory(module, "undeclared");
            undeclared.Should().Throw<InvalidOperationException>().WithMessage("*not declared*");

            var hostile = CreateBridgeModule(graph, configure: bridge =>
                bridge.Targets["system32"] = new ModuleDownloadBridgeTarget { Root = "system32", Relative = "evil" });
            var badRoot = () => graph.Downloads.ResolveTargetDirectory(hostile, "system32");
            badRoot.Should().Throw<InvalidOperationException>().WithMessage("*Unsupported target root*");
        }

        /// <summary>
        /// Verifies that relative paths resolve below the root only when they stay inside it, that traversal paths
        /// are rejected and that an absolute path is always rejected.
        /// </summary>
        [Theory]
        [InlineData("nested/file.txt", true)]
        [InlineData("../escape.txt", false)]
        [InlineData("a/../../escape.txt", false)]
        public void ResolveChildPath_blocks_traversal_and_absolute_paths(string relative, bool allowed)
        {
            var root = Path.Combine(Path.GetTempPath(), "ASLM-Tests", "sandbox-" + Guid.NewGuid().ToString("N"));

            try
            {
                if (allowed)
                {
                    var resolved = DownloadInstaller.ResolveChildPath(root, relative);
                    resolved.Should().StartWith(root);
                }
                else
                {
                    var act = () => DownloadInstaller.ResolveChildPath(root, relative);
                    act.Should().Throw<InvalidOperationException>();
                }

                var absolute = Path.Combine(root, "file.txt");
                var actAbsolute = () => DownloadInstaller.ResolveChildPath(root, absolute);
                actAbsolute.Should().Throw<InvalidOperationException>().WithMessage("*Absolute install paths*");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }


        // Helpers

        /// <summary>
        /// Creates a module with a downloads bridge that declares a models target and a tools target; the optional
        /// callback can add further targets.
        /// </summary>
        /// <param name="graph">Service graph whose isolated root hosts the module manifest.</param>
        /// <param name="configure">Optional callback that adjusts the bridge before it is returned.</param>
        /// <returns>A module with a unique id and a ready downloads bridge.</returns>
        internal static ModuleConfig CreateBridgeModule(TestServiceGraph graph, Action<ModuleDownloadsBridge>? configure = null)
        {
            var module = ModuleConfigBuilder.Create(
                id: $"bridge-{Guid.NewGuid():N}",
                configure: config => config.SourcePath = Path.Combine(graph.Root.ModulesDir, config.Id, "ASLM_Module.json"));
            module.DownloadsBridge = new ModuleDownloadsBridge
            {
                Targets = new Dictionary<string, ModuleDownloadBridgeTarget>(StringComparer.OrdinalIgnoreCase)
                {
                    ["models-target"] = new() { Root = "models", Relative = "downloads/bridge" },
                    ["tools-target"] = new() { Root = "tools", Relative = "" }
                }
            };
            configure?.Invoke(module.DownloadsBridge);
            return module;
        }

        /// <summary>
        /// Creates an install manifest for one resource key that runs the given actions in order.
        /// </summary>
        private static ModuleDownloadInstallManifest Manifest(string resourceKey, params ModuleDownloadInstallAction[] actions) => new()
        {
            ResourceKey = resourceKey,
            Title = resourceKey,
            Actions = [.. actions]
        };

        /// <summary>
        /// Computes the upper-case hexadecimal SHA-256 hash of a byte array.
        /// </summary>
        private static async Task<string> Sha256OfAsync(byte[] payload)
        {
            await using var stream = new MemoryStream(payload);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream));
        }
    }
}
