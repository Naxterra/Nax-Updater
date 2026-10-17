using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace NaxUpdater;

public sealed partial class MainWindow : Window
{
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
        AppWindow.Resize(new SizeInt32(1380, 860));
        WindowRoot.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        UpdateCaptionButtons();
        RootFrame.Navigate(typeof(MainPage));
        // Closing the window ends the process that owns a running installation's
        // lease and payload locks; wait until the change is finished.
        AppWindow.Closing += (_, args) =>
        {
            if (RootFrame.Content is MainPage page && page.DeferCloseUntilIdle()) args.Cancel = true;
        };
    }

    private void UpdateCaptionButtons()
    {
        AppWindow.TitleBar.ButtonForegroundColor = WindowRoot.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
    }
}
