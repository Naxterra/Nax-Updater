using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using NaxUpdater.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace NaxUpdater;

public sealed partial class MainPage : Page
{
    private readonly ObservableCollection<ApplicationRow> _visibleApplications = [];
    private readonly ObservableCollection<UpdateRow> _updates = [];
    private readonly ObservableCollection<ManufacturerDriverRow> _drivers = [];
    private readonly ApplicationInventoryService _inventoryService;
    // Some publisher servers refuse requests without a User-Agent (403), so the shared
    // client always sends one; requests that set their own keep it.
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30), DefaultRequestHeaders = { { "User-Agent", AppVersion.UserAgent } } };
    private readonly UpdateExecutionService _updateExecutionService = new();
    private readonly ApplicationRemovalService _applicationRemovalService = new();
    private readonly ApplicationIconService _applicationIconService = new();
    private readonly ManufacturerDriverService _manufacturerDriverService;
    private readonly FileUpdateOperationJournal _updateOperationJournal;
    private readonly string _sourceCacheRoot;
    // The sources of the latest update check; the driver view reuses its driver check while fresh.
    private SharedScanSources? _scanSources;
    private ManufacturerDriverSnapshot? _lastDriverSnapshot;
    private DateTimeOffset? _driversCheckedAt;
    private CancellationTokenSource? _iconLoadCancellation;
    private static readonly TimeSpan ScanDriverReuse = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(250);
    private readonly FileUpdateTransactionLeaseProvider _updateTransactionLease;
    public DriverColumnLayout DriverLayout { get; } = new();
    public TableColumnLayout ApplicationLayout { get; } = new("applications", [280, 130, 130, 150]);
    public TableColumnLayout UpdateColumns { get; } = new("updates", [280, 105, 105, 115, 130]);
    private IReadOnlyList<ApplicationRow> _allApplications = [];
    private IReadOnlyList<UpdateRow> _allUpdates = [];
    private IReadOnlyList<ManufacturerDriverRow> _allDrivers = [];
    private InventorySnapshot? _snapshot;
    private int _availableUpdateCount;
    private bool _loaded;
    private bool _updateBusy;
    private bool _massUpdateBusy;
    private bool _stopBatch;
    private bool _waitingForStoreQueue;
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
    private readonly ScanController _scans = new();
    private UpdateCheckSnapshot? _lastCheckSnapshot;
    private bool _checksComplete;
    private bool _userSelectedUpdate;
    private bool _applyingUpdateFilter;
    private WorkspaceTab _activeTab = WorkspaceTab.Updates;
    private int _appKpiFilter = -1;
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
        var localStateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NaxUpdater");
        _sourceCacheRoot = Path.Combine(localStateRoot, "SourceCache");
        _manufacturerDriverService = new ManufacturerDriverService(_httpClient, kyoceraCacheDirectory: _sourceCacheRoot);
        _updateOperationJournal = new FileUpdateOperationJournal(Path.Combine(localStateRoot, "update-operation.json"));
        _updateTransactionLease = new FileUpdateTransactionLeaseProvider(Path.Combine(localStateRoot, "update-transaction.lock"));
        MainInfo.IsOpen = App.ShowSafetyInformation;

        NavUpdatesText.Text = LocalizationService.Get("NavUpdatesLabel");
        NavAppsText.Text = LocalizationService.Get("NavAppsLabel");
        NavDriversText.Text = LocalizationService.Get("NavDriversLabel");
        ToolTipService.SetToolTip(NavUpdatesButton, LocalizationService.Get("NavUpdatesTooltip"));
        ToolTipService.SetToolTip(NavAppsButton, LocalizationService.Get("NavAppsTooltip"));
        ToolTipService.SetToolTip(NavDriversButton, LocalizationService.Get("NavDriversTooltip"));
        UpdateInspectorRetryLabel.Text = LocalizationService.Get("RetrySingleCheck");
        UpdateInspectorFolderLabel.Text = LocalizationService.Get("OpenInstallFolder");
        AppInspectorFolderLabel.Text = LocalizationService.Get("OpenInstallFolder");

        UpdateSortHeaders();
        UpdateResultSortHeaders();
        UpdateDriverSortHeaders();
        SwitchWorkspace(WorkspaceTab.Updates);
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
            InstalledHardwareDriver? observedHardwareDriver = null;
            if (interruptedOperation.ProviderId.StartsWith("manufacturer-driver:", StringComparison.Ordinal))
            {
                var driverSnapshot = await _manufacturerDriverService.ReadInstalledAsync();
                var installedDriver = DriverUpdateIdentity.Find(driverSnapshot.Drivers,
                    interruptedOperation.ApplicationIdentity, interruptedOperation.CorrelationKey,
                    interruptedOperation.DisplayName, interruptedOperation.ProviderId);
                observedHardwareDriver = installedDriver;
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
                    observedVersion = UpdateCorrelation.FindMatch(
                        _snapshot.Applications, "", interruptedOperation.CorrelationKey)?.NormalizedVersion;
                }
            }
            var recoveredAssessment = _allUpdates.Select(row => row.Source).FirstOrDefault(update =>
                update.ApplicationIdentity.Equals(interruptedOperation.ApplicationIdentity, StringComparison.OrdinalIgnoreCase));
            var reachedTarget = UpdateRecoveryVerifier.HasReachedTarget(interruptedOperation, observedVersion, recoveredAssessment, observedDriver: observedHardwareDriver);
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
        if (_scans.IsActive)
        {
            await _scans.CancelAsync();
            return;
        }
        if (_updateBusy || _massUpdateBusy) return;
        _runOutcomes.Clear();
        if (_activeTab == WorkspaceTab.Drivers)
        {
            await CheckManufacturerDriversAsync();
            return;
        }
        await ScanAndCheckAsync();
    }

    private Task ScanAndCheckAsync() => RunScanAsync(ScanAndCheckCoreAsync);

    // One owner covers the whole read, including inventory and background drivers.
    // Nested stages use the same token and cannot independently unlock the UI.
    private async Task RunScanAsync(Func<CancellationToken, Task> scan)
    {
        using var run = _scans.TryBegin();
        if (run is null) return;
        var wasBusy = _updateBusy;
        SetUpdateBusy(true, null);
        ScanButton.IsEnabled = true;
        ScanButtonLabel.Text = LocalizationService.Get("CancelScan");
        try { await scan(run.Token); }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            _scanSources = null;
            StatusText.Text = LocalizationService.Get("ScanCanceled") +
                (_lastCheckSnapshot is not null && !_checksComplete ? " " + LocalizationService.Get("FinishChecksBeforeUpdating") : "");
        }
        finally
        {
            run.Dispose();
            ScanButtonLabel.Text = LocalizationService.Get(_activeTab == WorkspaceTab.Drivers
                ? "CheckDriversTop" : "ApplicationScanText");
            SetUpdateBusy(wasBusy, null);
            RefreshRetryButton();
        }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_activeTab == WorkspaceTab.Drivers)
        {
            ApplyDriverFilter();
        }
        else if (_activeTab == WorkspaceTab.Updates)
        {
            ApplyUpdateFilter();
        }
        else
        {
            ApplyFilter();
        }
    }

    private void ScanAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ScanButton.IsEnabled)
        {
            ScanButton_Click(ScanButton, new RoutedEventArgs());
        }
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FilterBox.Focus(FocusState.Keyboard);
        FilterBox.SelectAll();
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!string.IsNullOrEmpty(FilterBox.Text))
        {
            args.Handled = true;
            FilterBox.Text = string.Empty;
        }
    }

    private void SwitchToUpdatesAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SwitchWorkspace(WorkspaceTab.Updates);
    }

    private void SwitchToAppsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SwitchWorkspace(WorkspaceTab.Applications);
    }

    private void SwitchToDriversAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (NavDriversButton.IsEnabled)
        {
            DriversButton_Click(NavDriversButton, new RoutedEventArgs());
        }
    }

    private void KpiCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var cardIndex))
        {
            return;
        }
        if (_activeTab == WorkspaceTab.Updates)
        {
            var targetIndex = cardIndex switch
            {
                0 => 1, // updates
                1 => 2, // known
                2 => 3, // current
                3 => 4, // errors
                _ => 0
            };
            UpdateStatusFilter.SelectedIndex = UpdateStatusFilter.SelectedIndex == targetIndex ? 0 : targetIndex;
            RefreshKpiCards();
            return;
        }
        if (_activeTab == WorkspaceTab.Drivers)
        {
            var targetIndex = cardIndex switch
            {
                0 => 0, // all
                1 => 2, // current
                2 => 1, // updates
                3 => 6, // errors
                _ => 0
            };
            DriverStatusFilter.SelectedIndex = cardIndex > 0 && DriverStatusFilter.SelectedIndex == targetIndex ? 0 : targetIndex;
            RefreshKpiCards();
            return;
        }
        if (cardIndex == 0)
        {
            _appKpiFilter = -1;
            if (!string.IsNullOrEmpty(FilterBox.Text))
            {
                FilterBox.Text = string.Empty;
            }
        }
        else
        {
            _appKpiFilter = _appKpiFilter == cardIndex ? -1 : cardIndex;
        }
        ApplyFilter();
        RefreshKpiCards();
    }

    private void ShowApplicationsButton_Click(object sender, RoutedEventArgs e) => SwitchWorkspace(WorkspaceTab.Applications);

    private void ShowUpdatesButton_Click(object sender, RoutedEventArgs e) => SwitchWorkspace(WorkspaceTab.Updates);

    private void ShowUpdatesWorkspace() => SwitchWorkspace(WorkspaceTab.Updates);

    private void SwitchWorkspace(WorkspaceTab tab)
    {
        _activeTab = tab;
        UpdatesWorkspace.Visibility = tab == WorkspaceTab.Updates ? Visibility.Visible : Visibility.Collapsed;
        InventoryWorkspace.Visibility = tab == WorkspaceTab.Applications ? Visibility.Visible : Visibility.Collapsed;
        DriversWorkspace.Visibility = tab == WorkspaceTab.Drivers ? Visibility.Visible : Visibility.Collapsed;

        var activeStyle = (Style)Application.Current.Resources["NavTabActiveButtonStyle"];
        var inactiveStyle = (Style)Application.Current.Resources["NavTabButtonStyle"];
        NavUpdatesButton.Style = tab == WorkspaceTab.Updates ? activeStyle : inactiveStyle;
        NavAppsButton.Style = tab == WorkspaceTab.Applications ? activeStyle : inactiveStyle;
        NavDriversButton.Style = tab == WorkspaceTab.Drivers ? activeStyle : inactiveStyle;

        ShowSystemComponentsCheckBox.IsEnabled = tab == WorkspaceTab.Applications;
        FilterBox.IsEnabled = true;
        FilterBox.PlaceholderText = LocalizationService.Get(tab == WorkspaceTab.Drivers
            ? "DriverFilterPlaceholder" : "ApplicationFilterPlaceholder");
        ScanButtonLabel.Text = LocalizationService.Get(_scans.IsActive
            ? "CancelScan"
            : tab == WorkspaceTab.Drivers ? "CheckDriversTop" : "ApplicationScanText");

        RefreshKpiCards();
        if (tab == WorkspaceTab.Updates)
        {
            ApplyUpdateFilter();
        }
        else if (tab == WorkspaceTab.Applications)
        {
            ApplyFilter();
        }
        else
        {
            ApplyDriverFilter();
        }
    }

    private void UpdateNavBadges()
    {
        var actionableUpdates = _allUpdates.Count(static row =>
            row.Source.IsInstallable || row.Source.Status is UpdateStatus.Available or UpdateStatus.StoreQueued);
        NavUpdatesBadge.Visibility = actionableUpdates > 0 ? Visibility.Visible : Visibility.Collapsed;
        NavUpdatesBadgeText.Text = actionableUpdates.ToString("N0");

        var appCount = _snapshot is null
            ? 0
            : ShowSystemComponentsCheckBox.IsChecked == true
                ? _snapshot.Applications.Count
                : _snapshot.Applications.Count(static app => !app.IsSystemComponent);
        NavAppsBadge.Visibility = appCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        NavAppsBadgeText.Text = appCount.ToString("N0");

        var driverUpdates = _lastDriverSnapshot?.Results.Count(static r => r.Status == ManufacturerDriverStatus.Available) ?? 0;
        NavDriversBadge.Visibility = driverUpdates > 0 ? Visibility.Visible : Visibility.Collapsed;
        NavDriversBadgeText.Text = driverUpdates.ToString("N0");
    }

    private void RefreshKpiCards()
    {
        var activeCard = -1;
        if (_activeTab == WorkspaceTab.Updates)
        {
            DetectedSummaryLabel.Text = LocalizationService.Get("SummaryInstallableUpdates");
            VersionSummaryLabel.Text = LocalizationService.Get("SummaryKnownReleases");
            PathSummaryLabel.Text = LocalizationService.Get("SummaryUpToDate");
            GuardSummaryLabel.Text = LocalizationService.Get("SummaryCheckIssues");

            if (_lastCheckSnapshot is null)
            {
                DetectedCountText.Text = "—";
                VersionCountText.Text = "—";
                PathCountText.Text = "—";
                GuardCountText.Text = "—";
            }
            else
            {
                DetectedCountText.Text = _allUpdates.Count(static r => MatchesUpdateStatusFilter(r, "updates")).ToString("N0");
                VersionCountText.Text = _allUpdates.Count(static r => MatchesUpdateStatusFilter(r, "known")).ToString("N0");
                PathCountText.Text = _allUpdates.Count(static r => MatchesUpdateStatusFilter(r, "current")).ToString("N0");
                GuardCountText.Text = _allUpdates.Count(static r => MatchesUpdateStatusFilter(r, "errors")).ToString("N0");
            }
            var filterTag = (UpdateStatusFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
            activeCard = filterTag switch
            {
                "updates" => 0,
                "known" => 1,
                "current" => 2,
                "errors" => 3,
                _ => -1
            };
        }
        else if (_activeTab == WorkspaceTab.Applications)
        {
            DetectedSummaryLabel.Text = LocalizationService.Get("ApplicationDetectedSummary");
            VersionSummaryLabel.Text = LocalizationService.Get("ApplicationVersionsSummary");
            PathSummaryLabel.Text = LocalizationService.Get("ApplicationPathsSummary");
            GuardSummaryLabel.Text = LocalizationService.Get("ApplicationGuardsSummary");
            if (_snapshot is null)
            {
                DetectedCountText.Text = "—";
                VersionCountText.Text = "—";
                PathCountText.Text = "—";
                GuardCountText.Text = "—";
            }
            else
            {
                var applications = ShowSystemComponentsCheckBox.IsChecked == true
                    ? _snapshot.Applications
                    : _snapshot.Applications.Where(static application => !application.IsSystemComponent).ToArray();
                DetectedCountText.Text = applications.Count.ToString("N0");
                VersionCountText.Text = applications.Count(static application => !string.IsNullOrWhiteSpace(application.InstalledVersion)).ToString("N0");
                PathCountText.Text = applications.Count(static application => !string.IsNullOrWhiteSpace(application.PrimaryInstallPath)).ToString("N0");
                GuardCountText.Text = (applications.Count(static application => application.BlockedProviders.Count > 0) + _snapshot.UnmatchedPolicies.Count).ToString("N0");
            }
            activeCard = _appKpiFilter;
        }
        else
        {
            DetectedSummaryLabel.Text = LocalizationService.Get("DriverGroupsSummary");
            VersionSummaryLabel.Text = LocalizationService.Get("SummaryUpToDate");
            PathSummaryLabel.Text = LocalizationService.Get("NavUpdatesLabel");
            GuardSummaryLabel.Text = LocalizationService.Get("DriverIssuesSummary");
            if (_lastDriverSnapshot is null)
            {
                DetectedCountText.Text = _allDrivers.Count == 0 ? "—" : _allDrivers.Count.ToString("N0");
                VersionCountText.Text = "—";
                PathCountText.Text = "—";
                GuardCountText.Text = "—";
            }
            else
            {
                DetectedCountText.Text = _allDrivers.Count.ToString("N0");
                VersionCountText.Text = _allDrivers.Count(static row => MatchesDriverStatusFilter(row, "current")).ToString("N0");
                PathCountText.Text = _allDrivers.Count(static row => MatchesDriverStatusFilter(row, "updates")).ToString("N0");
                GuardCountText.Text = _allDrivers.Count(static row => MatchesDriverStatusFilter(row, "errors")).ToString("N0");
            }
            var driverTag = (DriverStatusFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
            activeCard = driverTag switch
            {
                "current" => 1,
                "updates" => 2,
                "errors" => 3,
                _ => -1
            };
        }

        KpiCard1Border.BorderThickness = new Thickness(activeCard == 0 ? 2 : 1);
        KpiCard2Border.BorderThickness = new Thickness(activeCard == 1 ? 2 : 1);
        KpiCard3Border.BorderThickness = new Thickness(activeCard == 2 ? 2 : 1);
        KpiCard4Border.BorderThickness = new Thickness(activeCard == 3 ? 2 : 1);
    }

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

    public void ShowUnhandledError(Exception exception) =>
        ShowUnexpectedFailure(LocalizationService.Get("UnexpectedErrorTitle"), exception);

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
        _stopBatch = true;
        UpdateBar.Title = LocalizationService.Get("CloseAfterOperationTitle");
        UpdateBar.Message = LocalizationService.Get("CloseAfterOperationMessage");
        UpdateBar.Severity = InfoBarSeverity.Informational;
        UpdateBar.IsOpen = true;
        return true;
    }

    public void SaveLayoutPreferences()
    {
        ApplicationLayout.Save();
        UpdateColumns.Save();
        DriverLayout.Save();
    }

    private void ApplicationHeaderGrid_LayoutUpdated(object? sender, object e) =>
        ApplicationLayout.Synchronize(ApplicationNameColumn.ActualWidth, ApplicationVersionColumn.ActualWidth,
            ApplicationProviderColumn.ActualWidth, ApplicationDateColumn.ActualWidth);

    private void UpdateHeaderGrid_LayoutUpdated(object? sender, object e) =>
        UpdateColumns.Synchronize(UpdateNameColumn.ActualWidth, UpdateInstalledColumn.ActualWidth,
            UpdateAvailableColumn.ActualWidth, UpdateStatusColumn.ActualWidth, UpdateActionColumn.ActualWidth);

    private void DriverHeaderGrid_LayoutUpdated(object? sender, object e)
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

    // Launcher.LaunchUriAsync can return false in an unpackaged app without opening anything,
    // so the shell opens HTTP(S) pages then, and a failure is shown instead of doing nothing.
    private async Task OpenInBrowserAsync(Uri page)
    {
        try
        {
            var isWeb = page.Scheme == Uri.UriSchemeHttps || page.Scheme == Uri.UriSchemeHttp;
            var isStore = page.Scheme.Equals("ms-windows-store", StringComparison.OrdinalIgnoreCase);
            if (!isWeb && !isStore)
                throw new InvalidOperationException(page.Scheme);
            if (await Launcher.LaunchUriAsync(page)) return;
            if (!isWeb) throw new InvalidOperationException(LocalizationService.Get("OpenStoreLinkFailed"));
            Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            UpdateBar.Title = LocalizationService.Get("OpenPageFailedTitle");
            UpdateBar.Message = page.AbsoluteUri + Environment.NewLine + LocalizationService.Text(exception.Message);
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.IsOpen = true;
        }
    }

    private async Task OpenInstallFolderAsync(string? fileOrFolderPath)
    {
        if (string.IsNullOrWhiteSpace(fileOrFolderPath))
        {
            return;
        }
        try
        {
            var fullPath = Path.GetFullPath(fileOrFolderPath);
            if (!Path.IsPathFullyQualified(fullPath) ||
                fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                fullPath.Length < 3 || !char.IsAsciiLetter(fullPath[0]) || fullPath[1] != ':')
            {
                throw new NotSupportedException(fileOrFolderPath);
            }
            var target = Directory.Exists(fullPath)
                ? fullPath
                : File.Exists(fullPath)
                    ? Path.GetDirectoryName(fullPath)
                    : null;
            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
            {
                throw new DirectoryNotFoundException(fullPath);
            }
            if (await Launcher.LaunchFolderPathAsync(target))
            {
                return;
            }
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var startInfo = new ProcessStartInfo(explorer) { UseShellExecute = false };
            startInfo.ArgumentList.Add(target);
            using var process = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            UpdateBar.Title = LocalizationService.Get("OpenFolderFailedTitle");
            UpdateBar.Message = fileOrFolderPath + Environment.NewLine + LocalizationService.Text(exception.Message);
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.IsOpen = true;
        }
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = LocalizationService.Get("ClipboardBusyStatus");
        }
    }

    private void RefreshActionStates()
    {
        RefreshMassUpdateButton();
        RefreshUninstallButton();
        RefreshRetryButton();
        UpdateInspectorRetryButton.IsEnabled = !_updateBusy && !_massUpdateBusy && _snapshot is not null && UpdatesList.SelectedItem is UpdateRow;
        if (UpdatesList.SelectedItem is UpdateRow updateRow)
        {
            UpdateInspectorActionButton.IsEnabled = updateRow.CanAct;
        }
        if (DriversList.SelectedItem is ManufacturerDriverRow driverRow)
        {
            DriverInspectorActionButton.IsEnabled = driverRow.CanAct;
        }
    }

    private void SetUpdateBusy(bool busy, string? message)
    {
        _updateBusy = busy;
        ScanButton.IsEnabled = !busy;
        NavDriversButton.IsEnabled = !busy;
        RefreshActionStates();
        ScanProgress.IsActive = busy;
        ScanProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
        }
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
                    _stopBatch = true;
                    UpdateBar.Title = LocalizationService.Get("LanguageAfterOperationTitle");
                    UpdateBar.Message = LocalizationService.Get("LanguageAfterOperationMessage");
                    UpdateBar.Severity = InfoBarSeverity.Informational;
                    UpdateBar.IsOpen = true;
                    return;
                }
                if (_scans.IsActive) await _scans.CancelAsync();
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

    private enum WorkspaceTab
    {
        Updates,
        Applications,
        Drivers
    }

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
