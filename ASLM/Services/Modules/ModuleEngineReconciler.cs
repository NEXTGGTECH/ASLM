// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Installs missing module engine dependencies and reinstalls module-provided engines
    /// when their declarative manifest changes.
    /// </summary>
    public sealed class ModuleEngineReconciler
    {
        private readonly EngineInstaller _engineInstaller;

        /// <summary>
        /// Creates a reconciler backed by the shared engine installation service.
        /// </summary>
        public ModuleEngineReconciler(EngineInstaller engineInstaller)
        {
            _engineInstaller = engineInstaller;
        }

        /// <summary>
        /// Installs required engines and refreshes changed module-provided definitions.
        /// </summary>
        public async Task ReconcileRequiredEnginesAsync(
            ModuleConfig module,
            IProgress<string> log,
            IProgress<DownloadProgress>? downloadProgress = null,
            CancellationToken ct = default)
        {
            // Start with direct dependencies that must exist before module setup.
            var targetIds = module.Dependencies.Engines
                .Select(dependency => dependency.Id?.Trim())
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Module installation/update may have replaced ASLM_Module.json since the previous lookup.
            _engineInstaller.InvalidateCache();
            var available = _engineInstaller.DiscoverEngines();

            // An update must also reconcile an already-installed engine supplied by this module,
            // even when it is no longer a direct dependency of the provider itself.
            foreach (var installedDefinition in available.Where(engine =>
                         engine.IsModuleProvided &&
                         engine.Status.Installed &&
                         string.Equals(engine.OwnerModuleId, module.Id, StringComparison.OrdinalIgnoreCase)))
            {
                targetIds.Add(installedDefinition.Id);
            }

            if (targetIds.Count == 0)
            {
                return;
            }

            // Resolve support and fingerprint state before deciding whether installation is needed.
            foreach (var engineId in targetIds)
            {
                ct.ThrowIfCancellationRequested();
                var engine = available.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, engineId, StringComparison.OrdinalIgnoreCase));
                if (engine == null)
                {
                    throw new InvalidOperationException(
                        $"Required engine '{engineId}' was not found in Engines/ or ASLM_Module.json.");
                }

                engine.ResolveForPlatform(PlatformInfo.OsKey, PlatformInfo.ArchKey);
                if (!engine.IsSupportedOnCurrentPlatform)
                {
                    throw new PlatformNotSupportedException(
                        $"Engine '{engine.Name}' does not support {PlatformInfo.PlatformKey}.");
                }

                var definitionHash = EngineManifestFingerprint.Compute(engine);
                var manifestChanged = engine.IsModuleProvided &&
                    engine.Status.Installed &&
                    !string.Equals(
                        engine.Status.InstalledManifestHash,
                        definitionHash,
                        StringComparison.OrdinalIgnoreCase);

                if (engine.Status.Installed && !manifestChanged)
                {
                    log.Report($"[OK] Engine '{engine.Name}' already installed.");
                    continue;
                }

                if (manifestChanged)
                {
                    log.Report($"Engine manifest changed for '{engine.Name}', reinstalling...");
                }
                else
                {
                    log.Report($"Installing required engine '{engine.Name}'...");
                }

                await _engineInstaller.InstallAsync(engine, log, downloadProgress, ct);
            }

            _engineInstaller.InvalidateCache();
        }
    }
}
