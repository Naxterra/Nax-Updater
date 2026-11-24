using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using NaxUpdater.Services;
using System.Diagnostics;

namespace NaxUpdater;

public sealed partial class MainPage
{
    private async Task ScanAndCheckCoreAsync(CancellationToken token)
    {
        ConfigureApplicationHeader();
        InventoryWorkspace.Visibility = Visibility.Visible;
        UpdatesWorkspace.Visibility = Visibility.Collapsed;
        DriversWorkspace.Visibility = Visibility.Collapsed;
        FilterBox.IsEnabled = true;
        ShowSystemComponentsCheckBox.IsEnabled = true;
        _updates.Clear();
        _allUpdates = [];
        _lastCheckSnapshot = null;
        _checksComplete = false;
        _userSelectedUpdate = false;
        _availableUpdateCount = 0;
        _snapshot = null;
        _lastDriverSnapshot = null;
        _driversCheckedAt = null;
        UpdateBar.IsOpen = false;
        ShowUpdatesButton.IsEnabled = false;
        ShowUpdatesButton.Content = LocalizationService.Get("UpdatesLabel");
        var sources = new SharedScanSources(_httpClient, _sourceCacheRoot, token);
        _ = sources.DriversAsync();
        // The Store's query for all apps is the slowest single answer of a scan; it starts now.
        sources.StartStoreBatch();
        await ScanInventoryCoreAsync(token);
        if (_snapshot is not null)
            await CheckUpdatesCoreAsync(false, sources, token);
    }

    private async Task ScanInventoryCoreAsync(CancellationToken token)
    {
        StatusText.Text = LocalizationService.Get("ScanReading");
        IssueBar.IsOpen = false;
        _snapshot = null;
        var timer = Stopwatch.StartNew();
        try
        {
            var snapshot = await _inventoryService.ScanAsync(token);
            token.ThrowIfCancellationRequested();
            _snapshot = snapshot;
            _allApplications = snapshot.Applications.Select(application => new ApplicationRow(application, ApplicationLayout)).ToArray();
            ApplyFilter();
            StartApplicationIconLoad(_allApplications);
            UpdateSummary(snapshot);
            UpdatePolicies(snapshot);
            if (snapshot.Issues.Count > 0)
            {
                IssueBar.Title = LocalizationService.Get("InventoryIssuesTitle");
                IssueBar.Severity = InfoBarSeverity.Warning;
                IssueBar.Message = LocalizationService.Format("IssueCountMessage", snapshot.Issues.Count);
                IssueBar.IsOpen = true;
            }
            StatusText.Text = LocalizationService.Format("ScannedStatus", snapshot.Applications.Count, timer.Elapsed.TotalSeconds, snapshot.Issues.Count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            IssueBar.Title = LocalizationService.Get("InventoryScanFailed");
            IssueBar.Message = LocalizationService.Text(exception.Message);
            IssueBar.Severity = InfoBarSeverity.Error;
            IssueBar.IsOpen = true;
            StatusText.Text = LocalizationService.Get("ScanDidNotComplete");
        }
    }

    private void StartApplicationIconLoad(IReadOnlyList<ApplicationRow> rows)
    {
        _iconLoadCancellation?.Cancel();
        _iconLoadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _iconLoadCancellation = cancellation;
        _ = LoadApplicationIconsAsync(rows, cancellation.Token);
    }

    private async Task LoadApplicationIconsAsync(IReadOnlyList<ApplicationRow> rows, CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(rows.Select(async row =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                var icon = await _applicationIconService.LoadAsync(row.Source);
                if (!cancellationToken.IsCancellationRequested) row.Icon = icon;
            }));
        }
        catch (Exception)
        {
            // Keep neutral fallback icons if loading fails or is superseded by a newer scan.
        }
    }

    private void ShowSystemComponentsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        ApplyFilter();
        if (_snapshot is not null)
        {
            UpdateSummary(_snapshot);
        }
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }
        var requestedColumn = string.Equals(button.Tag?.ToString(), "InstallDate", StringComparison.Ordinal)
            ? ApplicationSortColumn.InstallDate
            : ApplicationSortColumn.Name;
        if (requestedColumn == _sortColumn)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = requestedColumn;
            _sortDescending = requestedColumn == ApplicationSortColumn.InstallDate;
        }
        UpdateSortHeaders();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var selectedIdentity = (ApplicationsList.SelectedItem as ApplicationRow)?.Source.Identity;
        var filter = FilterBox.Text.Trim();
        var showSystemComponents = ShowSystemComponentsCheckBox.IsChecked == true;
        var eligible = _allApplications.Where(row => showSystemComponents || !row.IsSystemComponent);
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? eligible.ToArray()
            : eligible.Where(row => MatchesFilter(row, filter)).ToArray();
        var sorted = SortApplications(filtered);

        ObservableCollectionSync.Apply(_visibleApplications, sorted);
        VisibleCountText.Text = LocalizationService.Format("ShownCount", _visibleApplications.Count);

        var restored = selectedIdentity is null
            ? null
            : _visibleApplications.FirstOrDefault(row => row.Source.Identity == selectedIdentity);
        ApplicationsList.SelectedItem = restored ?? _visibleApplications.FirstOrDefault();
    }

    private IEnumerable<ApplicationRow> SortApplications(IEnumerable<ApplicationRow> rows)
    {
        if (_sortColumn == ApplicationSortColumn.Name)
        {
            return _sortDescending
                ? rows.OrderByDescending(static row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                : rows.OrderBy(static row => row.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        return _sortDescending
            ? rows.OrderBy(static row => !row.HasInstallationDate)
                .ThenByDescending(static row => row.InstallationDateSortValue)
                .ThenBy(static row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            : rows.OrderBy(static row => !row.HasInstallationDate)
                .ThenBy(static row => row.InstallationDateSortValue)
                .ThenBy(static row => row.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private void UpdateSortHeaders()
    {
        var direction = _sortDescending ? " ↓" : " ↑";
        ApplicationHeaderText.Text = LocalizationService.Get("ApplicationLabel") +
                                     (_sortColumn == ApplicationSortColumn.Name ? direction : string.Empty);
        InstallDateHeaderText.Text = LocalizationService.Get("InstallDateLabel") +
                                     (_sortColumn == ApplicationSortColumn.InstallDate ? direction : string.Empty);
    }

    private static bool MatchesFilter(ApplicationRow row, string filter)
    {
        return row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               row.Publisher.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               row.Version.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               row.Provider.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               row.InstallationDate.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               row.Path.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               row.BlockedProviders.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplicationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ApplicationsList.SelectedItem is not ApplicationRow row)
        {
            DetailsPlaceholder.Visibility = Visibility.Visible;
            DetailsContent.Visibility = Visibility.Collapsed;
            UninstallButton.IsEnabled = false;
            return;
        }

        DetailsPlaceholder.Visibility = Visibility.Collapsed;
        DetailsContent.Visibility = Visibility.Visible;
        DetailNameText.Text = row.Name;
        DetailPublisherText.Text = row.Publisher;
        DetailVersionText.Text = row.VersionDetail;
        DetailPathText.Text = row.PathDetail;
        DetailProviderText.Text = row.Provider;
        DetailScopeText.Text = row.Scope;
        DetailInstallDateText.Text = row.InstallationDateDetail;
        DetailBlockedText.Text = row.BlockedProviders;
        RefreshUninstallButton();
        RemovalExplanation.Text = row.Source.RemovalPlan is not null ? string.Empty : LocalizationService.Get(
            row.Source.IsSystemComponent ? "RemovalWindowsComponent" : "RemovalUnavailable");
        RemovalExplanation.Visibility = row.Source.RemovalPlan is null ? Visibility.Visible : Visibility.Collapsed;
        EvidenceItems.ItemsSource = row.Source.Evidence.Select(static evidence => new EvidenceRow(evidence)).ToArray();
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (ApplicationsList.SelectedItem is not ApplicationRow row || row.Source.RemovalPlan is null || OperationBlocked())
        {
            return;
        }

        var method = LocalizationService.RemovalMethod(row.Source.RemovalPlan.Kind);
        var warning = CreateDialog(
            LocalizationService.Format("UninstallTitle", row.Name),
            LocalizationService.Format("UninstallWarning", row.Name, row.Publisher, method),
            LocalizationService.Get("Continue"),
            LocalizationService.Get("Cancel"));
        warning.DefaultButton = ContentDialogButton.Close;
        if (await warning.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var nameBox = new TextBox
        {
            Header = LocalizationService.Format("TypeNamePrompt", row.Name),
            PlaceholderText = row.Name
        };
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("TypeNameTitle"),
            Content = nameBox,
            PrimaryButtonText = LocalizationService.Get("UninstallConfirm"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false
        };
        nameBox.TextChanged += (_, _) =>
            confirmation.IsPrimaryButtonEnabled = string.Equals(nameBox.Text, row.Name, StringComparison.Ordinal);
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary || OperationBlocked())
        {
            return;
        }
        // Updates hold this lease for each transaction; a removal holds it too, so the
        // two can never run at the same time, even from another Nax-Updater instance.
        using var lease = _updateTransactionLease.TryAcquire();
        if (lease is null)
        {
            ShowOperationRunning();
            return;
        }

        _mutationActive = true;
        _scanSources = null; // A change to the system makes the scan's driver check stale.
        _lastDriverSnapshot = null;
        _driversCheckedAt = null;
        SetUpdateBusy(true, LocalizationService.Format("RemovingApplication", row.Name));
        try
        {
            var result = await _applicationRemovalService.RemoveAsync(row.Source);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? LocalizationService.Format("RemovalFailedWithCode", result.ExitCode));
            }

            await ScanAndCheckAsync();
            var stillPresent = RemovalVerification.IsStillPresent(row.Source, _snapshot);
            UpdateBar.Title = LocalizationService.Format(stillPresent is null ? "UninstallUnverifiedTitle" : "UninstallCompleteTitle", row.Name);
            UpdateBar.Message = stillPresent is null
                ? LocalizationService.Get("UninstallUnverified")
                : result.RestartRequired
                    ? LocalizationService.Get("UninstallRestart")
                    : stillPresent == true
                        ? LocalizationService.Get("UninstallStillPresent")
                        : LocalizationService.Get("UninstallComplete");
            UpdateBar.Severity = stillPresent != false || result.RestartRequired ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            UpdateBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            UpdateBar.Title = LocalizationService.Format("UninstallFailedTitle", row.Name);
            UpdateBar.Message = LocalizationService.Text(exception.Message);
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.IsOpen = true;
            StatusText.Text = UpdateBar.Title;
        }
        finally
        {
            SetUpdateBusy(false, null);
            EndMutation();
        }
    }

    private void RefreshUninstallButton() =>
        UninstallButton.IsEnabled = !_updateBusy && !_massUpdateBusy && !_restartPending &&
                                    _snapshot is not null && ApplicationsList.SelectedItem is ApplicationRow { Source.RemovalPlan: not null };

    private void UpdateSummary(InventorySnapshot snapshot)
    {
        var applications = ShowSystemComponentsCheckBox.IsChecked == true
            ? snapshot.Applications
            : snapshot.Applications.Where(static application => !application.IsSystemComponent).ToArray();
        DetectedCountText.Text = applications.Count.ToString("N0");
        VersionCountText.Text = applications.Count(static application => !string.IsNullOrWhiteSpace(application.InstalledVersion)).ToString("N0");
        PathCountText.Text = applications.Count(static application => !string.IsNullOrWhiteSpace(application.PrimaryInstallPath)).ToString("N0");
        GuardCountText.Text = (applications.Count(static application => application.BlockedProviders.Count > 0) + snapshot.UnmatchedPolicies.Count).ToString("N0");
    }

    private void UpdatePolicies(InventorySnapshot snapshot)
    {
        var policies = snapshot.UnmatchedPolicies.Select(static policy => new PolicyRow(policy)).ToArray();
        PolicyItems.ItemsSource = policies;
        UnmatchedPolicySection.Visibility = policies.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
