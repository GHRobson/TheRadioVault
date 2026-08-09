using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

internal static class RadioVaultArtwork
{
    private const int MaximumCachedImages = 512;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<long, UIImage> Images = [];
    private static readonly Queue<long> ImageOrder = [];
    private static readonly Dictionary<long, Task<UIImage?>> Loads = [];

    public static void Load(
        UIImageView target,
        MobileClientSession session,
        MobileBroadcastItem broadcast,
        UIImage? placeholder = null)
    {
        Prepare(target, broadcast.EpisodeId, placeholder);
        if (string.IsNullOrWhiteSpace(broadcast.Source.ArtworkPath)) return;
        _ = LoadAsync(target, broadcast.EpisodeId, () => session.LoadArtworkAsync(broadcast));
    }

    public static void Load(
        UIImageView target,
        MobileClientSession session,
        long episodeId,
        UIImage? placeholder = null)
    {
        Prepare(target, episodeId, placeholder);
        _ = LoadAsync(target, episodeId, () => session.LoadArtworkAsync(episodeId));
    }

    private static void Prepare(UIImageView target, long episodeId, UIImage? placeholder)
    {
        var sameEpisode = target.Tag == (nint)episodeId;
        target.Tag = (nint)episodeId;
        if (TryGetCached(episodeId, out var cached))
        {
            target.Image = cached;
            target.ContentMode = UIViewContentMode.ScaleAspectFill;
        }
        else if (!sameEpisode || target.Image is null)
        {
            target.Image = placeholder ?? RadioVaultIcons.Image(RadioVaultIcon.Radio, size: 42);
            target.ContentMode = UIViewContentMode.Center;
        }
        target.ClipsToBounds = true;
    }

    private static async Task LoadAsync(
        UIImageView target,
        long episodeId,
        Func<Task<byte[]?>> contentFactory)
    {
        var image = await GetOrLoadAsync(episodeId, contentFactory).ConfigureAwait(false);
        if (image is null) return;
        target.BeginInvokeOnMainThread(() =>
        {
            if (target.Tag != (nint)episodeId) return;
            target.ContentMode = UIViewContentMode.ScaleAspectFill;
            target.Image = image;
        });
    }

    private static Task<UIImage?> GetOrLoadAsync(long episodeId, Func<Task<byte[]?>> contentFactory)
    {
        lock (CacheGate)
        {
            if (Images.TryGetValue(episodeId, out var cached))
                return Task.FromResult<UIImage?>(cached);
            if (Loads.TryGetValue(episodeId, out var pending)) return pending;
            var load = DecodeAsync(episodeId, contentFactory);
            Loads[episodeId] = load;
            return load;
        }
    }

    private static async Task<UIImage?> DecodeAsync(long episodeId, Func<Task<byte[]?>> contentFactory)
    {
        UIImage? image = null;
        try
        {
            await Task.Yield();
            var content = await contentFactory().ConfigureAwait(false);
            if (content is not { Length: > 0 }) return null;
            using var data = NSData.FromArray(content);
            image = UIImage.LoadFromData(data);
            if (image is null) return null;
            lock (CacheGate)
            {
                if (!Images.ContainsKey(episodeId)) ImageOrder.Enqueue(episodeId);
                Images[episodeId] = image;
                while (Images.Count > MaximumCachedImages && ImageOrder.TryDequeue(out var oldest))
                    Images.Remove(oldest);
            }
            return image;
        }
        finally
        {
            lock (CacheGate) Loads.Remove(episodeId);
        }
    }

    private static bool TryGetCached(long episodeId, out UIImage? image)
    {
        lock (CacheGate) return Images.TryGetValue(episodeId, out image);
    }
}
