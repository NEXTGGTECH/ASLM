// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using Debug = System.Diagnostics.Debug;
using ASLM.Models;

namespace ASLM.Services.Internal
{
    // Module setting values

    /// <summary>
    /// Couples one setting with the runtime value loaded for the current refresh pass.
    /// </summary>
    /// <param name="Setting">Setting definition the value belongs to.</param>
    /// <param name="Value">Value loaded from the running module or a fallback, or null when there is none.</param>
    public sealed record LoadedSetting(ModuleSetting Setting, object? Value);

    /// <summary>
    /// Captures the initial effective value used to detect real user changes across UI rebuilds.
    /// </summary>
    /// <param name="DisplayValue">Effective value formatted for display, which is what dirty checks compare.</param>
    /// <param name="UseCustomValue">Whether a managed setting uses its custom value instead of the host value.</param>
    public sealed record SettingBaseline(string DisplayValue, bool UseCustomValue);


    // ASLM setting records

    /// <summary>
    /// Stores the initial ASLM values loaded for the current page session.
    /// </summary>
    /// <param name="UserName">Display name as loaded.</param>
    /// <param name="PortStart">First module port as loaded, kept as the draft text.</param>
    /// <param name="ApiServerEnabled">Whether the local API server was enabled when loaded.</param>
    public sealed record AslmBaseline(string UserName, string PortStart, bool ApiServerEnabled);

    /// <summary>
    /// Stores the initial console preferences loaded for the current page session.
    /// </summary>
    /// <param name="SidebarVisible">Whether the console sidebar is shown.</param>
    /// <param name="ShowCompletedProcesses">Whether finished processes stay listed in the console.</param>
    /// <param name="ShowIndividualModuleConsoles">Whether each module gets its own console.</param>
    public sealed record ConsoleBaseline(bool SidebarVisible, bool ShowCompletedProcesses, bool ShowIndividualModuleConsoles);

    /// <summary>
    /// Stores the initial update settings loaded for the current page session.
    /// </summary>
    /// <param name="CheckEnabled">Whether ASLM checks for updates.</param>
    /// <param name="AutoUpdateEnabled">Whether found updates are installed automatically.</param>
    /// <param name="AppChannel">Release channel used to update the application.</param>
    /// <param name="ModuleDefaultChannel">Release channel used for modules that do not choose their own.</param>
    public sealed record UpdateBaseline(
        bool CheckEnabled,
        bool AutoUpdateEnabled,
        string AppChannel,
        string ModuleDefaultChannel);

    /// <summary>
    /// Snapshot of editable ASLM drafts derived from persisted app data and runtime state.
    /// </summary>
    /// <param name="UserName">Persisted display name.</param>
    /// <param name="PortStart">Persisted first module port as draft text.</param>
    /// <param name="ApiServerEnabled">Whether the local API server is currently enabled.</param>
    /// <param name="RestoreLastPage">Whether ASLM restores the last shell page on startup.</param>
    /// <param name="DisableHomePage">Whether the home page is disabled.</param>
    /// <param name="ConsoleBaseline">Persisted console preferences.</param>
    /// <param name="UpdateBaseline">Persisted update preferences.</param>
    public sealed record AslmDraftSnapshot(
        string UserName,
        string PortStart,
        bool ApiServerEnabled,
        bool RestoreLastPage,
        bool DisableHomePage,
        ConsoleBaseline ConsoleBaseline,
        UpdateBaseline UpdateBaseline);


    // Operation results

    /// <summary>
    /// Summarizes the modules touched during one save operation.
    /// </summary>
    /// <param name="TouchedModules">Modules whose settings were committed and persisted by the save.</param>
    /// <param name="DeferredSettings">Labels of settings whose setter could not run; retried on next module start.</param>
    public sealed record ModuleSaveResult(HashSet<ModuleConfig> TouchedModules, List<string> DeferredSettings);

    /// <summary>
    /// Result of validating port draft strings.
    /// </summary>
    public readonly struct PortParseResult
    {
        /// <summary>
        /// Whether the draft is a valid port.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Parsed first module port; only meaningful when <see cref="Success"/> is true.
        /// </summary>
        public int ModulesStart { get; init; }

        /// <summary>
        /// User-facing explanation of why the draft is invalid; empty when <see cref="Success"/> is true.
        /// </summary>
        public string ErrorMessage { get; init; }
    }


    // Settings service

    /// <summary>
    /// Module discovery, setting load/save, validation, and other non-UI settings work for <see cref="Pages.SettingsView"/>.
    /// </summary>
    public sealed class SettingsService
    {
        // Fields

        /// <summary>
        /// Engine installer used to detect which engines ASLM manages itself.
        /// </summary>
        private readonly EngineInstaller _engineInstaller;

        /// <summary>
        /// Module installer used for discovery, manifest loading and manifest persistence.
        /// </summary>
        private readonly ModuleInstaller _moduleInstaller;

        /// <summary>
        /// Module runner used to run setting get/set commands and to restart modules.
        /// </summary>
        private readonly ModuleRunner _moduleRunner;

        /// <summary>
        /// Backing field of <see cref="ModuleCatalogRevision"/>; incremented atomically on every module change.
        /// </summary>
        private long _moduleCatalogRevision;


        // Initialization

        /// <summary>
        /// Creates the settings service with module discovery, persistence, and runtime dependencies.
        /// </summary>
        /// <param name="engineInstaller">Installer that knows the ASLM-managed engines.</param>
        /// <param name="moduleInstaller">
        /// Installer that discovers and persists modules; its change notifications bump the catalog revision.
        /// </param>
        /// <param name="moduleRunner">Runner that executes setting commands and restarts modules.</param>
        public SettingsService(
            EngineInstaller engineInstaller,
            ModuleInstaller moduleInstaller,
            ModuleRunner moduleRunner)
        {
            _engineInstaller = engineInstaller;
            _moduleInstaller = moduleInstaller!;
            _moduleRunner = moduleRunner;

            // Any installer-level module change invalidates manifest snapshots through the catalog revision.
            if (moduleInstaller != null)
            {
                moduleInstaller.ModulesChanged += OnModulesChanged;
            }
        }


        // Module identity and eligibility

        /// <summary>
        /// Stable key used to coordinate runtime work for one installed module instance.
        /// </summary>
        public static string GetModuleRuntimeKey(ModuleConfig module) => module.SourcePath;

        /// <summary>
        /// Gets the in-process revision used to avoid reparsing unchanged module manifests.
        /// </summary>
        public long ModuleCatalogRevision => Interlocked.Read(ref _moduleCatalogRevision);

        /// <summary>
        /// Returns whether one module should appear in the settings sidebar:
        /// it is installed, finished its first run and has at least one displayable setting.
        /// </summary>
        public static bool IsModuleEligibleForSettings(ModuleConfig module) =>
            module.Status.Installed &&
            module.Status.FirstRunCompleted &&
            module.Settings.Any(ShouldDisplaySetting);


        // Discovery

        /// <summary>
        /// Discovers installed modules and returns their configuration snapshots for the settings page.
        /// </summary>
        public Task<List<ModuleConfig>> DiscoverModulesAsync() => _moduleInstaller.DiscoverInstalledModulesAsync();

        /// <summary>
        /// Invalidates settings manifest snapshots after an installer-level module change.
        /// </summary>
        private void OnModulesChanged(object? sender, EventArgs e) =>
            Interlocked.Increment(ref _moduleCatalogRevision);


        // Port validation

        /// <summary>
        /// Validates the port draft values and returns parsed integers when valid.
        /// Accepted ports are 1024 to 65000 inclusive.
        /// </summary>
        public static PortParseResult TryParsePortStart(string draft)
        {
            // Reject text that is not an integer or lies outside the allowed unprivileged port range.
            if (!int.TryParse(draft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var modulesStart) ||
                modulesStart < 1024 ||
                modulesStart > 65000)
            {
                return new PortParseResult
                {
                    Success = false,
                    ErrorMessage = "Module start port must be between 1024 and 65000."
                };
            }

            return new PortParseResult
            {
                Success = true,
                ModulesStart = modulesStart,
                ErrorMessage = string.Empty
            };
        }


        // Profile and update validation

        /// <summary>
        /// Validates one display name draft and returns the trimmed value; blank names are rejected.
        /// </summary>
        public static bool TryValidateDisplayName(string? draft, out string normalizedName, out string errorMessage)
        {
            normalizedName = draft?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(normalizedName))
            {
                errorMessage = string.Empty;
                return true;
            }

            errorMessage = "Display name cannot be empty.";
            return false;
        }

        /// <summary>
        /// Builds normalized update settings from a draft snapshot. The module default mode is always <c>release</c>.
        /// </summary>
        public static bool TryValidateAndBuildUpdateSettings(UpdateBaseline draft, out AppUpdateSettings settings, out string errorMessage)
        {
            settings = new AppUpdateSettings();
            errorMessage = string.Empty;

            // Copy the draft values and let Normalize coerce channels and other fields to supported values.
            settings.CheckEnabled = draft.CheckEnabled;
            settings.AutoUpdateEnabled = draft.AutoUpdateEnabled;
            settings.AppChannel = draft.AppChannel;
            settings.ModuleDefaultMode = "release";
            settings.ModuleDefaultChannel = draft.ModuleDefaultChannel;
            settings.Normalize();
            return true;
        }


        // Save messaging

        /// <summary>
        /// Builds the save confirmation message, including deferred runtime updates when present.
        /// </summary>
        /// <param name="hasAslmChanges">
        /// True when built-in ASLM settings (account, ports, consoles, updates) or personalization
        /// (appearance, custom themes) were persisted in this save operation.
        /// </param>
        /// <param name="hasModuleChanges">True when module settings were persisted in this save operation.</param>
        /// <param name="deferredSettings">Labels of settings whose setter could not run; at most five are listed.</param>
        public static string BuildSaveMessage(bool hasAslmChanges, bool hasModuleChanges, List<string> deferredSettings)
        {
            // Nothing was saved, everything was applied, or some module settings are deferred and listed.
            if (!hasAslmChanges && !hasModuleChanges)
            {
                return "No changes to save.";
            }

            if (deferredSettings.Count == 0)
            {
                return "Settings saved and applied.";
            }

            var preview = string.Join("\n", deferredSettings.Take(5));
            return $"Settings saved. Some module settings could not be applied immediately and will be retried on next module start.\n\n{preview}";
        }


        // ASLM drafts and persistence

        /// <summary>
        /// Builds editable ASLM draft values from persisted app data and runtime API-state.
        /// </summary>
        public static AslmDraftSnapshot BuildAslmDraftSnapshot(AppDataStore appData, bool apiServerEnabled)
        {
            // Normalize first so the drafts start from valid, coerced values.
            appData.Data.Consoles.Normalize();
            appData.Data.Navigation.Normalize();
            appData.Data.Updates.Normalize();

            return new AslmDraftSnapshot(
                appData.Data.User.Name ?? string.Empty,
                appData.Data.Ports.ModulesStart.ToString(CultureInfo.InvariantCulture),
                apiServerEnabled,
                appData.Data.Navigation.RestoreLastPage,
                appData.Data.Navigation.DisableHomePage,
                new ConsoleBaseline(
                    appData.Data.Consoles.SidebarVisible,
                    appData.Data.Consoles.ShowCompletedProcesses,
                    appData.Data.Consoles.ShowIndividualModuleConsoles),
                new UpdateBaseline(
                    appData.Data.Updates.CheckEnabled,
                    appData.Data.Updates.AutoUpdateEnabled,
                    appData.Data.Updates.AppChannel,
                    appData.Data.Updates.ModuleDefaultChannel));
        }

        /// <summary>
        /// Writes ASLM and update drafts to persisted app data. The in-memory data is updated only;
        /// the caller persists it.
        /// </summary>
        public static void ApplyDraftsToAppData(
            AppDataStore appData,
            string userName,
            int modulesStart,
            ConsoleBaseline consoleDraft,
            AppUpdateSettings updateSettings,
            bool restoreLastPage,
            bool disableHomePage,
            bool legalAutoAcceptUpdates)
        {
            // The display name is only editable in builds with ASLM accounts; local accounts keep it in LocalName too.
            if (SunriseService.IsEnabled)
            {
                appData.Data.User.Name = userName;
                if (appData.Data.User.AccountMode == AppAccountMode.Local)
                {
                    appData.Data.User.LocalName = userName;
                }
            }

            // Ports, consoles and updates are copied field by field; updates are normalized after the copy.
            appData.Data.Ports.ModulesStart = modulesStart;
            appData.Data.Consoles.SidebarVisible = consoleDraft.SidebarVisible;
            appData.Data.Consoles.ShowCompletedProcesses = consoleDraft.ShowCompletedProcesses;
            appData.Data.Consoles.ShowIndividualModuleConsoles = consoleDraft.ShowIndividualModuleConsoles;
            appData.Data.Updates.CheckEnabled = updateSettings.CheckEnabled;
            appData.Data.Updates.AutoUpdateEnabled = updateSettings.AutoUpdateEnabled;
            appData.Data.Updates.AutoCheckPeriodHours = updateSettings.AutoCheckPeriodHours;
            appData.Data.Updates.AppChannel = updateSettings.AppChannel;
            appData.Data.Updates.ModuleDefaultMode = updateSettings.ModuleDefaultMode;
            appData.Data.Updates.ModuleDefaultChannel = updateSettings.ModuleDefaultChannel;
            appData.Data.Updates.Normalize();

            // Shell navigation and legal preferences follow, each normalized after its values are set.
            appData.Data.Navigation.RestoreLastPage = restoreLastPage;
            appData.Data.Navigation.DisableHomePage = disableHomePage;
            appData.Data.Navigation.Normalize();

            appData.Data.Legal.AutoAcceptUpdates = legalAutoAcceptUpdates;
            appData.Data.Legal.Normalize();
        }

        /// <summary>
        /// Creates the default update baseline used by reset actions in settings UI.
        /// </summary>
        public static UpdateBaseline BuildDefaultUpdateBaseline()
        {
            var defaults = new AppUpdateSettings();
            defaults.Normalize();
            return new UpdateBaseline(
                defaults.CheckEnabled,
                defaults.AutoUpdateEnabled,
                defaults.AppChannel,
                defaults.ModuleDefaultChannel);
        }

        /// <summary>
        /// Builds ASLM defaults for ports, API, console, navigation, and legal sections.
        /// </summary>
        public static (string PortStart, bool ApiServerEnabled, ConsoleBaseline ConsoleDefaults, bool RestoreLastPage, bool DisableHomePage, bool LegalAutoAcceptUpdates) BuildDefaultAslmDrafts()
        {
            var defaultPorts = new AppPortConfig();
            var defaultConsoles = new AppConsoleConfig();
            var defaultNavigation = new AppNavigationConfig();
            var defaultLegal = new AppLegalConfig();
            defaultLegal.Normalize();
            return (
                defaultPorts.ModulesStart.ToString(CultureInfo.InvariantCulture),
                new AppApiConfig().ServerEnabled,
                new ConsoleBaseline(
                    defaultConsoles.SidebarVisible,
                    defaultConsoles.ShowCompletedProcesses,
                    defaultConsoles.ShowIndividualModuleConsoles),
                defaultNavigation.RestoreLastPage,
                defaultNavigation.DisableHomePage,
                defaultLegal.AutoAcceptUpdates);
        }


        // Unsaved change detection

        /// <summary>
        /// Checks whether account display-name draft differs from baseline.
        /// </summary>
        public static bool HasUnsavedAccountChanges(string userName, AslmBaseline baseline) =>
            !string.Equals(userName, baseline.UserName, StringComparison.Ordinal);

        /// <summary>
        /// Checks whether ports draft differs from baseline.
        /// </summary>
        public static bool HasUnsavedPortChanges(string portStart, AslmBaseline baseline) =>
            !string.Equals(portStart, baseline.PortStart, StringComparison.Ordinal);

        /// <summary>
        /// Checks whether API-enabled draft differs from baseline.
        /// </summary>
        public static bool HasUnsavedApiServerChanges(bool apiServerEnabled, AslmBaseline baseline) =>
            apiServerEnabled != baseline.ApiServerEnabled;

        /// <summary>
        /// Checks whether console draft differs from baseline.
        /// </summary>
        public static bool HasUnsavedConsoleChanges(ConsoleBaseline draft, ConsoleBaseline baseline) =>
            draft != baseline;

        /// <summary>
        /// Checks whether update draft differs from baseline.
        /// </summary>
        public static bool HasUnsavedUpdateChanges(UpdateBaseline draft, UpdateBaseline baseline) =>
            draft.CheckEnabled != baseline.CheckEnabled ||
            draft.AutoUpdateEnabled != baseline.AutoUpdateEnabled ||
            !string.Equals(draft.AppChannel, baseline.AppChannel, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(draft.ModuleDefaultChannel, baseline.ModuleDefaultChannel, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Checks whether the legal auto-accept draft differs from baseline.
        /// </summary>
        public static bool HasUnsavedLegalChanges(bool legalAutoAcceptUpdates, bool legalBaseline) =>
            legalAutoAcceptUpdates != legalBaseline;

        /// <summary>
        /// Checks whether any ASLM setting except the account display name differs from its baseline:
        /// ports, API server, consoles, updates and legal preferences.
        /// </summary>
        public static bool HasUnsavedAslmSettingsChanges(
            string portStart,
            bool apiServerEnabled,
            ConsoleBaseline consoleDraft,
            UpdateBaseline updateDraft,
            bool legalAutoAcceptUpdates,
            AslmBaseline aslmBaseline,
            ConsoleBaseline consoleBaseline,
            UpdateBaseline updateBaseline,
            bool legalBaseline) =>
            HasUnsavedPortChanges(portStart, aslmBaseline) ||
            HasUnsavedApiServerChanges(apiServerEnabled, aslmBaseline) ||
            HasUnsavedConsoleChanges(consoleDraft, consoleBaseline) ||
            HasUnsavedUpdateChanges(updateDraft, updateBaseline) ||
            HasUnsavedLegalChanges(legalAutoAcceptUpdates, legalBaseline);


        // Runtime and module control

        /// <summary>
        /// Returns the effective runtime value for one module setting without reloading from disk.
        /// </summary>
        public object? GetResolvedSettingValue(ModuleConfig module, ModuleSetting setting) =>
            _moduleRunner.GetResolvedSettingValue(module, setting);

        /// <summary>
        /// Stops every running module process before applying settings that require a clean slate.
        /// </summary>
        public Task StopAllModulesAsync() => Task.Run(() => _moduleRunner.StopAllModulesAsync());

        /// <summary>
        /// Restarts one module using the same flow as the module management page.
        /// </summary>
        public async Task RestartModuleAsync(ModuleConfig module)
        {
            // Reload manifest so restart uses the latest on-disk settings and commands.
            var freshConfig = await Task.Run(() => _moduleInstaller.LoadModuleConfig(module.SourcePath));
            if (freshConfig != null)
            {
                module.Settings = freshConfig.Settings;
                module.Commands = freshConfig.Commands;
            }

            // Stop the module, give its process a moment to exit, then start it again without awaiting the run.
            await Task.Run(() => _moduleRunner.StopModuleAsync(module.SourcePath));
            await Task.Delay(1000);

            var restartLog = new Progress<string>(message => Debug.WriteLine($"[Restart] {message}"));
            _ = Task.Run(() => _moduleRunner.ExecuteRunAsync(module, restartLog, CancellationToken.None));
        }


        // Application restart

        /// <summary>
        /// Starts the launcher so it can relaunch ASLM after the current process exits.
        /// </summary>
        public static void StartLauncherForApplicationRestart()
        {
#if MACCATALYST
            // Mac Catalyst and Linux use their own detached relauncher helpers.
            MacAppRelauncher.StartDetachedRelaunch();
            return;
#elif LINUX
            LinuxAppRelauncher.StartDetachedRelaunch();
            return;
#else
            // Windows relaunches through the ASLM.exe launcher that sits in the install root.
            var root = ResolveInstallRoot();
            var launcherPath = Path.Combine(root, "ASLM.exe");
            if (!File.Exists(launcherPath))
            {
                throw new FileNotFoundException("ASLM launcher was not found.", launcherPath);
            }

            // The launcher waits for this process to exit before it starts ASLM again.
            var arguments = new[]
            {
                "--wait-process",
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            };

            if (DetachedProcessStarter.TryStartBreakawayProcess(launcherPath, root, arguments))
            {
                return;
            }

            // Fallback when breakaway process creation is unavailable.
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = root,
                UseShellExecute = false
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = System.Diagnostics.Process.Start(startInfo);

            if (process == null)
            {
                throw new InvalidOperationException("ASLM launcher did not start.");
            }
#endif
        }

        /// <summary>
        /// Starts the launcher so it can detect the prepared update after the current app exits.
        /// </summary>
        public static void StartLauncherForSelfUpdate() => StartLauncherForApplicationRestart();

        /// <summary>
        /// Resolves the ASLM install root used for launcher restarts and self-updates.
        /// A directory that holds a pending update marker wins; otherwise the parent of the app folder is used.
        /// </summary>
        public static string ResolveInstallRoot()
        {
            // Candidates are the parent of the app folder and the app folder itself.
            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var parentRoot = Directory.GetParent(appDir)?.FullName;
            var candidateRoots = new[]
            {
                parentRoot,
                appDir
            };

            // The root that already contains a prepared update is where the launcher must look for it.
            foreach (var root in candidateRoots.Where(static root => !string.IsNullOrWhiteSpace(root)))
            {
                var pendingPath = Path.Combine(root!, ".aslm-update", "pending.json");
                if (File.Exists(pendingPath))
                {
                    return root!;
                }
            }

            return parentRoot ?? appDir;
        }


        // Module display rules

        /// <summary>
        /// Restores detached module drafts without mutating the manifest before save.
        /// </summary>
        public static void ResetModuleToDefaults(ModuleSettingsDraft moduleDraft)
        {
            foreach (var draft in moduleDraft.Settings.Where(static draft => ShouldDisplaySetting(draft.Setting)))
            {
                draft.ResetToDefault();
            }

            RefreshModuleDraftVisibility(moduleDraft);
        }

        /// <summary>
        /// Filters out settings that should never be shown in the UI editor.
        /// </summary>
        public static bool ShouldDisplaySetting(ModuleSetting setting) =>
            !string.Equals(setting.NormalizedType, "port", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(setting.NormalizedType, "theme", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(setting.NormalizedType, "locale", StringComparison.OrdinalIgnoreCase) &&
            !setting.IsHostKey;

        /// <summary>
        /// Returns whether categories and explicit dependencies may affect this visible setting.
        /// </summary>
        public static bool IsSettingsMetadataEligible(ModuleSetting setting) =>
            ShouldDisplaySetting(setting);

        /// <summary>
        /// Evaluates whether a setting should currently be visible based on its controlling toggle.
        /// </summary>
        public static bool ShouldRenderSetting(
            ModuleSetting setting,
            IReadOnlyList<ModuleSetting> allSettings,
            IReadOnlyDictionary<string, object?> valuesByKey)
        {
            return ShouldRenderSettingCore(
                setting,
                allSettings,
                valuesByKey,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Updates render visibility for every setting from one consistent draft snapshot.
        /// </summary>
        public static void RefreshModuleDraftVisibility(ModuleSettingsDraft moduleDraft)
        {
            var definitions = moduleDraft.Settings
                .Where(static draft => ShouldDisplaySetting(draft.Setting))
                .Select(static draft => draft.Setting)
                .ToList();
            var valuesByKey = moduleDraft.BuildEffectiveValuesByKey();

            foreach (var draft in moduleDraft.Settings)
            {
                draft.SetVisibility(
                    ShouldDisplaySetting(draft.Setting) &&
                    ShouldRenderSetting(draft.Setting, definitions, valuesByKey));
            }
        }

        /// <summary>
        /// Evaluates explicit and legacy visibility rules while guarding recursive chains.
        /// </summary>
        private static bool ShouldRenderSettingCore(
            ModuleSetting setting,
            IReadOnlyList<ModuleSetting> allSettings,
            IReadOnlyDictionary<string, object?> valuesByKey,
            HashSet<string> visitStack)
        {
            // An explicit dependsOn rule shows the setting only while its boolean controller is on and visible itself.
            if (IsSettingsMetadataEligible(setting) && !string.IsNullOrWhiteSpace(setting.DependsOn))
            {
                // A malformed cycle must not make an arbitrary part of the settings page disappear.
                // The parser reports the authoring error; rendering remains fail-open.
                if (HasExplicitDependencyCycle(setting, allSettings))
                {
                    return true;
                }

                var explicitController = allSettings.FirstOrDefault(candidate =>
                    string.Equals(candidate.Key, setting.DependsOn, StringComparison.OrdinalIgnoreCase));

                // Invalid metadata is fail-open and is reported by ModuleManifestParser.
                if (explicitController == null ||
                    !IsSettingsMetadataEligible(explicitController) ||
                    explicitController.NormalizedType != "bool" ||
                    !visitStack.Add(setting.Key))
                {
                    return true;
                }

                // The controller is evaluated recursively; the visit stack guards against re-entering this setting.
                try
                {
                    if (!valuesByKey.TryGetValue(explicitController.Key, out var explicitValue) ||
                        !TryResolveBoolean(explicitValue, out var enabled))
                    {
                        return true;
                    }

                    return enabled && ShouldRenderSettingCore(
                        explicitController,
                        allSettings,
                        valuesByKey,
                        visitStack);
                }
                finally
                {
                    visitStack.Remove(setting.Key);
                }
            }

            // Legacy rule: a toggle whose key prefixes this setting's key (name grouping) controls its visibility.
            var controller = FindControllingSetting(setting, allSettings, valuesByKey);
            if (controller == null || !valuesByKey.TryGetValue(controller.Key, out var value))
            {
                return true;
            }

            return TryResolveBoolean(value, out var legacyEnabled) ? legacyEnabled : true;
        }

        /// <summary>
        /// Returns the trimmed description text shown under a setting title.
        /// </summary>
        public static string BuildSettingDescription(ModuleSetting setting) => setting.Description?.Trim() ?? string.Empty;

        /// <summary>
        /// Determines whether a setting should use the segmented active-engine selector.
        /// </summary>
        public static bool IsActiveEngineSelector(ModuleSetting setting) =>
            string.Equals(setting.Key, "llm-engine", StringComparison.OrdinalIgnoreCase) &&
            setting.AllowedValues is { Count: > 0 };

        /// <summary>
        /// Checks whether the current setting controls the visibility of any other setting.
        /// </summary>
        public static bool HasDependentSettings(ModuleConfig module, ModuleSetting setting) =>
            (setting.NormalizedType == "bool" &&
             IsSettingsMetadataEligible(setting) &&
             module.Settings.Any(other =>
                 IsSettingsMetadataEligible(other) &&
                 string.Equals(other.DependsOn, setting.Key, StringComparison.OrdinalIgnoreCase))) ||
            module.Settings.Any(other =>
                !string.Equals(other.Key, setting.Key, StringComparison.OrdinalIgnoreCase) &&
                IsGroupedUnder(setting.Key, other.Key) &&
                ShouldDisplaySetting(other));


        // Module validation and changes

        /// <summary>
        /// Validates detached setting drafts before they are committed to a module manifest.
        /// Read-only settings and host-managed values in automatic mode are skipped; the first error stops validation.
        /// </summary>
        public bool TryValidateModuleSettings(ModuleSettingsDraft moduleDraft, out string errorMessage)
        {
            errorMessage = string.Empty;

            foreach (var draft in moduleDraft.Settings.Where(static draft => ShouldDisplaySetting(draft.Setting)))
            {
                if (draft.IsReadOnly ||
                    draft.Setting.IsAutomaticallyManaged && !draft.UseCustomValue)
                {
                    continue;
                }

                if (!TryValidateSettingValue(draft.Setting, draft.Value, out errorMessage))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns whether a detached module draft differs from its accepted baseline.
        /// </summary>
        public static bool ModuleHasChangesComparedToBaseline(ModuleSettingsDraft moduleDraft) =>
            moduleDraft.HasChanges;


        // Module load and save

        /// <summary>
        /// Loads visible values into a detached module draft and accepts the resulting baseline.
        /// </summary>
        /// <param name="moduleDraft">Draft to fill.</param>
        /// <param name="reloadRuntimeValues">True to read values from the module; false to use fallback values.</param>
        public async Task LoadModuleDraftAsync(
            ModuleSettingsDraft moduleDraft,
            bool reloadRuntimeValues)
        {
            // Only settings that the editor can show are loaded.
            var editableDrafts = moduleDraft.Settings
                .Where(static draft => ShouldDisplaySetting(draft.Setting))
                .ToList();
            if (editableDrafts.Count == 0)
            {
                return;
            }

            // Runtime values are read one setting at a time; otherwise fallbacks avoid running any module command.
            IReadOnlyList<LoadedSetting> loaded;
            if (reloadRuntimeValues)
            {
                var runtimeValues = new List<LoadedSetting>(editableDrafts.Count);
                foreach (var draft in editableDrafts)
                {
                    // Keep one module ordered while moving each potentially expensive getter off the caller thread.
                    var runtimeValue = await Task.Run(
                        () => LoadSettingValueAsync(moduleDraft.Module, draft.Setting));
                    runtimeValues.Add(runtimeValue);
                }

                loaded = runtimeValues;
            }
            else
            {
                loaded = editableDrafts
                    .Select(draft => new LoadedSetting(
                        draft.Setting,
                        GetFallbackValue(moduleDraft.Module, draft.Setting)))
                    .ToArray();
            }

            ApplyLoadedSettingsToDraft(moduleDraft, loaded);
        }

        /// <summary>
        /// Applies one runtime load batch to detached setting drafts without touching the manifest model.
        /// </summary>
        public void ApplyLoadedSettingsToDraft(
            ModuleSettingsDraft moduleDraft,
            IEnumerable<LoadedSetting> loadedSettings)
        {
            // Managed settings also receive the host-resolved value; auto-detected ASLM engines load as read-only.
            foreach (var loaded in loadedSettings)
            {
                var draft = moduleDraft.GetSetting(loaded.Setting.Key);
                var automaticValue = loaded.Setting.IsAutomaticallyManaged
                    ? _moduleRunner.GetResolvedSettingValue(moduleDraft.Module, loaded.Setting)
                    : null;
                draft.LoadRuntimeValue(
                    loaded.Value,
                    automaticValue,
                    IsAutoDetectedAslmEngine(loaded.Setting));
            }
        }

        /// <summary>
        /// Commits changed detached drafts, applies runtime setters, and persists the module manifest.
        /// </summary>
        public async Task<ModuleSaveResult> SaveActiveModuleAsync(ModuleSettingsDraft moduleDraft)
        {
            // Collect only editable drafts that really changed; with none, there is nothing to commit or persist.
            var touchedModules = new HashSet<ModuleConfig>();
            var deferredSettings = new List<string>();
            var changedDrafts = moduleDraft.Settings
                .Where(static draft =>
                    ShouldDisplaySetting(draft.Setting) &&
                    !draft.IsReadOnly &&
                    draft.HasChanges)
                .ToList();

            if (changedDrafts.Count == 0)
            {
                return new ModuleSaveResult(touchedModules, deferredSettings);
            }

            // Commit every draft before command execution so injected settings form one consistent snapshot.
            moduleDraft.ApplyToModule();
            touchedModules.Add(moduleDraft.Module);

            // Apply runtime setters for changed settings; failures and unavailable manifests are deferred, not fatal.
            foreach (var draft in changedDrafts)
            {
                var setting = draft.Setting;
                if (string.IsNullOrWhiteSpace(setting.SetExec))
                {
                    continue;
                }

                if (!File.Exists(moduleDraft.Module.SourcePath))
                {
                    deferredSettings.Add($"{moduleDraft.Module.Name}: {setting.Name}");
                    continue;
                }

                try
                {
                    var displayValue = setting.FormatValueForDisplay(draft.EffectiveValue);
                    var applyResult = await Task.Run(() => _moduleRunner.ExecuteSettingCommandAsync(
                        moduleDraft.Module,
                        setting,
                        isSet: true,
                        newValue: displayValue,
                        CancellationToken.None));

                    if (applyResult == null)
                    {
                        deferredSettings.Add($"{moduleDraft.Module.Name}: {setting.Name}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"Failed to apply setting '{setting.Key}' for module '{moduleDraft.Module.Name}': {ex.Message}");
                    deferredSettings.Add($"{moduleDraft.Module.Name}: {setting.Name}");
                }
            }

            // Persist the manifest, then make the saved values the new baseline.
            await Task.Run(() => _moduleInstaller.SaveConfigAsync(moduleDraft.Module));
            moduleDraft.AcceptChanges();
            return new ModuleSaveResult(touchedModules, deferredSettings);
        }

        /// <summary>
        /// Loads one setting value from runtime get-exec or manifest fallback.
        /// Engine settings report installation state, and host-managed settings in automatic mode never run the command.
        /// </summary>
        public async Task<LoadedSetting> LoadSettingValueAsync(
            ModuleConfig module,
            ModuleSetting setting,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Settings that map to an ASLM engine are answered from the engine installation state.
            if (IsAutoDetectedAslmEngine(setting))
            {
                return new LoadedSetting(setting, IsAslmEngineInstalled(setting.Key));
            }

            // Without a command to run, or while ASLM manages the value, the fallback is the answer.
            var fallbackValue = GetFallbackValue(module, setting);
            if (setting.IsAutomaticallyManaged && !setting.UseCustomValue)
            {
                return new LoadedSetting(setting, fallbackValue);
            }

            if (string.IsNullOrWhiteSpace(setting.GetExec) || !File.Exists(module.SourcePath))
            {
                return new LoadedSetting(setting, fallbackValue);
            }

            // Ask the module for the live value; cancellation propagates, any other failure uses the fallback.
            try
            {
                var rawValue = await _moduleRunner.ExecuteSettingCommandAsync(
                    module,
                    setting,
                    false,
                    null,
                    cancellationToken).ConfigureAwait(false);
                return rawValue == null
                    ? new LoadedSetting(setting, fallbackValue)
                    : new LoadedSetting(setting, setting.ParseSerializedValue(rawValue));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read setting '{setting.Key}' for module '{module.Name}': {ex.Message}");
                return new LoadedSetting(setting, fallbackValue);
            }
        }


        // Fallback values

        /// <summary>
        /// Resolves the best available value when runtime loading is skipped or fails.
        /// </summary>
        public object? GetFallbackValue(ModuleConfig module, ModuleSetting setting) =>
            IsAutoDetectedAslmEngine(setting)
                ? IsAslmEngineInstalled(setting.Key)
                : setting.IsAutomaticallyManaged && !setting.UseCustomValue
                    ? _moduleRunner.GetResolvedSettingValue(module, setting) ?? setting.Value ?? setting.Default
                    : setting.Value ?? setting.Default;


        // Engine detection

        /// <summary>
        /// Detects whether an engine-style setting maps directly to an ASLM engine installation.
        /// </summary>
        public bool IsAutoDetectedAslmEngine(ModuleSetting setting)
        {
            if (!string.Equals(setting.NormalizedType, "engine", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return _engineInstaller
                .DiscoverEngines()
                .Any(engine => engine.Id.Equals(setting.Key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Checks whether the specified ASLM engine is currently installed on the system.
        /// </summary>
        public bool IsAslmEngineInstalled(string engineId) =>
            _engineInstaller.GetEngineConfig(engineId) != null;


        // Setting value validation

        /// <summary>
        /// Validates one setting value according to its declared manifest type.
        /// Empty values and booleans are always accepted; unknown types are not checked.
        /// </summary>
        public static bool TryValidateSettingValue(ModuleSetting setting, object? rawValueObj, out string errorMessage)
        {
            errorMessage = string.Empty;
            var rawValue = rawValueObj?.ToString();

            if (rawValueObj is bool || string.IsNullOrWhiteSpace(rawValue))
            {
                return true;
            }

            var type = setting.NormalizedType;

            // Numeric types.
            if (type is "int" or "integer" or "port")
            {
                if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    errorMessage = $"Invalid integer numeric value for '{setting.Name}'.";
                    return false;
                }
            }
            else if (type is "long")
            {
                if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    errorMessage = $"Invalid long integer numeric value for '{setting.Name}'.";
                    return false;
                }
            }
            else if (type is "float" or "double" or "number")
            {
                if (!double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _))
                {
                    errorMessage = $"Invalid numeric value for '{setting.Name}'.";
                    return false;
                }
            }
            else if (type is "bool" or "engine")
            {
                // Boolean and engine toggles.
                if (!bool.TryParse(rawValue, out _) && !string.Equals(rawValue, "true", StringComparison.OrdinalIgnoreCase) && !string.Equals(rawValue, "false", StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = $"Invalid boolean value for '{setting.Name}'.";
                    return false;
                }
            }
            else if (type is "json" or "object" or "array")
            {
                // Structured JSON payloads are parsed only when the value looks like JSON.
                var trimmed = rawValue!.Trim();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                {
                    try
                    {
                        using var jsonDocument = System.Text.Json.JsonDocument.Parse(trimmed);
                    }
                    catch
                    {
                        errorMessage = $"Invalid JSON payload for '{setting.Name}'.";
                        return false;
                    }
                }
            }

            return true;
        }


        // Setting visibility

        /// <summary>
        /// Finds the boolean toggle that controls whether <paramref name="setting"/> is visible.
        /// </summary>
        private static ModuleSetting? FindControllingSetting(
            ModuleSetting setting,
            IReadOnlyList<ModuleSetting> allSettings,
            IReadOnlyDictionary<string, object?> valuesByKey) =>
            allSettings
                .Where(candidate =>
                    !string.Equals(candidate.Key, setting.Key, StringComparison.OrdinalIgnoreCase) &&
                    IsVisibilityToggle(candidate, valuesByKey) &&
                    IsGroupedUnder(candidate.Key, setting.Key))
                .OrderByDescending(candidate => candidate.Key.Length)
                .FirstOrDefault();

        /// <summary>
        /// Returns whether <paramref name="childKey"/> is grouped under <paramref name="parentKey"/> by naming convention.
        /// </summary>
        private static bool IsGroupedUnder(string parentKey, string childKey) =>
            childKey.StartsWith(parentKey + "_", StringComparison.OrdinalIgnoreCase) ||
            childKey.StartsWith(parentKey + "-", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns whether <paramref name="setting"/> acts as a visibility toggle for dependent settings.
        /// </summary>
        private static bool IsVisibilityToggle(ModuleSetting setting, IReadOnlyDictionary<string, object?> valuesByKey)
        {
            if (!valuesByKey.TryGetValue(setting.Key, out var value))
            {
                return setting.NormalizedType is "bool" or "engine";
            }

            return value is bool;
        }

        /// <summary>
        /// Converts persisted boolean values without depending on their storage representation.
        /// </summary>
        private static bool TryResolveBoolean(object? value, out bool result)
        {
            if (value is bool boolValue)
            {
                result = boolValue;
                return true;
            }

            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result);
        }

        /// <summary>
        /// Detects malformed explicit dependency cycles so rendering can remain fail-open.
        /// </summary>
        private static bool HasExplicitDependencyCycle(
            ModuleSetting start,
            IReadOnlyList<ModuleSetting> allSettings)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = start;

            while (IsSettingsMetadataEligible(current) && !string.IsNullOrWhiteSpace(current.DependsOn))
            {
                if (!visited.Add(current.Key))
                {
                    return true;
                }

                var controller = allSettings.FirstOrDefault(candidate =>
                    string.Equals(candidate.Key, current.DependsOn, StringComparison.OrdinalIgnoreCase));
                if (controller == null ||
                    !IsSettingsMetadataEligible(controller) ||
                    controller.NormalizedType != "bool")
                {
                    return false;
                }

                current = controller;
            }

            return false;
        }
    }
}
