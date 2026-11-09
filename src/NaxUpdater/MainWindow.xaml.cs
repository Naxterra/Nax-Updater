using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using NaxUpdater.Services;
using Windows.Graphics;

namespace NaxUpdater;

public sealed partial class MainWindow : Window
{
    private SizeInt32 _restoredSize;
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
        var preferences = ViewSettings.Current;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        _restoredSize = new SizeInt32(Math.Min(preferences.Width, workArea.Width), Math.Min(preferences.Height, workArea.Height));
        AppWindow.Resize(_restoredSize);
        if (preferences.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        AppWindow.Changed += (_, change) =>
        {
            if (change.DidSizeChange && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
                _restoredSize = AppWindow.Size;
        };
        WindowRoot.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        UpdateCaptionButtons();
        RootFrame.Navigate(typeof(MainPage));
        // Closing the window ends the process that owns a running installation's
        // lease and payload locks; wait until the change is finished.
        AppWindow.Closing += (_, args) =>
        {
            if (RootFrame.Content is MainPage page)
            {
                if (page.DeferCloseUntilIdle()) { args.Cancel = true; return; }
            }
            SaveViewPreferences();
        };
    }

    public void SaveViewPreferences()
    {
        if (RootFrame.Content is MainPage page) page.SaveLayoutPreferences();
        ViewSettings.Current = ViewSettings.Current with
        {
            Width = _restoredSize.Width,
            Height = _restoredSize.Height,
            Maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized }
        };
        ViewSettings.Save();
    }

    private void UpdateCaptionButtons()
    {
        AppWindow.TitleBar.ButtonForegroundColor = WindowRoot.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
    }
}
