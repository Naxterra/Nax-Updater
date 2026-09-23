using Microsoft.UI.Xaml;
using System.Globalization;

namespace NaxUpdater;

public partial class App : Application
{
    public static Window MainWindow { get; private set; } = null!;
    public static string CurrentLanguage { get; private set; } = "en-US";
    public static bool ShowSafetyInformation { get; private set; } = true;

    public App()
    {
        CurrentLanguage = LoadLanguage();
        ShowSafetyInformation = LoadBooleanSetting("show-safety-information.txt", true);
        Services.TaskbarIdentity.Initialize();
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = CurrentLanguage;
        var culture = CultureInfo.GetCultureInfo(CurrentLanguage);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.ProcessPath is { } executable &&
            Core.Services.UpdateHostLifetime.TryDetachFromLauncher(Environment.GetCommandLineArgs(), executable))
        {
            Exit();
            return;
        }
        // Acquired only after the launcher detach above: the detaching launcher
        // exits without ever showing a window, so it must not hold the lock.
        if (!TryAcquireSingleInstance())
        {
            ActivateExistingInstance();
            Exit();
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private const string SingleInstanceName = @"Local\NaxUpdater.SingleInstance";
    private static Mutex? _instanceLock;

    private static bool TryAcquireSingleInstance()
    {
        try
        {
            _instanceLock = new Mutex(false, SingleInstanceName);
            if (_instanceLock.WaitOne(TimeSpan.Zero)) return true;
        }
        catch (AbandonedMutexException)
        {
            return true; // The previous owner crashed; the lock now belongs to this process.
        }
        catch (UnauthorizedAccessException)
        {
            // Owned by an instance running at a higher integrity level (Run as administrator).
        }
        _instanceLock?.Dispose();
        _instanceLock = null;
        return false;
    }

    private static void ActivateExistingInstance()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id || process.MainWindowHandle == IntPtr.Zero) continue;
                if (IsIconic(process.MainWindowHandle)) ShowWindow(process.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(process.MainWindowHandle);
                return;
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

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
        // Hand the single-instance lock over before the replacement starts.
        _instanceLock?.ReleaseMutex();
        _instanceLock?.Dispose();
        _instanceLock = null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = true });
        MainWindow.Close();
    }

    public static void SetShowSafetyInformation(bool show)
    {
        ShowSafetyInformation = show;
        try
        {
            var settingsDirectory = GetSettingsDirectory();
            Directory.CreateDirectory(settingsDirectory);
            File.WriteAllText(Path.Combine(settingsDirectory, "show-safety-information.txt"), show.ToString());
        }
        catch
        {
            // Keep the in-memory preference when the settings directory is unavailable.
        }
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

    private static bool LoadBooleanSetting(string fileName, bool fallback)
    {
        try
        {
            var path = Path.Combine(GetSettingsDirectory(), fileName);
            return File.Exists(path) && bool.TryParse(File.ReadAllText(path).Trim(), out var saved)
                ? saved
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string GetSettingsDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NaxUpdater");
}
