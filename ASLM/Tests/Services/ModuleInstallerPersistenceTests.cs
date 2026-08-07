// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

/// <summary>
/// Covers manifest persistence behavior used by multi-step module launches.
/// </summary>
[Collection("ModuleManifestDiscovery")]
public sealed class ModuleInstallerPersistenceTests
{
    /// <summary>
    /// Verifies that checkpoint saves persist state without rebuilding module-backed UI.
    /// </summary>
    [Fact]
    public async Task SaveConfigAsync_can_persist_without_raising_modules_changed()
    {
        using var layout = new AslmFileSystemLayout();
        var moduleDir = Path.Combine(layout.ModulesDir, "persistence-module");
        var manifestPath = Path.Combine(moduleDir, ModuleManifestDiscovery.ManifestFileName);
        Directory.CreateDirectory(moduleDir);

        var module = ModuleConfigBuilder.Create(
            configure: config =>
            {
                config.Id = "persistence-module";
                config.SourcePath = manifestPath;
                config.Status.Installed = true;
                config.Status.FirstRunCompleted = true;
            });
        var installer = new ModuleInstaller(null!, null!, null!);
        var changeCount = 0;
        installer.ModulesChanged += (_, _) => changeCount++;

        await installer.SaveConfigAsync(module, raiseModulesChanged: false);

        changeCount.Should().Be(0);
        var saved = await installer.LoadModuleConfig(manifestPath);
        saved.Should().NotBeNull();
        saved!.Status.Installed.Should().BeTrue();
        saved.Status.FirstRunCompleted.Should().BeTrue();

        module.Status.Enabled = true;
        await installer.SaveConfigAsync(module);

        changeCount.Should().Be(1);
    }

    /// <summary>
    /// Verifies that a short manifest read lock does not abort a state transition.
    /// </summary>
    [Fact]
    public async Task SaveConfigAsync_retries_transient_manifest_sharing_violations()
    {
        using var layout = new AslmFileSystemLayout();
        var moduleDir = Path.Combine(layout.ModulesDir, "locked-module");
        var manifestPath = Path.Combine(moduleDir, ModuleManifestDiscovery.ManifestFileName);
        Directory.CreateDirectory(moduleDir);

        var module = ModuleConfigBuilder.Create(
            configure: config =>
            {
                config.Id = "locked-module";
                config.SourcePath = manifestPath;
            });
        var installer = new ModuleInstaller(null!, null!, null!);
        await installer.SaveConfigAsync(module, raiseModulesChanged: false);

        module.Status.Installed = true;
        module.Status.FirstRunCompleted = true;
        var readLock = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var saveTask = installer.SaveConfigAsync(module, raiseModulesChanged: false);

        await Task.Delay(75);
        readLock.Dispose();
        await saveTask;

        var saved = await installer.LoadModuleConfig(manifestPath);
        saved.Should().NotBeNull();
        saved!.Status.Installed.Should().BeTrue();
        saved.Status.FirstRunCompleted.Should().BeTrue();
    }
}
