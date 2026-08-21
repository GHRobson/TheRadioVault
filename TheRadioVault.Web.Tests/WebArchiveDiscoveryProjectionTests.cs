using TheRadioVault.Web.Models;
using TheRadioVault.Web.Services;
using static TheRadioVault.Web.Tests.TestAssert;

namespace TheRadioVault.Web.Tests;

internal static class WebArchiveDiscoveryProjectionTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases { get; } =
    [
        ("Archive discovery projects one canonical dashboard", ProjectsCanonicalDashboard),
        ("Archive discovery applies one bounded result policy", AppliesBoundedResultPolicy)
    ];

    private static void ProjectsCanonicalDashboard()
    {
        var today = new DateTime(2026, 8, 14);
        var episodes = new[]
        {
            Episode(1, "Bennington", new DateTime(2018, 8, 14), "InProgress", position: 60, favourite: true),
            Episode(2, "Bennington", new DateTime(2019, 8, 14), "Unplayed"),
            Episode(3, "Ron & Fez", new DateTime(2010, 2, 1), "Completed", position: 100),
            Episode(4, "Ron & Fez", new DateTime(2011, 3, 2), "Unplayed")
        };

        var result = WebArchiveDiscoveryProjection.Build(episodes, 12, today);

        Equal(4, result.Library.BroadcastCount);
        Equal(2, result.Library.ShowCount);
        Equal(1L, result.ContinueListening.Single().Id);
        Equal(1L, result.Favourites.Single().Id);
        Equal("1,2", string.Join(",", result.OnThisDay.Select(value => value.Id).Order()));
        True(result.Unheard.All(value => value.Status.Equals("Unplayed", StringComparison.OrdinalIgnoreCase)));
        Equal("Bennington,Ron & Fez", string.Join(",", result.Shows.Select(value => value.Name)));
    }

    private static void AppliesBoundedResultPolicy()
    {
        var episodes = Enumerable.Range(1, 80)
            .Select(index => Episode(index, "Archive", new DateTime(2000, 1, 1).AddDays(index), "Unplayed"))
            .ToArray();

        var result = WebArchiveDiscoveryProjection.Build(episodes, 500, new DateTime(2026, 8, 14));
        Equal(50, result.Recent.Count);
        Equal(50, result.Unheard.Count);
    }

    private static WebEpisode Episode(
        long id,
        string show,
        DateTime date,
        string status,
        long position = 0,
        bool favourite = false)
        => new(
            id,
            show,
            $"Broadcast {id}",
            date,
            string.Empty,
            string.Empty,
            string.Empty,
            100,
            position,
            status,
            favourite,
            position > 0 ? date.AddHours(1) : null,
            date.AddDays(1),
            $"/{id}.mp3",
            string.Empty);
}
