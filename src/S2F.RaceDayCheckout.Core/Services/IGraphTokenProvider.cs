namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>Supplies Microsoft Graph access tokens (MSAL lives in the WPF host).</summary>
public interface IGraphTokenProvider
{
    bool IsSignedIn { get; }
    string? AccountEmail { get; }

    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    Task<bool> SignInAsync(IntPtr? parentWindow = null, CancellationToken cancellationToken = default);
    Task SignOutAsync();
}

/// <summary>No Graph auth — local Library/Active only (tests / offline stubs). Not OneDrive Desktop.</summary>
public sealed class NullGraphTokenProvider : IGraphTokenProvider
{
    public bool IsSignedIn => false;
    public string? AccountEmail => null;

    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<bool> SignInAsync(IntPtr? parentWindow = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task SignOutAsync() => Task.CompletedTask;
}
