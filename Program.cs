using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace KOClient;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build().Run();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private const string AppTitle = "KO Client - DEADSHOT.io";
    private const string RepositoryUrl = "https://github.com/fluxmeansflex/KO-DS-Client";
    private const string AssetBaseUrl = "https://raw.githubusercontent.com/fluxmeansflex/KO-DS-Client/main/assets/";
    private const string AssetManifestUrl = AssetBaseUrl + "asset-paths.json?v=2";
    private static readonly HttpClient AssetClient = new();
    private static readonly Dictionary<string, Task<byte[]>> AssetCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object AssetCacheLock = new();
    private static Dictionary<string, string> AssetUrls = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> AssetByName = new(StringComparer.OrdinalIgnoreCase);
    private static string[] AssetFilters = [];
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    private CoreWebView2Environment? _environment;
    private bool _isFullscreen;
    private Rectangle _restoreBounds;
    private FormBorderStyle _restoreBorderStyle;
    private FormWindowState _restoreWindowState;
    private bool _restoreTopMost;

    public MainForm()
    {
        Text = AppTitle;
        Width = 1280;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        Icon = ReadAppIcon();
        WindowThemeSync.Attach(this);
        _webView.KeyUp += OnWebViewKeyUp;
        Controls.Add(_webView);
        Shown += async (_, _) => await InitializeAsync();
    }

    private static Dictionary<string, string> CreateAssetUrls(IEnumerable<string> paths)
    {
        return paths.ToDictionary(
            path => path,
            path => $"{AssetBaseUrl}{path}?v=2",
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> CreateAssetByName(IEnumerable<string> paths)
    {
        return paths.ToDictionary(
            path => path.Split('/')[^1],
            path => path,
            StringComparer.OrdinalIgnoreCase);
    }

    private static string[] CreateAssetFilters(IEnumerable<string> paths)
    {
        return paths
            .SelectMany(path => new[]
            {
                $"https://deadshot.io/{path}*",
                $"https://deadshot.io/*/{Path.GetFileName(path)}*"
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task LoadAssetManifestAsync()
    {
        try
        {
            var json = await AssetClient.GetStringAsync(AssetManifestUrl);
            var paths = JsonSerializer.Deserialize<string[]>(json);
            if (paths is not { Length: > 0 } || paths.Any(string.IsNullOrWhiteSpace))
            {
                return;
            }

            var assetUrls = CreateAssetUrls(paths);
            var assetByName = CreateAssetByName(paths);
            var assetFilters = CreateAssetFilters(paths);
            AssetUrls = assetUrls;
            AssetByName = assetByName;
            AssetFilters = assetFilters;
        }
        catch
        {
            //ko-client
        }
    }

    private async Task InitializeAsync()
    {
        var options = new CoreWebView2EnvironmentOptions(BrowserArguments());
        _environment = await CoreWebView2Environment.CreateAsync(null, ProfileDir(), options);
        await _webView.EnsureCoreWebView2Async(_environment);
        await LoadAssetManifestAsync();

        ConfigureCore(_webView.CoreWebView2);
        _webView.CoreWebView2.Navigate("https://deadshot.io");
        _ = CheckForUpdateAsync();
    }

    private static async Task CheckForUpdateAsync()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                return;
            }
            var result = MessageBox.Show(
                $"A new version is available: {update.TargetFullRelease.Version}. Update now?",
                AppTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (result != DialogResult.Yes)
            {
                return;
            }
            await manager.DownloadUpdatesAsync(update);
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (NotInstalledException)
        {
            //ko-client
        }
        catch (AcquireLockFailedException)
        {
            //ko-client
        }
        catch (ChecksumFailedException ex)
        {
            Debug.WriteLine($"err update checksum: {ex.Message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"err update: {ex.Message}");
        }
    }

    private void ConfigureCore(CoreWebView2 core)
    {
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsReputationCheckingRequired = false;
        core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("DEBUG") == "1";
        foreach (var filter in AssetFilters)
        {
            core.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        }
        foreach (var domain in BlockedDomains)
        {
            core.AddWebResourceRequestedFilter($"*://{domain}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.AddWebResourceRequestedFilter($"*://*.{domain}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        }
        core.WebResourceRequested += OnWebResourceRequested;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DocumentTitleChanged += (_, _) => Text = AppTitle;
        core.ContainsFullScreenElementChanged += (_, _) =>
            SetFullscreen(core.ContainsFullScreenElement);
    }

    private void OnWebViewKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.F11)
        {
            return;
        }

        e.Handled = true;
        BeginInvoke(() => SetFullscreen(!_isFullscreen));
    }

    private void SetFullscreen(bool fullscreen)
    {
        if (_isFullscreen == fullscreen)
        {
            return;
        }

        _isFullscreen = fullscreen;
        if (fullscreen)
        {
            _restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _restoreBorderStyle = FormBorderStyle;
            _restoreWindowState = WindowState;
            _restoreTopMost = TopMost;
            var screenBounds = Screen.FromControl(this).Bounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            Bounds = screenBounds;
            return;
        }

        TopMost = _restoreTopMost;
        FormBorderStyle = _restoreBorderStyle;
        WindowState = FormWindowState.Normal;
        Bounds = _restoreBounds;
        WindowState = _restoreWindowState;
    }
    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = e.Request.Uri;
        if (TryMatchDeadshotAsset(uri, out var assetPath))
        {
            using var deferral = e.GetDeferral();
            try
            {
                var data = await LoadAssetAsync(assetPath);
                var cacheControl = string.Equals(assetPath, "final.pkg", StringComparison.OrdinalIgnoreCase)
                    ? "no-cache, no-store, must-revalidate"
                    : "public, max-age=31536000";
                e.Response = CreateResponse(new MemoryStream(data), 200, "OK", ContentTypeFor(assetPath), cacheControl);
            }
            catch
            {
                RemoveCachedAsset(assetPath);
            }
            return;
        }
        if (IsDeadshotUrl(uri))
        {
            return;
        }

        if (IsBlockedHost(uri))
        {
            e.Response = _environment!.CreateWebResourceResponse(
                Stream.Null,
                204,
                "No Content",
                "Content-Length: 0\r\nAccess-Control-Allow-Origin: *\r\n");
        }
    }

    private CoreWebView2WebResourceResponse CreateResponse(Stream content, int statusCode, string reasonPhrase, string contentType, string cacheControl = "public, max-age=31536000")
    {
        return _environment!.CreateWebResourceResponse(
            content,
            statusCode,
            reasonPhrase,
            $"Content-Type: {contentType}\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: {cacheControl}\r\n");
    }

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Docs: https://learn.microsoft.com/vi-vn/dotnet/api/microsoft.web.webview2.core.corewebview2newwindowrequestedeventargs?view=webview2-dotnet-1.0.4022.49
        e.Handled = true;
        if (!e.IsUserInitiated)
        {
            return;
        }
        if (IsGoogleLoginUrl(e.Uri) && _environment is not null)
        {
            var deferral = e.GetDeferral();
            try
            {
                var popup = new LoginForm(_environment);
                popup.Show(this);
                await popup.InitializeAsync();
                e.NewWindow = popup.Core;
            }
            finally
            {
                deferral.Complete();
            }
            return;
        }
        OpenExternalUrl(e.Uri);
    }
    private static void OpenExternalUrl(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return;
        }
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
    private static bool IsGoogleLoginUrl(string rawUrl)
    {
        return Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            && uri.Host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsDeadshotUrl(string rawUrl)
    {
        return Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            && uri.Host.Equals("deadshot.io", StringComparison.OrdinalIgnoreCase);
    }
    private static string ProfileDir()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(baseDir, "KO Client - DEADSHOT.io", "WebView2");
        Directory.CreateDirectory(dir);
        return dir;
    }
    private static string BrowserArguments()
    {
        return string.Join(' ',
            "--autoplay-policy=no-user-gesture-required",
            "--disable-background-timer-throttling",
            "--disable-backgrounding-occluded-windows",
            "--disable-component-update",
            "--disable-features=msSmartScreenProtection",
            "--disable-renderer-backgrounding",
            "--disable-sync",
            "--enable-gpu-rasterization",
            "--ignore-gpu-blocklist",
            "--no-first-run"
            // ,"--remote-debugging-port=0"
            );
    }
    private static readonly string[] BlockedDomains =
    [
        "adnxs.com",
        "adsafeprotected.com",
        "amazon-adsystem.com",
        "cloudflareinsights.com",
        "doubleclick.net",
        "google-analytics.com",
        "googleadservices.com",
        "googlesyndication.com",
        "googletagmanager.com",
        "googletagservices.com",
        "imasdk.googleapis.com",
        "pubmatic.com",
        "rubiconproject.com",
        "scorecardresearch.com",
        "smilewanted.com",
        "the-ozone-project.com",
        "tynt.com",
        "vntsm.com",
        "yellowblue.io"
    ];
    private static bool IsBlockedHost(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return BlockedDomains.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase));
    }
    private static bool TryMatchDeadshotAsset(string rawUrl, out string assetPath)
    {
        assetPath = string.Empty;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("deadshot.io", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var requestPath = NormalizeRequestPath(uri.AbsolutePath);
        if (requestPath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
        {
            requestPath = requestPath["assets/".Length..];
        }
        if (AssetUrls.ContainsKey(requestPath))
        {
            assetPath = requestPath;
            return true;
        }
        var matchingPath = AssetUrls.Keys.FirstOrDefault(path =>
            requestPath.EndsWith($"/assets/{path}", StringComparison.OrdinalIgnoreCase)
            || requestPath.EndsWith($"/{path}", StringComparison.OrdinalIgnoreCase));
        if (matchingPath is not null)
        {
            assetPath = matchingPath;
            return true;
        }
        var fileName = requestPath.Split('/')[^1];
        return AssetByName.TryGetValue(fileName, out assetPath!);
    }
    private static Task<byte[]> LoadAssetAsync(string assetPath)
    {
        if (string.Equals(assetPath, "final.pkg", StringComparison.OrdinalIgnoreCase))
        {
            return AssetClient.GetByteArrayAsync(AssetUrls[assetPath]);
        }

        lock (AssetCacheLock)
        {
            if (!AssetCache.TryGetValue(assetPath, out var download))
            {
                download = AssetClient.GetByteArrayAsync(AssetUrls[assetPath]);
                AssetCache[assetPath] = download;
            }

            return download;
        }
    }
    private static void RemoveCachedAsset(string assetPath)
    {
        lock (AssetCacheLock)
        {
            AssetCache.Remove(assetPath);
        }
    }
    private static string NormalizeRequestPath(string path)
    {
        return Uri.UnescapeDataString(path).Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }
    private static string ContentTypeFor(string assetPath)
    {
        return Path.GetExtension(assetPath).ToLowerInvariant() switch
        {
            ".css" => "text/css; charset=utf-8",
            ".glb" => "model/gltf-binary",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".ico" => "image/x-icon",
            ".svg" => "image/svg+xml",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            ".mp3" => "audio/mpeg",
            ".pkg" => "application/octet-stream",
            _ => "application/octet-stream"
        };
    }
    internal static Icon ReadAppIcon()
    {
        return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
    }
}
internal sealed class LoginForm : Form
{
    private readonly CoreWebView2Environment _environment;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private CoreWebView2? _core;

    public LoginForm(CoreWebView2Environment environment)
    {
        _environment = environment;
        Text = "Google Login";
        Icon = MainForm.ReadAppIcon();
        Width = 900;
        Height = 720;
        StartPosition = FormStartPosition.CenterParent;
        WindowThemeSync.Attach(this);
        Controls.Add(_webView);
    }
    public CoreWebView2 Core => _core!;
    public async Task InitializeAsync()
    {
        await _webView.EnsureCoreWebView2Async(_environment);
        _core = _webView.CoreWebView2;
        _core.WindowCloseRequested += OnWindowCloseRequested;
        _core.NavigationCompleted += OnNavigationCompleted;
    }

    private void OnWindowCloseRequested(object? sender, object e)
    {
        Close();
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (MainFormIsDeadshot(_webView.Source?.ToString()))
        {
            await Task.Delay(500);
            if (!IsDisposed)
            {
                Close();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _core is not null)
        {
            _core.WindowCloseRequested -= OnWindowCloseRequested;
            _core.NavigationCompleted -= OnNavigationCompleted;
            _core = null;
        }

        base.Dispose(disposing);
    }
    private static bool MainFormIsDeadshot(string? rawUrl)
    {
        return rawUrl is not null
            && Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            && uri.Host.Equals("deadshot.io", StringComparison.OrdinalIgnoreCase);
    }
}
internal static partial class WindowThemeSync
{
    private const int DwMwaUseImmersiveDarkMode = 20;
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    public static void Attach(Form form)
    {
        form.HandleCreated += OnHandleCreated;
        form.Disposed += OnDisposed;
        if (form.IsHandleCreated)
        {
            Apply(form);
        }
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }
    private static void OnHandleCreated(object? sender, EventArgs e)
    {
        if (sender is Form form)
        {
            Apply(form);
        }
    }
    private static void OnDisposed(object? sender, EventArgs e)
    {
        if (sender is not Form form)
        {
            return;
        }
        form.HandleCreated -= OnHandleCreated;
        form.Disposed -= OnDisposed;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General
            && e.Category != UserPreferenceCategory.VisualStyle
            && e.Category != UserPreferenceCategory.Color)
        {
            return;
        }
        foreach (Form form in Application.OpenForms)
        {
            if (form.IsDisposed || !form.IsHandleCreated)
            {
                continue;
            }

            if (form.InvokeRequired)
            {
                form.BeginInvoke(() => Apply(form));
                continue;
            }
            Apply(form);
        }
    }
    private static void Apply(Form form)
    {
        if (!OperatingSystem.IsWindows() || !form.IsHandleCreated)
        {
            return;
        }
        var useDarkTitleBar = !UsesLightTheme();
        DwmSetWindowAttribute(form.Handle, DwMwaUseImmersiveDarkMode, ref useDarkTitleBar, sizeof(int));
    }
    private static bool UsesLightTheme()
    {
        using var personalize = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        var value = personalize?.GetValue(AppsUseLightTheme);
        return value switch
        {
            0 => false,
            _ => true
        };
    }
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, [MarshalAs(UnmanagedType.Bool)] ref bool attributeValue, int attributeSize);
}
