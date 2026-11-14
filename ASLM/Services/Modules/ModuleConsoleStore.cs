// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using ASLM.Models;
using ASLM.Localization;

namespace ASLM.Services.Modules
{
    // Console store

    /// <summary>
    /// Stores in-memory console sessions for modules and their spawned processes.
    /// Output is buffered per session with line and size limits, so the UI can show the console of a module
    /// at any time without owning the process streams. All state is guarded by a single lock.
    /// </summary>
    public sealed class ModuleConsoleStore
    {
        // Fields and constants

        /// <summary>
        /// Identifier of the per-module session that collects lifecycle messages.
        /// </summary>
        private const string OverviewSessionId = "overview";

        /// <summary>
        /// Identifier of the per-module session that collects installer and updater output.
        /// </summary>
        private const string MaintenanceSessionId = "maintenance";

        /// <summary>
        /// Maximum number of lines buffered per session; the oldest lines are dropped beyond it.
        /// </summary>
        private const int MaxLinesPerSession = 5000;

        /// <summary>
        /// Maximum number of lines returned for display.
        /// </summary>
        internal const int MaxDisplayedLines = 1000;

        /// <summary>
        /// Character budget of the text returned for display, 200 characters per displayed line on average; the oldest
        /// visible lines are dropped beyond it.
        /// </summary>
        private const int MaxDisplayedCharacters = 200000;

        /// <summary>
        /// Maximum length of one buffered line; longer lines are cut when they are stored.
        /// </summary>
        private const int MaxStoredLineLength = 4000;

        /// <summary>
        /// Maximum length of one line returned for display; longer lines are cut when they are shown.
        /// </summary>
        private const int MaxDisplayedLineLength = 1200;

        /// <summary>
        /// Delay that merges bursts of changes into a single <see cref="StateChanged"/> notification.
        /// </summary>
        private const int StateChangedDebounceMilliseconds = 75;

        /// <summary>
        /// Guards all mutable state of the store.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// Console state of every known module, keyed by module manifest path.
        /// </summary>
        private readonly Dictionary<string, ModuleConsoleModuleState> _modules = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Running maintenance activities per module manifest path; each activity is keyed by its own scope token.
        /// </summary>
        private readonly Dictionary<string, Dictionary<Guid, ModuleActivity>> _activities = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Last assigned line sequence number; orders lines across sessions and modules.
        /// </summary>
        private long _lineSequence;

        /// <summary>
        /// Non-zero while a debounced <see cref="StateChanged"/> notification is already queued.
        /// </summary>
        private int _stateChangeQueued;


        // Events

        /// <summary>
        /// Raised whenever the console store changes.
        /// </summary>
        public event EventHandler? StateChanged;


        // Maintenance activity

        /// <summary>
        /// Tracks transient operations independently of persisted installed/enabled flags.
        /// The returned scope removes the activity when disposed.
        /// </summary>
        internal IDisposable BeginActivity(string sourcePath, ModuleActivity activity)
        {
            var token = Guid.NewGuid();
            lock (_sync)
            {
                if (!_activities.TryGetValue(sourcePath, out var active))
                    _activities[sourcePath] = active = [];
                active[token] = activity;
            }

            RaiseStateChanged();
            return new ActivityScope(this, sourcePath, token);
        }

        /// <summary>
        /// Buffers installer/updater output even while no console view is open.
        /// Returns <paramref name="downstream"/> unchanged when it already is the maintenance log of this module;
        /// otherwise the returned reporter writes to the maintenance session and forwards every message downstream.
        /// In the first case <paramref name="reset"/> has no effect.
        /// </summary>
        /// <param name="module">Module whose maintenance session receives the output.</param>
        /// <param name="downstream">Optional reporter that also receives every message.</param>
        /// <param name="reset">True to start a fresh maintenance session even when one already exists.</param>
        internal IProgress<string> CreateMaintenanceLog(ModuleConfig module, IProgress<string>? downstream, bool reset)
        {
            // Nested maintenance steps pass the log along; wrapping it again would duplicate every line.
            if (downstream is MaintenanceProgress existing && ReferenceEquals(existing.Store, this) &&
                string.Equals(existing.SourcePath, module.SourcePath, StringComparison.OrdinalIgnoreCase))
                return downstream;
            lock (_sync)
            {
                EnsureModuleCore(module);
                var state = _modules[module.SourcePath];
                if (reset || !state.Sessions.ContainsKey(MaintenanceSessionId))
                    state.Sessions[MaintenanceSessionId] = new ModuleConsoleSessionState
                    {
                        Id = MaintenanceSessionId,
                        Title = L.Get(LocalizationKeys.ModuleUpdate_ActivityTitle_Activity),
                        Stage = "Maintenance",
                        StartedUtc = DateTimeOffset.UtcNow
                    };
            }

            RaiseStateChanged();
            return new MaintenanceProgress(this, module.SourcePath, downstream);
        }

        /// <summary>
        /// Returns the strongest running maintenance activity of a module, or null when none is running.
        /// Does not allocate console text, so module cards can poll it cheaply.
        /// </summary>
        internal ModuleActivity? GetActivity(string sourcePath)
        {
            lock (_sync)
                return _activities.TryGetValue(sourcePath, out var active) && active.Count > 0
                    ? active.Values.Max() : null;
        }

        /// <summary>
        /// Returns the maintenance activity, the running state of tracked processes, a key identifying the current
        /// maintenance session and the maintenance console text of a module in one consistent read.
        /// </summary>
        internal ModuleMaintenanceSnapshot GetMaintenanceSnapshot(string sourcePath)
        {
            lock (_sync)
            {
                ModuleActivity? activity = _activities.TryGetValue(sourcePath, out var active) && active.Count > 0
                    ? active.Values.Max() : null;
                _modules.TryGetValue(sourcePath, out var module);
                ModuleConsoleSessionState? session = null;
                module?.Sessions.TryGetValue(MaintenanceSessionId, out session);
                var running = module?.Sessions.Values.Any(item => item.IsTrackedProcess && item.IsRunning) == true;
                return new(activity, running, $"{sourcePath}|{session?.StartedUtc.Ticks}",
                    session?.Lines.Count > 0 ? string.Join(Environment.NewLine, BuildDisplayLines(session.Lines)) : string.Empty);
            }
        }


        // Nested types

        /// <summary>
        /// Progress reporter that appends every non-empty message to the maintenance session of one module
        /// and forwards all messages, empty ones included, to an optional downstream reporter.
        /// </summary>
        private sealed class MaintenanceProgress(ModuleConsoleStore store, string sourcePath, IProgress<string>? downstream) : IProgress<string>
        {
            /// <summary>
            /// Store that owns the maintenance session; used to recognize a log that belongs to the same store.
            /// </summary>
            internal ModuleConsoleStore Store => store;

            /// <summary>
            /// Manifest path of the module whose maintenance session receives the messages.
            /// </summary>
            internal string SourcePath => sourcePath;

            /// <summary>
            /// Appends the message to the maintenance session and forwards it downstream.
            /// </summary>
            public void Report(string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    lock (store._sync)
                    {
                        var module = store._modules[sourcePath];
                        store.AppendLineCore(module.Sessions[MaintenanceSessionId], value);
                        module.LastActivityUtc = DateTimeOffset.UtcNow;
                    }

                    store.RaiseStateChanged();
                }

                downstream?.Report(value);
            }
        }

        /// <summary>
        /// Scope of one running maintenance activity; disposing it ends the activity.
        /// </summary>
        private sealed class ActivityScope(ModuleConsoleStore store, string sourcePath, Guid token) : IDisposable
        {
            /// <summary>
            /// Removes the activity from the store and notifies listeners; repeated disposal does nothing.
            /// </summary>
            public void Dispose()
            {
                lock (store._sync)
                {
                    if (!store._activities.TryGetValue(sourcePath, out var active) || !active.Remove(token)) return;
                    if (active.Count == 0) store._activities.Remove(sourcePath);
                }

                store.RaiseStateChanged();
            }
        }


        // Module registration

        /// <summary>
        /// Ensures that all supplied modules exist in the console store.
        /// </summary>
        public void EnsureModules(IEnumerable<ModuleConfig> modules)
        {
            var changed = false;

            lock (_sync)
            {
                foreach (var module in modules)
                {
                    changed |= EnsureModuleCore(module);
                }
            }

            if (changed)
            {
                RaiseStateChanged();
            }
        }

        /// <summary>
        /// Ensures that one module exists in the console store and refreshes its metadata.
        /// </summary>
        public void EnsureModule(ModuleConfig module)
        {
            var changed = false;

            lock (_sync)
            {
                changed = EnsureModuleCore(module);
            }

            if (changed)
            {
                RaiseStateChanged();
            }
        }


        // Overview logging

        /// <summary>
        /// Appends one line to the shared overview console for a module.
        /// </summary>
        public void AppendOverviewLine(ModuleConfig module, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            lock (_sync)
            {
                EnsureModuleCore(module);
                var moduleState = _modules[module.SourcePath];
                AppendLineCore(GetOrCreateOverviewSessionCore(moduleState), message);
                moduleState.LastActivityUtc = DateTimeOffset.UtcNow;
            }

            RaiseStateChanged();
        }

        /// <summary>
        /// Appends one line to the shared overview console using the module source path.
        /// </summary>
        public void AppendOverviewLine(string moduleSourcePath, string message)
        {
            if (string.IsNullOrWhiteSpace(moduleSourcePath) || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var changed = false;

            lock (_sync)
            {
                if (_modules.TryGetValue(moduleSourcePath, out var moduleState))
                {
                    AppendLineCore(GetOrCreateOverviewSessionCore(moduleState), message);
                    moduleState.LastActivityUtc = DateTimeOffset.UtcNow;
                    changed = true;
                }
            }

            if (changed)
            {
                RaiseStateChanged();
            }
        }

        /// <summary>
        /// Updates the last known enabled state of a module inside the console store.
        /// </summary>
        public void UpdateModuleEnabledState(string moduleSourcePath, bool isEnabled)
        {
            if (string.IsNullOrWhiteSpace(moduleSourcePath))
            {
                return;
            }

            var changed = false;

            lock (_sync)
            {
                if (_modules.TryGetValue(moduleSourcePath, out var moduleState) &&
                    moduleState.IsEnabled != isEnabled)
                {
                    moduleState.IsEnabled = isEnabled;
                    moduleState.LastActivityUtc = DateTimeOffset.UtcNow;
                    changed = true;
                }
            }

            if (changed)
            {
                RaiseStateChanged();
            }
        }


        // Process sessions

        /// <summary>
        /// Creates a tracked console session for a started module process.
        /// </summary>
        /// <param name="module">Module that owns the process.</param>
        /// <param name="command">Manifest command the process was started for; supplies the title and description.</param>
        /// <param name="stage">Lifecycle stage shown for the session; <c>Command</c> when blank.</param>
        /// <param name="commandLine">Command line echoed as the first line of the session, when not blank.</param>
        /// <param name="process">Started process whose id the session records.</param>
        /// <param name="isTrackedProcess">True for long-running processes that count as running module processes.</param>
        /// <returns>A handle that identifies the new session for later output and completion calls.</returns>
        public ModuleConsoleSessionHandle StartProcessSession(
            ModuleConfig module,
            ModuleCommand command,
            string stage,
            string commandLine,
            Process process,
            bool isTrackedProcess)
        {
            ModuleConsoleSessionState sessionState;

            lock (_sync)
            {
                EnsureModuleCore(module);

                // Create the session; an unnamed command is titled by its executable.
                var moduleState = _modules[module.SourcePath];
                sessionState = new ModuleConsoleSessionState
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = string.IsNullOrWhiteSpace(command.Name)
                        ? GetFallbackTitle(command, process)
                        : command.Name,
                    CommandDescription = command.Description,
                    Stage = string.IsNullOrWhiteSpace(stage) ? "Command" : stage,
                    CommandLine = commandLine,
                    ProcessId = process.Id,
                    IsRunning = true,
                    IsTrackedProcess = isTrackedProcess,
                    StartedUtc = DateTimeOffset.UtcNow
                };

                // Register the session and echo the command line as its first output line.
                moduleState.Sessions[sessionState.Id] = sessionState;
                moduleState.LastActivityUtc = sessionState.StartedUtc;

                if (!string.IsNullOrWhiteSpace(commandLine))
                {
                    AppendLineCore(sessionState, commandLine);
                }
            }

            RaiseStateChanged();

            return new ModuleConsoleSessionHandle(module.SourcePath, sessionState.Id);
        }

        /// <summary>
        /// Appends one line to a process console session. Empty and whitespace-only lines are kept, so the output
        /// keeps its blank lines.
        /// </summary>
        public void AppendProcessLine(ModuleConsoleSessionHandle handle, string message)
        {
            WriteProcessLine(handle, stream: null, message, isComplete: true);
        }

        /// <summary>
        /// Shows the unfinished last line of one output stream of a process session. Every update rewrites the same
        /// console line, so a line that is still being written, such as a progress bar that redraws itself, stays one
        /// line.
        /// </summary>
        internal void UpdateProcessLine(ModuleConsoleSessionHandle handle, ProcessOutputStream stream, string text)
        {
            WriteProcessLine(handle, stream, text, isComplete: false);
        }

        /// <summary>
        /// Appends a finished line of one output stream of a process session; it replaces the unfinished line of that
        /// stream when one is shown.
        /// </summary>
        internal void CompleteProcessLine(ModuleConsoleSessionHandle handle, ProcessOutputStream stream, string text)
        {
            WriteProcessLine(handle, stream, text, isComplete: true);
        }

        /// <summary>
        /// Writes one line to a process console session. Unknown handles are ignored.
        /// </summary>
        /// <param name="handle">Session that receives the line.</param>
        /// <param name="stream">Output stream of the line, or null for a line outside the process streams.</param>
        /// <param name="text">Text of the line.</param>
        /// <param name="isComplete">True when the line is finished and is not rewritten any more.</param>
        private void WriteProcessLine(ModuleConsoleSessionHandle handle, ProcessOutputStream? stream, string text, bool isComplete)
        {
            var changed = false;

            lock (_sync)
            {
                if (TryGetSessionCore(handle, out var moduleState, out var sessionState))
                {
                    if (stream.HasValue && sessionState.OpenLines.TryGetValue(stream.Value, out var openLine))
                    {
                        // The stream already shows an unfinished line: rewrite it, and close it once it is finished.
                        SetLineTextCore(sessionState, openLine, text);
                        if (isComplete)
                        {
                            sessionState.OpenLines.Remove(stream.Value);
                        }
                    }
                    else
                    {
                        // Otherwise append a new line; an unfinished one stays open for the next rewrite of its stream.
                        var line = AppendLineCore(sessionState, text);
                        if (stream.HasValue && !isComplete)
                        {
                            sessionState.OpenLines[stream.Value] = line;
                        }
                    }

                    moduleState.LastActivityUtc = DateTimeOffset.UtcNow;
                    changed = true;
                }
            }

            if (changed)
            {
                RaiseStateChanged();
            }
        }

        /// <summary>
        /// Marks a process console session as completed and records its exit code.
        /// Unknown handles are ignored.
        /// </summary>
        public void CompleteProcessSession(ModuleConsoleSessionHandle handle, int? exitCode)
        {
            var changed = false;

            lock (_sync)
            {
                if (TryGetSessionCore(handle, out var moduleState, out var sessionState))
                {
                    sessionState.IsRunning = false;
                    sessionState.ExitCode = exitCode;
                    sessionState.EndedUtc = DateTimeOffset.UtcNow;
                    moduleState.LastActivityUtc = sessionState.EndedUtc;
                    changed = true;
                }
            }

            if (changed)
            {
                RaiseStateChanged();
            }
        }

        /// <summary>
        /// Synchronizes observed child processes that were started by a running module process.
        /// Each observed process gets its own session without captured output, because its streams are not
        /// available; sessions of processes missing from the latest scan are marked as ended.
        /// </summary>
        /// <param name="module">Module that owns the observed processes.</param>
        /// <param name="ownerHandle">Session of the module process whose children were scanned.</param>
        /// <param name="observedProcesses">Child processes found by the latest scan.</param>
        public void SyncObservedProcesses(
            ModuleConfig module,
            ModuleConsoleSessionHandle ownerHandle,
            IReadOnlyList<ObservedProcessInfo> observedProcesses)
        {
            var stateChanged = false;

            lock (_sync)
            {
                EnsureModuleCore(module);

                var moduleState = _modules[module.SourcePath];
                GetOrCreateOverviewSessionCore(moduleState);

                // Nothing to sync when the owner session is unknown.
                var ownerSessionId = ownerHandle.SessionId;
                if (string.IsNullOrWhiteSpace(ownerSessionId) ||
                    !moduleState.Sessions.ContainsKey(ownerSessionId))
                {
                    return;
                }

                // Ids of the processes seen in this scan, used below to detect processes that disappeared.
                var activeProcessIds = observedProcesses
                    .Select(process => process.ProcessId)
                    .ToHashSet();

                // Create or revive observed subprocess sessions for the current scan.
                foreach (var process in observedProcesses)
                {
                    var existingSession = moduleState.Sessions.Values
                        .FirstOrDefault(session => session.ProcessId == process.ProcessId &&
                                                   string.Equals(session.ObservedOwnerSessionId, ownerSessionId, StringComparison.OrdinalIgnoreCase));

                    // A known process is kept running; a session that had ended counts as a state change.
                    if (existingSession != null)
                    {
                        var wasCompleted = !existingSession.IsRunning || existingSession.EndedUtc.HasValue;
                        existingSession.IsRunning = true;
                        existingSession.EndedUtc = null;
                        moduleState.LastActivityUtc = DateTimeOffset.UtcNow;
                        stateChanged |= wasCompleted;
                        continue;
                    }

                    // A new process gets a session whose id is derived from the process id.
                    var sessionState = new ModuleConsoleSessionState
                    {
                        Id = $"observed:{process.ProcessId}",
                        Title = process.ProcessName,
                        CommandDescription = "Observed subprocess started by a module.",
                        Stage = "Service",
                        CommandLine = process.ProcessName,
                        ProcessId = process.ProcessId,
                        IsRunning = true,
                        IsTrackedProcess = false,
                        IsObservedProcess = true,
                        ObservedOwnerSessionId = ownerSessionId,
                        StartedUtc = DateTimeOffset.UtcNow
                    };

                    moduleState.Sessions[sessionState.Id] = sessionState;
                    moduleState.LastActivityUtc = sessionState.StartedUtc;
                    AppendLineCore(sessionState, "Observed subprocess detected. Direct stdout/stderr capture is unavailable.");
                    stateChanged = true;
                }

                // Mark observed subprocess sessions that disappeared from the latest scan as ended.
                foreach (var sessionState in moduleState.Sessions.Values
                             .Where(session => session.IsObservedProcess &&
                                               string.Equals(session.ObservedOwnerSessionId, ownerSessionId, StringComparison.OrdinalIgnoreCase) &&
                                               session.IsRunning &&
                                               session.ProcessId.HasValue &&
                                               !activeProcessIds.Contains(session.ProcessId.Value)))
                {
                    sessionState.IsRunning = false;
                    sessionState.EndedUtc = DateTimeOffset.UtcNow;
                    moduleState.LastActivityUtc = sessionState.EndedUtc;

                    if (sessionState.Lines.Count == 1)
                    {
                        AppendLineCore(sessionState, "Observed subprocess exited.");
                    }

                    stateChanged = true;
                }
            }

            if (stateChanged)
            {
                RaiseStateChanged();
            }
        }


        // Snapshot and display

        /// <summary>
        /// Returns an immutable snapshot of the current console store.
        /// </summary>
        public IReadOnlyList<ModuleConsoleModuleSnapshot> GetSnapshot()
        {
            lock (_sync)
            {
                return _modules.Values
                    .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(module => CreateModuleSnapshotCore(
                        module,
                        _activities.TryGetValue(module.SourcePath, out var active)
                            ? active.Values
                            : Array.Empty<ModuleActivity>()))
                    .ToList();
            }
        }

        /// <summary>
        /// Returns the buffered console text for one session, trimmed to a UI-safe size.
        /// </summary>
        public string GetSessionText(string moduleSourcePath, string sessionId)
        {
            return string.Join(Environment.NewLine, GetSessionLines(moduleSourcePath, sessionId));
        }

        /// <summary>
        /// Returns the buffered console lines for one session, trimmed to a UI-safe size.
        /// </summary>
        public IReadOnlyList<string> GetSessionLines(string moduleSourcePath, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(moduleSourcePath) || string.IsNullOrWhiteSpace(sessionId))
            {
                return ["No output yet."];
            }

            lock (_sync)
            {
                if (!_modules.TryGetValue(moduleSourcePath, out var moduleState) ||
                    !moduleState.Sessions.TryGetValue(sessionId, out var sessionState) ||
                    sessionState.Lines.Count == 0)
                {
                    return ["No output yet."];
                }

                return BuildDisplayLines(sessionState.Lines);
            }
        }

        /// <summary>
        /// Returns the merged console lines for one module-level unified console.
        /// </summary>
        public IReadOnlyList<string> GetUnifiedModuleLines(string moduleSourcePath)
        {
            if (string.IsNullOrWhiteSpace(moduleSourcePath))
            {
                return ["No module logs yet."];
            }

            lock (_sync)
            {
                if (!_modules.TryGetValue(moduleSourcePath, out var moduleState))
                {
                    return ["No module logs yet."];
                }

                var (mergedEntries, totalLineCount) = SelectLatestDisplayEntries(BuildUnifiedModuleEntries(moduleState));

                if (totalLineCount == 0)
                {
                    return ["No module logs yet."];
                }

                return BuildDisplayLines(mergedEntries, totalLineCount);
            }
        }

        /// <summary>
        /// Returns the merged console lines for multiple modules.
        /// </summary>
        public IReadOnlyList<string> GetUnifiedOverviewLines(IReadOnlyCollection<string> moduleSourcePaths)
        {
            if (moduleSourcePaths.Count == 0)
            {
                return ["No active module logs yet."];
            }

            lock (_sync)
            {
                var modulePathSet = moduleSourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var (mergedEntries, totalLineCount) = SelectLatestDisplayEntries(_modules.Values
                    .Where(module => modulePathSet.Contains(module.SourcePath))
                    .SelectMany(BuildUnifiedModuleEntries));

                if (totalLineCount == 0)
                {
                    return ["No active module logs yet."];
                }

                return BuildDisplayLines(mergedEntries, totalLineCount);
            }
        }

        /// <summary>
        /// Builds unified log entries from overview and settings sessions for one module.
        /// </summary>
        private static IEnumerable<ModuleConsoleLineState> BuildUnifiedModuleEntries(ModuleConsoleModuleState module)
        {
            // Only the overview and the settings sessions belong to the unified module log; process output
            // stays in its own consoles.
            foreach (var session in module.Sessions.Values)
            {
                var includeSession = string.Equals(session.Id, OverviewSessionId, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(session.Stage, "Settings", StringComparison.OrdinalIgnoreCase);

                if (!includeSession)
                {
                    continue;
                }

                var prefix = string.Equals(session.Id, OverviewSessionId, StringComparison.OrdinalIgnoreCase)
                    ? $"[{module.Name}]"
                    : $"[{module.Name}] [{session.Title}]";

                // Re-emit each line with a local timestamp and a module (and session) prefix; the original sequence
                // is kept so merged logs stay in chronological order.
                foreach (var line in session.Lines)
                {
                    yield return new ModuleConsoleLineState
                    {
                        Sequence = line.Sequence,
                        TimestampUtc = line.TimestampUtc,
                        Text = $"[{line.TimestampUtc.ToLocalTime():HH:mm:ss}] {prefix} {line.Text}"
                    };
                }
            }
        }


        // State helpers

        /// <summary>
        /// Ensures one module exists and refreshes its name and enabled flag when they changed.
        /// Returns true when the stored name or enabled flag had to be updated.
        /// The caller must hold the store lock.
        /// </summary>
        private bool EnsureModuleCore(ModuleConfig module)
        {
            // Create the state on first use; its empty name and disabled flag are filled in by the checks below.
            if (!_modules.TryGetValue(module.SourcePath, out var moduleState))
            {
                moduleState = new ModuleConsoleModuleState
                {
                    SourcePath = module.SourcePath
                };

                _modules[module.SourcePath] = moduleState;
            }

            var changed = false;

            if (!string.Equals(moduleState.Name, module.Name, StringComparison.Ordinal))
            {
                moduleState.Name = module.Name;
                changed = true;
            }

            if (moduleState.IsEnabled != module.Status.Enabled)
            {
                moduleState.IsEnabled = module.Status.Enabled;
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Returns the shared overview session for a module, creating it on first use.
        /// </summary>
        private ModuleConsoleSessionState GetOrCreateOverviewSessionCore(ModuleConsoleModuleState moduleState)
        {
            if (!moduleState.Sessions.TryGetValue(OverviewSessionId, out var sessionState))
            {
                sessionState = new ModuleConsoleSessionState
                {
                    Id = OverviewSessionId,
                    Title = "Overview",
                    Stage = "Lifecycle",
                    IsRunning = false,
                    StartedUtc = DateTimeOffset.UtcNow
                };

                moduleState.Sessions[OverviewSessionId] = sessionState;
            }

            return sessionState;
        }

        /// <summary>
        /// Resolves a display title from the command definition or process start info.
        /// </summary>
        private static string GetFallbackTitle(ModuleCommand command, Process process)
        {
            if (!string.IsNullOrWhiteSpace(command.Exec))
            {
                return command.Exec;
            }

            return process.StartInfo.FileName;
        }

        /// <summary>
        /// Appends one buffered line to a session, enforces the per-session retention limit and returns the new line.
        /// The caller must hold the store lock.
        /// </summary>
        private ModuleConsoleLineState AppendLineCore(ModuleConsoleSessionState sessionState, string message)
        {
            // Stamp the line with a store-wide sequence number so lines of different sessions can be merged in order.
            var line = new ModuleConsoleLineState
            {
                Sequence = ++_lineSequence,
                TimestampUtc = DateTimeOffset.UtcNow
            };
            sessionState.Lines.Add(line);
            SetLineTextCore(sessionState, line, message);

            // Keep only the newest lines of the session. An open line that was dropped can no longer be rewritten,
            // so its stream continues on a new line.
            if (sessionState.Lines.Count > MaxLinesPerSession)
            {
                sessionState.Lines.RemoveRange(0, sessionState.Lines.Count - MaxLinesPerSession);
                if (sessionState.OpenLines.Count > 0)
                {
                    var oldestSequence = sessionState.Lines[0].Sequence;
                    foreach (var stream in sessionState.OpenLines
                                 .Where(pair => pair.Value.Sequence < oldestSequence)
                                 .Select(pair => pair.Key)
                                 .ToList())
                    {
                        sessionState.OpenLines.Remove(stream);
                    }
                }
            }

            return line;
        }

        /// <summary>
        /// Sets the text of a buffered line, cutting it to the stored line length, and shows it as the session preview.
        /// The caller must hold the store lock.
        /// </summary>
        private static void SetLineTextCore(ModuleConsoleSessionState sessionState, ModuleConsoleLineState line, string message)
        {
            line.Text = message.Length > MaxStoredLineLength
                ? message[..MaxStoredLineLength] + " ... [truncated]"
                : message;
            sessionState.LastPreview = line.Text.Trim();
        }


        // Display trimming

        /// <summary>
        /// Truncates one line to the maximum length shown in the UI.
        /// </summary>
        private static string TrimLineForDisplay(string line)
        {
            if (line.Length <= MaxDisplayedLineLength)
            {
                return line;
            }

            return line[..MaxDisplayedLineLength] + " ...";
        }

        /// <summary>
        /// Selects the newest lines by sequence while preserving total line count for trim messaging.
        /// The result is ordered by ascending sequence.
        /// </summary>
        private static (List<ModuleConsoleLineState> Lines, int TotalCount) SelectLatestDisplayEntries(IEnumerable<ModuleConsoleLineState> entries)
        {
            // A min-heap bounded to MaxDisplayedLines keeps the newest lines in one pass: whenever it overflows,
            // the entry with the lowest sequence (the oldest line) is evicted.
            var heap = new PriorityQueue<ModuleConsoleLineState, long>();
            var totalCount = 0;

            foreach (var entry in entries)
            {
                totalCount++;
                heap.Enqueue(entry, entry.Sequence);
                if (heap.Count > MaxDisplayedLines)
                {
                    heap.Dequeue();
                }
            }

            // Drain the surviving entries and sort them explicitly, so the result is in ascending sequence order.
            var latestEntries = new List<ModuleConsoleLineState>(heap.Count);
            while (heap.TryDequeue(out var entry, out _))
            {
                latestEntries.Add(entry);
            }

            latestEntries.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            return (latestEntries, totalCount);
        }

        /// <summary>
        /// Builds UI-safe display lines with line-count and character-budget trimming.
        /// A banner line is prepended when older output was omitted.
        /// </summary>
        /// <param name="lines">Buffered lines in ascending sequence order.</param>
        /// <param name="totalLineCountOverride">Number of lines before an earlier selection step, when it trimmed already.</param>
        private static IReadOnlyList<string> BuildDisplayLines(IReadOnlyList<ModuleConsoleLineState> lines, int? totalLineCountOverride = null)
        {
            // Take the newest tail of buffered lines, applying per-line display truncation.
            var startIndex = Math.Max(0, lines.Count - MaxDisplayedLines);
            var selectedLines = new List<string>(lines.Count - startIndex);
            var currentLength = 0;

            for (var index = startIndex; index < lines.Count; index++)
            {
                var text = TrimLineForDisplay(lines[index].Text);
                selectedLines.Add(text);
                currentLength += text.Length + Environment.NewLine.Length;
            }

            // Drop oldest visible lines until the rendered text fits the character budget.
            var truncatedByCharacters = false;
            var firstVisibleIndex = 0;
            while (selectedLines.Count - firstVisibleIndex > 1 && currentLength > MaxDisplayedCharacters)
            {
                truncatedByCharacters = true;
                currentLength -= selectedLines[firstVisibleIndex].Length + Environment.NewLine.Length;
                firstVisibleIndex++;
            }

            if (firstVisibleIndex > 0)
            {
                selectedLines.RemoveRange(0, firstVisibleIndex);
            }

            // Prepend a trim banner when older buffered output was omitted.
            var totalLineCount = totalLineCountOverride ?? lines.Count;
            var trimmedLineCount = totalLineCount - selectedLines.Count;
            if (trimmedLineCount > 0 || truncatedByCharacters)
            {
                selectedLines.Insert(0, $"[Console output trimmed: showing the latest {selectedLines.Count} lines]");
            }

            return selectedLines;
        }


        // Lookup, snapshots and notification

        /// <summary>
        /// Resolves a live session handle to its module and session state under the store lock.
        /// </summary>
        private bool TryGetSessionCore(
            ModuleConsoleSessionHandle handle,
            out ModuleConsoleModuleState moduleState,
            out ModuleConsoleSessionState sessionState)
        {
            moduleState = null!;
            sessionState = null!;

            if (string.IsNullOrWhiteSpace(handle.ModuleSourcePath) ||
                string.IsNullOrWhiteSpace(handle.SessionId))
            {
                return false;
            }

            if (!_modules.TryGetValue(handle.ModuleSourcePath, out var resolvedModuleState))
            {
                return false;
            }

            if (!resolvedModuleState.Sessions.TryGetValue(handle.SessionId, out var resolvedSessionState))
            {
                return false;
            }

            moduleState = resolvedModuleState;
            sessionState = resolvedSessionState;
            return true;
        }

        /// <summary>
        /// Projects one module's mutable state into an immutable UI snapshot.
        /// Console text is not copied; sessions carry only counts and a preview.
        /// </summary>
        /// <param name="moduleState">Module state to project.</param>
        /// <param name="activities">Maintenance activities that are running for the module.</param>
        private static ModuleConsoleModuleSnapshot CreateModuleSnapshotCore(
            ModuleConsoleModuleState moduleState,
            IReadOnlyCollection<ModuleActivity> activities)
        {
            // The maintenance log has no process of its own; it counts as running while an installation or update
            // writes to it.
            var isMaintaining = activities.Any(activity => activity is ModuleActivity.Installing or ModuleActivity.Updating);

            // Returns whether a session is running, including the maintenance log during maintenance.
            bool IsRunning(ModuleConsoleSessionState session) =>
                session.IsRunning ||
                (isMaintaining && string.Equals(session.Id, MaintenanceSessionId, StringComparison.Ordinal));

            // Order sessions: running first, then the overview session, then newest first.
            var sessions = moduleState.Sessions.Values
                .OrderByDescending(IsRunning)
                .ThenBy(session => session.Id == OverviewSessionId ? 0 : 1)
                .ThenByDescending(session => session.StartedUtc)
                .Select(session => new ModuleConsoleSessionSnapshot
                {
                    Id = session.Id,
                    Title = session.Title,
                    CommandDescription = session.CommandDescription,
                    Stage = session.Stage,
                    CommandLine = session.CommandLine,
                    ProcessId = session.ProcessId,
                    IsRunning = IsRunning(session),
                    IsTrackedProcess = session.IsTrackedProcess,
                    IsObservedProcess = session.IsObservedProcess,
                    ExitCode = session.ExitCode,
                    StartedUtc = session.StartedUtc,
                    EndedUtc = session.EndedUtc,
                    LineCount = session.Lines.Count,
                    Preview = session.LastPreview,
                    Text = string.Empty
                })
                .ToList();

            // Observed child processes are excluded from the active process count; only tracked processes count.
            return new ModuleConsoleModuleSnapshot
            {
                SourcePath = moduleState.SourcePath,
                Name = moduleState.Name,
                IsEnabled = moduleState.IsEnabled,
                HasActivity = activities.Count > 0,
                LastActivityUtc = moduleState.LastActivityUtc,
                ActiveProcessCount = sessions.Count(session => session.IsTrackedProcess && !session.IsObservedProcess && session.IsRunning),
                Sessions = sessions
            };
        }

        /// <summary>
        /// Raises <see cref="StateChanged"/> on a background task with debouncing.
        /// </summary>
        private void RaiseStateChanged()
        {
            // Only the first change of a burst queues a notification; later changes are covered by it.
            if (Interlocked.Exchange(ref _stateChangeQueued, 1) == 1)
            {
                return;
            }

            // The flag is cleared before raising, so a change made by a handler queues the next notification.
            _ = Task.Run(async () =>
            {
                await Task.Delay(StateChangedDebounceMilliseconds);
                Interlocked.Exchange(ref _stateChangeQueued, 0);
                StateChanged?.Invoke(this, EventArgs.Empty);
            });
        }
    }


    // Snapshots and handles

    /// <summary>
    /// Represents the console snapshot for one module.
    /// </summary>
    public sealed class ModuleConsoleModuleSnapshot
    {
        /// <summary>
        /// Gets or sets the module manifest path.
        /// </summary>
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the module display name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether the module is enabled.
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Gets or sets whether a maintenance activity, such as an installation, update, restart or stop, is running
        /// for the module.
        /// </summary>
        public bool HasActivity { get; set; }

        /// <summary>
        /// Gets or sets the number of currently active tracked processes.
        /// </summary>
        public int ActiveProcessCount { get; set; }

        /// <summary>
        /// Gets or sets the last activity timestamp in UTC.
        /// </summary>
        public DateTimeOffset? LastActivityUtc { get; set; }

        /// <summary>
        /// Gets or sets the console sessions for the module.
        /// </summary>
        public IReadOnlyList<ModuleConsoleSessionSnapshot> Sessions { get; set; } = [];
    }

    /// <summary>
    /// Represents one immutable console session snapshot.
    /// </summary>
    public sealed class ModuleConsoleSessionSnapshot
    {
        /// <summary>
        /// Gets or sets the session identifier.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the session title.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the command description.
        /// </summary>
        public string CommandDescription { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the session stage.
        /// </summary>
        public string Stage { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the process command line.
        /// </summary>
        public string CommandLine { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the operating system process identifier.
        /// </summary>
        public int? ProcessId { get; set; }

        /// <summary>
        /// Gets or sets whether the session is still running.
        /// </summary>
        public bool IsRunning { get; set; }

        /// <summary>
        /// Gets or sets whether the session belongs to a long-running tracked process.
        /// </summary>
        public bool IsTrackedProcess { get; set; }

        /// <summary>
        /// Gets or sets whether the session was discovered as a child process without direct stream capture.
        /// </summary>
        public bool IsObservedProcess { get; set; }

        /// <summary>
        /// Gets or sets the exit code when the session completed.
        /// </summary>
        public int? ExitCode { get; set; }

        /// <summary>
        /// Gets or sets the start time in UTC.
        /// </summary>
        public DateTimeOffset StartedUtc { get; set; }

        /// <summary>
        /// Gets or sets the completion time in UTC.
        /// </summary>
        public DateTimeOffset? EndedUtc { get; set; }

        /// <summary>
        /// Gets or sets the number of buffered lines.
        /// </summary>
        public int LineCount { get; set; }

        /// <summary>
        /// Gets or sets the latest line preview.
        /// </summary>
        public string Preview { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the full buffered console text.
        /// </summary>
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// References one live console session in the store.
    /// </summary>
    /// <param name="ModuleSourcePath">Manifest path of the module that owns the session.</param>
    /// <param name="SessionId">Identifier of the session inside that module.</param>
    public readonly record struct ModuleConsoleSessionHandle(string ModuleSourcePath, string SessionId);


    // Maintenance activity and observed processes

    /// <summary>
    /// Kind of transient maintenance operation running for a module.
    /// The order is significant: when several activities of one module overlap, the highest value is reported,
    /// so parent operations (updating, installing) remain visible during their nested stop and restart steps.
    /// </summary>
    internal enum ModuleActivity
    {
        /// <summary>
        /// The module is being stopped.
        /// </summary>
        Stopping,

        /// <summary>
        /// The module is being restarted.
        /// </summary>
        Restarting,

        /// <summary>
        /// The module is being updated to a newer version.
        /// </summary>
        Updating,

        /// <summary>
        /// The module is being installed.
        /// </summary>
        Installing
    }

    /// <summary>
    /// Maintenance state of one module, read consistently under a single store lock.
    /// </summary>
    /// <param name="Activity">Strongest running maintenance activity, or null when none is running.</param>
    /// <param name="IsRunning">True when a tracked process of the module is running.</param>
    /// <param name="SessionKey">Identifies the current maintenance session; changes when a new session starts.</param>
    /// <param name="Text">Maintenance console text prepared for display, or empty when there is none.</param>
    internal sealed record ModuleMaintenanceSnapshot(ModuleActivity? Activity, bool IsRunning, string SessionKey, string Text);

    /// <summary>
    /// Describes one observed child process started by a module at runtime.
    /// </summary>
    public sealed class ObservedProcessInfo
    {
        /// <summary>
        /// Operating system process identifier.
        /// </summary>
        public int ProcessId { get; set; }

        /// <summary>
        /// Process name shown as the title of the observed session.
        /// </summary>
        public string ProcessName { get; set; } = string.Empty;
    }


    // Mutable store state

    /// <summary>
    /// Mutable per-module console state held inside the store.
    /// </summary>
    internal sealed class ModuleConsoleModuleState
    {
        /// <summary>
        /// Manifest path that identifies the module in the store.
        /// </summary>
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>
        /// Module display name, refreshed from the manifest.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Last known enabled flag of the module.
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Time of the latest console activity of the module, or null when there was none yet.
        /// </summary>
        public DateTimeOffset? LastActivityUtc { get; set; }

        /// <summary>
        /// Console sessions of the module by session id.
        /// </summary>
        public Dictionary<string, ModuleConsoleSessionState> Sessions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mutable console session state for one process or overview stream.
    /// </summary>
    internal sealed class ModuleConsoleSessionState
    {
        /// <summary>
        /// Session identifier, unique inside its module.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Title shown for the session.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Description of the manifest command that started the session.
        /// </summary>
        public string CommandDescription { get; set; } = string.Empty;

        /// <summary>
        /// Lifecycle stage of the session, such as Command, Settings, Lifecycle or Maintenance.
        /// </summary>
        public string Stage { get; set; } = string.Empty;

        /// <summary>
        /// Full command line of the process, echoed as the first output line.
        /// </summary>
        public string CommandLine { get; set; } = string.Empty;

        /// <summary>
        /// Operating system process identifier, or null for sessions without a process.
        /// </summary>
        public int? ProcessId { get; set; }

        /// <summary>
        /// Whether the process of the session is still running.
        /// </summary>
        public bool IsRunning { get; set; }

        /// <summary>
        /// Whether the session belongs to a long-running process that ASLM tracks as a running module process.
        /// </summary>
        public bool IsTrackedProcess { get; set; }

        /// <summary>
        /// Whether the session describes a child process that was only observed, without stream capture.
        /// </summary>
        public bool IsObservedProcess { get; set; }

        /// <summary>
        /// Id of the session whose process started the observed child; empty for other sessions.
        /// </summary>
        public string ObservedOwnerSessionId { get; set; } = string.Empty;

        /// <summary>
        /// Exit code of the process once it completed, or null while it runs or when the code is unknown.
        /// </summary>
        public int? ExitCode { get; set; }

        /// <summary>
        /// Time when the session started.
        /// </summary>
        public DateTimeOffset StartedUtc { get; set; }

        /// <summary>
        /// Time when the session ended, or null while it is running.
        /// </summary>
        public DateTimeOffset? EndedUtc { get; set; }

        /// <summary>
        /// Trimmed text of the latest line, shown as the session preview.
        /// </summary>
        public string LastPreview { get; set; } = string.Empty;

        /// <summary>
        /// Buffered output lines in the order they were added.
        /// </summary>
        public List<ModuleConsoleLineState> Lines { get; } = [];

        /// <summary>
        /// Unfinished last lines of the process output streams; each one is rewritten until its line ends.
        /// </summary>
        public Dictionary<ProcessOutputStream, ModuleConsoleLineState> OpenLines { get; } = [];
    }

    /// <summary>
    /// One buffered console line with ordering metadata.
    /// </summary>
    internal sealed class ModuleConsoleLineState
    {
        /// <summary>
        /// Store-wide sequence number that orders lines across sessions and modules.
        /// </summary>
        public long Sequence { get; set; }

        /// <summary>
        /// Time when the line was added.
        /// </summary>
        public DateTimeOffset TimestampUtc { get; set; }

        /// <summary>
        /// Line text, possibly truncated to the storage limit; the text of an open line is rewritten until it ends.
        /// </summary>
        public string Text { get; set; } = string.Empty;
    }
}
