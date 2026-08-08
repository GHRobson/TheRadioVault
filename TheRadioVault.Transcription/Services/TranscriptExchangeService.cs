using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TheRadioVault.Core.Services;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

public sealed class TranscriptExchangeService : ITranscriptExchangeService
{
    public const string FileExtension = ".trvtranscript";
    private const string PackageEntryName = "transcript.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly ITranscriptRepository _repository;

    public TranscriptExchangeService(ITranscriptRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task ExportAsync(long episodeId, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("A destination path is required.", nameof(destinationPath));
        var transcript = await _repository.GetForEpisodeAsync(episodeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This broadcast does not have a transcript to export.");
        var identity = await _repository.GetEpisodeIdentityAsync(episodeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The broadcast could not be found.");

        var portableTranscript = CopyTranscript(transcript, TranscriptMetadataSanitizer.CreatePortableMetadata(transcript.MetadataJson));
        var package = new TranscriptPackage { Episode = identity, Transcript = portableTranscript };
        ValidatePackage(package);

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var tempPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var entry = archive.CreateEntry(PackageEntryName, CompressionLevel.SmallestSize);
                await using var entryStream = entry.Open();
                await JsonSerializer.SerializeAsync(entryStream, package, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            File.Move(tempPath, destinationPath, true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public async Task<TranscriptImportResult> ImportAsync(long episodeId, string sourcePath, bool replaceExisting, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("The transcript package could not be found.", sourcePath);

        var checksum = await ComputeChecksumAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var package = await ReadPackageAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        ValidatePackage(package);
        var targetIdentity = await _repository.GetEpisodeIdentityAsync(episodeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The destination broadcast could not be found.");
        ValidateEpisodeMatch(package.Episode, targetIdentity);

        var existing = await _repository.GetForEpisodeAsync(episodeId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !replaceExisting)
            throw new InvalidOperationException("This broadcast already has a transcript. Choose replace to import over it.");

        var imported = CopyTranscript(package.Transcript, TranscriptMetadataSanitizer.CreatePortableMetadata(package.Transcript.MetadataJson), episodeId, "import");
        var saved = await _repository.SaveAsync(imported, cancellationToken).ConfigureAwait(false);
        await _repository.RecordImportAsync(episodeId, package.PackageId, Path.GetFullPath(sourcePath), checksum, existing?.Revision ?? 0, cancellationToken).ConfigureAwait(false);

        return new TranscriptImportResult(episodeId, saved.Id, saved.Revision, saved.Segments.Count, saved.WordCount, existing is not null, package.PackageId);
    }

    private static async Task<TranscriptPackage> ReadPackageAsync(string sourcePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var signature = new byte[4];
        var read = await stream.ReadAsync(signature.AsMemory(0, signature.Length), cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        var isZip = read >= 2 && signature[0] == (byte)'P' && signature[1] == (byte)'K';
        if (!isZip)
            return await JsonSerializer.DeserializeAsync<TranscriptPackage>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The transcript package is empty or invalid.");

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entry = archive.GetEntry(PackageEntryName)
            ?? throw new InvalidDataException("The compressed transcript package has no transcript.json entry.");
        if (entry.Length > 512L * 1024 * 1024)
            throw new InvalidDataException("The transcript package is unreasonably large.");
        await using var entryStream = entry.Open();
        return await JsonSerializer.DeserializeAsync<TranscriptPackage>(entryStream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The transcript package is empty or invalid.");
    }

    private static async Task<string> ComputeChecksumAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static TranscriptDocument CopyTranscript(TranscriptDocument source, string metadata, long? episodeId = null, string? sourceName = null) => new()
    {
        EpisodeId = episodeId ?? source.EpisodeId,
        Status = source.Status,
        Language = source.Language,
        EngineId = source.EngineId,
        EngineVersion = source.EngineVersion,
        ModelId = source.ModelId,
        Source = sourceName ?? source.Source,
        FullText = source.FullText,
        WordCount = source.WordCount,
        DurationMs = source.DurationMs,
        HasWordTimings = source.HasWordTimings,
        HasSpeakerDiarization = source.HasSpeakerDiarization,
        CreatedAt = source.CreatedAt,
        CompletedAt = source.CompletedAt,
        MetadataJson = metadata,
        Segments = source.Segments,
        Speakers = source.Speakers ?? Array.Empty<TranscriptSpeakerCluster>()
    };

    public static void ValidatePackage(TranscriptPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.FormatVersion is < 1 or > TranscriptPackage.CurrentFormatVersion)
            throw new InvalidDataException($"Transcript package format {package.FormatVersion} is not supported.");
        if (string.IsNullOrWhiteSpace(package.PackageId) || package.PackageId.Length > 200)
            throw new InvalidDataException("The transcript package has no valid package ID.");
        if (package.Episode is null) throw new InvalidDataException("The transcript package has no broadcast identity.");
        if (package.Transcript is null) throw new InvalidDataException("The transcript package has no transcript.");
        if (package.Transcript.Segments is null) throw new InvalidDataException("The transcript package has no segment collection.");
        if (string.IsNullOrWhiteSpace(package.Transcript.FullText) && package.Transcript.Segments.Count == 0)
            throw new InvalidDataException("The transcript package contains no transcript text.");

        var speakerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var speaker in package.Transcript.Speakers ?? Array.Empty<TranscriptSpeakerCluster>())
        {
            if (string.IsNullOrWhiteSpace(speaker.SpeakerKey)) throw new InvalidDataException("A transcript speaker has no stable speaker key.");
            if (!speakerKeys.Add(speaker.SpeakerKey.Trim())) throw new InvalidDataException($"Speaker key '{speaker.SpeakerKey}' occurs more than once.");
            if (speaker.AssignmentConfidence is < 0 or > 1) throw new InvalidDataException($"Speaker '{speaker.SpeakerKey}' has an invalid assignment confidence.");
            if (speaker.AssignmentState is SpeakerAssignmentState.Confirmed or SpeakerAssignmentState.Suggested && string.IsNullOrWhiteSpace(speaker.PersonName))
                throw new InvalidDataException($"Speaker '{speaker.SpeakerKey}' is assigned without a person name.");
        }

        long previousEnd = 0;
        var expectedIndex = 0;
        foreach (var segment in package.Transcript.Segments.OrderBy(x => x.Index))
        {
            if (segment.Index != expectedIndex) throw new InvalidDataException("Transcript segment indexes must be contiguous and start at zero.");
            if (segment.StartMs < 0 || segment.EndMs < segment.StartMs) throw new InvalidDataException($"Transcript segment {segment.Index} has invalid timestamps.");
            if (segment.StartMs < previousEnd) throw new InvalidDataException($"Transcript segment {segment.Index} overlaps the previous segment.");
            if (string.IsNullOrWhiteSpace(segment.Text)) throw new InvalidDataException($"Transcript segment {segment.Index} has no text.");
            if (segment.Confidence is < 0 or > 1) throw new InvalidDataException($"Transcript segment {segment.Index} has an invalid confidence value.");
            long previousWordEnd = segment.StartMs;
            foreach (var word in segment.Words ?? Array.Empty<TranscriptWord>())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) throw new InvalidDataException($"Transcript segment {segment.Index} contains an empty timed word.");
                if (word.StartMs < segment.StartMs || word.EndMs < word.StartMs || word.EndMs > segment.EndMs) throw new InvalidDataException($"Transcript segment {segment.Index} contains a word outside its time range.");
                if (word.StartMs < previousWordEnd) throw new InvalidDataException($"Transcript segment {segment.Index} contains overlapping word timings.");
                if (word.Confidence is < 0 or > 1) throw new InvalidDataException($"Transcript segment {segment.Index} contains an invalid word confidence value.");
                previousWordEnd = word.EndMs;
            }
            previousEnd = segment.EndMs;
            expectedIndex++;
        }
    }

    private static void ValidateEpisodeMatch(TranscriptEpisodeIdentity packageEpisode, TranscriptEpisodeIdentity targetEpisode)
    {
        ArgumentNullException.ThrowIfNull(packageEpisode);
        ArgumentNullException.ThrowIfNull(targetEpisode);
        var packageUid = (packageEpisode.BroadcastUid ?? "").Trim();
        var targetUid = (targetEpisode.BroadcastUid ?? "").Trim();
        if (IsPortableBroadcastUid(packageUid) && IsPortableBroadcastUid(targetUid))
        {
            if (!string.Equals(packageUid, targetUid, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("This transcript package belongs to a different broadcast.");
            return;
        }
        var packageShow = MetadataNormalizer.NormalizeCollection(packageEpisode.Show) ?? (packageEpisode.Show ?? "").Trim();
        var targetShow = MetadataNormalizer.NormalizeCollection(targetEpisode.Show) ?? (targetEpisode.Show ?? "").Trim();
        var sameShow = string.Equals(packageShow, targetShow, StringComparison.OrdinalIgnoreCase);
        var samePart = Math.Max(1, packageEpisode.PartNumber) == Math.Max(1, targetEpisode.PartNumber);
        var sameBroadcastKey = packageEpisode.AirDate.HasValue && targetEpisode.AirDate.HasValue
            ? packageEpisode.AirDate.Value.Date == targetEpisode.AirDate.Value.Date
            : packageEpisode.AirDate is null && targetEpisode.AirDate is null && string.Equals((packageEpisode.Title ?? "").Trim(), (targetEpisode.Title ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        if (!sameShow || !samePart || !sameBroadcastKey) throw new InvalidDataException("This transcript package does not match the selected show, date and part.");
    }

    private static bool IsPortableBroadcastUid(string value) => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("BROADCAST-", StringComparison.OrdinalIgnoreCase);
}
