using Microsoft.UI.Xaml;
using System.Globalization;

namespace NaxUpdater;

public partial class App : Application
{
    public static Window MainWindow { get; private set; } = null!;
    public static string CurrentLanguage { get; private set; } = "en-US";

    public App()
    {
        CurrentLanguage = LoadLanguage();
        Services.TaskbarIdentity.Initialize();
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = CurrentLanguage;
        var culture = CultureInfo.GetCultureInfo(CurrentLanguage);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    public static void RestartWithLanguage(string language)
    {
        if (language is not ("en-US" or "de-DE") || language == CurrentLanguage)
        {
            return;
        }
        var settingsDirectory = GetSettingsDirectory();
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "language.txt"), language);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = true });
        MainWindow.Close();
    }

    private static string LoadLanguage()
    {
        try
        {
            var path = Path.Combine(GetSettingsDirectory(), "language.txt");
            if (File.Exists(path))
            {
                var saved = File.ReadAllText(path).Trim();
                if (saved is "en-US" or "de-DE")
                {
                    return saved;
                }
            }
        }
        catch
        {
            // Use the Windows language when settings are unavailable.
        }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase)
            ? "de-DE"
            : "en-US";
    }

    private static string GetSettingsDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NaxUpdater");
}
