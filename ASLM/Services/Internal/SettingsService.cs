// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using Debug = System.Diagnostics.Debug;
using ASLM.Models;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Couples one setting with the runtime value loaded for the current refresh pass.
    /// </summary>
    public sealed record LoadedSetting(ModuleSetting Setting, object? Value);

    /// <summary>
    /// Captures the initial effective value used to detect real user changes across UI rebuilds.
    /// </summary>
    public sealed record SettingBaseline(string DisplayValue, bool UseCustomValue);

    /// <summary>
    /// Stores the initial ASLM values loaded for the current page session.
    /// </summary>
    public sealed record AslmBaseline(string UserName, string PortStart, bool ApiServerEnabled);

    /// <summary>
    /// Stores the initial console preferences loaded for the current page session.
    /// </summary>
    public sealed record ConsoleBaseline(bool SidebarVisible, bool ShowCompletedProcesses, bool ShowIndividualModuleConsoles);

    /// <summary>
    /// Stores the initial update settings loaded for the current page session.
    /// </summary>
    public sealed record UpdateBaseline(
        bool CheckEnabled,
        bool AutoUpdateEnabled,
        string AppChannel,
        string ModuleDefaultChannel);

    /// <summary>
    /// Snapshot of editable ASLM drafts derived from persisted app data and runtime state.
    /// </summary>
    public sealed record AslmDraftSnapshot(
        string UserName,
        string PortStart,
        bool ApiServerEnabled,
        bool RestoreLastPage,
        ConsoleBaseline ConsoleBaseline,
        UpdateBaseline UpdateBaseline);

    /// <summary>
    /// Summarizes the modules touched during one save operation.
    /// </summary>
    public sealed record ModuleSaveResult(HashSet<ModuleConfig> TouchedModules, List<string> DeferredSettings);

    /// <summary>
    /// Result of validating port draft strings.
    /// </summary>
    public readonly struct PortParseResult
    {
        public bool Success { get; init; }
        public int ModulesStart { get; init; }
        public string ErrorMessage { get; init; }
    }

    /// <summary>
    /// Module discovery, setting load/save, validation, and other non-UI settings work for <see cref="Pages.SettingsView"/>.
    /// </summary>
    public sealed class SettingsService
    {
        private readonly EngineInstaller _engineInstaller;
        private readonly ModuleInstaller _moduleInstaller;
        private readonly ModuleRunner _moduleRunner;

        // Constructor

        /// <summary>
        /// Creates the settings service with module discovery, persistence, and runtime dependencies.
        /// </summary>
        public SettingsService(
            EngineInstaller engineInstaller,
            ModuleInstaller moduleInstaller,
            ModuleRunner moduleRunner)
        {
            _engineInstaller = engineInstaller;
            _moduleInstaller = moduleInstaller;
            _moduleRunner = moduleRunner;
        }


        /// <summary>
        /// Stable key used to remember whether live runtime values were already loaded for a module.
        /// </summary>
        public static string GetModuleRuntimeKey(ModuleConfig module) => module.SourcePath;

        /// <summary>
        /// Returns whether one module should appear in the settings sidebar.
        /// </summary>
        public static bool IsModuleEligibleForSettings(ModuleConfig module) =>
            module.Status.Installed &&
            module.Status.FirstRunCompleted &&
            module.Settings.Any(ShouldDisplaySetting);

        // Discovery

        /// <summary>
        /// Discovers installed modules and returns their configuration snapshots for the settings page.
        /// </summary>
        public Task<List<ModuleConfig>> DiscoverModulesAsync() => _moduleInstaller.DiscoverModulesAsync();


        // Port validation

        /// <summary>
        /// Validates the port draft values and returns parsed integers when valid.
        /// </summary>
        public static PortParseResult TryParsePortStart(string draft)
        {
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


        // Profile & update validation

        /// <summary>
        /// Validates one display name draft and returns trimmed value.
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
        /// Reads and validates update settings from a draft snapshot.
        /// </summary>
        public static bool TryValidateAndBuildUpdateSettings(UpdateBaseline draft, out AppUpdateSettings settings, out string errorMessage)
        {
            settings = new AppUpdateSettings();
            errorMessage = string.Empty;

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
        public static string BuildSaveMessage(bool hasAslmChanges, bool hasModuleChanges, List<string> deferredSettings)
        {
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


        // ASLM drafts & persistence

        /// <summary>
        /// Builds editable ASLM draft values from persisted app data and runtime API-state.
        /// </summary>
        public static AslmDraftSnapshot BuildAslmDraftSnapshot(AppDataStore appData, bool apiServerEnabled)
        {
            appData.Data.Consoles.Normalize();
            appData.Data.Navigation.Normalize();
            appData.Data.Updates.Normalize();

            return new AslmDraftSnapshot(
                appData.Data.User.Name ?? string.Empty,
                appData.Data.Ports.ModulesStart.ToString(CultureInfo.InvariantCulture),
                apiServerEnabled,
                appData.Data.Navigation.RestoreLastPage,
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
        /// Writes ASLM and update drafts to persisted app data.
        /// </summary>
        public static void ApplyDraftsToAppData(
            AppDataStore appData,
            string userName,
            int modulesStart,
            ConsoleBaseline consoleDraft,
            AppUpdateSettings updateSettings,
            bool restoreLastPage,
            bool legalAutoAcceptUpdates)
        {
            appData.Data.User.Name = userName;
            if (appData.Data.User.AccountMode == AppAccountMode.Local)
            {
                appData.Data.User.LocalName = userName;
            }

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

            appData.Data.Navigation.RestoreLastPage = restoreLastPage;
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
        public static (string PortStart, bool ApiServerEnabled, ConsoleBaseline ConsoleDefaults, bool RestoreLastPage, bool LegalAutoAcceptUpdates) BuildDefaultAslmDrafts()
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
        /// Checks whether non-account ASLM settings differ from baseline.
        /// </summary>
        public static bool HasUnsavedLegalChanges(bool legalAutoAcceptUpdates, bool legalBaseline) =>
            legalAutoAcceptUpdates != legalBaseline;

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


        // Runtime & module control

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
            MacAppRelauncher.StartDetachedRelaunch();
            return;
#else
            var root = ResolveInstallRoot();
            var launcherPath = Path.Combine(root, "ASLM.exe");
            if (!File.Exists(launcherPath))
            {
                throw new FileNotFoundException("ASLM launcher was not found.", launcherPath);
            }

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
        /// </summary>
        public static string ResolveInstallRoot()
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var parentRoot = Directory.GetParent(appDir)?.FullName;
            var candidateRoots = new[]
            {
                parentRoot,
                appDir
            };

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
        /// Returns whether categories and explicit dependencies may affect this user setting.
        /// ASLM-managed engine/path/data/models settings remain on their legacy rendering path.
        /// </summary>
        public static bool IsSettingsMetadataEligible(ModuleSetting setting) =>
            ShouldDisplaySetting(setting) &&
            setting.NormalizedType is not ("engine" or "path" or "data" or "models");

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


        // Module validation & changes

        /// <summary>
        /// Validates detached setting drafts before they are committed to a module manifest.
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


        // Module load & save

        /// <summary>
        /// Loads visible values into a detached module draft and accepts the resulting baseline.
        /// </summary>
        public async Task LoadModuleDraftAsync(
            ModuleSettingsDraft moduleDraft,
            bool reloadRuntimeValues)
        {
            var editableDrafts = moduleDraft.Settings
                .Where(static draft => ShouldDisplaySetting(draft.Setting))
                .ToList();
            if (editableDrafts.Count == 0)
            {
                return;
            }

            // Runtime getters execute only for explicitly requested refreshes; other passes use manifest fallbacks.
            var loaded = reloadRuntimeValues
                ? await Task.WhenAll(editableDrafts.Select(draft => LoadSettingValueAsync(moduleDraft.Module, draft.Setting)))
                : editableDrafts
                    .Select(draft => new LoadedSetting(
                        draft.Setting,
                        GetFallbackValue(moduleDraft.Module, draft.Setting)))
                    .ToArray();

            ApplyLoadedSettingsToDraft(moduleDraft, loaded);
        }

        /// <summary>
        /// Applies one runtime load batch to detached setting drafts without touching the manifest model.
        /// </summary>
        public void ApplyLoadedSettingsToDraft(
            ModuleSettingsDraft moduleDraft,
            IEnumerable<LoadedSetting> loadedSettings)
        {
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

            await Task.Run(() => _moduleInstaller.SaveConfigAsync(moduleDraft.Module));
            return new ModuleSaveResult(touchedModules, deferredSettings);
        }

        /// <summary>
        /// Loads one setting value from runtime get-exec or manifest fallback.
        /// </summary>
        public async Task<LoadedSetting> LoadSettingValueAsync(ModuleConfig module, ModuleSetting setting)
        {
            if (IsAutoDetectedAslmEngine(setting))
            {
                return new LoadedSetting(setting, IsAslmEngineInstalled(setting.Key));
            }

            var fallbackValue = GetFallbackValue(module, setting);
            if (setting.IsAutomaticallyManaged && !setting.UseCustomValue)
            {
                return new LoadedSetting(setting, fallbackValue);
            }

            if (string.IsNullOrWhiteSpace(setting.GetExec) || !File.Exists(module.SourcePath))
            {
                return new LoadedSetting(setting, fallbackValue);
            }

            try
            {
                var rawValue = await Task.Run(() => _moduleRunner.ExecuteSettingCommandAsync(module, setting, false, null, CancellationToken.None));
                return rawValue == null
                    ? new LoadedSetting(setting, fallbackValue)
                    : new LoadedSetting(setting, setting.ParseSerializedValue(rawValue));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read setting '{setting.Key}' for module '{module.Name}': {ex.Message}");
                return new LoadedSetting(setting, fallbackValue);
            }
        }


        // Setting values & baselines

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
            // Boolean and engine toggles.
            else if (type is "bool" or "engine")
            {
                if (!bool.TryParse(rawValue, out _) && !string.Equals(rawValue, "true", StringComparison.OrdinalIgnoreCase) && !string.Equals(rawValue, "false", StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = $"Invalid boolean value for '{setting.Name}'.";
                    return false;
                }
            }
            // Structured JSON payloads when the value looks like JSON.
            else if (type is "json" or "object" or "array")
            {
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
