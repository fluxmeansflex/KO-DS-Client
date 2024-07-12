using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace KOClient;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private const string AppTitle = "KO Client - DEADSHOT.io";
    private const string LatestReleaseApi = "https://api.github.com/repos/fluxmeansflex/KO-DS-Client/releases/latest";
    private const string InstallerAssetSuffix = ".exe";
    private const string AssetBaseUrl = "https://raw.githubusercontent.com/fluxmeansflex/KO-DS-Client/main/assets/";
    private static readonly string[] AssetPaths =
    [
        "ar2.glb",
        "audio/1-kill.mp3",
        "audio/2-kill.mp3",
        "audio/3-kill.mp3",
        "audio/4-kill.mp3",
        "audio/5-kill.mp3",
        "audio/ar-kill1.mp3",
        "audio/ar-kill2.mp3",
        "audio/ar-kill3.mp3",
        "audio/ar-kill4.mp3",
        "audio/ar-kill5.mp3",
        "audio/ko-awp.mp3",
        "audio/ko-famas.mp3",
        "audio/ko-scar.mp3",
        "audio/ko-shotgun.mp3",
        "awp.glb",
        "css/settings.css",
        "css/username.css",
        "favicon.ico",
        "favicon.png",
        "final.pkg",
        "js/core.js",
        "promo/background0.webp",
        "promo/background1.webp",
        "promo/background2.webp",
        "promo/background3.webp",
        "promo/background4.webp",
        "promo/background5.webp",
        "promo/background6.webp",
        "promo/background7.webp",
        "promo/button-skin.webp",
        "promo/logo.png",
        "promo/logo.webp",
        "shotgun.glb",
        "skins/compressed/koar.webp",
        "skins/compressed/koar0.webp",
        "skins/compressed/koar0-2.webp",
        "skins/compressed/koar0-2small.webp",
        "skins/compressed/koar0small.webp",
        "skins/compressed/koawp.webp",
        "skins/compressed/koawp0.webp",
        "skins/compressed/koawp0small.webp",
        "skins/compressed/koshotgun.webp",
        "skins/compressed/koshotgun0.webp",
        "skins/compressed/koshotgun0small.webp",
        "skins/compressed/kosmg.webp",
        "skins/compressed/kosmg0.webp",
        "skins/compressed/kosmg0small.webp",
        "textures/koarscope.webp",
        "textures/koimpactatlas.png",
        "textures/koblurredsniperscopemobile.webp",
        "textures/kosniperscope.webp",
        "vector.glb",
        "weapons/ar2/ar2-scope-2.glb",
        "maps/training/lightmap0.png",
        "maps/training/map.glb"
    ];
    private static readonly Assembly AppAssembly = Assembly.GetExecutingAssembly();
    private static readonly HttpClient AssetClient = new();
    private static readonly Dictionary<string, Task<byte[]>> AssetCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object AssetCacheLock = new();
    private static readonly Dictionary<string, string> AssetUrls = CreateAssetUrls();
    private static readonly Dictionary<string, string> AssetByName = CreateAssetByName();
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
        Controls.Add(_webView);
        Shown += async (_, _) => await InitializeAsync();
    }

    private static Dictionary<string, string> CreateAssetUrls()
    {
        return AssetPaths.ToDictionary(
            path => path,
            path => $"{AssetBaseUrl}{path}?v=2",
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> CreateAssetByName()
    {
        return AssetPaths.ToDictionary(
            path => path.Split('/')[^1],
            path => path,
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task InitializeAsync()
    {
        var options = new CoreWebView2EnvironmentOptions(BrowserArguments());
        _environment = await CoreWebView2Environment.CreateAsync(null, ProfileDir(), options);
        await _webView.EnsureCoreWebView2Async(_environment);

        ConfigureCore(_webView.CoreWebView2);
        _webView.CoreWebView2.Navigate("https://deadshot.io");
        _ = CheckForUpdateAsync();
    }

    private static async Task CheckForUpdateAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KOClient", CurrentVersion().ToString()));
            using var response = await client.GetAsync(LatestReleaseApi);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync();
            using var release = await JsonDocument.ParseAsync(body);
            var root = release.RootElement;
            var latestVersionText = root.GetProperty("tag_name").GetString();
            if (!TryParseVersion(latestVersionText, out var latestVersion)
                || latestVersion <= CurrentVersion())
            {
                return;
            }
            var installerUrl = FindInstallerUrl(root);
            if (installerUrl is null)
            {
                return;
            }
            var result = MessageBox.Show(
                $"A new version is available: {latestVersion}. Update now?",
                AppTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (result != DialogResult.Yes)
            {
                return;
            }
            var installerPath = await DownloadInstallerAsync(client, installerUrl, latestVersion);
            Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /NORESTART /CLOSEAPPLICATIONS"
            });
            Application.Exit();
        }
        catch
        {
            // ko-client
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
        core.AddWebResourceRequestedFilter("https://deadshot.io/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        foreach (var domain in BlockedDomains)
        {
            core.AddWebResourceRequestedFilter($"*://{domain}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.AddWebResourceRequestedFilter($"*://*.{domain}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        }
        core.WebResourceRequested += OnWebResourceRequested;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DocumentTitleChanged += (_, _) => Text = AppTitle;
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (core.ContainsFullScreenElement)
            {
                EnterFullscreen();
                return;
            }

            ExitFullscreen();
        };
    }
    private void EnterFullscreen()
    {
        if (_isFullscreen)
        {
            return;
        }
        _isFullscreen = true;
        _restoreBounds = Bounds;
        _restoreBorderStyle = FormBorderStyle;
        _restoreWindowState = WindowState;
        _restoreTopMost = TopMost;
        var screenBounds = Screen.FromControl(this).Bounds;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        WindowState = FormWindowState.Normal;
        Bounds = screenBounds;
    }
    private void ExitFullscreen()
    {
        if (!_isFullscreen)
        {
            return;
        }
        _isFullscreen = false;
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
            // "--remote-debugging-port=0",
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows",
            "--disable-features=msSmartScreenProtection",
            "--enable-gpu-rasterization",
            "--ignore-gpu-blocklist",
            "--no-first-run",
            "--disable-sync",
            "--disable-component-update");
    }
    private static Version CurrentVersion()
    {
        return AppAssembly.GetName().Version ?? new Version(0, 0, 0, 0);
    }
    private static bool TryParseVersion(string? rawVersion, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }
        var normalized = rawVersion.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out version!);
    }
    private static string? FindInstallerUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name is null || !name.EndsWith(InstallerAssetSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return asset.GetProperty("browser_download_url").GetString();
        }
        return null;
    }
    private static async Task<string> DownloadInstallerAsync(HttpClient client, string installerUrl, Version version)
    {
        var fileName = $"KOClient-{version}-Setup.exe";
        var installerPath = Path.Combine(Path.GetTempPath(), fileName);
        await using var download = await client.GetStreamAsync(installerUrl);
        await using var file = File.Create(installerPath);
        await download.CopyToAsync(file);
        return installerPath;
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
    public CoreWebView2 Core => _webView.CoreWebView2;
    public async Task InitializeAsync()
    {
        await _webView.EnsureCoreWebView2Async(_environment);
        _webView.CoreWebView2.WindowCloseRequested += (_, _) => Close();
        _webView.CoreWebView2.NavigationCompleted += async (_, _) =>
        {
            if (MainFormIsDeadshot(_webView.Source?.ToString()))
            {
                await Task.Delay(500);
                Close();
            }
        };
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
