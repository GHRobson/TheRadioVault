using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

internal static class RadioVaultArtwork
{
    public static void Load(
        UIImageView target,
        MobileClientSession session,
        MobileBroadcastItem broadcast,
        UIImage? placeholder = null)
    {
        Prepare(target, broadcast.EpisodeId, placeholder);
        if (string.IsNullOrWhiteSpace(broadcast.Source.ArtworkPath)) return;
        _ = LoadAsync(target, broadcast.EpisodeId, session.LoadArtworkAsync(broadcast));
    }

    public static void Load(
        UIImageView target,
        MobileClientSession session,
        long episodeId,
        UIImage? placeholder = null)
    {
        Prepare(target, episodeId, placeholder);
        _ = LoadAsync(target, episodeId, session.LoadArtworkAsync(episodeId));
    }

    private static void Prepare(UIImageView target, long episodeId, UIImage? placeholder)
    {
        target.Tag = (nint)episodeId;
        target.Image = placeholder ?? RadioVaultIcons.Image(RadioVaultIcon.Radio, size: 42);
        target.ContentMode = UIViewContentMode.Center;
        target.ClipsToBounds = true;
    }

    private static async Task LoadAsync(UIImageView target, long episodeId, Task<byte[]?> contentTask)
    {
        var content = await contentTask.ConfigureAwait(false);
        if (content is not { Length: > 0 }) return;
        using var data = NSData.FromArray(content);
        var image = UIImage.LoadFromData(data);
        if (image is null) return;
        target.BeginInvokeOnMainThread(() =>
        {
            if (target.Tag != (nint)episodeId)
            {
                image.Dispose();
                return;
            }
            target.ContentMode = UIViewContentMode.ScaleAspectFill;
            target.Image = image;
        });
    }
}
