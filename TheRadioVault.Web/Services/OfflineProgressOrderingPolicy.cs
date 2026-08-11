namespace TheRadioVault.Web.Services;

/// <summary>
/// Orders durable progress by when listening actually happened, rather than by
/// when a temporarily offline client eventually reconnects and uploads it.
/// </summary>
public static class OfflineProgressOrderingPolicy
{
    private static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(5);

    public static bool IsStale(
        DateTimeOffset? capturedAt,
        DateTime? canonicalLastPlayedAt,
        DateTimeOffset receivedAt)
    {
        if (capturedAt is not { } captured) return false;
        captured = captured.ToUniversalTime();
        if (captured > receivedAt.ToUniversalTime() + MaximumFutureClockSkew) return true;
        if (canonicalLastPlayedAt is not { } canonical) return false;
        return captured <= canonical.ToUniversalTime();
    }

    public static DateTimeOffset EffectivePlayedAt(DateTimeOffset? capturedAt, DateTimeOffset receivedAt)
    {
        if (capturedAt is not { } captured) return receivedAt.ToUniversalTime();
        captured = captured.ToUniversalTime();
        return captured > receivedAt.ToUniversalTime() + MaximumFutureClockSkew
            ? receivedAt.ToUniversalTime()
            : captured;
    }
}
