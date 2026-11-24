using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using NaxUpdater.Services;

namespace NaxUpdater;

public sealed partial class MainPage
{
    private void ApplyDriverFilter()
    {
        var selectedIdentity = (DriversList.SelectedItem as ManufacturerDriverRow)?.Source.Driver.Identity;
        var filter = FilterBox.Text.Trim();
        var statusFilter = (DriverStatusFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var filtered = _allDrivers
            .Where(row => (string.IsNullOrWhiteSpace(filter) || MatchesDriverFilter(row, filter)) &&
                          MatchesDriverStatusFilter(row, statusFilter))
            .ToArray();
        var sorted = SortDriverRows(filtered);
        ObservableCollectionSync.Apply(_drivers, sorted);
        var direct = _allDrivers.Count(static row => row.CanUpdate);
        var manufacturerPackages = _allDrivers.Count(static row => row.Source.IsLiveChecked);
        DriverCountText.Text = string.IsNullOrWhiteSpace(filter) && statusFilter == "all"
            ? LocalizationService.Format("DriverCountFormat", direct, manufacturerPackages, _allDrivers.Count)
            : LocalizationService.Format("FilteredDriverCountFormat", direct, manufacturerPackages, _drivers.Count, _allDrivers.Count);
        var restored = selectedIdentity is null
            ? null
            : _drivers.FirstOrDefault(row => row.Source.Driver.Identity == selectedIdentity);
        DriversList.SelectedItem = restored ?? _drivers.FirstOrDefault();
    }

    private static bool MatchesDriverFilter(ManufacturerDriverRow row, string filter) =>
        row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        row.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        row.Installed.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        row.Available.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        row.Provider.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        row.Status.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        row.Source.Message.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesDriverStatusFilter(ManufacturerDriverRow row, string filter) => filter switch
    {
        "updates" => row.Source.Status == ManufacturerDriverStatus.Available,
        "current" => row.Source.Status is ManufacturerDriverStatus.Current or ManufacturerDriverStatus.NoUpdateRequired,
        "managed" => row.Source.Status == ManufacturerDriverStatus.VendorSoftwareManaged,
        "source" => row.Source.Status == ManufacturerDriverStatus.OfficialSourceOnly,
        "unverified" => row.Source.Status == ManufacturerDriverStatus.NoVerifiedCatalog,
        "errors" => row.Source.HasCheckIssue,
        _ => true
    };

    private void DriverStatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriversList is not null && DriverCountText is not null)
        {
            ApplyDriverFilter();
        }
    }

    private void DriverSortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var requested = button.Tag?.ToString() == "Name" ? ResultSortColumn.Name : ResultSortColumn.Status;
        if (requested == _driverSortColumn)
        {
            _driverSortDescending = !_driverSortDescending;
        }
        else
        {
            _driverSortColumn = requested;
            _driverSortDescending = false;
        }
        UpdateDriverSortHeaders();
        ApplyDriverFilter();
    }

    private IEnumerable<ManufacturerDriverRow> SortDriverRows(IEnumerable<ManufacturerDriverRow> rows)
    {
        if (_driverSortColumn == ResultSortColumn.Name)
        {
            return _driverSortDescending
                ? rows.OrderByDescending(static row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                : rows.OrderBy(static row => row.Name, StringComparer.CurrentCultureIgnoreCase);
        }
        return _driverSortDescending
            ? rows.OrderByDescending(static row => DriverStatusPriority(row.Source.Status))
                .ThenBy(static row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            : rows.OrderBy(static row => DriverStatusPriority(row.Source.Status))
                .ThenBy(static row => row.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private void UpdateDriverSortHeaders()
    {
        var direction = _driverSortDescending ? " ↓" : " ↑";
        DriverNameHeaderText.Text = LocalizationService.Get("DriverDeviceLabel") +
                                    (_driverSortColumn == ResultSortColumn.Name ? direction : string.Empty);
        DriverStatusHeaderText.Text = LocalizationService.Get("StatusLabel") +
                                      (_driverSortColumn == ResultSortColumn.Status ? direction : string.Empty);
    }

    private static int DriverStatusPriority(ManufacturerDriverStatus status) => status switch
    {
        ManufacturerDriverStatus.Available => 0,
        ManufacturerDriverStatus.Error => 1,
        ManufacturerDriverStatus.Current => 2,
        ManufacturerDriverStatus.NoUpdateRequired => 3,
        ManufacturerDriverStatus.VendorSoftwareManaged => 4,
        ManufacturerDriverStatus.OfficialSourceOnly => 5,
        _ => 6
    };

    private async void DriversButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _massUpdateBusy)
        {
            return;
        }
        InventoryWorkspace.Visibility = Visibility.Collapsed;
        UpdatesWorkspace.Visibility = Visibility.Collapsed;
        DriversWorkspace.Visibility = Visibility.Visible;
        ConfigureDriverHeader();
        FilterBox.IsEnabled = true;
        ShowSystemComponentsCheckBox.IsEnabled = false;
        await CheckManufacturerDriversAsync(reuseScan: true);
    }

    // Opening the driver view shows the latest scan's driver check while it is fresh; "check
    // drivers" and the check after a driver install always run a new one.
    private Task CheckManufacturerDriversAsync(bool reuseScan = false) =>
        RunScanAsync(token => CheckManufacturerDriversCoreAsync(reuseScan, token));

    private async Task CheckManufacturerDriversCoreAsync(bool reuseScan, CancellationToken token)
    {
        if (reuseScan && _lastDriverSnapshot is { } cachedSnapshot && _driversCheckedAt is { } checkedAt &&
            DateTimeOffset.UtcNow - checkedAt < ScanDriverReuse)
        {
            ApplyDriverFilter();
            var cachedDirect = cachedSnapshot.Results.Count(static result => result.ExecutableUpdate?.IsInstallable == true);
            var cachedDownloads = cachedSnapshot.Results.Count(static result =>
                result.Status == ManufacturerDriverStatus.Available && result.ExecutableUpdate is null);
            var cachedPackages = cachedSnapshot.CheckedSourceCount;
            UpdateDriverSummary(cachedSnapshot, cachedPackages, cachedDirect);
            StatusText.Text = cachedDirect + cachedDownloads == 0
                ? LocalizationService.Format("DriversCheckedNoUpdates", cachedPackages)
                : LocalizationService.Format("DriverUpdatesAvailable", cachedDirect, cachedDownloads);
            return;
        }
        StatusText.Text = LocalizationService.Get("CheckingManufacturerDrivers");
        UpdateBar.IsOpen = false;
        try
        {
            ManufacturerDriverSnapshot? reused = null;
            if (reuseScan && _scanSources is { DriversStartedAt: { } started } sources && DateTimeOffset.UtcNow - started < ScanDriverReuse)
            {
                try { reused = await sources.DriversAsync().WaitAsync(token); }
                catch (Exception) when (!token.IsCancellationRequested) { reused = null; }
            }
            if (reused is null) _scanSources = null;
            var rawSnapshot = reused ?? await _manufacturerDriverService.CheckAsync(token);
            token.ThrowIfCancellationRequested();
            var generationId = Guid.NewGuid();
            var snapshot = rawSnapshot with
            {
                Results = rawSnapshot.Results
                    .Select(result => BindDriverExecutionPlan(result, rawSnapshot.CheckedAt, generationId))
                    .ToArray()
            };
            _allDrivers = snapshot.Results.Select(result => new ManufacturerDriverRow(result, DriverLayout)).ToArray();
            _lastDriverSnapshot = snapshot;
            _driversCheckedAt = DateTimeOffset.UtcNow;
            ApplyDriverFilter();
            var direct = snapshot.Results.Count(static result => result.ExecutableUpdate?.IsInstallable == true);
            var manufacturerDownloads = snapshot.Results.Count(static result =>
                result.Status == ManufacturerDriverStatus.Available && result.ExecutableUpdate is null);
            var checkedPackages = snapshot.CheckedSourceCount;
            var available = direct + manufacturerDownloads;
            UpdateDriverSummary(snapshot, checkedPackages, direct);
            UpdateBar.Title = available == 0
                ? LocalizationService.Format("DriversCheckedNoUpdates", checkedPackages)
                : LocalizationService.Format("DriverUpdatesAvailable", direct, manufacturerDownloads);
            UpdateBar.Message = snapshot.CheckIssueCount == 0
                ? LocalizationService.Get("ManufacturerDriverSafety")
                : LocalizationService.Format("DriverScanIssues", snapshot.CheckIssueCount);
            UpdateBar.Severity = snapshot.CheckIssueCount > 0
                ? InfoBarSeverity.Warning
                : available > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
            UpdateBar.IsOpen = true;
            StatusText.Text = UpdateBar.Title;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            StatusText.Text = LocalizationService.Get("DriverCheckFailed");
            UpdateBar.Title = LocalizationService.Get("DriverCheckFailed");
            UpdateBar.Message = LocalizationService.Text(exception.Message);
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.IsOpen = true;
        }
    }

    private void UpdateDriverSummary(ManufacturerDriverSnapshot snapshot, int checkedPackages, int installableUpdates)
    {
        DetectedCountText.Text = snapshot.Results.Count.ToString("N0");
        VersionCountText.Text = checkedPackages.ToString("N0");
        PathCountText.Text = installableUpdates.ToString("N0");
        GuardCountText.Text = snapshot.CheckIssueCount.ToString("N0");
    }

    private async void DriverRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _massUpdateBusy || _restartPending || sender is not Button { DataContext: ManufacturerDriverRow row })
        {
            return;
        }
        DriversList.SelectedItem = row;
        if (!row.CanUpdate)
        {
            if (row.Source.SourceUri is { } page)
            {
                await OpenInBrowserAsync(page);
            }
            return;
        }

        var update = row.Source.ExecutableUpdate!;
        _mutationActive = true;
        _scanSources = null; // A change to the system makes the scan's driver check stale.
        _lastDriverSnapshot = null;
        _driversCheckedAt = null;
        SetUpdateBusy(true, LocalizationService.Format("RevalidatingUpdate", row.Name));
        DriverProgress.Visibility = Visibility.Visible;
        DriverProgress.IsIndeterminate = true;
        try
        {
            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NaxUpdater",
                "DriverDownloads");
            var progress = new Progress<UpdateTransactionProgress>(state =>
            {
                DriverProgress.IsIndeterminate = state.Fraction is null;
                if (state.Fraction is double fraction)
                {
                    DriverProgress.Value = Math.Clamp(fraction * 100, 0, 100);
                }
                StatusText.Text = state.Stage switch
                {
                    UpdateTransactionStage.Revalidating => LocalizationService.Format("RevalidatingUpdate", row.Name),
                    UpdateTransactionStage.Preparing => LocalizationService.Format("PreparingDriverUpdate", row.Name),
                    UpdateTransactionStage.Applying => LocalizationService.Format("ApplyingUpdate", row.Name),
                    UpdateTransactionStage.Verifying => LocalizationService.Format("VerifyingUpdate", row.Name),
                    _ => StatusText.Text
                };
            });
            var backend = new DefaultUpdateTransactionBackend(
                _updateExecutionService,
                new UpdatePackageDownloader(new NativeAuthenticodeVerifier()),
                RevalidateDriverUpdateAsync);
            var transaction = await new UpdateTransactionCoordinator(
                backend,
                _updateOperationJournal,
                _updateTransactionLease).ApplyAsync(
                update,
                cacheRoot,
                progress);
            UpdateBar.Title = transaction.IsSuccess
                ? LocalizationService.Format("DriverUpdatedTitle", row.Name)
                : transaction.RequiresRestart
                    ? LocalizationService.Format("UpdateRestartRequiredTitle", row.Name)
                    : LocalizationService.Format("DriverNotUpdatedTitle", row.Name);
            UpdateBar.Message = transaction.RequiresRestart || transaction.RestartRecommended
                ? LocalizationService.Get("RestartRequired")
                : transaction.IsSuccess
                    ? LocalizationService.Get("DriverUpdateCompleted")
                    : LocalizationService.Text(transaction.Error) ?? LocalizationService.Get("UpdateTransactionFailed");
            UpdateBar.Severity = transaction.RequiresRestart || transaction.RestartRecommended
                ? InfoBarSeverity.Warning
                : transaction.IsSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            if (transaction.JournalWarning is { } journalWarning)
                UpdateBar.Message += Environment.NewLine + LocalizationService.Text(journalWarning);
            UpdateBar.IsOpen = true;
            _restartPending = transaction.RequiresRestart;
        }
        catch (Exception exception)
        {
            ShowUnexpectedFailure(LocalizationService.Format("DriverNotUpdatedTitle", row.Name), exception);
        }
        finally
        {
            var outcomeTitle = UpdateBar.Title;
            var outcomeMessage = UpdateBar.Message;
            var outcomeSeverity = UpdateBar.Severity;
            try { await CheckManufacturerDriversAsync(); }
            finally
            {
                UpdateBar.Title = outcomeTitle;
                UpdateBar.Message = outcomeMessage;
                UpdateBar.Severity = outcomeSeverity;
                UpdateBar.IsOpen = true;
                StatusText.Text = outcomeTitle;
                DriverProgress.Visibility = Visibility.Collapsed;
                DriverProgress.IsIndeterminate = false;
                SetUpdateBusy(false, null);
                EndMutation();
            }
        }
    }

    private static ManufacturerDriverResult BindDriverExecutionPlan(
        ManufacturerDriverResult result,
        DateTimeOffset checkedAt,
        Guid generationId)
    {
        if (result.ExecutableUpdate?.ExecutionPlan is not { } plan)
        {
            return result;
        }
        var update = result.ExecutableUpdate with
        {
            ProviderAuthority = UpdateProviderAuthority.ProducerRelease,
            ProviderSelectionReason = "Exact manufacturer package, hardware identity, release hash, and driver payload",
            CandidateProviderIds = new[] { result.ExecutableUpdate.ProviderId },
            Applicability = UpdateApplicability.Applicable,
            CorrelationKey = $"driver:{result.Driver.Identity}",
            ExecutionPlan = plan with
            {
                CreatedAt = checkedAt,
                ExpiresAt = checkedAt + TimeSpan.FromMinutes(15),
                InstalledVersionPrecondition = result.ExecutableUpdate.InstalledVersion,
                CheckGenerationId = generationId,
                RunningExecutablePaths = []
            }
        };
        var validationError = UpdatePlanValidator.Validate(update, checkedAt);
        if (validationError is not null)
        {
            return result with
            {
                Status = ManufacturerDriverStatus.Error,
                Message = validationError,
                ExecutableUpdate = null
            };
        }
        return result with { ExecutableUpdate = update };
    }

    private async Task<UpdateCheckResult?> RevalidateDriverUpdateAsync(
        UpdateCheckResult previous,
        CancellationToken cancellationToken)
    {
        var installed = await _manufacturerDriverService.ReadInstalledAsync(cancellationToken);
        var observed = DriverUpdateIdentity.Find(installed.Drivers, previous.ApplicationIdentity,
            previous.CorrelationKey, previous.DisplayName, previous.ProviderId);
        if (observed is not null &&
            DriverUpdateIdentity.HasReachedTarget(observed, previous.AvailableVersion, previous.ProviderId))
            return DriverUpdateIdentity.ObservedState(previous, observed, "The installed driver target was verified directly from Windows.");
        var snapshot = await _manufacturerDriverService.CheckAsync(cancellationToken);
        var generationId = Guid.NewGuid();
        var matched = DriverUpdateIdentity.Find(snapshot.Results.Select(static result => result.Driver),
            previous.ApplicationIdentity, previous.CorrelationKey, previous.DisplayName, previous.ProviderId);
        var result = matched is null ? null : snapshot.Results.FirstOrDefault(candidate => candidate.Driver.Identity == matched.Identity);
        if (result is null)
        {
            return null;
        }
        var bound = BindDriverExecutionPlan(result, snapshot.CheckedAt, generationId);
        if (bound.ExecutableUpdate is not null)
        {
            return bound.ExecutableUpdate;
        }
        return DriverUpdateIdentity.ObservedState(previous, result.Driver, result.Message);
    }
}
