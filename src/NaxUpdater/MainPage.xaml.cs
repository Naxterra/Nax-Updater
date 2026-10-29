using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using NaxUpdater.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.System;

namespace NaxUpdater;

public sealed partial class MainPage : Page
{
    private readonly ObservableCollection<ApplicationRow> _visibleApplications = [];
    private readonly ObservableCollection<UpdateRow> _updates = [];
    private readonly ObservableCollection<ManufacturerDriverRow> _drivers = [];
    private readonly ApplicationInventoryService _inventoryService;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly UpdateExecutionService _updateExecutionService = new();
    private readonly ApplicationRemovalService _applicationRemovalService = new();
    private readonly ApplicationIconService _applicationIconService = new();
    private readonly ManufacturerDriverService _manufacturerDriverService;
    private readonly FileUpdateOperationJournal _updateOperationJournal;
    private readonly FileUpdateTransactionLeaseProvider _updateTransactionLease;
    public DriverColumnLayout DriverLayout { get; } = new();
    private IReadOnlyList<ApplicationRow> _allApplications = [];
    private IReadOnlyList<UpdateRow> _allUpdates = [];
    private IReadOnlyList<ManufacturerDriverRow> _allDrivers = [];
    private InventorySnapshot? _snapshot;
    private int _availableUpdateCount;
    private bool _loaded;
    private bool _updateBusy;
    private bool _massUpdateBusy;
    private bool _stopBatch;
    private CancellationTokenSource? _operationCancellation;
    private bool _restartPending;
    // An update of this run asked for a Windows restart (verified or still pending).
    private bool _restartRecommended;
    // One gate for everything that changes the machine (updates, driver installs,
    // removals). While it is held, no other change may start, and a language
    // restart or window close waits until it is released.
    private bool _mutationActive;
    private string? _pendingLanguage;
    private bool _closeWhenIdle;
    public bool IsChangingSystem => _mutationActive || _massUpdateBusy;
    // Outcomes of updates started since the last manual scan, by application identity.
    private readonly Dictionary<string, (UpdateRunState State, string? Message)> _runOutcomes = new(StringComparer.Ordinal);
    private CancellationTokenSource? _providerCheckCancellation;
    private ApplicationSortColumn _sortColumn = ApplicationSortColumn.Name;
    private bool _sortDescending;
    private ResultSortColumn _updateSortColumn = ResultSortColumn.Status;
    private bool _updateSortDescending;
    private ResultSortColumn _driverSortColumn = ResultSortColumn.Status;
    private bool _driverSortDescending;

    public MainPage()
    {
        InitializeComponent();
        ApplicationsList.ItemsSource = _visibleApplications;
        UpdatesList.ItemsSource = _updates;
        DriversList.ItemsSource = _drivers;
        _inventoryService = new ApplicationInventoryService(Path.Combine(
            AppContext.BaseDirectory,
            "Configuration",
            "application-policies.json"));
        _manufacturerDriverService = new ManufacturerDriverService(_httpClient);
        var localStateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NaxUpdater");
        _updateOperationJournal = new FileUpdateOperationJournal(Path.Combine(localStateRoot, "update-operation.json"));
        _updateTransactionLease = new FileUpdateTransactionLeaseProvider(Path.Combine(localStateRoot, "update-transaction.lock"));
        MainInfo.IsOpen = App.ShowSafetyInformation;
        UpdateSortHeaders();
        UpdateResultSortHeaders();
        UpdateDriverSortHeaders();
    }

    private void MainInfo_CloseButtonClick(InfoBar sender, object args) => App.SetShowSafetyInformation(false);

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        var recoveryLease = _updateTransactionLease.TryAcquire();
        var interruptedOperation = recoveryLease is null ? null : _updateOperationJournal.ReadIncomplete();
        // A failure that was already recorded is rechecked once more, but it is reported
        // again only when it turns out to have succeeded after all.
        var knownFailure = false;
        if (interruptedOperation is null && recoveryLease is not null &&
            _updateOperationJournal.ReadLatest() is { Stage: UpdateTransactionStage.FailedNeedsAttention } failed)
        {
            interruptedOperation = failed;
            knownFailure = true;
        }
        // The lease is held through the first scan only while there is something to
        // recover, so that no new change starts before recovery has recorded its result.
        if (interruptedOperation is null)
        {
            recoveryLease?.Dispose();
            recoveryLease = null;
        }
        try
        {
            await RecoverInterruptedOperationAsync(interruptedOperation, knownFailure);
        }
        catch (Exception exception)
        {
            ShowUnexpectedFailure(LocalizationService.Get("UpdateCheckFailed"), exception);
        }
        finally
        {
            recoveryLease?.Dispose();
        }
    }

    private async Task RecoverInterruptedOperationAsync(UpdateOperationRecord? interruptedOperation, bool knownFailure)
    {
        await ScanAndCheckAsync();
        if (interruptedOperation is not null)
        {
            var bootedAt = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            var waitingForRestart = interruptedOperation.Stage == UpdateTransactionStage.PendingReboot &&
                                    bootedAt <= interruptedOperation.UpdatedAt;
            string? observedVersion;
            if (interruptedOperation.ProviderId.StartsWith("manufacturer-driver:", StringComparison.Ordinal))
            {
                var driverSnapshot = await _manufacturerDriverService.ReadInstalledAsync();
                var installedDriver = DriverUpdateIdentity.Find(driverSnapshot.Drivers,
                    interruptedOperation.ApplicationIdentity, interruptedOperation.CorrelationKey,
                    interruptedOperation.DisplayName, interruptedOperation.ProviderId);
                observedVersion = installedDriver is null ? null : DriverUpdateIdentity.InstalledReleaseVersion(installedDriver);
            }
            else
            {
                observedVersion = _snapshot is null ? null : UpdateCorrelation.FindMatch(
                    _snapshot.Applications,
                    interruptedOperation.ApplicationIdentity,
                    interruptedOperation.CorrelationKey)?.NormalizedVersion;
                if (observedVersion is null && _snapshot is not null && !string.IsNullOrWhiteSpace(interruptedOperation.CorrelationKey))
                {
                    // The identity match (if any) had no resolved version yet;
                    // fall back to a pure correlation-key match, which may point
                    // at a fresher inventory entry with a known version. Passing
                    // an identity no real application can equal forces FindMatch
                    // into its correlation-only path, reusing its ambiguity guard.
                    observedVersion = UpdateCorrelation.FindMatch(
                        _snapshot.Applications, "", interruptedOperation.CorrelationKey)?.NormalizedVersion;
                }
            }
            var recoveredAssessment = _allUpdates.Select(row => row.Source).FirstOrDefault(update =>
                update.ApplicationIdentity.Equals(interruptedOperation.ApplicationIdentity, StringComparison.OrdinalIgnoreCase));
            var reachedTarget = UpdateRecoveryVerifier.HasReachedTarget(interruptedOperation, observedVersion, recoveredAssessment);
            // A restart request only blocks further changes while the target is not yet
            // installed; an update that already reached it is recorded as completed below.
            // A driver stays blocking until Windows restarts, because its new version is
            // reported before the new driver is loaded.
            if (waitingForRestart && (!reachedTarget ||
                interruptedOperation.ProviderId.StartsWith("manufacturer-driver:", StringComparison.Ordinal)))
            {
                _restartPending = true;
                RefreshMassUpdateButton();
                UpdateBar.Title = LocalizationService.Format("UpdateRestartRequiredTitle", interruptedOperation.DisplayName);
                UpdateBar.Message = LocalizationService.Get("RestartRequired");
                UpdateBar.Severity = InfoBarSeverity.Warning;
                UpdateBar.IsOpen = true;
                return;
            }
            var changeMayHaveStarted = interruptedOperation.Stage is
                UpdateTransactionStage.Applying or
                UpdateTransactionStage.Verifying or
                UpdateTransactionStage.PendingReboot or
                UpdateTransactionStage.FailedNeedsAttention or
                UpdateTransactionStage.Indeterminate;
            if (knownFailure && !reachedTarget) return;
            var recoveredStage = reachedTarget
                ? UpdateTransactionStage.Succeeded
                : changeMayHaveStarted
                    ? UpdateTransactionStage.FailedNeedsAttention
                    : UpdateTransactionStage.CanceledBeforeChange;
            _updateOperationJournal.Record(
                interruptedOperation,
                recoveredStage,
                DateTimeOffset.UtcNow,
                reachedTarget ? null : interruptedOperation.Error ?? "Nax-Updater restarted before the target version could be confirmed.");
            // A failed verification is not a reboot requirement. A fresh update
            // transaction revalidates its own target; unrelated apps remain usable.
            UpdateBar.Title = reachedTarget
                ? LocalizationService.Format("RecoveredUpdateTitle", interruptedOperation.DisplayName)
                : LocalizationService.Format("InterruptedUpdateTitle", interruptedOperation.DisplayName);
            UpdateBar.Message = reachedTarget
                ? LocalizationService.Get("RecoveredUpdateMessage")
                : LocalizationService.Get("InterruptedUpdateMessage");
            UpdateBar.Severity = reachedTarget ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            UpdateBar.IsOpen = true;
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_providerCheckCancellation is not null)
        {
            await _providerCheckCancellation.CancelAsync();
            return;
        }
        if (_updateBusy || _massUpdateBusy) return;
        _runOutcomes.Clear();
        if (DriversWorkspace.Visibility == Visibility.Visible)
        {
            await CheckManufacturerDriversAsync();
            return;
        }
        await ScanAndCheckAsync();
    }

    private async Task ScanAndCheckAsync()
    {
        ConfigureApplicationHeader();
        InventoryWorkspace.Visibility = Visibility.Visible;
        UpdatesWorkspace.Visibility = Visibility.Collapsed;
        DriversWorkspace.Visibility = Visibility.Collapsed;
        FilterBox.IsEnabled = true;
        ShowSystemComponentsCheckBox.IsEnabled = true;
        _updates.Clear();
        _allUpdates = [];
        _availableUpdateCount = 0;
        _snapshot = null;
        UpdateBar.IsOpen = false;
        ShowUpdatesButton.IsEnabled = false;
        ShowUpdatesButton.Content = LocalizationService.Get("UpdatesLabel");
        await ScanAsync();
        if (_snapshot is not null)
        {
            await CheckUpdatesAsync(showWorkspace: false);
        }
    }

    private async Task ScanAsync()
    {
        ScanButton.IsEnabled = false;
        ScanProgress.IsActive = true;
        ScanProgress.Visibility = Visibility.Visible;
        StatusText.Text = LocalizationService.Get("ScanReading");
        IssueBar.IsOpen = false;
        var timer = Stopwatch.StartNew();

        try
        {
            _snapshot = await _inventoryService.ScanAsync();
            _allApplications = _snapshot.Applications.Select(static application => new ApplicationRow(application)).ToArray();
            ApplyFilter();
            _ = LoadApplicationIconsAsync(_allApplications);
            UpdateSummary(_snapshot);
            UpdatePolicies(_snapshot);
            if (_snapshot.Issues.Count > 0)
            {
                IssueBar.Message = LocalizationService.Format("IssueCountMessage", _snapshot.Issues.Count);
                IssueBar.IsOpen = true;
            }
            StatusText.Text = LocalizationService.Format("ScannedStatus", _snapshot.Applications.Count, timer.Elapsed.TotalSeconds, _snapshot.Issues.Count);
        }
        catch (Exception exception)
        {
            IssueBar.Title = LocalizationService.Get("InventoryScanFailed");
            IssueBar.Message = LocalizationService.Text(exception.Message);
            IssueBar.Severity = InfoBarSeverity.Error;
            IssueBar.IsOpen = true;
            StatusText.Text = LocalizationService.Get("ScanDidNotComplete");
        }
        finally
        {
            ScanButton.IsEnabled = true;
            ScanProgress.IsActive = false;
            ScanProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadApplicationIconsAsync(IReadOnlyList<ApplicationRow> rows)
    {
        await Task.WhenAll(rows.Select(async row =>
        {
            row.Icon = await _applicationIconService.LoadAsync(row.Source);
        }));
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DriversWorkspace is { Visibility: Visibility.Visible })
        {
            ApplyDriverFilter();
        }
        else if (UpdatesWorkspace is { Visibility: Visibility.Visible })
        {
            ApplyUpdateFilter();
        }
        else
        {
            ApplyFilter();
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

        _visibleApplications.Clear();
        foreach (var row in sorted)
        {
            _visibleApplications.Add(row);
        }
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

    private void ApplyUpdateFilter()
    {
        var selectedIdentity = (UpdatesList.SelectedItem as UpdateRow)?.Source.ApplicationIdentity;
        var filter = FilterBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? _allUpdates
            : _allUpdates.Where(row => MatchesUpdateFilter(row, filter)).ToArray();

        var sorted = SortUpdateRows(filtered);
        _updates.Clear();
        foreach (var row in sorted)
        {
            _updates.Add(row);
        }

        UpdateCountText.Text = string.IsNullOrWhiteSpace(filter)
            ? LocalizationService.Format("UpdateCountFormat", _availableUpdateCount, _allUpdates.Count)
            : LocalizationService.Format("FilteredUpdateCountFormat", _availableUpdateCount, _updates.Count, _allUpdates.Count);
        var restored = selectedIdentity is null
            ? null
            : _updates.FirstOrDefault(row => row.Source.ApplicationIdentity == selectedIdentity);
        UpdatesList.SelectedItem = restored ?? _updates.FirstOrDefault();
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
        _drivers.Clear();
        foreach (var row in sorted)
        {
            _drivers.Add(row);
        }
        var direct = _allDrivers.Count(static row => row.CanUpdate);
        var manufacturerPackages = _allDrivers.Count(static row =>
            (row.Source.Status == ManufacturerDriverStatus.Available && !row.CanUpdate) ||
            (row.Source.Status == ManufacturerDriverStatus.OfficialSourceOnly && row.Source.AvailableVersion is not null));
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
        "errors" => row.Source.Status == ManufacturerDriverStatus.Error,
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
        SetUpdateBusy(true, LocalizationService.Format("RemovingApplication", row.Name));
        try
        {
            var result = await _applicationRemovalService.RemoveAsync(row.Source);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? LocalizationService.Format("RemovalFailedWithCode", result.ExitCode));
            }

            await ScanAndCheckAsync();
            var stillPresent = _snapshot?.Applications.Any(application =>
                application.Identity == row.Source.Identity ||
                (application.DisplayName.Equals(row.Name, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(application.PrimaryInstallPath, row.Source.PrimaryInstallPath, StringComparison.OrdinalIgnoreCase))) == true;
            UpdateBar.Title = LocalizationService.Format("UninstallCompleteTitle", row.Name);
            UpdateBar.Message = result.RestartRequired
                ? LocalizationService.Get("UninstallRestart")
                : stillPresent
                    ? LocalizationService.Get("UninstallStillPresent")
                    : LocalizationService.Get("UninstallComplete");
            UpdateBar.Severity = stillPresent ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            UpdateBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            UpdateBar.Title = LocalizationService.Format("UninstallFailedTitle", row.Name);
            UpdateBar.Message = LocalizationService.Text(exception.Message);
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.IsOpen = true;
        }
        finally
        {
            SetUpdateBusy(false, null);
            EndMutation();
        }
    }

    private void RefreshUninstallButton() =>
        UninstallButton.IsEnabled = !_updateBusy && !_massUpdateBusy && !_restartPending &&
                                    ApplicationsList.SelectedItem is ApplicationRow { Source.RemovalPlan: not null };

    private bool OperationBlocked()
    {
        if (!_updateBusy && !_massUpdateBusy && !_restartPending) return false;
        ShowOperationRunning();
        return true;
    }

    private void ShowUnexpectedFailure(string title, Exception exception)
    {
        UpdateBar.Title = title;
        UpdateBar.Message = LocalizationService.Text(exception.Message);
        UpdateBar.Severity = InfoBarSeverity.Error;
        UpdateBar.IsOpen = true;
    }

    private void ShowOperationRunning()
    {
        UpdateBar.Title = LocalizationService.Get("OperationRunningTitle");
        UpdateBar.Message = LocalizationService.Get("OperationRunningMessage");
        UpdateBar.Severity = InfoBarSeverity.Informational;
        UpdateBar.IsOpen = true;
    }

    // Ends one change. When no change is left, a deferred language restart or
    // window close runs now.
    private void EndMutation()
    {
        _mutationActive = false;
        if (IsChangingSystem) return;
        if (_pendingLanguage is { } language)
        {
            _pendingLanguage = null;
            App.RestartWithLanguage(language);
        }
        else if (_closeWhenIdle)
        {
            App.MainWindow.Close();
        }
    }

    // Called by the window before it closes. Returns true when the close must wait
    // for the running change; the window then closes on its own afterwards.
    public bool DeferCloseUntilIdle()
    {
        if (!IsChangingSystem) return false;
        _closeWhenIdle = true;
        UpdateBar.Title = LocalizationService.Get("CloseAfterOperationTitle");
        UpdateBar.Message = LocalizationService.Get("CloseAfterOperationMessage");
        UpdateBar.Severity = InfoBarSeverity.Informational;
        UpdateBar.IsOpen = true;
        return true;
    }

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

    private async Task CheckUpdatesAsync(bool showWorkspace)
    {
        var checkTimer = Stopwatch.StartNew();
        if (_snapshot is null)
        {
            await ScanAsync();
        }
        if (_snapshot is null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _providerCheckCancellation = cancellation;
        SetUpdateBusy(true, LocalizationService.Get("CheckingProviders"));
        ScanButton.IsEnabled = true;
        ScanButtonLabel.Text = LocalizationService.Get("CancelProviderCheck");
        UpdateBar.IsOpen = false;
        try
        {
            var catalogPath = Path.Combine(AppContext.BaseDirectory, "Configuration", "update-providers.json");
            var catalog = await UpdateProviderCatalogLoader.LoadAsync(catalogPath);
            var service = new UpdateCheckService(_httpClient, catalog);
            var progress = new Progress<UpdateCheckProgress>(state =>
            {
                if (!ReferenceEquals(_providerCheckCancellation, cancellation)) return;
                StatusText.Text = state.Phase == "sources"
                    ? LocalizationService.Get("RefreshingProviderCatalogs")
                    : LocalizationService.Format("ProviderCheckProgress", state.Completed, state.Total, state.ApplicationName ?? "");
            });
            var result = await service.CheckAsync(_snapshot, cancellation.Token, progress);
            ReconcileCompletedOperation(result);
            await SaveScanDiagnosticsAsync(result, checkTimer.Elapsed);

            var applicationsByIdentity = _allApplications.ToDictionary(static row => row.Source.Identity, StringComparer.Ordinal);
            _allUpdates = result.Results.Select(update =>
            {
                var row = new UpdateRow(update, applicationsByIdentity.GetValueOrDefault(update.ApplicationIdentity));
                // A completed update that still has an offer shows the offer, not "updated".
                if (_runOutcomes.TryGetValue(update.ApplicationIdentity, out var outcome) &&
                    !(outcome.State == UpdateRunState.Succeeded && update.IsInstallable))
                {
                    row.RunState = outcome.State;
                    row.RunMessage = outcome.Message;
                }
                return row;
            }).ToArray();
            var available = result.InstallableUpdateCount;
            var knownReleases = result.KnownReleaseCount;
            _availableUpdateCount = available;
            var errors = result.Results.Count(static update => update.Status == UpdateStatus.Error);
            ApplyUpdateFilter();
            RefreshMassUpdateButton();
            ShowUpdatesButton.Content = LocalizationService.Format(
                "UpdatesNavigationCoverage",
                available + knownReleases,
                result.CheckedVersionCount,
                result.Results.Count);
            ShowUpdatesButton.IsEnabled = _allUpdates.Count > 0;
            if (showWorkspace)
            {
                ShowUpdatesWorkspace();
            }
            StatusText.Text = LocalizationService.Format(
                "UpdateCheckedStatusDetailed",
                result.CheckedVersionCount,
                available,
                result.ManagedExternallyCount,
                result.UnsupportedApplicationCount,
                result.FailedCheckCount,
                knownReleases);

            UpdateBar.Title = available > 0
                ? LocalizationService.Format("UpdatesAvailableTitle", available)
                : result.StoreQueueCount > 0
                    ? LocalizationService.Format("StoreQueueTitle", result.StoreQueueCount)
                : knownReleases > 0
                    ? LocalizationService.Format("ReleasesKnownTitle", knownReleases)
                    : LocalizationService.Get(result.AllCurrent ? "ApplicationsCurrent" :
                        errors > 0 ? "ChecksIncomplete" : "NoUpdatesAmongChecked");
            UpdateBar.Message = StatusText.Text;
            StatusText.Text += $" · {checkTimer.Elapsed.TotalSeconds:N1} s";
            UpdateBar.Severity = errors > 0 ? InfoBarSeverity.Warning : available > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
            UpdateBar.IsOpen = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = LocalizationService.Get("ProviderCheckCanceled");
            UpdateBar.IsOpen = false;
        }
        catch (Exception exception)
        {
            UpdateBar.Title = LocalizationService.Get("UpdateCheckFailed");
            UpdateBar.Message = LocalizationService.Text(exception.Message);
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.IsOpen = true;
            StatusText.Text = LocalizationService.Get("UpdateCheckDidNotComplete");
        }
        finally
        {
            _providerCheckCancellation = null;
            ScanButtonLabel.Text = LocalizationService.Get("ApplicationScanText");
            SetUpdateBusy(false, null);
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

    private void ShowApplicationsButton_Click(object sender, RoutedEventArgs e)
    {
        ConfigureApplicationHeader();
        InventoryWorkspace.Visibility = Visibility.Visible;
        UpdatesWorkspace.Visibility = Visibility.Collapsed;
        DriversWorkspace.Visibility = Visibility.Collapsed;
        FilterBox.IsEnabled = true;
        ShowSystemComponentsCheckBox.IsEnabled = true;
        ApplyFilter();
    }

    private void ShowUpdatesButton_Click(object sender, RoutedEventArgs e) => ShowUpdatesWorkspace();

    private void ShowUpdatesWorkspace()
    {
        ConfigureApplicationHeader();
        InventoryWorkspace.Visibility = Visibility.Collapsed;
        UpdatesWorkspace.Visibility = Visibility.Visible;
        DriversWorkspace.Visibility = Visibility.Collapsed;
        FilterBox.IsEnabled = true;
        ShowSystemComponentsCheckBox.IsEnabled = false;
        ApplyUpdateFilter();
    }

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
        await CheckManufacturerDriversAsync();
    }

    private async Task CheckManufacturerDriversAsync()
    {
        SetUpdateBusy(true, LocalizationService.Get("CheckingManufacturerDrivers"));
        UpdateBar.IsOpen = false;
        try
        {
            var rawSnapshot = await _manufacturerDriverService.CheckAsync();
            var generationId = Guid.NewGuid();
            var snapshot = rawSnapshot with
            {
                Results = rawSnapshot.Results
                    .Select(result => BindDriverExecutionPlan(result, rawSnapshot.CheckedAt, generationId))
                    .ToArray()
            };
            _allDrivers = snapshot.Results.Select(result => new ManufacturerDriverRow(result, DriverLayout)).ToArray();
            ApplyDriverFilter();
            var direct = snapshot.Results.Count(static result => result.ExecutableUpdate?.IsInstallable == true);
            var manufacturerDownloads = snapshot.Results.Count(static result =>
                result.Status == ManufacturerDriverStatus.Available && result.ExecutableUpdate is null);
            var checkedPackages = snapshot.Results.Count(static result =>
                result.Status == ManufacturerDriverStatus.OfficialSourceOnly && result.AvailableVersion is not null);
            var available = direct + manufacturerDownloads;
            UpdateDriverSummary(snapshot, checkedPackages, direct);
            UpdateBar.Title = available == 0
                ? LocalizationService.Format("DriversCheckedNoUpdates", checkedPackages)
                : LocalizationService.Format("DriverUpdatesAvailable", direct, manufacturerDownloads);
            UpdateBar.Message = snapshot.Issues.Count == 0
                ? LocalizationService.Get("ManufacturerDriverSafety")
                : LocalizationService.Format("DriverScanIssues", snapshot.Issues.Count);
            UpdateBar.Severity = snapshot.Issues.Count > 0
                ? InfoBarSeverity.Warning
                : available > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
            UpdateBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            UpdateBar.Title = LocalizationService.Get("DriverCheckFailed");
            UpdateBar.Message = LocalizationService.Text(exception.Message);
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.IsOpen = true;
        }
        finally
        {
            SetUpdateBusy(false, null);
        }
    }

    private void ConfigureApplicationHeader()
    {
        ScanButtonLabel.Text = LocalizationService.Get(_providerCheckCancellation is null ? "ApplicationScanText" : "CancelProviderCheck");
        FilterBox.PlaceholderText = LocalizationService.Get("ApplicationFilterPlaceholder");
        ShowSystemComponentsCheckBox.Visibility = Visibility.Visible;
        DriversButton.Visibility = Visibility.Visible;
        DetectedSummaryLabel.Text = LocalizationService.Get("ApplicationDetectedSummary");
        VersionSummaryLabel.Text = LocalizationService.Get("ApplicationVersionsSummary");
        PathSummaryLabel.Text = LocalizationService.Get("ApplicationPathsSummary");
        GuardSummaryLabel.Text = LocalizationService.Get("ApplicationGuardsSummary");
        if (_snapshot is not null)
        {
            UpdateSummary(_snapshot);
        }
    }

    private void ConfigureDriverHeader()
    {
        ScanButtonLabel.Text = LocalizationService.Get("CheckDriversTop");
        FilterBox.PlaceholderText = LocalizationService.Get("DriverFilterPlaceholder");
        ShowSystemComponentsCheckBox.Visibility = Visibility.Collapsed;
        DriversButton.Visibility = Visibility.Collapsed;
        DetectedSummaryLabel.Text = LocalizationService.Get("DriverGroupsSummary");
        VersionSummaryLabel.Text = LocalizationService.Get("OfficialPackagesSummary");
        PathSummaryLabel.Text = LocalizationService.Get("InstallableDriverUpdatesSummary");
        GuardSummaryLabel.Text = LocalizationService.Get("DriverIssuesSummary");
        DetectedCountText.Text = _allDrivers.Count == 0 ? "—" : _allDrivers.Count.ToString("N0");
        VersionCountText.Text = "—";
        PathCountText.Text = "—";
        GuardCountText.Text = "—";
    }

    private void UpdateDriverSummary(ManufacturerDriverSnapshot snapshot, int checkedPackages, int installableUpdates)
    {
        DetectedCountText.Text = snapshot.Results.Count.ToString("N0");
        VersionCountText.Text = checkedPackages.ToString("N0");
        PathCountText.Text = installableUpdates.ToString("N0");
        GuardCountText.Text = snapshot.Issues.Count.ToString("N0");
    }

    private void DriverHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DriversWorkspace.Visibility != Visibility.Visible ||
            DriverNameColumn.ActualWidth <= 0 || DriverInstalledColumn.ActualWidth <= 0 ||
            DriverAvailableColumn.ActualWidth <= 0 || DriverSourceColumn.ActualWidth <= 0 ||
            DriverStatusColumn.ActualWidth <= 0 || DriverActionColumn.ActualWidth <= 0)
        {
            return;
        }
        DriverLayout.Synchronize(
            DriverNameColumn.ActualWidth,
            DriverInstalledColumn.ActualWidth,
            DriverAvailableColumn.ActualWidth,
            DriverSourceColumn.ActualWidth,
            DriverStatusColumn.ActualWidth,
            DriverActionColumn.ActualWidth);
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
            if (row.Source.SourceUri is not null)
            {
                await Launcher.LaunchUriAsync(row.Source.SourceUri);
            }
            return;
        }

        var update = row.Source.ExecutableUpdate!;
        _mutationActive = true;
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
                new UpdatePackageDownloader(_httpClient, new NativeAuthenticodeVerifier()),
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
            // An async void handler must not let an exception end the process.
            ShowUnexpectedFailure(LocalizationService.Format("DriverNotUpdatedTitle", row.Name), exception);
        }
        finally
        {
            DriverProgress.Visibility = Visibility.Collapsed;
            DriverProgress.IsIndeterminate = false;
            SetUpdateBusy(false, null);
            EndMutation();
        }
        await CheckManufacturerDriversAsync();
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
            CandidateProviderIds = [result.ExecutableUpdate.ProviderId],
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
            VersionOrder.Compare(DriverUpdateIdentity.InstalledReleaseVersion(observed), previous.AvailableVersion) >= 0)
            return ObservedDriverState(previous, observed, "The installed driver target was verified directly from Windows.");
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
        return ObservedDriverState(previous, result.Driver, result.Message);
    }

    private static UpdateCheckResult ObservedDriverState(UpdateCheckResult previous, InstalledHardwareDriver driver, string message) =>
        new(
            driver.Identity,
            driver.DeviceName,
            DriverUpdateIdentity.InstalledReleaseVersion(driver),
            null,
            UpdateStatus.Current,
            previous.ProviderId,
            previous.ProviderDisplayName,
            previous.Language,
            previous.LanguageSource,
            previous.Architecture,
            previous.Channel,
            previous.ReleaseNotesUrl,
            message,
            null,
            UpdateProviderAuthority.ProducerRelease,
            "Manufacturer driver state was re-read after apply",
            [previous.ProviderId],
            UpdateApplicability.NotRequired,
            previous.CorrelationKey ?? $"driver:{driver.Identity}");

    // The details panel follows the selected row while an update changes its state.
    private UpdateRow? _detailsRow;

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
        UpdateReleaseText.Text = row.ReleaseNotes;
        UpdateMessageText.Text = row.Message;
    }

    private async void UpdateRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _massUpdateBusy || _restartPending || sender is not Button { DataContext: UpdateRow row } ||
            !row.CanInstall || row.Source.ExecutionPlan is null)
        {
            return;
        }
        UpdatesList.SelectedItem = row;
        await ExecuteUpdateAsync(row, rescanAfterSuccess: true);
    }

    private async void UpdateAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _massUpdateBusy || _restartPending)
        {
            return;
        }
        var queue = _allUpdates
            .Where(static row => row.CanInstall)
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
            await ScanAsync();
            await CheckUpdatesAsync(showWorkspace: true);
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
        var storeQueue = row.Source.ExecutionPlan.Kind == UpdateExecutionKind.NativeStoreQueue;
        StopOperationButton.Content = LocalizationService.Get(storeQueue ? "StopStoreWaiting" : "StopAfterCurrent");
        StopOperationButton.Visibility = storeQueue || _massUpdateBusy ? Visibility.Visible : Visibility.Collapsed;
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
                new UpdatePackageDownloader(_httpClient, new NativeAuthenticodeVerifier()),
                RevalidateUpdateAsync);
            var transaction = await new UpdateTransactionCoordinator(
                backend,
                _updateOperationJournal,
                _updateTransactionLease).ApplyAsync(
                row.Source,
                cacheRoot,
                transactionProgress, operationCancellation.Token);

            // Windows (e.g. the Store working through its own queue) can finish the
            // same update between the scan and this action; revalidation then finds
            // no offer because the app is already newer. That is a completed update.
            var alreadyUpdated = transaction.Stage == UpdateTransactionStage.NoLongerApplicable &&
                transaction.FreshAssessment is { Status: UpdateStatus.Current } fresh &&
                VersionOrder.Compare(fresh.InstalledVersion, row.Source.InstalledVersion) > 0;
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

            if (rescanAfterSuccess)
            {
                await ScanAsync();
                if (_snapshot is not null)
                {
                    await CheckUpdatesAsync(showWorkspace: true);
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
        if (StopOperationButton.Content?.ToString() == LocalizationService.Get("StopStoreWaiting") && _operationCancellation is { } cancellation)
            await cancellation.CancelAsync();
    }

    private async Task<UpdateCheckResult?> RevalidateUpdateAsync(
        UpdateCheckResult previous,
        CancellationToken cancellationToken)
    {
        var inventory = await _inventoryService.ScanAsync(cancellationToken);
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
            [application],
            [],
            inventory.Issues);
        var result = await new UpdateCheckService(_httpClient, catalog)
            .CheckAsync(isolatedInventory, cancellationToken);
        return result.Results.SingleOrDefault();
    }

    private void SetUpdateBusy(bool busy, string? message)
    {
        _updateBusy = busy;
        ScanButton.IsEnabled = !busy;
        DriversButton.IsEnabled = !busy;
        // The lists stay enabled so they can be scrolled and inspected while an
        // update runs; row actions are locked instead (RefreshMassUpdateButton).
        ShowUpdatesButton.IsEnabled = (!busy || IsChangingSystem) && _allUpdates.Count > 0;
        RefreshMassUpdateButton();
        RefreshUninstallButton();
        ScanProgress.IsActive = busy;
        ScanProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
        }
    }

    private void RefreshMassUpdateButton()
    {
        // Same predicate as UpdateAllButton_Click (CanInstall), so queued Store
        // rows that "Update all" will resume are counted, not shown as (0).
        var verifiedUpdates = _allUpdates.Count(static row =>
            row.CanInstall &&
            row.Source.ExecutionPlan?.Kind is not (UpdateExecutionKind.StorePackage or UpdateExecutionKind.NativeStorePackage or UpdateExecutionKind.NativeStoreQueue));
        var storeActions = _allUpdates.Count(static row =>
            row.CanInstall &&
            row.Source.ExecutionPlan?.Kind is UpdateExecutionKind.StorePackage or UpdateExecutionKind.NativeStorePackage or UpdateExecutionKind.NativeStoreQueue);
        UpdateAllButton.Content = storeActions > 0
            ? LocalizationService.Format("UpdateAllCountWithStore", verifiedUpdates, storeActions)
            : LocalizationService.Format("UpdateAllCount", verifiedUpdates);
        UpdateAllButton.IsEnabled = !_updateBusy && !_massUpdateBusy && !_restartPending && verifiedUpdates + storeActions > 0;
        var locked = _updateBusy || _massUpdateBusy || _restartPending;
        foreach (var row in _allUpdates) row.ActionsLocked = locked;
    }

    private async void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        var languageBox = new ComboBox
        {
            Header = LocalizationService.Get("SettingsLanguage"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "English", "Deutsch" },
            SelectedIndex = App.CurrentLanguage == "de-DE" ? 1 : 0
        };
        var safetyInformationToggle = new ToggleSwitch
        {
            Header = LocalizationService.Get("SettingsSafetyInformation"),
            OffContent = LocalizationService.Get("Disabled"),
            OnContent = LocalizationService.Get("Enabled"),
            IsOn = App.ShowSafetyInformation
        };
        var content = new StackPanel
        {
            Spacing = 10,
            MinWidth = 360,
            Children =
            {
                languageBox,
                safetyInformationToggle,
                new TextBlock
                {
                    Text = LocalizationService.Get("SettingsRestartHint"),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = PresentationBrushes.Get("NaxBlueBrush")
                },
                new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 8, 0, 6),
                    Background = PresentationBrushes.Get("NaxBlueBrush"),
                    Opacity = 0.3
                },
                new TextBlock
                {
                    Text = LocalizationService.Get("AboutTitle"),
                    FontSize = 18,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = PresentationBrushes.Get("NaxPurpleBrush")
                },
                new TextBlock
                {
                    Text = "Nax-Updater",
                    FontSize = 20,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                },
                new TextBlock
                {
                    Text = LocalizationService.Format("AboutVersion", GetApplicationVersion()),
                    IsTextSelectionEnabled = true,
                    Foreground = PresentationBrushes.Get("NaxGreenBrush")
                },
                new TextBlock
                {
                    Text = LocalizationService.Get("AboutDescription"),
                    TextWrapping = TextWrapping.Wrap
                },
                new HyperlinkButton
                {
                    Content = LocalizationService.Get("AboutRepository"),
                    NavigateUri = new Uri("https://github.com/Naxterra/Nax-Updater"),
                    Padding = new Thickness(0, 4, 0, 4),
                    HorizontalAlignment = HorizontalAlignment.Left
                },
                new TextBlock
                {
                    Text = LocalizationService.Get("AboutCopyright"),
                    FontSize = 11,
                    Foreground = PresentationBrushes.Get("NaxBlueBrush")
                }
            }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("SettingsTitle"),
            Content = content,
            PrimaryButtonText = LocalizationService.Get("Apply"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            App.SetShowSafetyInformation(safetyInformationToggle.IsOn);
            MainInfo.IsOpen = safetyInformationToggle.IsOn;
            var selectedLanguage = languageBox.SelectedIndex == 1 ? "de-DE" : "en-US";
            if (!selectedLanguage.Equals(App.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            {
                if (IsChangingSystem)
                {
                    // Restarting would close the host of a running installation.
                    _pendingLanguage = selectedLanguage;
                    UpdateBar.Title = LocalizationService.Get("LanguageAfterOperationTitle");
                    UpdateBar.Message = LocalizationService.Get("LanguageAfterOperationMessage");
                    UpdateBar.Severity = InfoBarSeverity.Informational;
                    UpdateBar.IsOpen = true;
                    return;
                }
                if (_providerCheckCancellation is not null) await _providerCheckCancellation.CancelAsync();
                App.RestartWithLanguage(selectedLanguage);
            }
        }
    }

    private static string GetApplicationVersion()
    {
        var version = typeof(MainPage).Assembly.GetName().Version;
        return version is null ? "—" : version.ToString(3);
    }

    private ContentDialog CreateDialog(
        string title,
        string content,
        string primaryText,
        string? closeText = null) => new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText ?? string.Empty,
            DefaultButton = ContentDialogButton.Primary
        };

    private enum ApplicationSortColumn
    {
        Name,
        InstallDate
    }

    private enum ResultSortColumn
    {
        Name,
        Status
    }
}
