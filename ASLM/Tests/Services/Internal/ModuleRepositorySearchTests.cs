// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies explicit search submission, duplicate prevention and cancellation independently of native controls.
    /// </summary>
    public sealed class ModuleRepositorySearchTests
    {
        /// <summary>
        /// Verifies that typing sends nothing and repeated clicks share the pending lookup.
        /// </summary>
        [Fact]
        public async Task Typing_does_not_search_and_pending_clicks_do_not_duplicate_requests()
        {
            var calls = 0;
            var completion = new TaskCompletionSource<ModuleConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
            var search = new ModuleRepositorySearch((query, _) =>
            {
                calls++;
                query.Should().Be("owner/repo");
                return completion.Task;
            });
            search.Query = "owner";
            search.Query = "owner/repo";
            calls.Should().Be(0);

            var pending = search.SearchAsync();
            search.IsSearching.Should().BeTrue();
            search.CanSearch.Should().BeFalse();
            await search.SearchAsync();
            calls.Should().Be(1);
            var module = new ModuleConfig { Id = "found" };
            completion.SetResult(module);
            await pending;
            search.Result.Should().BeSameAs(module);
            search.CanSearch.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that an edited query cancels its request and a late response cannot replace a newer result.
        /// </summary>
        [Fact]
        public async Task Editing_discards_late_results_even_when_the_transport_ignores_cancellation()
        {
            var first = new TaskCompletionSource<ModuleConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = CancellationToken.None;
            var search = new ModuleRepositorySearch((query, ct) =>
            {
                if (query == "owner/first")
                {
                    token = ct;
                    return first.Task;
                }
                return Task.FromResult(new ModuleConfig { Id = "second" });
            });
            search.Query = "owner/first";
            var pending = search.SearchAsync();
            search.Query = "owner/second";
            token.IsCancellationRequested.Should().BeTrue();
            await search.SearchAsync();
            first.SetResult(new ModuleConfig { Id = "stale" });
            await pending;
            search.Result!.Id.Should().Be("second");
            search.Error.Should().BeNull();
        }

        /// <summary>
        /// Verifies that navigation cancels pending work without publishing a failure or clearing completed results.
        /// </summary>
        [Fact]
        public async Task Navigation_cancels_pending_work_and_keeps_completed_results()
        {
            var completion = new TaskCompletionSource<ModuleConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
            var search = new ModuleRepositorySearch((_, _) => completion.Task) { Query = "owner/repo" };
            var pending = search.SearchAsync();
            search.Cancel();
            completion.SetException(new HttpRequestException("Abandoned request"));
            await pending;
            search.Error.Should().BeNull();
            search.Result.Should().BeNull();
            search.IsSearching.Should().BeFalse();

            var finished = new ModuleRepositorySearch((_, _) => Task.FromResult(new ModuleConfig { Id = "done" }))
            {
                Query = "owner/repo"
            };
            await finished.SearchAsync();
            finished.Cancel();
            finished.Result!.Id.Should().Be("done");
        }

        /// <summary>
        /// Verifies that empty input does not submit a request and failures are retained for presentation.
        /// </summary>
        [Fact]
        public async Task Empty_input_is_ignored_and_lookup_failures_are_reported()
        {
            var error = new InvalidDataException("Invalid manifest");
            var calls = 0;
            var search = new ModuleRepositorySearch((_, _) =>
            {
                calls++;
                return Task.FromException<ModuleConfig>(error);
            });
            await search.SearchAsync();
            calls.Should().Be(0);
            search.Query = "owner/repo";
            await search.SearchAsync();
            search.Error.Should().BeSameAs(error);
            search.Result.Should().BeNull();
            search.IsSearching.Should().BeFalse();
        }
    }
}
