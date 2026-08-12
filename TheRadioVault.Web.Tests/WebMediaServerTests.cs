using System.Text;
using TheRadioVault.Web.Tests.Fixtures;
using static TheRadioVault.Web.Tests.Fixtures.WebServerFixture;
using static TheRadioVault.Web.Tests.TestAssert;

namespace TheRadioVault.Web.Tests;

internal static class WebMediaServerTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases { get; } =
    [
        ("Positioned web audio is stable across Safari ranges", PositionedWebAudioIsStableAcrossSafariRanges)
    ];

static void PositionedWebAudioIsStableAcrossSafariRanges()
{
    if (!OperatingSystem.IsWindows())
        return;
    var stem = Path.Combine(Path.GetTempPath(), $"radiovault-positioned-{Guid.NewGuid():N}");
    var sourceWavePath = stem + ".wav";
    var path = stem + ".mp3";
    try
    {
        const int sampleRate = 44_100;
        const int seconds = 5;
        var dataLength = sampleRate * seconds * 2;
        using (var file = File.Create(sourceWavePath))
        using (var writer = new BinaryWriter(file, Encoding.ASCII, leaveOpen: false))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            for (var sampleIndex = 0; sampleIndex < sampleRate * seconds; sampleIndex++)
            {
                var elapsed = sampleIndex / (double)sampleRate;
                var frequency = 220d + elapsed * 90d;
                writer.Write((short)(Math.Sin(2d * Math.PI * frequency * elapsed) * short.MaxValue * 0.45d));
            }
        }
        using (var source = new NAudio.Wave.WaveFileReader(sourceWavePath))
            NAudio.Wave.MediaFoundationEncoder.EncodeToMp3(source, path, 96_000);

        WithCustomWebServer(new TestWebArchiveProvider(audioPath: path), async (port, token) =>
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"http://127.0.0.1:{port}/api/v1/broadcasts/9/media-start?positionMs=2000&token={Uri.EscapeDataString(token)}");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 63);
            using var response = await client.SendAsync(request);
            Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode);
            Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Equal(64, bytes.Length);
            Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
            var positionedLength = response.Content.Headers.ContentRange?.Length ?? 0;
            var positionedEtag = response.Headers.ETag?.Tag;
            True(positionedLength > bytes.Length);
            True(!string.IsNullOrWhiteSpace(positionedEtag));

            using var zeroRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"http://127.0.0.1:{port}/api/v1/broadcasts/9/media-start?positionMs=0&positioned=1&token={Uri.EscapeDataString(token)}");
            zeroRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 63);
            using var zeroResponse = await client.SendAsync(zeroRequest);
            Equal(System.Net.HttpStatusCode.PartialContent, zeroResponse.StatusCode);
            Equal("audio/wav", zeroResponse.Content.Headers.ContentType?.MediaType);
            var zeroBytes = await zeroResponse.Content.ReadAsByteArrayAsync();
            Equal("RIFF", Encoding.ASCII.GetString(zeroBytes, 0, 4));
            var zeroLength = zeroResponse.Content.Headers.ContentRange?.Length ?? 0;
            var zeroEtag = zeroResponse.Headers.ETag?.Tag;
            True(zeroLength > positionedLength);
            True(zeroLength - positionedLength > 100_000);
            True(!string.IsNullOrWhiteSpace(zeroEtag));
            True(!string.Equals(positionedEtag, zeroEtag, StringComparison.Ordinal));

            var positionedUrl =
                $"http://127.0.0.1:{port}/api/v1/broadcasts/9/media-start?positionMs=2000&positioned=1&streamSession=range-stability&token={Uri.EscapeDataString(token)}";
            var continuousBytes = await client.GetByteArrayAsync(positionedUrl);
            using var rangedBytes = new MemoryStream(continuousBytes.Length);
            const int safariRangeSize = 32 * 1024;
            for (var start = 0; start < continuousBytes.Length; start += safariRangeSize)
            {
                var end = Math.Min(continuousBytes.Length - 1, start + safariRangeSize - 1);
                using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, positionedUrl);
                rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                using var rangeResponse = await client.SendAsync(rangeRequest);
                Equal(System.Net.HttpStatusCode.PartialContent, rangeResponse.StatusCode);
                var rangeBytes = await rangeResponse.Content.ReadAsByteArrayAsync();
                Equal(end - start + 1, rangeBytes.Length);
                await rangedBytes.WriteAsync(rangeBytes);
            }
            Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(continuousBytes)),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rangedBytes.ToArray())));
        });
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(sourceWavePath)) File.Delete(sourceWavePath);
    }
}
}
