using System.Runtime.InteropServices;

namespace NaxUpdater.Services;

internal static class TaskbarIdentity
{
    private const string AppUserModelId = "Naxterra.NaxUpdater";

    public static void Initialize()
    {
        _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
