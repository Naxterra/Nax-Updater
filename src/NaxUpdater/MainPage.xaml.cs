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
        if (DriversWorkspace.Visibility == Visibility.Visible)
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
            ScanButtonLabel.Text = LocalizationService.Get(DriversWorkspace.Visibility == Visibility.Visible
                ? "CheckDriversTop" : "ApplicationScanText");
            SetUpdateBusy(wasBusy, null);
            RefreshRetryButton();
        }
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
        // Update checks never include Windows components (Windows Update services them), so
        // the switch has nothing to show here and is hidden instead of greyed out.
        ShowSystemComponentsCheckBox.Visibility = Visibility.Collapsed;
        ApplyUpdateFilter();
    }

    private void ConfigureApplicationHeader()
    {
        ScanButtonLabel.Text = LocalizationService.Get(!_scans.IsActive ? "ApplicationScanText" : "CancelScan");
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
        ScanButtonLabel.Text = LocalizationService.Get(!_scans.IsActive ? "CheckDriversTop" : "CancelScan");
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

    private void SetUpdateBusy(bool busy, string? message)
    {
        _updateBusy = busy;
        ScanButton.IsEnabled = !busy;
        DriversButton.IsEnabled = !busy;
        // The lists stay enabled so they can be scrolled and inspected while an
        // update runs; row actions are locked instead (RefreshMassUpdateButton).
        ShowUpdatesButton.IsEnabled = _allUpdates.Count > 0;
        RefreshMassUpdateButton();
        RefreshUninstallButton();
        RefreshRetryButton();
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
