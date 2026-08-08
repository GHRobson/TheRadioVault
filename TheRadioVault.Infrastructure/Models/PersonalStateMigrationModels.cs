using System;
using System.Collections.Generic;
using System.Linq;

namespace TheRadioVault.Models;

public sealed class PersonalStatePackManifest
{
    public int SchemaVersion { get; set; }
    public string PackageType { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string SourceMachineName { get; set; } = "";
    public string StateSha256 { get; set; } = "";
    public int BroadcastStateCount { get; set; }
    public int MomentCount { get; set; }
    public int QueueCount { get; set; }
}

public sealed class PersonalStatePack
{
    public List<PersonalStateBroadcastRecord> Broadcasts { get; set; } = new();
    public List<PersonalStateMomentRecord> Moments { get; set; } = new();
    public List<PersonalStateQueueRecord> Queue { get; set; } = new();
}

public sealed class PersonalStateBroadcastIdentity
{
    public string CanonicalKey { get; set; } = "";
    public string CollectionName { get; set; } = "";
    public string? AirDate { get; set; }
    public string BroadcastSlot { get; set; } = "";
    public string BroadcastUid { get; set; } = "";
    public string Headline { get; set; } = "";

    public string Display => string.Join(" · ", new[]
    {
        CollectionName,
        string.IsNullOrWhiteSpace(AirDate) ? "Unknown date" : AirDate,
        BroadcastSlot
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class PersonalStateBroadcastRecord
{
    public PersonalStateBroadcastIdentity Identity { get; set; } = new();
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public bool Completed { get; set; }
    public bool Favourite { get; set; }
    public double PlaybackSpeed { get; set; } = 1d;
    public DateTimeOffset? FirstPlayedAtUtc { get; set; }
    public DateTimeOffset? LastPlayedAtUtc { get; set; }
    public int PlayCount { get; set; }
    public int CompletionCount { get; set; }
}

public sealed class PersonalStateMomentRecord
{
    public PersonalStateBroadcastIdentity Identity { get; set; } = new();
    public long PositionMs { get; set; }
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class PersonalStateQueueRecord
{
    public PersonalStateBroadcastIdentity Identity { get; set; } = new();
    public int Position { get; set; }
}

public sealed class PersonalStateImportPreview
{
    public string SourcePath { get; set; } = "";
    public PersonalStatePackManifest Manifest { get; set; } = new();
    public int MatchedBroadcasts { get; set; }
    public int UnmatchedBroadcasts { get; set; }
    public int ProgressUpdates { get; set; }
    public int PreservedNewerDesktopProgress { get; set; }
    public int FavouriteAdditions { get; set; }
    public int CompletionAdditions { get; set; }
    public int MomentAdditions { get; set; }
    public int DuplicateMoments { get; set; }
    public int QueueAdditions { get; set; }
    public int DuplicateQueueItems { get; set; }
    public List<string> UnmatchedItems { get; set; } = new();
    public List<string> ProtectedItems { get; set; } = new();
    public bool CanApply => ProgressUpdates + FavouriteAdditions + CompletionAdditions + MomentAdditions + QueueAdditions > 0;
}

public sealed class PersonalStateImportResult
{
    public string BackupPath { get; set; } = "";
    public string ReportPath { get; set; } = "";
    public int PlaybackRecordsUpdated { get; set; }
    public int FavouritesAdded { get; set; }
    public int CompletionsAdded { get; set; }
    public int MomentsAdded { get; set; }
    public int QueueItemsAdded { get; set; }
    public int UnmatchedItems { get; set; }
    public int ProtectedItems { get; set; }
}
