using TagLib;
using TheRadioVault.Media.Contracts;
using TheRadioVault.Media.Models;

namespace TheRadioVault.Media.Services;

public sealed class TagLibAudioMetadataService : IAudioMetadataReader, IAudioMetadataWriter
{
    public AudioMetadata Read(string path)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        var picture = tag.Pictures?.FirstOrDefault();
        return new AudioMetadata(
            Clean(tag.Title),
            Clean(tag.Comment),
            CleanMany(tag.Performers),
            CleanMany(tag.Genres),
            Math.Max(0, (long)file.Properties.Duration.TotalMilliseconds),
            picture?.Data?.Data,
            picture?.MimeType,
            tag.Year,
            Clean(tag.Album),
            CleanMany(tag.AlbumArtists));
    }

    public void Write(MediaWriteRequest request)
    {
        using var file = TagLib.File.Create(request.Path);
        file.Tag.Title = request.Title;
        file.Tag.Album = request.Album;
        file.Tag.AlbumArtists = request.AlbumArtists.Where(NotBlank).ToArray();
        file.Tag.Performers = request.Performers.Where(NotBlank).ToArray();
        file.Tag.Genres = request.Genres.Where(NotBlank).ToArray();
        file.Tag.Year = request.Year ?? 0;
        file.Tag.Comment = request.Comment;

        if (!string.IsNullOrWhiteSpace(request.ArtworkPath) && System.IO.File.Exists(request.ArtworkPath))
        {
            var picture = new Picture(request.ArtworkPath);
            file.Tag.Pictures = new IPicture[] { picture };
        }

        file.Save();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool NotBlank(string value) => !string.IsNullOrWhiteSpace(value);
    private static string[] CleanMany(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
        .Where(NotBlank)
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
