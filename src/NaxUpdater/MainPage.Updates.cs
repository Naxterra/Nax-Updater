using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using NaxUpdater.Services;
using System.Diagnostics;

namespace NaxUpdater;

public sealed partial class MainPage
{
    // The details panel follows the selected row while an update changes its state.
    private UpdateRow? _detailsRow;

    private void ApplyUpdateFilter()
    {
        var selectedIdentity = _userSelectedUpdate
            ? (UpdatesList.SelectedItem as UpdateRow)?.Source.ApplicationIdentity
            : null;
        var filter = FilterBox.Text.Trim();
        var statusFilter = (UpdateStatusFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var filtered = _allUpdates
            .Where(row => (string.IsNullOrWhiteSpace(filter) || MatchesUpdateFilter(row, filter)) &&
                          MatchesUpdateStatusFilter(row, statusFilter))
            .ToArray();

        var sorted = SortUpdateRows(filtered);
        _applyingUpdateFilter = true;
        try
        {
            ObservableCollectionSync.Apply(_updates, sorted);

            UpdateCountText.Text = string.IsNullOrWhiteSpace(filter) && statusFilter == "all"
                ? LocalizationService.Format("UpdateCountFormat", _availableUpdateCount, _allUpdates.Count)
                : LocalizationService.Format("FilteredUpdateCountFormat", _availableUpdateCount, _updates.Count, _allUpdates.Count);
            var restored = selectedIdentity is null
                ? null
                : _updates.FirstOrDefault(row => row.Source.ApplicationIdentity == selectedIdentity);
            UpdatesList.SelectedItem = restored ?? _updates.FirstOrDefault();
        }
        finally
        {
            _applyingUpdateFilter = false;
        }
    }

    private static bool MatchesUpdateStatusFilter(UpdateRow row, string filter) => filter switch
    {
        "updates" => row.Source.IsInstallable || row.Source.Status is UpdateStatus.Available or UpdateStatus.StoreQueued,
        "known" => row.Source.Status == UpdateStatus.NewerReleaseKnown,
        "current" => row.Source.Status == UpdateStatus.Current,
        "errors" => row.Source.Status == UpdateStatus.Error,
        "other" => row.Source.Status is UpdateStatus.ManagedExternally or UpdateStatus.Unsupported,
        _ => true
    };

    private void UpdateStatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UpdatesList is not null && UpdateCountText is not null)
        {
            ApplyUpdateFilter();
        }
    }

    private static bool MatchesUpdateFilter(UpdateRow row, string filter) =>
        row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        row.Installed.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        row.Available.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        row.Provider.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        row.Status.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (row.Application?.Publisher.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (row.Application?.Source.PrimaryInstallPath?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private void UpdateResultSortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var requested = button.Tag?.ToString() == "Name" ? ResultSortColumn.Name : ResultSortColumn.Status;
        if (requested == _updateSortColumn)
        {
            _updateSortDescending = !_updateSortDescending;
        }
        else
        {
            _updateSortColumn = requested;
            _updateSortDescending = false;
        }
        UpdateResultSortHeaders();
        ApplyUpdateFilter();
    }

    private IEnumerable<UpdateRow> SortUpdateRows(IEnumerable<UpdateRow> rows)
    {
        if (_updateSortColumn == ResultSortColumn.Name)
        {
            return _updateSortDescending
                ? rows.OrderByDescending(static row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                : rows.OrderBy(static row => row.Name, StringComparer.CurrentCultureIgnoreCase);
        }
        return _updateSortDescending
            ? rows.OrderByDescending(static row => UpdateStatusPriority(row.Source.Status))
                .ThenBy(static row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            : rows.OrderBy(static row => UpdateStatusPriority(row.Source.Status))
                .ThenBy(static row => row.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private void UpdateResultSortHeaders()
    {
        var direction = _updateSortDescending ? " ↓" : " ↑";
        UpdateNameHeaderText.Text = LocalizationService.Get("ApplicationLabel") +
                                    (_updateSortColumn == ResultSortColumn.Name ? direction : string.Empty);
        UpdateStatusHeaderText.Text = LocalizationService.Get("StatusLabel") +
                                      (_updateSortColumn == ResultSortColumn.Status ? direction : string.Empty);
    }

    private static int UpdateStatusPriority(UpdateStatus status) => status switch
    {
        UpdateStatus.StoreQueued => 1,
        UpdateStatus.Available => 0,
        UpdateStatus.NewerReleaseKnown => 1,
        UpdateStatus.Error => 2,
        UpdateStatus.Current => 3,
        UpdateStatus.ManagedExternally => 4,
        _ => 5
    };

    private async void RetryFailedChecksButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _massUpdateBusy || _snapshot is null) return;
        await RunScanAsync(token => CheckUpdatesCoreAsync(true, null, token, retryFailed: true));
    }

    private void RefreshRetryButton()
    {
        var eligible = _snapshot?.Applications.Count(a => !a.IsSystemComponent) ?? 0;
        var incomplete = _allUpdates.Any(r => r.Source.Status == UpdateStatus.Error) ||
                         eligible > 0 && _lastCheckSnapshot is not null && !_checksComplete;
        RetryFailedChecksButton.Visibility = incomplete ? Visibility.Visible : Visibility.Collapsed;
        RetryFailedChecksButton.IsEnabled = !_updateBusy && !_massUpdateBusy && _snapshot is not null;
    }

    private void PublishUpdateResults(UpdateCheckSnapshot snapshot)
    {
        _lastCheckSnapshot = snapshot;
        var applications = _allApplications.ToDictionary(r => r.Source.Identity, StringComparer.Ordinal);
        var existing = _allUpdates.ToDictionary(r => r.Source.ApplicationIdentity, StringComparer.Ordinal);
        _allUpdates = snapshot.Results.Select(update =>
        {
            var application = applications.GetValueOrDefault(update.ApplicationIdentity);
            var row = existing.TryGetValue(update.ApplicationIdentity, out var prior) &&
                      ReferenceEquals(prior.Source, update) && ReferenceEquals(prior.Application, application)
                ? prior : new UpdateRow(update, application, UpdateColumns);
            if (_runOutcomes.TryGetValue(update.ApplicationIdentity, out var outcome) &&
                !(outcome.State == UpdateRunState.Succeeded && update.IsInstallable) &&
                !(outcome.State == UpdateRunState.Failed && update.AvailabilityReason == UpdateAvailabilityReason.DesktopRuntimePatchInstalled))
            {
                row.RunState = outcome.State;
                row.RunMessage = outcome.Message;
            }
            else
            {
                row.RunState = UpdateRunState.None;
                row.RunMessage = null;
            }
            return row;
        }).ToArray();
        _availableUpdateCount = snapshot.InstallableUpdateCount;
        ApplyUpdateFilter();
        RefreshMassUpdateButton();
        ShowUpdatesButton.Content = LocalizationService.Format("UpdatesNavigationCoverage",
            snapshot.InstallableUpdateCount + snapshot.KnownReleaseCount,
            snapshot.CheckedVersionCount, _snapshot?.Applications.Count(a => !a.IsSystemComponent) ?? snapshot.Results.Count);
        ShowUpdatesButton.IsEnabled = _allUpdates.Count > 0;
        RefreshRetryButton();
    }

    private async Task CheckUpdatesCoreAsync(bool showWorkspace, SharedScanSources? sources, CancellationToken token,
        bool retryFailed = false)
    {
        sources ??= new SharedScanSources(_httpClient, _sourceCacheRoot, token);
        _ = sources.DriversAsync();
        _scanSources = sources;
        var checkTimer = Stopwatch.StartNew();
        if (_snapshot is null) await ScanInventoryCoreAsync(token);
        if (_snapshot is null) return;
        var inventory = _snapshot;
        var previous = retryFailed ? _lastCheckSnapshot : null;
        var partial = (previous?.Results ?? []).ToDictionary(r => r.ApplicationIdentity, StringComparer.Ordinal);
        _checksComplete = false;
        if (!retryFailed) _userSelectedUpdate = false;
        PublishUpdateResults(new(DateTimeOffset.UtcNow, partial.Values.ToArray(),
            partial.Values.Count(r => r.Status == UpdateStatus.Unsupported)));
        var acceptingProgress = true;
        var hasUnpublishedProgress = false;
        var lastPublish = Stopwatch.GetTimestamp();
        var flushTimer = DispatcherQueue.CreateTimer();
        flushTimer.Interval = ProgressPublishInterval;
        flushTimer.IsRepeating = false;
        void FlushPartialProgress()
        {
            flushTimer.Stop();
            if (!hasUnpublishedProgress) return;
            hasUnpublishedProgress = false;
            lastPublish = Stopwatch.GetTimestamp();
            PublishUpdateResults(new(DateTimeOffset.UtcNow, partial.Values.ToArray(),
                partial.Values.Count(r => r.Status == UpdateStatus.Unsupported)));
        }
        flushTimer.Tick += (_, _) =>
        {
            if (!acceptingProgress || token.IsCancellationRequested) return;
            FlushPartialProgress();
        };
        UpdateBar.IsOpen = false;
        try
        {
            var catalogPath = Path.Combine(AppContext.BaseDirectory, "Configuration", "update-providers.json");
            var catalog = await UpdateProviderCatalogLoader.LoadAsync(catalogPath, token);
            var service = new UpdateCheckService(_httpClient, catalog, sharedSources: sources);
            var progress = new Progress<UpdateCheckProgress>(state =>
            {
                if (!acceptingProgress || token.IsCancellationRequested) return;
                StatusText.Text = state.Phase == "sources"
                    ? LocalizationService.Get("RefreshingProviderCatalogs")
                    : LocalizationService.Format("ProviderCheckProgress", state.Completed, state.Total, state.ApplicationName ?? "");
                if (state.Result is { } result)
                {
                    partial[result.ApplicationIdentity] = result;
                    hasUnpublishedProgress = true;
                    // Throttle intermediate list refreshes so 150 concurrent checks do not
                    // rebuild, sort, and relayout the table on every individual completion,
                    // while a trailing timer guarantees bursts flush within 250 ms.
                    if (Stopwatch.GetElapsedTime(lastPublish) >= ProgressPublishInterval || state.Completed == state.Total)
                    {
                        FlushPartialProgress();
                    }
                    else if (!flushTimer.IsRunning)
                    {
                        flushTimer.Start();
                    }
                }
            });
            var result = previous is null
                ? await service.CheckAsync(inventory, token, progress)
                : await service.RetryFailedAsync(inventory, previous, token, progress);
            acceptingProgress = false;
            flushTimer.Stop();
            token.ThrowIfCancellationRequested();
            ReconcileCompletedOperation(result);
            await SaveScanDiagnosticsAsync(result, checkTimer.Elapsed);
            _checksComplete = true;
            if (result.FailedCheckCount == 0 && inventory.Issues.Count == 0)
            {
                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NaxUpdater", "Downloads");
                await Task.Run(() => UpdatePayloadCache.Sweep(downloads, result.Results));
            }
            hasUnpublishedProgress = false;
            PublishUpdateResults(result);
            if (showWorkspace) ShowUpdatesWorkspace();
            var errors = result.FailedCheckCount;
            StatusText.Text = LocalizationService.Format("UpdateCheckedStatusDetailed", result.CheckedVersionCount,
                result.InstallableUpdateCount, result.ManagedExternallyCount, result.UnsupportedApplicationCount,
                errors, result.KnownReleaseCount);
            UpdateBar.Title = result.InstallableUpdateCount > 0
                ? LocalizationService.Format("UpdatesAvailableTitle", result.InstallableUpdateCount)
                : result.StoreQueueCount > 0 ? LocalizationService.Format("StoreQueueTitle", result.StoreQueueCount)
                : result.KnownReleaseCount > 0 ? LocalizationService.Format("ReleasesKnownTitle", result.KnownReleaseCount)
                : LocalizationService.Get(result.AllCurrent ? "ApplicationsCurrent" : errors > 0 ? "ChecksIncomplete" : "NoUpdatesAmongChecked");
            UpdateBar.Message = StatusText.Text;
            StatusText.Text += $" · {checkTimer.Elapsed.TotalSeconds:N1} s";
            UpdateBar.Severity = errors > 0 ? InfoBarSeverity.Warning : result.InstallableUpdateCount > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
            UpdateBar.IsOpen = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            acceptingProgress = false;
            FlushPartialProgress();
            throw;
        }
        catch (Exception exception)
        {
            ShowUnexpectedFailure(LocalizationService.Get("UpdateCheckFailed"), exception);
            StatusText.Text = LocalizationService.Get("UpdateCheckDidNotComplete");
        }
        finally
        {
            acceptingProgress = false;
            flushTimer.Stop();
        }
    }

    private static async Task SaveScanDiagnosticsAsync(UpdateCheckSnapshot snapshot, TimeSpan elapsed)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NaxUpdater");
            Directory.CreateDirectory(directory);
            var report = new { snapshot.CheckedAt, ElapsedSeconds = elapsed.TotalSeconds, snapshot.CheckedVersionCount,
                snapshot.FailedCheckCount, snapshot.InstallableUpdateCount, snapshot.StoreQueueCount,
                Results = snapshot.Results.Select(r => new { r.DisplayName, r.ProviderId, r.InstalledVersion, r.AvailableVersion, r.Architecture,
                    Status = r.Status.ToString(), r.Message, r.SourceChecks }) };
            await File.WriteAllTextAsync(Path.Combine(directory, "last-scan.json"), System.Text.Json.JsonSerializer.Serialize(report));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void ReconcileCompletedOperation(UpdateCheckSnapshot snapshot)
    {
        if (_operationCancellation is not null || _massUpdateBusy) return;
        using var lease = _updateTransactionLease.TryAcquire();
        if (lease is null) return; // Startup recovery or another update owns it.
        var operation = _updateOperationJournal.ReadLatest();
        if (operation is null || operation.Stage is not (UpdateTransactionStage.FailedNeedsAttention or UpdateTransactionStage.Indeterminate)) return;
        var assessment = snapshot.Results.FirstOrDefault(r => r.ApplicationIdentity.Equals(operation.ApplicationIdentity, StringComparison.OrdinalIgnoreCase));
        if (UpdateRecoveryVerifier.HasReachedTarget(operation, assessment?.InstalledVersion, assessment))
            _updateOperationJournal.Record(operation, UpdateTransactionStage.Succeeded, DateTimeOffset.UtcNow);
    }

    private async void UpdateReleaseLink_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateReleaseLink.Tag is Uri page)
            await OpenInBrowserAsync(page);
    }

    private void DetailsRow_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not UpdateRow row || !ReferenceEquals(row, _detailsRow)) return;
        if (e.PropertyName is nameof(UpdateRow.Status) or nameof(UpdateRow.RunState))
            UpdateProviderText.Text = $"{row.Provider} · {row.Status}";
        if (e.PropertyName is nameof(UpdateRow.Message) or nameof(UpdateRow.RunMessage))
            UpdateMessageText.Text = row.Message;
    }

    private void UpdatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_applyingUpdateFilter && UpdatesList.SelectedItem is UpdateRow)
            _userSelectedUpdate = true;
        if (_detailsRow is not null) _detailsRow.PropertyChanged -= DetailsRow_PropertyChanged;
        _detailsRow = UpdatesList.SelectedItem as UpdateRow;
        if (_detailsRow is not null) _detailsRow.PropertyChanged += DetailsRow_PropertyChanged;
        if (UpdatesList.SelectedItem is not UpdateRow row)
        {
            UpdateDetailsPlaceholder.Visibility = Visibility.Visible;
            UpdateDetailsContent.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateDetailsPlaceholder.Visibility = Visibility.Collapsed;
        UpdateDetailsContent.Visibility = Visibility.Visible;
        UpdateNameText.Text = row.Name;
        UpdateProviderText.Text = $"{row.Provider} · {row.Status}";
        UpdateVersionText.Text = row.VersionChange;
        UpdateSourcesText.Text = string.Join(Environment.NewLine, (row.Source.SourceChecks ?? []).Select(source =>
            $"{LocalizationService.Text(source.ProviderDisplayName)}: {(source.Status is UpdateStatus.ManagedExternally or UpdateStatus.Unsupported ? LocalizationService.Get("StatusNotChecked") : UpdateRow.StatusText(source.Status, source.AvailabilityReason, source.RolloutThresholdPercent))}"));
        UpdatePlatformText.Text = row.PlatformDetail;
        UpdateSecurityText.Text = row.SecurityDetail;
        if (Uri.TryCreate(row.Source.ReleaseNotesUrl, UriKind.Absolute, out var releaseUri) &&
            (releaseUri.Scheme == Uri.UriSchemeHttps || releaseUri.Scheme == Uri.UriSchemeHttp ||
             releaseUri.Scheme.Equals("ms-windows-store", StringComparison.OrdinalIgnoreCase)))
        {
            UpdateReleaseLink.Tag = releaseUri;
            UpdateReleaseLinkText.Text = releaseUri.AbsoluteUri;
            UpdateReleaseLink.Visibility = Visibility.Visible;
            UpdateReleaseText.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateReleaseLink.Tag = null;
            UpdateReleaseLink.Visibility = Visibility.Collapsed;
            UpdateReleaseText.Text = row.ReleaseNotes;
            UpdateReleaseText.Visibility = Visibility.Visible;
        }
        UpdateMessageText.Text = row.Message;
    }

    private async void UpdateRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_checksComplete || _updateBusy || _massUpdateBusy || _restartPending || sender is not Button { DataContext: UpdateRow row } ||
            !row.CanInstall || row.Source.ExecutionPlan is null)
        {
            return;
        }
        UpdatesList.SelectedItem = row;
        await ExecuteUpdateAsync(row, rescanAfterSuccess: true);
    }

    private async void UpdateAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_checksComplete || _updateBusy || _massUpdateBusy || _restartPending)
        {
            return;
        }
        var queue = _allUpdates
            .Where(static row => row.Source.InUpdateAll)
            .OrderBy(static row => row.Source.ExecutionPlan?.Kind is UpdateExecutionKind.StorePackage or UpdateExecutionKind.NativeStorePackage or UpdateExecutionKind.NativeStoreQueue ? 1 : 0)
            .ThenBy(static row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (queue.Length == 0)
        {
            return;
        }

        _massUpdateBusy = true;
        _stopBatch = false;
        _restartPending = false;
        _restartRecommended = false;
        _runOutcomes.Clear();
        foreach (var row in _allUpdates)
        {
            row.RunState = UpdateRunState.None;
            row.RunMessage = null;
        }
        foreach (var row in queue) row.RunState = UpdateRunState.Queued;
        RefreshMassUpdateButton();
        var completed = 0;
        var failed = 0;
        try
        {
            foreach (var row in queue)
            {
                if (_stopBatch) break;
                UpdatesList.SelectedItem = row;
                StatusText.Text = LocalizationService.Format("MassUpdateProgress", completed + failed + 1, queue.Length, row.Name);
                if (await ExecuteUpdateAsync(row, rescanAfterSuccess: false))
                {
                    completed++;
                }
                else
                {
                    failed++;
                    if (_restartPending)
                    {
                        break;
                    }
                }
            }
            foreach (var row in queue.Where(static row => row.RunState == UpdateRunState.Queued))
            {
                row.RunState = UpdateRunState.Skipped;
                _runOutcomes[row.Source.ApplicationIdentity] = (UpdateRunState.Skipped, null);
            }
            if (!_closeWhenIdle && _pendingLanguage is null)
                await RescanAfterChangeAsync();
            UpdateBar.Title = LocalizationService.Get("MassUpdateCompleteTitle");
            UpdateBar.Message = LocalizationService.Format("MassUpdateCompleteMessage", completed, failed) +
                (_restartRecommended ? Environment.NewLine + LocalizationService.Get("MassUpdateRestartRecommended") : string.Empty);
            UpdateBar.Severity = failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            UpdateBar.IsOpen = true;
        }
        finally
        {
            _massUpdateBusy = false;
            RefreshMassUpdateButton();
            EndMutation();
        }
    }

    private async Task<bool> ExecuteUpdateAsync(UpdateRow row, bool rescanAfterSuccess)
    {
        if (!row.CanInstall || row.Source.ExecutionPlan is null)
        {
            return false;
        }
        _mutationActive = true;
        SetUpdateBusy(true, LocalizationService.Format("RevalidatingUpdate", row.Name));
        using var operationCancellation = new CancellationTokenSource();
        _operationCancellation = operationCancellation;
        _waitingForStoreQueue = row.Source.ExecutionPlan.Kind == UpdateExecutionKind.NativeStoreQueue;
        StopOperationButton.Content = LocalizationService.Get(_waitingForStoreQueue ? "StopStoreWaiting" : "StopAfterCurrent");
        StopOperationButton.Visibility = _waitingForStoreQueue || _massUpdateBusy ? Visibility.Visible : Visibility.Collapsed;
        StopOperationButton.IsEnabled = true;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = true;
        row.RunState = UpdateRunState.Preparing;
        row.RunMessage = null;
        try
        {
            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NaxUpdater",
                "Downloads");
            var transactionProgress = new Progress<UpdateTransactionProgress>(state =>
            {
                UpdateProgress.IsIndeterminate = state.Fraction is null;
                if (state.Fraction is double fraction)
                {
                    UpdateProgress.Value = Math.Clamp(fraction * 100, 0, 100);
                }
                row.RunState = state.Stage switch
                {
                    UpdateTransactionStage.Revalidating or UpdateTransactionStage.Preparing => UpdateRunState.Preparing,
                    UpdateTransactionStage.Quiescing => UpdateRunState.Closing,
                    UpdateTransactionStage.Applying => UpdateRunState.Installing,
                    UpdateTransactionStage.Verifying => UpdateRunState.Verifying,
                    _ => row.RunState
                };
                StatusText.Text = state.Stage switch
                {
                    UpdateTransactionStage.Revalidating => LocalizationService.Format("RevalidatingUpdate", row.Name),
                    UpdateTransactionStage.Preparing => LocalizationService.Format("PreparingUpdate", row.Name),
                    UpdateTransactionStage.Quiescing => LocalizationService.Format("TransactionClosingApplications", row.Name),
                    UpdateTransactionStage.Applying => LocalizationService.Format("ApplyingUpdate", row.Name),
                    UpdateTransactionStage.Verifying => LocalizationService.Format("VerifyingUpdate", row.Name),
                    _ => StatusText.Text
                };
            });
            var backend = new DefaultUpdateTransactionBackend(
                _updateExecutionService,
                new UpdatePackageDownloader(new NativeAuthenticodeVerifier()),
                RevalidateUpdateAsync);
            var transaction = await new UpdateTransactionCoordinator(
                backend,
                _updateOperationJournal,
                _updateTransactionLease).ApplyAsync(
                row.Source,
                cacheRoot,
                transactionProgress, operationCancellation.Token);

            var alreadyUpdated = UpdateTransactionCoordinator.CompletedElsewhere(transaction, row.Source);
            var succeeded = transaction.IsSuccess || alreadyUpdated;
            UpdateBar.Title = succeeded
                ? LocalizationService.Format("UpdateCompletedTitle", row.Name)
                : transaction.RequiresRestart
                    ? LocalizationService.Format("UpdateRestartRequiredTitle", row.Name)
                : transaction.Stage == UpdateTransactionStage.NoLongerApplicable
                    ? LocalizationService.Format("UpdateNoLongerApplicableTitle", row.Name)
                    : LocalizationService.Format("NotUpdatedTitle", row.Name);
            UpdateBar.Message = transaction.RemainingProcessNames is { Count: > 0 }
                ? LocalizationService.Format("TransactionCloseFailed", string.Join(", ", transaction.RemainingProcessNames))
                : transaction.Stage == UpdateTransactionStage.PendingReboot
                    ? LocalizationService.Get("RestartRequired")
                    : succeeded
                        ? row.Source.ExecutionPlan.Kind == UpdateExecutionKind.NativeStoreQueue || alreadyUpdated
                            ? LocalizationService.Format("StoreOperationCompleted", transaction.FreshAssessment?.InstalledVersion ?? "?")
                            : transaction.RestartRecommended
                                ? LocalizationService.Get("UpdateCompletedRestartRecommended")
                                : LocalizationService.Get("UpdateCompletedMessage")
                        : LocalizationService.Text(transaction.Error) ?? LocalizationService.Get("UpdateTransactionFailed");
            UpdateBar.Severity = succeeded
                ? InfoBarSeverity.Success
                : transaction.RequiresRestart
                    ? InfoBarSeverity.Warning
                : transaction.Stage is UpdateTransactionStage.NoLongerApplicable or UpdateTransactionStage.CanceledBeforeChange
                    ? InfoBarSeverity.Informational
                    : InfoBarSeverity.Error;
            UpdateBar.IsOpen = true;
            _restartPending = transaction.RequiresRestart;
            _restartRecommended |= transaction.RestartRecommended || transaction.RequiresRestart;
            row.RunState = succeeded ? UpdateRunState.Succeeded
                : transaction.RequiresRestart ? UpdateRunState.RestartRequired
                : transaction.Stage is UpdateTransactionStage.NoLongerApplicable or UpdateTransactionStage.CanceledBeforeChange ? UpdateRunState.NotApplicable
                : UpdateRunState.Failed;
            if (transaction.JournalWarning is { } journalWarning)
                UpdateBar.Message += Environment.NewLine + LocalizationService.Text(journalWarning);
            row.RunMessage = !succeeded ? UpdateBar.Message
                : transaction.RestartRecommended ? LocalizationService.Get("UpdateCompletedRestartRecommended") : null;
            _runOutcomes[row.Source.ApplicationIdentity] = (row.RunState, row.RunMessage);

            var unchanged = transaction.Stage is UpdateTransactionStage.CanceledBeforeChange or UpdateTransactionStage.FailedBeforeChange;
            if (!unchanged)
            {
                _scanSources = null; // A change to the system makes the scan's driver check stale.
                _lastDriverSnapshot = null;
                _driversCheckedAt = null;
            }
            if (rescanAfterSuccess && !unchanged && !_closeWhenIdle && _pendingLanguage is null)
            {
                var outcomeTitle = UpdateBar.Title;
                var outcomeMessage = UpdateBar.Message;
                var outcomeSeverity = UpdateBar.Severity;
                try { await RescanAfterChangeAsync(); }
                finally
                {
                    if (_checksComplete && (_snapshot?.Issues.Count ?? 0) == 0)
                    {
                        UpdateBar.Title = outcomeTitle;
                        UpdateBar.Message = outcomeMessage;
                        UpdateBar.Severity = outcomeSeverity;
                        UpdateBar.IsOpen = true;
                        StatusText.Text = outcomeTitle;
                    }
                }
            }
            return succeeded;
        }
        catch (Exception exception)
        {
            // An exception here would end the process in the middle of "Alle aktualisieren".
            ShowUnexpectedFailure(LocalizationService.Format("NotUpdatedTitle", row.Name), exception);
            row.RunState = UpdateRunState.Failed;
            row.RunMessage = UpdateBar.Message;
            _runOutcomes[row.Source.ApplicationIdentity] = (row.RunState, row.RunMessage);
            return false;
        }
        finally
        {
            _waitingForStoreQueue = false;
            _operationCancellation = null;
            StopOperationButton.Visibility = Visibility.Collapsed;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateProgress.IsIndeterminate = false;
            SetUpdateBusy(false, null);
            EndMutation();
        }
    }

    private async void StopOperationButton_Click(object sender, RoutedEventArgs e)
    {
        _stopBatch = true;
        StopOperationButton.IsEnabled = false;
        if (_waitingForStoreQueue && _operationCancellation is { } cancellation)
            await cancellation.CancelAsync();
    }

    private async Task<UpdateCheckResult?> RevalidateUpdateAsync(
        UpdateCheckResult previous,
        CancellationToken cancellationToken)
    {
        var inventory = previous.ExecutionPlan?.Kind is UpdateExecutionKind.StorePackage or UpdateExecutionKind.NativeStorePackage or UpdateExecutionKind.NativeStoreQueue &&
            previous.ApplicationIdentity.StartsWith("msix:", StringComparison.OrdinalIgnoreCase)
            ? await _inventoryService.ScanStorePackageAsync(previous.ApplicationIdentity[5..], cancellationToken)
            : await _inventoryService.ScanAsync(cancellationToken);
        var application = UpdateCorrelation.FindMatch(
            inventory.Applications, previous.ApplicationIdentity, previous.CorrelationKey);
        if (application is null)
        {
            return null;
        }
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "Configuration", "update-providers.json");
        var catalog = await UpdateProviderCatalogLoader.LoadAsync(catalogPath, cancellationToken);
        var isolatedInventory = new InventorySnapshot(
            inventory.ScannedAt,
            new[] { application },
            [],
            inventory.Issues);
        var result = await new UpdateCheckService(_httpClient, catalog, useStoreBatchCheck: false)
            .CheckAsync(isolatedInventory, cancellationToken);
        return result.Results.SingleOrDefault();
    }

    private async Task RescanAfterChangeAsync()
    {
        await RunScanAsync(async token =>
        {
            var sources = new SharedScanSources(_httpClient, _sourceCacheRoot, token);
            _ = sources.DriversAsync();
            sources.StartStoreBatch();
            await ScanInventoryCoreAsync(token);
            if (_snapshot is not null)
            {
                await CheckUpdatesCoreAsync(showWorkspace: true, sources, token);
                return;
            }
            _checksComplete = false;
            RefreshMassUpdateButton();
        });
        if (_snapshot is null)
        {
            _checksComplete = false;
            RefreshMassUpdateButton();
        }
    }

    private void RefreshMassUpdateButton()
    {
        var verifiedUpdates = _allUpdates.Count(static row =>
            row.Source.InUpdateAll &&
            row.Source.ExecutionPlan?.Kind is not (UpdateExecutionKind.StorePackage or UpdateExecutionKind.NativeStorePackage or UpdateExecutionKind.NativeStoreQueue));
        var storeActions = _allUpdates.Count(static row =>
            row.Source.InUpdateAll &&
            row.Source.ExecutionPlan?.Kind is UpdateExecutionKind.StorePackage or UpdateExecutionKind.NativeStorePackage or UpdateExecutionKind.NativeStoreQueue);
        UpdateAllButton.Content = storeActions > 0
            ? LocalizationService.Format("UpdateAllCountWithStore", verifiedUpdates, storeActions)
            : LocalizationService.Format("UpdateAllCount", verifiedUpdates);
        UpdateAllButton.IsEnabled = _checksComplete && !_updateBusy && !_massUpdateBusy && !_restartPending && verifiedUpdates + storeActions > 0;
        var locked = !_checksComplete || _updateBusy || _massUpdateBusy || _restartPending;
        foreach (var row in _allUpdates) row.ActionsLocked = locked;
        foreach (var row in _allDrivers) row.ActionsLocked = _updateBusy || _massUpdateBusy || _restartPending;
    }
}
