// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Keeps explicit repository searches cancellable without sending requests when text changes.
    /// </summary>
    internal sealed class ModuleRepositorySearch
    {
        // State

        /// <summary>
        /// Resolves a repository only after SearchAsync is invoked.
        /// </summary>
        private readonly Func<string, CancellationToken, Task<ModuleConfig>> _find;

        /// <summary>
        /// Holds the current text independently of the last submitted request.
        /// </summary>
        private string _query = string.Empty;

        /// <summary>
        /// Cancels the running request without disposing its token source before it finishes.
        /// </summary>
        private CancellationTokenSource? _cancellation;

        /// <summary>
        /// Identifies the newest request or edit so late responses cannot replace current state.
        /// </summary>
        private int _revision;

        /// <summary>
        /// Notifies the host when search text, results or progress change.
        /// </summary>
        internal event EventHandler? Changed;

        /// <summary>
        /// Gets or sets text without triggering network activity.
        /// </summary>
        internal string Query
        {
            get => _query;
            set
            {
                if (_query == value) return;
                Cancel();
                _query = value;
                Result = null;
                Error = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Gets whether an explicit lookup is still running.
        /// </summary>
        internal bool IsSearching { get; private set; }

        /// <summary>
        /// Gets whether the current text can be submitted without duplicating a pending request.
        /// </summary>
        internal bool CanSearch => !IsSearching && !string.IsNullOrWhiteSpace(Query);

        /// <summary>
        /// Gets the validated result of the latest submitted query.
        /// </summary>
        internal ModuleConfig? Result { get; private set; }

        /// <summary>
        /// Gets the latest lookup failure for presentation by the host.
        /// </summary>
        internal Exception? Error { get; private set; }


        // Search lifecycle

        /// <summary>
        /// Creates a search state over an injectable repository lookup.
        /// </summary>
        internal ModuleRepositorySearch(Func<string, CancellationToken, Task<ModuleConfig>> find)
        {
            _find = find;
        }

        /// <summary>
        /// Submits the current query once and discards results superseded by edits or cancellation.
        /// </summary>
        internal async Task<ModuleConfig?> SearchAsync()
        {
            if (!CanSearch) return null;
            var revision = ++_revision;
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            IsSearching = true;
            Result = null;
            Error = null;
            Changed?.Invoke(this, EventArgs.Empty);
            try
            {
                var result = await _find(Query, cancellation.Token);
                if (revision == _revision && !cancellation.IsCancellationRequested) Result = result;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // An edit or navigation intentionally abandoned this lookup.
            }
            catch (Exception ex)
            {
                if (revision == _revision) Error = ex;
            }
            finally
            {
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                if (revision == _revision)
                {
                    IsSearching = false;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            return revision == _revision ? Result : null;
        }

        /// <summary>
        /// Stops pending work while retaining an already completed result for navigation back to the section.
        /// </summary>
        internal void Cancel()
        {
            ++_revision;
            _cancellation?.Cancel();
            IsSearching = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
