using System.Collections.Concurrent;
using AVFoundation;
using Foundation;
using TheRadioVault.Client.Mobile.Platform;

namespace TheRadioVault.Client.iOS;

internal sealed class IosMediaResourceLoader : AVAssetResourceLoaderDelegate
{
    private static readonly NSString ErrorDomain = new("com.ghrobson.theradiovault.streaming");
    private readonly MobilePlaybackSource _source;
    private readonly ConcurrentDictionary<nint, CancellationTokenSource> _requests = new();
    private bool _disposed;

    public IosMediaResourceLoader(MobilePlaybackSource source) => _source = source;

    public override bool ShouldWaitForLoadingOfRequestedResource(
        AVAssetResourceLoader resourceLoader,
        AVAssetResourceLoadingRequest loadingRequest)
    {
        if (_disposed) return false;
        var cancellation = new CancellationTokenSource();
        var key = (nint)loadingRequest.Handle;
        if (!_requests.TryAdd(key, cancellation))
        {
            cancellation.Dispose();
            return false;
        }
        _ = ServeAsync(key, loadingRequest, cancellation);
        return true;
    }

    public override void DidCancelLoadingRequest(
        AVAssetResourceLoader resourceLoader,
        AVAssetResourceLoadingRequest loadingRequest)
    {
        if (_requests.TryRemove((nint)loadingRequest.Handle, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private async Task ServeAsync(
        nint key,
        AVAssetResourceLoadingRequest loadingRequest,
        CancellationTokenSource cancellation)
    {
        try
        {
            var dataRequest = loadingRequest.DataRequest;
            var offset = dataRequest is null
                ? 0L
                : Math.Max(dataRequest.RequestedOffset, dataRequest.CurrentOffset);
            var range = dataRequest is null
                ? "bytes=0-0"
                : dataRequest.RequestsAllDataToEndOfResource
                    ? $"bytes={offset}-"
                    : $"bytes={offset}-{offset + Math.Max(1, dataRequest.RequestedLength) - 1}";

            using var response = await _source.OpenResponseAsync(range, cancellation.Token).ConfigureAwait(false);
            PopulateContentInformation(loadingRequest.ContentInformationRequest, response);

            if (dataRequest is not null)
            {
                await using var input = await response.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
                var buffer = new byte[128 * 1024];
                long remaining = dataRequest.RequestsAllDataToEndOfResource
                    ? long.MaxValue
                    : Math.Max(0, dataRequest.RequestedLength);
                while (remaining > 0 && !cancellation.IsCancellationRequested && !loadingRequest.IsCancelled)
                {
                    var requested = (int)Math.Min(buffer.Length, remaining);
                    var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellation.Token).ConfigureAwait(false);
                    if (read <= 0) break;
                    using var data = NSData.FromArray(buffer.AsSpan(0, read).ToArray());
                    dataRequest.Respond(data);
                    if (remaining != long.MaxValue) remaining -= read;
                }
            }

            if (!cancellation.IsCancellationRequested && !loadingRequest.IsCancelled)
                loadingRequest.FinishLoading();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || loadingRequest.IsCancelled) { }
        catch (Exception exception)
        {
            if (!loadingRequest.IsCancelled)
            {
                using var description = new NSString(exception.Message);
                using var descriptionKey = new NSString("NSLocalizedDescription");
                using var details = NSDictionary.FromObjectAndKey(description, descriptionKey);
                using var error = NSError.FromDomain(ErrorDomain, -1, details);
                loadingRequest.FinishLoadingWithError(error);
            }
        }
        finally
        {
            if (_requests.TryRemove(key, out var active)) active.Dispose();
        }
    }

    private static void PopulateContentInformation(
        AVAssetResourceLoadingContentInformationRequest? information,
        HttpResponseMessage response)
    {
        if (information is null) return;
        information.ByteRangeAccessSupported = true;
        var totalLength = response.Content.Headers.ContentRange?.Length ??
                          response.Content.Headers.ContentLength;
        if (totalLength is > 0) information.ContentLength = totalLength.Value;
        information.ContentType = ContentTypeFor(response.Content.Headers.ContentType?.MediaType);
    }

    private static string ContentTypeFor(string? mimeType) => mimeType?.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => "public.mp3",
        "audio/mp4" or "audio/x-m4a" or "audio/m4a" => "public.mpeg-4-audio",
        "audio/aac" or "audio/aacp" => "public.aac-audio",
        "audio/wav" or "audio/x-wav" => "com.microsoft.waveform-audio",
        "audio/flac" or "audio/x-flac" => "org.xiph.flac",
        "audio/ogg" => "org.xiph.ogg-audio",
        _ => "public.audio"
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            foreach (var request in _requests.Values) request.Cancel();
            foreach (var request in _requests.Values) request.Dispose();
            _requests.Clear();
        }
        base.Dispose(disposing);
    }
}
