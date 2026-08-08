namespace TheRadioVault.Presentation.Infrastructure;

public static class ConnectedViewRefreshPolicy
{
    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromSeconds(30);

    public static bool IsFresh(
        DateTimeOffset loadedAt,
        DateTimeOffset? now = null,
        TimeSpan? maximumAge = null)
    {
        if (loadedAt == DateTimeOffset.MinValue) return false;
        var age = (now ?? DateTimeOffset.UtcNow) - loadedAt;
        return age >= TimeSpan.Zero && age < (maximumAge ?? DefaultMaximumAge);
    }
}
