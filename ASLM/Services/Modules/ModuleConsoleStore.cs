// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using ASLM.Models;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Stores in-memory console sessions for modules and their spawned processes.
    /// </summary>
    public sealed class ModuleConsoleStore
    {
        // Fields and constants

        private const string OverviewSessionId = "overview";
        private const int MaxLinesPerSession = 5000;
        private const int MaxDisplayedLines = 250;
        private const int MaxDisplayedCharacters = 60000;
        private const int MaxStoredLineLength = 4000;
        private const int MaxDisplayedLineLength = 1200;
        private const int StateChangedDebounceMilliseconds = 75;

        private readonly object _sync = new();
        private readonly Dictionary<string, ModuleConsoleModuleState> _modules = new(StringComparer.OrdinalIgnoreCase);
        private long _lineSequence;
        private int _stateChangeQueued;


        // Events

        /// <summary>
        /// Raised whenever the console store changes.
        /// </summary>
        public event EventHandler? StateChanged;


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
        /// Appends one line to a process console session.
        /// </summary>
        public void AppendProcessLine(ModuleConsoleSessionHandle handle, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var changed = false;

            lock (_sync)
            {
                if (TryGetSessionCore(handle, out var moduleState, out var sessionState))
                {
                    AppendLineCore(sessionState, message);
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
        /// Marks a process console session as completed.
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
        /// </summary>
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
                var ownerSessionId = ownerHandle.SessionId;
                if (string.IsNullOrWhiteSpace(ownerSessionId) ||
                    !moduleState.Sessions.ContainsKey(ownerSessionId))
                {
                    return;
                }

                var activeProcessIds = observedProcesses
                    .Select(process => process.ProcessId)
                    .ToHashSet();

                // Create or revive observed subprocess sessions for the current scan.
                foreach (var process in observedProcesses)
                {
                    var existingSession = moduleState.Sessions.Values
                        .FirstOrDefault(session => session.ProcessId == process.ProcessId &&
                                                   string.Equals(session.ObservedOwnerSessionId, ownerSessionId, StringComparison.OrdinalIgnoreCase));

                    if (existingSession != null)
                    {
                        var wasCompleted = !existingSession.IsRunning || existingSession.EndedUtc.HasValue;
                        existingSession.IsRunning = true;
                        existingSession.EndedUtc = null;
                        moduleState.LastActivityUtc = DateTimeOffset.UtcNow;
                        stateChanged |= wasCompleted;
                        continue;
                    }

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
                    .Select(CreateModuleSnapshotCore)
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


        // Internal helpers

        /// <summary>
        /// Ensures one module exists and refreshes its name and enabled flag when they changed.
        /// </summary>
        private bool EnsureModuleCore(ModuleConfig module)
        {
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
        /// Appends one buffered line to a session and enforces per-session retention limits.
        /// </summary>
        private void AppendLineCore(ModuleConsoleSessionState sessionState, string message)
        {
            if (message.Length > MaxStoredLineLength)
            {
                message = message[..MaxStoredLineLength] + " ... [truncated]";
            }

            sessionState.Lines.Add(new ModuleConsoleLineState
            {
                Sequence = ++_lineSequence,
                TimestampUtc = DateTimeOffset.UtcNow,
                Text = message
            });
            sessionState.LastPreview = message.Trim();

            if (sessionState.Lines.Count > MaxLinesPerSession)
            {
                sessionState.Lines.RemoveRange(0, sessionState.Lines.Count - MaxLinesPerSession);
            }
        }

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
        /// </summary>
        private static (List<ModuleConsoleLineState> Lines, int TotalCount) SelectLatestDisplayEntries(IEnumerable<ModuleConsoleLineState> entries)
        {
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
        /// </summary>
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
        /// </summary>
        private static ModuleConsoleModuleSnapshot CreateModuleSnapshotCore(ModuleConsoleModuleState moduleState)
        {
            var sessions = moduleState.Sessions.Values
                .OrderByDescending(session => session.IsRunning)
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
                    IsRunning = session.IsRunning,
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

            return new ModuleConsoleModuleSnapshot
            {
                SourcePath = moduleState.SourcePath,
                Name = moduleState.Name,
                IsEnabled = moduleState.IsEnabled,
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
            if (Interlocked.Exchange(ref _stateChangeQueued, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(StateChangedDebounceMilliseconds);
                Interlocked.Exchange(ref _stateChangeQueued, 0);
                StateChanged?.Invoke(this, EventArgs.Empty);
            });
        }
    }


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
    public readonly record struct ModuleConsoleSessionHandle(string ModuleSourcePath, string SessionId);

    /// <summary>
    /// Describes one observed child process started by a module at runtime.
    /// </summary>
    public sealed class ObservedProcessInfo
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
    }


    /// <summary>
    /// Mutable per-module console state held inside the store.
    /// </summary>
    internal sealed class ModuleConsoleModuleState
    {
        public string SourcePath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public DateTimeOffset? LastActivityUtc { get; set; }
        public Dictionary<string, ModuleConsoleSessionState> Sessions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mutable console session state for one process or overview stream.
    /// </summary>
    internal sealed class ModuleConsoleSessionState
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string CommandDescription { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public string CommandLine { get; set; } = string.Empty;
        public int? ProcessId { get; set; }
        public bool IsRunning { get; set; }
        public bool IsTrackedProcess { get; set; }
        public bool IsObservedProcess { get; set; }
        public string ObservedOwnerSessionId { get; set; } = string.Empty;
        public int? ExitCode { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? EndedUtc { get; set; }
        public string LastPreview { get; set; } = string.Empty;
        public List<ModuleConsoleLineState> Lines { get; } = [];
    }

    /// <summary>
    /// One buffered console line with ordering metadata.
    /// </summary>
    internal sealed class ModuleConsoleLineState
    {
        public long Sequence { get; set; }
        public DateTimeOffset TimestampUtc { get; set; }
        public string Text { get; set; } = string.Empty;
    }
}
