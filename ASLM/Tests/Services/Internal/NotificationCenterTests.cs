// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Specialized;
using ASLM.Localization;
using ASLM.Models;
using ASLM.Services.Internal;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies the in-app notification lifecycle: publishing, download progress wiring,
    /// dismissal, and the debounced persistence round-trip.
    /// </summary>
    public sealed class NotificationCenterTests : IDisposable
    {
        // Fixture state

        /// <summary>
        /// Isolated ASLM root whose data folder receives the persisted notifications file.
        /// </summary>
        private readonly UniqueAslmRoot _root = new("notifications");

        /// <summary>
        /// Removes the isolated root together with the notifications file.
        /// </summary>
        public void Dispose() => _root.Dispose();

        /// <summary>
        /// Creates a notification center that runs UI-thread work inline, so mutations take effect immediately.
        /// </summary>
        private NotificationCenter CreateCenter() => new(new InlineDispatcher());


        // Operation keys and update notifications

        /// <summary>
        /// Verifies that operation keys are built from the lower-cased, trimmed source kind and source id.
        /// </summary>
        [Theory]
        [InlineData("Module", "ASLM-Chat", "module:aslm-chat")]
        [InlineData(" Engine ", " Ollama ", "engine:ollama")]
        public void BuildOperationKey_normalizes_source_parts(string kind, string id, string expected)
        {
            NotificationCenter.BuildOperationKey(kind, id).Should().Be(expected);
        }

        /// <summary>
        /// Verifies that the journal and toast share a versioned offer with optional title breaks.
        /// </summary>
        [Fact]
        public void PublishUpdateCandidate_adds_a_row_and_raises_the_toast_event()
        {
            var center = CreateCenter();
            AppNotification? toasted = null;
            center.NotificationPublished += (_, notification) => toasted = notification;

            const string name = "ASLM ExtensionIdentifier";
            center.PublishUpdateOffer("module", "aslm-chat", "2.0", name, "2.0", "1.0");

            center.Notifications.ToList().Should().ContainSingle();
            var row = center.Notifications.ToList().Single();
            row.Id.Should().Be("update:module:aslm-chat:2.0");
            row.Category.Should().Be(AppNotificationCategory.Updates);
            row.CardTitle.Should().Contain("\u00AD");
            row.CardTitle.Replace("\u00AD", "", StringComparison.Ordinal).Should()
                .Be(L.Get(LocalizationKeys.Notifications_UpdateAvailableFromFormat, name, "1.0", "2.0"));
            row.HasCardStatus.Should().BeFalse();
            toasted.Should().NotBeNull().And.BeSameAs(row);
        }

        /// <summary>
        /// Verifies that one offer and one actionable toast survive repeated or superseded results.
        /// </summary>
        [Fact]
        public void Update_offers_replace_older_versions_and_withdraw_on_selection_change()
        {
            var center = CreateCenter();
            var published = new List<AppNotification>();
            var withdrawn = new List<AppNotification>();
            var actions = new List<AppNotification>();
            center.NotificationPublished += (_, row) => published.Add(row);
            center.NotificationWithdrawn += (_, row) => withdrawn.Add(row);
            center.UpdateNotificationActionRequested += (_, action) => actions.Add(action.Notification);

            center.PublishUpdateOffer("module", "m", "2.0", "M", "2.0");
            var first = center.Notifications.Single();
            center.PublishUpdateOffer("module", "m", "3.0", "M", "3.0");
            var latest = center.Notifications.Single();
            center.PublishUpdateOffer("module", "m", "3.0", "M", "3.0");

            center.RequestUpdateNotificationAction(first, updateNow: true);
            center.RequestUpdateNotificationAction(latest, updateNow: true);
            center.WithdrawUpdateOffers("module", "m");
            center.PublishUpdateOffer("module", "m", "4.0", "M", "4.0", isCurrent: () => false);
            center.RequestUpdateNotificationAction(latest, updateNow: true);

            center.Notifications.Should().BeEmpty();
            published.Should().Equal(first, latest);
            withdrawn.Should().Equal(first, latest);
            actions.Should().Equal(latest);
        }


        // Download notifications

        /// <summary>
        /// Verifies that cancellation targets the selected attempt and clearing the journal leaves it running.
        /// </summary>
        [Fact]
        public async Task Cancel_action_targets_only_the_live_attempt_and_leaves_its_result()
        {
            var center = CreateCenter();
            using var first = new OperationCancellation(CancellationToken.None);
            using var second = new OperationCancellation(CancellationToken.None);
            var firstKey = await center.StartDownloadAsync("install", "Model A", "", "resource", "a",
                requestCancellation: first.TryCancel);
            var secondKey = await center.StartDownloadAsync("install", "Model B", "", "resource", "b",
                requestCancellation: second.TryCancel);
            var canceled = center.Notifications.Single(row => row.Id == firstKey);
            var committing = center.Notifications.Single(row => row.Id == secondKey);

            center.DismissAll();
            center.Notifications.Should().Contain([canceled, committing]);
            first.CanCancel.Should().BeTrue();
            second.CanCancel.Should().BeTrue();

            center.RequestDownloadCancellation(canceled);
            center.RequestDownloadCancellation(canceled);
            var beginCanceledCommit = first.BeginCommit;
            beginCanceledCommit.Should().Throw<OperationCanceledException>();
            canceled.CanCancelOperation.Should().BeFalse();
            canceled.CanDismiss.Should().BeFalse();
            second.CanCancel.Should().BeTrue();

            // Even if the UI still shows the action for a moment, the service has closed cancellation.
            second.BeginCommit();
            center.RequestDownloadCancellation(committing);
            second.Token.IsCancellationRequested.Should().BeFalse();

            center.CancelDownload(firstKey, LocalizationKeys.Notifications_OperationCanceled);
            canceled.CardStatusText.Should().Be(L.Get(LocalizationKeys.Notifications_OperationCanceled));
            canceled.CanDismiss.Should().BeTrue();
            center.DismissAll();
            center.Notifications.Should().ContainSingle().Which.Should().BeSameAs(committing);
        }

        /// <summary>
        /// Verifies that a download row is in progress after the start, stays in progress and shows a status text while
        /// bytes are reported, updates its subtitle for a new file, and clears file and speed after transferring.
        /// </summary>
        [Fact]
        public async Task Download_flow_progresses_from_start_through_bytes_to_completion()
        {
            var center = CreateCenter();

            var attempt = await center.StartDownloadAsync("install", "Model", "Model", "resource", "model");
            var row = center.Notifications.Single(n => n.Id == attempt);
            row.IsInProgress.Should().BeTrue();
            var changes = 0;
            center.NotificationsChanged += (_, _) => changes++;

            center.ReportDownloadProgress(attempt, new DownloadProgress(0, 512, 0, "model", 512));
            row.IsInProgress.Should().BeTrue();
            var downloadStatus = $"Model - {L.Get(LocalizationKeys.Settings_Downloading)}";
            row.CardStatusText.Should().Be(downloadStatus);
            row.TransferSpeedDisplay.Should().Be("512 B/s");
            row.TransferPercentDisplay.Should().NotContain("%");

            // A new file updates the bound subtitle even while the status and byte counts stay the same.
            var subtitleChanges = new List<string>();
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AppNotification.CardStatusText)) subtitleChanges.Add(row.CardStatusText);
            };
            center.ReportDownloadProgress(attempt, new DownloadProgress(0, 512, 0, "model.zip", 512));
            subtitleChanges.Should().Equal($"{downloadStatus} - model.zip");

            center.ReportDownloadStatus(attempt, "Applying");
            row.IsInProgress.Should().BeTrue();
            row.CardStatusText.Should().Be("Model - Applying");
            row.ShowDownloadMetricsRow.Should().BeFalse();
            row.TransferSpeedDisplay.Should().NotContain("B/s");

            center.CompleteDownload(attempt, "Model installed");
            row.IsInProgress.Should().BeFalse();
            changes.Should().BeGreaterThan(0, "aggregate change events fire for the UI");
        }

        /// <summary>
        /// Verifies that progress and completion reports for an operation that was never started do not create
        /// a notification row.
        /// </summary>
        [Fact]
        public async Task Download_progress_for_unknown_operations_is_ignored()
        {
            var center = CreateCenter();

            center.ReportDownloadProgress("resource:ghost", new DownloadProgress(0.5, 1, 2, "ghost"));
            center.CompleteDownload("resource:ghost", "done");

            center.Notifications.ToList().Should().BeEmpty();
            await Task.CompletedTask;
        }

        /// <summary>
        /// Verifies that a failed download is no longer in progress and its row has error severity.
        /// </summary>
        [Fact]
        public async Task FailDownload_marks_the_row_with_an_error_severity()
        {
            var center = CreateCenter();
            var attempt = await center.StartDownloadAsync("install", "Bad", "Downloading bad", "resource", "bad");

            center.FailDownload(attempt, "Checksum failed");

            var row = center.Notifications.Single(n => n.Id == attempt);
            row.IsInProgress.Should().BeFalse();
            row.Severity.Should().Be(AppNotificationSeverity.Error);
        }


        /// <summary>
        /// Verifies that starting an update replaces its offer without emptying the list and retains distinct outcomes.
        /// </summary>
        [Fact]
        public async Task Update_attempts_replace_the_offer_and_keep_distinct_results()
        {
            var center = CreateCenter();
            center.PublishUpdateOffer("module", "m", "2.0", "M", "2.0");
            center.Notifications.Single().CardTitle.Should()
                .Be(L.Get(LocalizationKeys.Notifications_UpdateAvailableFormat, "M", "2.0"));
            var changes = new List<(NotifyCollectionChangedAction Action, int Count)>();
            ((INotifyCollectionChanged)center.Notifications).CollectionChanged +=
                (_, change) => changes.Add((change.Action, center.Notifications.Count));

            var first = await center.StartDownloadAsync(
                "module-update", "M", "", "module", "m", "2.0", true, targetVersion: "2.0");
            changes.Should().Equal((NotifyCollectionChangedAction.Add, 2), (NotifyCollectionChangedAction.Remove, 1));
            var active = center.Notifications.Single();
            active.Id.Should().Be(first);
            active.CardTitle.Should().Be(L.Get(LocalizationKeys.Notifications_UpdatingFormat, "M", "2.0"));
            active.CardStatusText.Should().Be(active.StatusText);
            center.PublishUpdateOffer("module", "m", "2.0", "M", "2.0");
            center.Notifications.Single().Id.Should().Be(first);
            center.FailDownload(first, "Failed");

            var retry = await center.StartDownloadAsync("module-update", "Updating", "M 2.0", "module", "m", "2.0", true);
            center.FailDownload(retry, "Failed again");
            center.ReportDownloadStatus(first, "Stale");
            center.Notifications.Single().StatusText.Should().Be("Failed again");
            var secondRetry = await center.StartDownloadAsync(
                "module-update", "Updating", "M 2.0", "module", "m", "2.0", true);
            center.ReportDownloadProgress(retry, new DownloadProgress(0.5, 1, 2, "Stale"));
            center.Notifications.Single(row => row.Id == secondRetry).ProgressFraction.Should().Be(0);
            center.CompleteDownload(secondRetry, "M updated successfully.");
            var completed = center.Notifications.Single(row => row.Id == secondRetry);
            completed.CardTitle.Should().Be(L.Get(LocalizationKeys.Notifications_UpdatedFormat, "M", "2.0"));
            completed.HasCardStatus.Should().BeFalse();

            var next = await center.StartDownloadAsync("module-update", "Updating", "M 3.0", "module", "m", "3.0", true);
            center.CompleteDownload(next, "Installed");
            center.Notifications.Select(row => (row.VersionKey, row.Severity)).Should().BeEquivalentTo(new[]
            {
                ("2.0", AppNotificationSeverity.Error),
                ("2.0", AppNotificationSeverity.Success),
                ("3.0", AppNotificationSeverity.Success)
            });
        }


        // Dismissal

        /// <summary>
        /// Verifies that each dismissal path keeps protected offers and active updates until they are resolved.
        /// </summary>
        [Fact]
        public async Task Dismiss_and_clear_all_keep_protected_notifications()
        {
            var center = CreateCenter();
            center.PublishUpdateOffer("app", "aslm", "2.0", "ASLM", "2.0");
            center.PublishUpdateOffer("engine", "ollama", "2.0", "Ollama", "2.0");
            center.PublishUpdateOffer("module", "other", "2.0", "Other module", "2.0");
            var download = await center.StartDownloadAsync("install", "Model", "Model", "resource", "model");
            var update = await center.StartDownloadAsync("module-update", "M", "", "module", "m", "2.0");

            // Neither an action request nor direct dismissal can bypass the protected row's controls.
            var appOffer = center.Notifications.Single(row => row.SourceKind == "app");
            var engineOffer = center.Notifications.Single(row => row.SourceKind == "engine");
            var active = center.Notifications.Single(row => row.Id == update);
            foreach (var row in new[] { appOffer, engineOffer, active })
            {
                row.CanDeferUpdate.Should().BeFalse();
                center.RequestUpdateNotificationAction(row, updateNow: false);
                center.Dismiss(row);
            }

            appOffer.OffersUpdateActions.Should().BeTrue();
            engineOffer.OffersUpdateActions.Should().BeTrue();
            center.Dismiss(center.Notifications.Single(row => row.Id == download));
            center.Notifications.Should().HaveCount(4);

            center.DismissAll();
            center.Notifications.Should().BeEquivalentTo(new[] { appOffer, engineOffer, active });

            center.ReportDownloadStatus(update, "Applying update");
            active.CanDismiss.Should().BeFalse();
            center.CompleteDownload(update, "Updated");
            active.CanDismiss.Should().BeTrue();
            center.DismissAll();
            center.Notifications.Should().BeEquivalentTo(new[] { appOffer, engineOffer });

            // Invalidation can still withdraw an offer, and ordinary downloads still publish their outcomes.
            center.WithdrawUpdateOffer(appOffer);
            center.ReportDownloadProgress(download, new DownloadProgress(0.5, 1, 2, "Model"));
            center.Notifications.Should().Equal(engineOffer);
            center.CompleteDownload(download, "Model installed");
            center.Notifications.Should().Contain(row => row.Id == download);
        }


        // Persistence

        /// <summary>
        /// Verifies that deferral hides actions on the existing row across restart until a newer offer replaces it.
        /// </summary>
        [Fact]
        public async Task Deferred_module_offer_keeps_its_actions_hidden_until_replaced()
        {
            var center = CreateCenter();
            await center.InitializeAsync();
            center.PublishUpdateOffer("module", "m", "2.0", "M", "2.0");
            var offer = center.Notifications.Single();
            var withdrawn = new List<AppNotification>();
            var actions = new List<AppNotification>();
            center.NotificationWithdrawn += (_, row) => withdrawn.Add(row);
            center.UpdateNotificationActionRequested += (_, action) => actions.Add(action.Notification);

            offer.CanDeferUpdate.Should().BeTrue();
            center.RequestUpdateNotificationAction(offer, updateNow: false);
            center.RequestUpdateNotificationAction(offer, updateNow: true);
            center.PublishUpdateOffer("module", "m", "2.0", "M", "2.0");

            center.Notifications.Should().Equal(offer);
            offer.OffersUpdateActions.Should().BeFalse();
            offer.CanDeferUpdate.Should().BeFalse();
            withdrawn.Should().Equal(offer);
            actions.Should().BeEmpty();

            var persisted = Path.Combine(_root.DataAppDir, "ASLM_Notifications.json");
            await TestWait.UntilAsync(() => File.Exists(persisted) &&
                File.ReadAllText(persisted).Contains("\"OffersUpdateActions\": false", StringComparison.Ordinal),
                because: "deferral is saved with the offer");

            var reloaded = CreateCenter();
            await reloaded.InitializeAsync();
            var deferred = reloaded.Notifications.Single();
            deferred.OffersUpdateActions.Should().BeFalse();
            deferred.CanDismiss.Should().BeTrue();

            reloaded.PublishUpdateOffer("module", "m", "3.0", "M", "3.0");
            var replacement = reloaded.Notifications.Single();
            replacement.VersionKey.Should().Be("3.0");
            replacement.OffersUpdateActions.Should().BeTrue();
            replacement.CanDeferUpdate.Should().BeTrue();
            await TestWait.UntilAsync(() => File.ReadAllText(persisted).Contains("\"VersionKey\": \"3.0\"", StringComparison.Ordinal),
                because: "the replacement supersedes the saved deferral");
        }

        /// <summary>
        /// Verifies that notifications are written to the notifications file after a debounce delay and that a new
        /// center loads the update and download rows from it.
        /// </summary>
        [Fact]
        public async Task Notifications_persist_debounced_and_reload_on_the_next_session()
        {
            // Arrange: publish an update row and a completed download row.
            var center = CreateCenter();
            await center.InitializeAsync();
            center.PublishUpdateOffer("app", "app", "9.9.9", "ASLM", "9.9.9");
            var attempt = await center.StartDownloadAsync(
                "module-install", "ASLM Chat", "", "module", "chat", "v2", targetVersion: "v2");
            center.CompleteDownload(attempt, L.Get(LocalizationKeys.ModuleInfo_Installed));

            // Wait until the debounced save has created the file.
            var persisted = Path.Combine(_root.DataAppDir, "ASLM_Notifications.json");
            await TestWait.UntilAsync(() => File.Exists(persisted),
                because: "completed mutations schedule a debounced save");

            // A second center reads both rows back.
            var reloaded = CreateCenter();
            await reloaded.InitializeAsync();

            reloaded.Notifications.Select(n => n.Id).Should().Contain("update:app:app:9.9.9").And.Contain(attempt);
            var installed = reloaded.Notifications.Single(row => row.Id == attempt);
            installed.CardTitle.Should().Be(L.Get(LocalizationKeys.Notifications_InstalledVersionFormat, "ASLM Chat", "v2"));
            installed.HasCardStatus.Should().BeFalse();
        }

        /// <summary>
        /// Verifies that restored offers stay actionable and that old operation results use the active language.
        /// </summary>
        [Fact]
        public async Task Legacy_journal_restores_one_actionable_offer_and_keeps_the_result()
        {
            await File.WriteAllTextAsync(Path.Combine(_root.DataAppDir, "ASLM_Notifications.json"), """
                {
                  "FileVersion": 1,
                  "Notifications": [
                    { "Id": "update:module:m:3", "Category": "Updates", "SourceKind": "module", "SourceId": "m",
                      "Message": "M: \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d\u043d\u0430\u044f \u0432\u0435\u0440\u0441\u0438\u044f -> v3",
                      "StatusText": "New version found", "DetailText": "Channel: release",
                      "UpdatedAt": "2026-01-03T00:00:00Z", "OffersUpdateActions": false },
                    { "Id": "update:module:m:2", "Category": "Updates", "SourceKind": "module", "SourceId": "m",
                      "UpdatedAt": "2026-01-02T00:00:00Z", "OffersUpdateActions": true },
                    { "Id": "download:module-update:m", "Category": "Downloads", "SourceKind": "module", "SourceId": "m",
                      "Message": "Example Python Module main@a7880e6",
                      "StatusText": "\u0423\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d",
                      "UpdatedAt": "2026-01-01T00:00:00Z", "Severity": "Success" },
                    { "Id": "download:app-update:aslm", "Category": "Downloads", "SourceKind": "app", "SourceId": "aslm",
                      "Message": "ASLM v2", "StatusText": "Operation interrupted.",
                      "UpdatedAt": "2025-12-31T00:00:00Z", "Severity": "Warning" }
                  ]
                }
                """);
            var center = CreateCenter();
            await center.InitializeAsync();

            center.Notifications.Select(row => row.Id).Should()
                .Equal("update:module:m:3", "download:module-update:m", "download:app-update:aslm");
            var offer = center.Notifications[0];
            offer.OffersUpdateActions.Should().BeTrue();
            offer.CardTitle.Should().Be(L.Get(LocalizationKeys.Notifications_UpdateAvailableFormat, "M", "v3"));
            offer.HasCardStatus.Should().BeFalse();

            var installed = center.Notifications[1];
            installed.CardTitle.Replace("\u00AD", "", StringComparison.Ordinal).Should().Be(
                L.Get(LocalizationKeys.Notifications_InstalledVersionFormat, "Example Python Module", "main@a7880e6"));
            installed.HasCardStatus.Should().BeFalse();

            center.Notifications[2].CardStatusText.Should().Be(L.Get(LocalizationKeys.Notifications_OperationInterrupted));
        }

        /// <summary>
        /// Verifies that the staged release stays pending and becomes successful once the installed tag confirms it.
        /// </summary>
        [Fact]
        public async Task Prepared_application_update_uses_the_installed_release_after_restart()
        {
            var center = CreateCenter();
            await center.InitializeAsync();
            var attempt = await center.StartDownloadAsync(
                "app-update", "ASLM", "", "app", "aslm", "v2.0", true, targetVersion: "2.0");
            await center.PrepareDownloadForRestartAsync(attempt, "Ready to restart");

            var pending = CreateCenter();
            await pending.InitializeAsync(installedReleaseTag: "v1.0", pendingReleaseTag: "v2.0");
            pending.Notifications.Single().AwaitingRestart.Should().BeTrue();
            pending.Notifications.Single().CardStatusText.Should().Be("Ready to restart");
            pending.DismissAll();
            pending.Notifications.Should().ContainSingle();

            var applied = CreateCenter();
            await applied.InitializeAsync(installedReleaseTag: "v2.0");
            var row = applied.Notifications.Single();
            row.AwaitingRestart.Should().BeFalse();
            row.CanDismiss.Should().BeTrue();
            row.Severity.Should().Be(AppNotificationSeverity.Success);
            row.CardTitle.Should().Be(L.Get(LocalizationKeys.Notifications_UpdatedFormat, "ASLM", "2.0"));
            row.HasCardStatus.Should().BeFalse();
        }

        /// <summary>
        /// Verifies that after 230 published notifications both the live list and the persisted file hold at most 200
        /// rows.
        /// </summary>
        [Fact]
        public async Task Persisted_history_is_capped_at_two_hundred_rows()
        {
            var center = CreateCenter();
            await center.InitializeAsync();
            for (var i = 0; i < 230; i++)
            {
                center.PublishUpdateOffer("module", $"m-{i}", "1.0", $"M{i}", "1.0");
            }

            center.Notifications.ToList().Should().HaveCount(200, "both the live list and the file are capped");

            // Wait for the file and give the debounced save time to write the final state.
            var persisted = Path.Combine(_root.DataAppDir, "ASLM_Notifications.json");
            await TestWait.UntilAsync(() => File.Exists(persisted));
            await Task.Delay(1500);
            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(persisted));

            document.RootElement.GetProperty("Notifications").GetArrayLength().Should()
                .BeLessThanOrEqualTo(200);
        }
    }
}
