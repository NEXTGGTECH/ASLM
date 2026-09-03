// Copyright NEXTGGTECH. Apache License 2.0.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

/// <summary>
/// Verifies exact module and source-author trust resolution.
/// </summary>
[Collection("ModuleManifestDiscovery")]
public sealed class ModuleTrustServiceTests
{
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Verifies the exact official ASLM Chat rule still resolves successfully.
    /// </summary>
    [Fact]
    public void Resolve_returns_official_for_catalog_module()
    {
        var service = CreateService();
        var module = ModuleConfigBuilder.Create(
            id: "aslm-chat",
            configure: config => config.Source.Repo = "NEXTGGTECH/ASLM-Chat");

        service.Resolve(module).Should().Be(ModuleTrustLevel.Official);
    }

    /// <summary>
    /// Verifies every canonical GitHub repository owned by a trusted author is official.
    /// </summary>
    [Fact]
    public void Resolve_returns_official_for_trusted_github_author()
    {
        var service = CreateService();
        var module = ModuleConfigBuilder.Create(
            id: "another-module",
            configure: config => config.Source.Repo = "NEXTGGTECH/Another-Module");

        service.Resolve(module).Should().Be(ModuleTrustLevel.Official);
    }

    /// <summary>
    /// Verifies source and repository comparisons normalize harmless casing and whitespace.
    /// </summary>
    [Fact]
    public void Resolve_normalizes_trusted_source_identity()
    {
        var service = CreateService();
        var module = ModuleConfigBuilder.Create(
            id: "another-module",
            configure: config =>
            {
                config.Source.Type = " GitHub ";
                config.Source.Repo = " NEXTGGTECH/Another-Module ";
            });

        service.Resolve(module).Should().Be(ModuleTrustLevel.Official);
    }

    /// <summary>
    /// Verifies a trusted repository declaration cannot bypass its required source provider.
    /// </summary>
    [Fact]
    public void Resolve_rejects_trusted_repo_from_another_source()
    {
        var service = CreateService();
        var module = ModuleConfigBuilder.Create(
            id: "aslm-chat",
            configure: config =>
            {
                config.Source.Type = "custom";
                config.Source.Repo = "NEXTGGTECH/ASLM-Chat";
            });

        service.Resolve(module).Should().Be(ModuleTrustLevel.Unreviewed);
    }

    /// <summary>
    /// Verifies similar owner names do not match a trusted GitHub author.
    /// </summary>
    [Fact]
    public void Resolve_rejects_lookalike_github_author()
    {
        var service = CreateService();
        var module = ModuleConfigBuilder.Create(
            id: "another-module",
            configure: config => config.Source.Repo = "NEXTGGTECH-evil/Another-Module");

        service.Resolve(module).Should().Be(ModuleTrustLevel.Unreviewed);
    }

    /// <summary>
    /// Verifies malformed or wildcard repository declarations fail closed.
    /// </summary>
    [Theory]
    [InlineData("NEXTGGTECH/Module/Extra")]
    [InlineData("https://github.com/NEXTGGTECH/Module")]
    [InlineData("/NEXTGGTECH/Module/")]
    [InlineData("NEXTGGTECH/*")]
    public void Resolve_rejects_noncanonical_github_repository(string repo)
    {
        var service = CreateService();
        var module = ModuleConfigBuilder.Create(
            id: "another-module",
            configure: config => config.Source.Repo = repo);

        service.Resolve(module).Should().Be(ModuleTrustLevel.Unreviewed);
    }

    /// <summary>
    /// Verifies modules outside every exact and author rule remain unreviewed.
    /// </summary>
    [Fact]
    public void Resolve_returns_unreviewed_for_unknown_module()
    {
        var service = CreateService();
        var module = ModuleConfigBuilder.Create(id: "unknown-module");

        service.Resolve(module).Should().Be(ModuleTrustLevel.Unreviewed);
    }

    /// <summary>
    /// Verifies reviewed rules without a source provider are discarded.
    /// </summary>
    [Fact]
    public void Payload_rejects_rule_without_source()
    {
        var payload = new SignedReviewedModulesPayload
        {
            Modules =
            [
                new ReviewedModuleTrustEntry
                {
                    Id = "reviewed-module",
                    Repo = "Community/Reviewed-Module"
                }
            ]
        };

        payload.Normalize();

        payload.Modules.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a signed cache restores an exact module rule with its source provider.
    /// </summary>
    [Fact]
    public async Task Initialize_restores_exact_module_rule()
    {
        using var layout = new AslmFileSystemLayout();
        var payload = new SignedReviewedModulesPayload
        {
            FileVersion = 1,
            IssuedAt = "2026-09-03T00:00:00Z",
            Modules =
            [
                new ReviewedModuleTrustEntry
                {
                    Source = "github",
                    Id = "reviewed-module",
                    Repo = "Community/Reviewed-Module"
                }
            ]
        };
        WriteSignedTrustCache(layout, payload);

        var service = CreateService();
        await service.InitializeAsync();
        var module = ModuleConfigBuilder.Create(
            id: "reviewed-module",
            configure: config => config.Source.Repo = "Community/Reviewed-Module");

        service.Resolve(module).Should().Be(ModuleTrustLevel.CommunityReviewed);
    }

    /// <summary>
    /// Verifies a signed author rule trusts all canonical repositories owned by that account.
    /// </summary>
    [Fact]
    public async Task Initialize_restores_author_rule()
    {
        using var layout = new AslmFileSystemLayout();
        var payload = new SignedReviewedModulesPayload
        {
            FileVersion = 1,
            IssuedAt = "2026-09-03T00:00:00Z",
            Modules =
            [
                new ReviewedModuleTrustEntry
                {
                    Source = "github",
                    Repo = "Community/*"
                }
            ]
        };
        WriteSignedTrustCache(layout, payload);

        var service = CreateService();
        await service.InitializeAsync();
        var module = ModuleConfigBuilder.Create(
            id: "community-tool",
            configure: config => config.Source.Repo = "Community/Tool");

        service.Resolve(module).Should().Be(ModuleTrustLevel.CommunityReviewed);
    }

    /// <summary>
    /// Verifies reviewed author rules also require their declared source provider.
    /// </summary>
    [Fact]
    public async Task Initialize_rejects_reviewed_author_from_another_source()
    {
        using var layout = new AslmFileSystemLayout();
        var payload = new SignedReviewedModulesPayload
        {
            FileVersion = 1,
            IssuedAt = "2026-09-03T00:00:00Z",
            Modules =
            [
                new ReviewedModuleTrustEntry
                {
                    Source = "github",
                    Repo = "Community/*"
                }
            ]
        };
        WriteSignedTrustCache(layout, payload);

        var service = CreateService();
        await service.InitializeAsync();
        var module = ModuleConfigBuilder.Create(
            id: "community-tool",
            configure: config =>
            {
                config.Source.Type = "custom";
                config.Source.Repo = "Community/Tool";
            });

        service.Resolve(module).Should().Be(ModuleTrustLevel.Unreviewed);
    }

    /// <summary>
    /// Creates a trust service with the standard test logger.
    /// </summary>
    private static ModuleTrustService CreateService() =>
        new(TestLoggerFactory.Create<ModuleTrustService>());

    /// <summary>
    /// Writes a source configuration and a correctly signed reviewed-list cache.
    /// </summary>
    private static void WriteSignedTrustCache(
        AslmFileSystemLayout layout,
        SignedReviewedModulesPayload payload)
    {
        payload.Normalize();

        using var rsa = RSA.Create(2048);
        var canonicalJson = JsonSerializer.Serialize(payload.ToUnsignedBody(), CanonicalJsonOptions);
        payload.Signature = Convert.ToBase64String(rsa.SignData(
            Encoding.UTF8.GetBytes(canonicalJson),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        // The cache is accepted only when its detached signature matches this configured key.
        var sourceConfig = new ModuleTrustSourceConfig
        {
            PublicKeyBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())
        };
        File.WriteAllText(
            Path.Combine(layout.DataAppDir, "ASLM_ModuleTrustSource.json"),
            JsonSerializer.Serialize(sourceConfig, CacheJsonOptions));

        var cache = new ReviewedModulesCacheDocument
        {
            FetchedAt = "2026-09-03T00:00:00Z",
            Payload = payload,
            Signature = payload.Signature
        };
        File.WriteAllText(
            Path.Combine(layout.DataAppDir, "ASLM_ReviewedModules.cache.json"),
            JsonSerializer.Serialize(cache, CacheJsonOptions));
    }

}
