namespace TheRadioVault.Models;

public enum ExploreFacetKind
{
    Show,
    Year,
    Person,
    Topic,
    Station,
    Source
}

public sealed class ExploreFacetItem
{
    public ExploreFacetKind Kind { get; set; }
    public string Value { get; set; } = string.Empty;
    public int Count { get; set; }
    public string Detail { get; set; } = string.Empty;
    public string SearchText { get; set; } = string.Empty;
    public string KindDisplay => Kind.ToString();
    public string CountDisplay => Count.ToString("N0");
}

public sealed class ExploreSnapshot
{
    public IReadOnlyList<ExploreFacetItem> Shows { get; set; } = Array.Empty<ExploreFacetItem>();
    public IReadOnlyList<ExploreFacetItem> Years { get; set; } = Array.Empty<ExploreFacetItem>();
    public IReadOnlyList<ExploreFacetItem> People { get; set; } = Array.Empty<ExploreFacetItem>();
    public IReadOnlyList<ExploreFacetItem> Topics { get; set; } = Array.Empty<ExploreFacetItem>();
    public IReadOnlyList<ExploreFacetItem> Stations { get; set; } = Array.Empty<ExploreFacetItem>();
    public IReadOnlyList<ExploreFacetItem> Sources { get; set; } = Array.Empty<ExploreFacetItem>();
}
