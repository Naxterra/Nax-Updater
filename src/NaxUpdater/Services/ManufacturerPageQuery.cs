using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NaxUpdater.Core.Services;
using System.Text;
using System.Text.Json;

namespace NaxUpdater.Services;

// Some public manufacturer catalogs require a browser transport. Use an isolated
// WebView profile, never the user's browser, and never expose an update action.
internal static class ManufacturerPageQuery
{
    private static Window? _window;

    public static bool TryStart(string[] arguments)
    {
        if (arguments.Length < 2 || arguments[1] != "--manufacturer-source") return false;
        _ = RunAsync(arguments);
        return true;
    }

    private static async Task RunAsync(string[] arguments)
    {
        string? html = null;
        string? error = null;
        var address = arguments.Length == 3 ? arguments[2] : "";
        WebView2? browser = null;
        try
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !ManufacturerSourceQuery.IsAllowed(uri))
                throw new InvalidOperationException("Unsupported manufacturer catalog address.");
            _window = new Window();
            browser = new WebView2();
            _window.Content = browser;
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NaxUpdater", "ManufacturerWeb", uri.Host);
            var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, profile, null);
            await browser.EnsureCoreWebView2Async(environment);
            var core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}");
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Deny;
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) loaded.TrySetException(new IOException("Manufacturer browser navigation failed."));
                else loaded.TrySetResult();
            };
            core.Navigate(uri.AbsoluteUri);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(8));
            // Let a manufacturer's normal public-page redirect and catalog rendering finish.
            await Task.Delay(1200);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var catalogReady = uri.Host switch
                {
                    "www.dell.com" => "!!document.querySelector('a[id^=\"download-\"][href^=\"https://dl.dell.com/\"]')",
                    "mysupport.razer.com" => "!!document.querySelector('a[data-answer-id]') && document.body.innerText.includes('Firmware')",
                    "www.intel.com" => "!!document.querySelector('meta[name=\"DownloadVersion\"]')",
                    _ => "!!document.querySelector('pre') && document.body.innerText.includes('downloads')"
                };
                var ready = await core.ExecuteScriptAsync("document.readyState === 'complete' && !!document.body && (" + catalogReady + ")");
                if (ready == "true" && !core.Source.Contains("/pre_page_check/", StringComparison.Ordinal)) break;
                await Task.Delay(400);
            }
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var final) || !ManufacturerSourceQuery.IsAllowed(final))
                throw new InvalidDataException("Manufacturer catalog redirected to an unsupported address.");
            var encoded = await core.ExecuteScriptAsync("document.documentElement.outerHTML");
            html = JsonSerializer.Deserialize<string>(encoded);
            if (html is null || html.Length > ManufacturerSourceQuery.MaximumPageCharacters)
                throw new InvalidDataException("Manufacturer catalog response was empty or too large.");
        }
        catch (Exception exception) { error = exception.Message; html = null; }
        finally
        {
            browser?.Close();
        }
        try
        {
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            output.Write(JsonSerializer.Serialize(new ManufacturerPageResponse(address, html, error)));
            output.Flush();
        }
        finally { _window?.Close(); Application.Current.Exit(); }
    }
}
