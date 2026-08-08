namespace TheRadioVault.Core.Services;

public sealed record ArchiveHealthPresentation(
    string Headline,
    string Detail,
    string Glyph,
    bool NeedsAttention);

public static class ArchiveHealthPresentationPolicy
{
    public static ArchiveHealthPresentation Create(
        int criticalIssues,
        int warningIssues,
        int unavailableFiles,
        int researchDecisions)
    {
        criticalIssues = Math.Max(0, criticalIssues);
        warningIssues = Math.Max(0, warningIssues);
        unavailableFiles = Math.Max(0, unavailableFiles);
        researchDecisions = Math.Max(0, researchDecisions);

        if (criticalIssues > 0 || unavailableFiles > 0)
        {
            var parts = new List<string>();
            if (unavailableFiles > 0) parts.Add($"{unavailableFiles:N0} unavailable file{(unavailableFiles == 1 ? "" : "s")}");
            if (criticalIssues > 0) parts.Add($"{criticalIssues:N0} critical issue{(criticalIssues == 1 ? "" : "s")}");
            return new ArchiveHealthPresentation(
                "Archive needs attention",
                string.Join(" · ", parts),
                "!",
                true);
        }

        if (warningIssues > 0)
        {
            return new ArchiveHealthPresentation(
                "Archive has warnings",
                $"{warningIssues:N0} warning{(warningIssues == 1 ? "" : "s")} can be reviewed without changing any audio files.",
                "!",
                true);
        }

        if (researchDecisions > 0)
        {
            return new ArchiveHealthPresentation(
                "Archive healthy",
                $"Your recordings are available. {researchDecisions:N0} research decision{(researchDecisions == 1 ? "" : "s")} await your attention.",
                "✓",
                false);
        }

        return new ArchiveHealthPresentation(
            "Archive healthy",
            "No storage, preservation or research problems need your attention.",
            "✓",
            false);
    }

    public static string FormatBackupAge(DateTimeOffset? latestBackupAt, DateTimeOffset now)
    {
        if (!latestBackupAt.HasValue) return "No local backup found";
        var age = now - latestBackupAt.Value;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age < TimeSpan.FromHours(24)) return "Today";
        var days = Math.Max(1, (int)Math.Floor(age.TotalDays));
        if (days == 1) return "Yesterday";
        return days > 30 ? $"Overdue · {days:N0} days ago" : $"{days:N0} days ago";
    }
}
