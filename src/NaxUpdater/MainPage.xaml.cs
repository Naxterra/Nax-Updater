using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using NaxUpdater.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace NaxUpdater;

public sealed partial class MainPage : Page
{
    private readonly ObservableCollection<ApplicationRow> _visibleApplications = [];
    private readonly ObservableCollection<UpdateRow> _updates = [];
    private readonly ApplicationInventoryService _inventoryService;
    private readonly HttpClient _httpClient = new();
    private readonly UpdateExecutionService _updateExecutionService = new();
    private readonly ApplicationRemovalService _applicationRemovalService = new();
    private IReadOnlyList<ApplicationRow> _allApplications = [];
    private InventorySnapshot? _snapshot;
    private bool _loaded;
    private bool _updateBusy;
    private bool _massUpdateBusy;
    private ApplicationSortColumn _sortColumn = ApplicationSortColumn.Name;
    private bool _sortDescending;

    public MainPage()
    {
        InitializeComponent();
        ApplicationsList.ItemsSource = _visibleApplications;
        UpdatesList.ItemsSource = _updates;
        _inventoryService = new ApplicationInventoryService(Path.Combine(
            AppContext.BaseDirectory,
            "Configuration",
            "application-policies.json"));
        MainInfo.IsOpen = App.ShowSafetyInformation;
        UpdateSortHeaders();
    }

    private void MainInfo_CloseButtonClick(InfoBar sender, object args) => App.SetShowSafetyInformation(false);

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        await ScanAndCheckAsync();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanAndCheckAsync();

    private async Task ScanAndCheckAsync()
    {
        InventoryWorkspace.Visibility = Visibility.Visible;
        UpdatesWorkspace.Visibility = Visibility.Collapsed;
        FilterBox.IsEnabled = true;
        ShowSystemComponentsCheckBox.IsEnabled = true;
        _updates.Clear();
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
            IssueBar.Message = exception.Message;
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

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

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
        UninstallButton.IsEnabled = row.Source.RemovalPlan is not null;
        EvidenceItems.ItemsSource = row.Source.Evidence.Select(static evidence => new EvidenceRow(evidence)).ToArray();
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (ApplicationsList.SelectedItem is not ApplicationRow row || row.Source.RemovalPlan is null)
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
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetUpdateBusy(true, LocalizationService.Format("RemovingApplication", row.Name));
        UninstallButton.IsEnabled = false;
        try
        {
            var result = await _applicationRemovalService.RemoveAsync(row.Source);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? $"Removal failed with code {result.ExitCode}.");
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
            UpdateBar.Message = exception.Message;
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.IsOpen = true;
        }
        finally
        {
            SetUpdateBusy(false, null);
            UninstallButton.IsEnabled = ApplicationsList.SelectedItem is ApplicationRow selected && selected.Source.RemovalPlan is not null;
        }
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
        if (_snapshot is null)
        {
            await ScanAsync();
        }
        if (_snapshot is null)
        {
            return;
        }

        SetUpdateBusy(true, LocalizationService.Get("CheckingProviders"));
        UpdateBar.IsOpen = false;
        try
        {
            var catalogPath = Path.Combine(AppContext.BaseDirectory, "Configuration", "update-providers.json");
            var catalog = await UpdateProviderCatalogLoader.LoadAsync(catalogPath);
            var service = new UpdateCheckService(_httpClient, catalog);
            var result = await service.CheckAsync(_snapshot);

            _updates.Clear();
            foreach (var update in result.Results)
            {
                _updates.Add(new UpdateRow(update));
            }
            var available = result.Results.Count(static update => update.Status == UpdateStatus.Available);
            var errors = result.Results.Count(static update => update.Status == UpdateStatus.Error);
            UpdateCountText.Text = LocalizationService.Format("UpdateCountFormat", available, _updates.Count);
            RefreshMassUpdateButton();
            ShowUpdatesButton.Content = LocalizationService.Format(
                "UpdatesNavigationCoverage",
                available,
                result.Results.Count - result.UnsupportedApplicationCount,
                result.Results.Count);
            ShowUpdatesButton.IsEnabled = _updates.Count > 0;
            UpdatesList.SelectedItem = _updates.FirstOrDefault();
            if (showWorkspace)
            {
                ShowUpdatesWorkspace();
            }
            StatusText.Text = LocalizationService.Format(
                "UpdateCheckedStatus",
                result.Results.Count - result.UnsupportedApplicationCount,
                available,
                result.UnsupportedApplicationCount);

            UpdateBar.Title = available == 0
                ? LocalizationService.Get("ApplicationsCurrent")
                : LocalizationService.Format("UpdatesAvailableTitle", available);
            UpdateBar.Message = errors == 0
                ? LocalizationService.Get("InstallersPreserve")
                : LocalizationService.Format("ProviderErrors", errors);
            UpdateBar.Severity = errors > 0 ? InfoBarSeverity.Warning : available > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
            UpdateBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            UpdateBar.Title = LocalizationService.Get("UpdateCheckFailed");
            UpdateBar.Message = exception.Message;
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.IsOpen = true;
            StatusText.Text = LocalizationService.Get("UpdateCheckDidNotComplete");
        }
        finally
        {
            SetUpdateBusy(false, null);
        }
    }

    private void ShowApplicationsButton_Click(object sender, RoutedEventArgs e)
    {
        InventoryWorkspace.Visibility = Visibility.Visible;
        UpdatesWorkspace.Visibility = Visibility.Collapsed;
        FilterBox.IsEnabled = true;
        ShowSystemComponentsCheckBox.IsEnabled = true;
    }

    private void ShowUpdatesButton_Click(object sender, RoutedEventArgs e) => ShowUpdatesWorkspace();

    private void ShowUpdatesWorkspace()
    {
        InventoryWorkspace.Visibility = Visibility.Collapsed;
        UpdatesWorkspace.Visibility = Visibility.Visible;
        FilterBox.IsEnabled = false;
        ShowSystemComponentsCheckBox.IsEnabled = false;
    }

    private void UpdatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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
        UpdateLanguageText.Text = row.LanguageDetail;
        UpdatePlatformText.Text = row.PlatformDetail;
        UpdateSecurityText.Text = row.SecurityDetail;
        UpdateReleaseText.Text = row.ReleaseNotes;
        UpdateMessageText.Text = row.Message;
    }

    private async void UpdateRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _massUpdateBusy || sender is not Button { DataContext: UpdateRow row } button ||
            !row.CanInstall || row.Source.ExecutionPlan is null)
        {
            return;
        }
        UpdatesList.SelectedItem = row;
        await ExecuteUpdateAsync(row, button, rescanAfterSuccess: true);
    }

    private async void UpdateAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _massUpdateBusy)
        {
            return;
        }
        var queue = _updates
            .Where(static row => row.CanInstall)
            .OrderBy(static row => row.Source.ExecutionPlan?.Kind == UpdateExecutionKind.StorePackage ? 1 : 0)
            .ThenBy(static row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (queue.Length == 0)
        {
            return;
        }

        _massUpdateBusy = true;
        var completed = 0;
        var failed = 0;
        try
        {
            foreach (var row in queue)
            {
                UpdatesList.SelectedItem = row;
                StatusText.Text = LocalizationService.Format("MassUpdateProgress", completed + failed + 1, queue.Length, row.Name);
                if (await ExecuteUpdateAsync(row, null, rescanAfterSuccess: false))
                {
                    completed++;
                }
                else
                {
                    failed++;
                }
            }
            await ScanAsync();
            await CheckUpdatesAsync(showWorkspace: true);
            UpdateBar.Title = LocalizationService.Get("MassUpdateCompleteTitle");
            UpdateBar.Message = LocalizationService.Format("MassUpdateCompleteMessage", completed, failed);
            UpdateBar.Severity = failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            UpdateBar.IsOpen = true;
        }
        finally
        {
            _massUpdateBusy = false;
            RefreshMassUpdateButton();
        }
    }

    private async Task<bool> ExecuteUpdateAsync(UpdateRow row, Button? button, bool rescanAfterSuccess)
    {
        if (!row.CanInstall || row.Source.ExecutionPlan is null)
        {
            return false;
        }

        var running = _updateExecutionService.FindRunningProcesses(row.Source);
        if (running.Count > 0)
        {
            UpdateBar.Title = LocalizationService.Format("CloseFirstTitle", row.Name);
            UpdateBar.Message = LocalizationService.Format("CloseProcessesMessage", string.Join(", ", running));
            UpdateBar.Severity = InfoBarSeverity.Warning;
            UpdateBar.IsOpen = true;
            return false;
        }

        var plan = row.Source.ExecutionPlan;
        SetUpdateBusy(true, LocalizationService.Format("PreparingUpdate", row.Name));
        if (button is not null)
        {
            button.IsEnabled = false;
        }
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = plan.Kind is UpdateExecutionKind.NativeCommand or UpdateExecutionKind.StorePackage;
        UpdateProgress.Value = 0;
        try
        {
            VerifiedInstaller? installer = null;
            if (plan.Kind is UpdateExecutionKind.DownloadedExe or UpdateExecutionKind.DownloadedMsi or UpdateExecutionKind.DownloadedZipMsi)
            {
                var cacheRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NaxUpdater",
                    "Downloads");
                var progress = new Progress<double>(value => UpdateProgress.Value = value * 100);
                installer = await new UpdatePackageDownloader(_httpClient, new NativeAuthenticodeVerifier())
                    .DownloadAndVerifyAsync(row.Source, cacheRoot, progress);
                UpdateProgress.IsIndeterminate = true;
                StatusText.Text = plan.RequireAuthenticode
                    ? LocalizationService.Format("VerifiedInstallerStarting", Path.GetFileName(installer.Path), installer.Signer)
                    : LocalizationService.Format("HashVerifiedInstallerStarting", Path.GetFileName(installer.Path));
            }

            var result = await _updateExecutionService.ExecuteAsync(row.Source, installer);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? $"Installer exited with code {result.ExitCode}.");
            }

            UpdateBar.Title = LocalizationService.Format("UpdateCompletedTitle", row.Name);
            UpdateBar.Message = result.ExitCode == 3010
                ? LocalizationService.Get("RestartRequired")
                : LocalizationService.Get("UpdateCompletedMessage");
            UpdateBar.Severity = InfoBarSeverity.Success;
            UpdateBar.IsOpen = true;
            if (rescanAfterSuccess)
            {
                await ScanAsync();
                await CheckUpdatesAsync(showWorkspace: true);
            }
            return true;
        }
        catch (ApplicationStillRunningException exception)
        {
            UpdateBar.Title = LocalizationService.Get("ApplicationStillRunning");
            UpdateBar.Message = LocalizationService.Format("CloseProcessesMessage", string.Join(", ", exception.ProcessNames));
            UpdateBar.Severity = InfoBarSeverity.Warning;
            UpdateBar.IsOpen = true;
            return false;
        }
        catch (Exception exception)
        {
            UpdateBar.Title = LocalizationService.Format("NotUpdatedTitle", row.Name);
            UpdateBar.Message = exception.Message;
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.IsOpen = true;
            return false;
        }
        finally
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateProgress.IsIndeterminate = false;
            if (button is not null)
            {
                button.IsEnabled = row.CanInstall;
            }
            SetUpdateBusy(false, null);
        }
    }

    private void SetUpdateBusy(bool busy, string? message)
    {
        _updateBusy = busy;
        ScanButton.IsEnabled = !busy;
        ShowUpdatesButton.IsEnabled = !busy && _updates.Count > 0;
        RefreshMassUpdateButton();
        ScanProgress.IsActive = busy;
        ScanProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
        }
    }

    private void RefreshMassUpdateButton()
    {
        var verifiedUpdates = _updates.Count(static row =>
            row.CanInstall && row.Source.Status == UpdateStatus.Available &&
            row.Source.ExecutionPlan?.Kind != UpdateExecutionKind.StorePackage);
        var storeActions = _updates.Count(static row =>
            row.CanInstall && row.Source.Status == UpdateStatus.Available &&
            row.Source.ExecutionPlan?.Kind == UpdateExecutionKind.StorePackage);
        UpdateAllButton.Content = storeActions > 0
            ? LocalizationService.Format("UpdateAllCountWithStore", verifiedUpdates, storeActions)
            : LocalizationService.Format("UpdateAllCount", verifiedUpdates);
        UpdateAllButton.IsEnabled = !_updateBusy && !_massUpdateBusy && verifiedUpdates + storeActions > 0;
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
                    Text = "NaxUpdater",
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
}
