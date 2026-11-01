using System.IO;
using System.Security.Cryptography;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Desktop;
using S2F.RaceDayCheckout.Core.Models;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Services;

/// <summary>
/// MSAL public-client auth for the shared M365 Business OneDrive race account
/// (<c>timing@start2finish.onmicrosoft.com</c>) — Graph <c>/me/drive</c> only.
/// </summary>
public sealed class GraphAuthService : IGraphTokenProvider
{
    private static readonly string[] Scopes = ["Files.ReadWrite", "User.Read", "offline_access"];

    /// <summary>Legacy personal-MSA cache — ignored after the Business OneDrive cutover.</summary>
    private const string LegacyPersonalCacheFileName = "token_cache.bin";

    /// <summary>Work-account token cache (new file so personal MSA tokens cannot stick).</summary>
    private const string OrgCacheFileName = "token_cache_m365.bin";

    private readonly string _clientId;
    private readonly string _tenantId;
    private readonly string _cachePath;
    private readonly string _cacheDir;
    private IPublicClientApplication? _app;
    private IAccount? _account;
    private bool _cacheRegistered;
    /// <summary>In-memory token for parallel Graph bursts (avoid N× AcquireTokenSilent).</summary>
    private string? _memoryToken;
    private DateTimeOffset _memoryTokenExpires = DateTimeOffset.MinValue;

    public GraphAuthService(string? clientId = null, string? tenantId = null)
    {
        _clientId = string.IsNullOrWhiteSpace(clientId)
            ? AppSettings.DefaultGraphClientId
            : clientId.Trim();
        _tenantId = string.IsNullOrWhiteSpace(tenantId)
            ? AppSettings.DefaultGraphTenantId
            : tenantId.Trim();
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "S2FRaceDayCheckout",
            "msal");
        Directory.CreateDirectory(_cacheDir);
        _cachePath = Path.Combine(_cacheDir, OrgCacheFileName);
        TryDeleteLegacyPersonalCache();
    }

    public bool IsSignedIn
    {
        get
        {
            EnsureApp();
            return _account is not null;
        }
    }

    public string? AccountEmail
    {
        get
        {
            EnsureApp();
            return _account?.Username;
        }
    }

    public async Task<bool> SignInAsync(IntPtr? parentWindow = null, CancellationToken cancellationToken = default)
    {
        var app = EnsureApp();
        try
        {
            // Embedded WebView2 only — closes with the dialog; no leftover Edge tab / no system-browser options.
            var builder = app.AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(true);
            if (parentWindow is { } hwnd && hwnd != IntPtr.Zero)
                builder = builder.WithParentActivityOrWindow(hwnd);

            var result = await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            _account = result.Account;
            _memoryToken = result.AccessToken;
            _memoryTokenExpires = result.ExpiresOn;
            return true;
        }
        catch (MsalClientException ex) when (ex.ErrorCode == "authentication_canceled")
        {
            return false;
        }
    }

    public async Task SignOutAsync()
    {
        var app = EnsureApp();
        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
        foreach (var account in accounts)
            await app.RemoveAsync(account).ConfigureAwait(false);
        _account = null;
        _memoryToken = null;
        _memoryTokenExpires = DateTimeOffset.MinValue;
        try
        {
            if (File.Exists(_cachePath))
                File.Delete(_cachePath);
        }
        catch
        {
            // ignore
        }
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // Parallel download/upload: reuse token until near expiry (MSAL silent is otherwise per-request).
        if (!string.IsNullOrEmpty(_memoryToken)
            && DateTimeOffset.UtcNow < _memoryTokenExpires - TimeSpan.FromMinutes(2))
            return _memoryToken;

        var app = EnsureApp();
        if (_account is null)
        {
            _account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
            if (_account is null)
                return null;
        }

        try
        {
            var silent = await app.AcquireTokenSilent(Scopes, _account)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            _account = silent.Account;
            _memoryToken = silent.AccessToken;
            _memoryTokenExpires = silent.ExpiresOn;
            return _memoryToken;
        }
        catch (MsalUiRequiredException)
        {
            _memoryToken = null;
            _memoryTokenExpires = DateTimeOffset.MinValue;
            return null;
        }
    }

    private IPublicClientApplication EnsureApp()
    {
        if (_app is not null)
            return _app;

        _app = PublicClientApplicationBuilder
            .Create(_clientId)
            .WithAuthority($"https://login.microsoftonline.com/{_tenantId}")
            .WithRedirectUri("http://localhost")
            .WithClientName("S2F Race Day Timing")
            .WithWindowsEmbeddedBrowserSupport()
            .Build();

        if (!_cacheRegistered)
        {
            RegisterDpapiCache(_app);
            _cacheRegistered = true;
            try
            {
                _account = _app.GetAccountsAsync().GetAwaiter().GetResult().FirstOrDefault();
            }
            catch
            {
                _account = null;
            }
        }

        return _app;
    }

    private void TryDeleteLegacyPersonalCache()
    {
        try
        {
            var legacy = Path.Combine(_cacheDir, LegacyPersonalCacheFileName);
            if (File.Exists(legacy))
                File.Delete(legacy);
        }
        catch
        {
            // Non-fatal — org cache file is what MSAL reads now.
        }
    }

    private void RegisterDpapiCache(IPublicClientApplication app)
    {
        app.UserTokenCache.SetBeforeAccess(args =>
        {
            if (!File.Exists(_cachePath))
                return;
            try
            {
                var protectedBytes = File.ReadAllBytes(_cachePath);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                args.TokenCache.DeserializeMsalV3(bytes);
            }
            catch
            {
                // Corrupt cache — start fresh.
            }
        });
        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged)
                return;
            try
            {
                var bytes = args.TokenCache.SerializeMsalV3();
                var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_cachePath, protectedBytes);
            }
            catch
            {
                // Non-fatal — next launch may require sign-in again.
            }
        });
    }
}
