namespace TheRadioVault.Core.Domain;

public sealed record BroadcastIdentity(
    string CollectionName,
    DateOnly? AirDate,
    int PartNumber = 1)
{
    public string StableId => Services.BroadcastIdentityService.CreateStableId(CollectionName, AirDate, PartNumber);
}
