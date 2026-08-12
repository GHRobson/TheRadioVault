using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Active model download renews its inactivity deadline", ActiveDownloadRenewsInactivityDeadlineAsync),
    ("Stalled response headers time out", StalledResponseHeadersTimeOutAsync),
    ("Stalled model download times out and removes its partial file", StalledDownloadTimesOutAndCleansUpAsync),
    ("Caller cancellation remains cancellation and removes its partial file", CallerCancellationRemainsCancellationAsync),
    ("Timed-out model download can be retried", TimedOutDownloadCanBeRetriedAsync),
    ("In-app setup installs official transcription assets safely", InAppSetupInstallsOfficialAssetsSafelyAsync)
};

var selectedTests = args.Length == 0
    ? tests
    : tests.Where(test => args.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
if (selectedTests.Length == 0)
{
    Console.Error.WriteLine("No transcription tests matched the supplied filters.");
    return 2;
}

var failures = new List<string>();
foreach (var test in selectedTests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{selectedTests.Length - failures.Count}/{selectedTests.Length} transcription tests passed.");
return failures.Count == 0 ? 0 : 1;

static async Task ActiveDownloadRenewsInactivityDeadlineAsync()
{
    var root = CreateTemporaryDirectory("active");
    try
    {
        var payload = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray();
        using var handler = new DelegateHandler(_ => Response(
            new StreamContent(new PacedStream(payload, TimeSpan.FromMilliseconds(40), chunkSize: 1))));
        using var service = new WhisperDownloadService(
            root,
            handler,
            new WhisperDownloadPolicy(TimeSpan.FromMilliseconds(300)));

        var path = await service.DownloadModelAsync(Model("active.bin", payload.Length));

        SequenceEqual(payload, await File.ReadAllBytesAsync(path), "downloaded model bytes");
        Equal(1, handler.RequestCount, "request count");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

static async Task StalledDownloadTimesOutAndCleansUpAsync()
{
    var root = CreateTemporaryDirectory("stalled");
    try
    {
        using var handler = new DelegateHandler(_ => Response(
            new StreamContent(new PacedStream([1, 2, 3], TimeSpan.Zero, chunkSize: 3, stallAtEnd: true))));
        using var service = new WhisperDownloadService(
            root,
            handler,
            new WhisperDownloadPolicy(TimeSpan.FromMilliseconds(80)));

        var exception = await ThrowsAsync<WhisperDownloadTimeoutException>(
            () => service.DownloadModelAsync(Model("stalled.bin", approximateBytes: 0)));

        Equal("Model", exception.Stage, "timeout stage");
        Ensure(exception.Message.Contains("try again", StringComparison.OrdinalIgnoreCase), "The timeout did not provide retry guidance.");
        Ensure(!File.Exists(Path.Combine(root, "Models", "stalled.bin")), "The incomplete model became visible as an installed model.");
        Ensure(!DownloadFiles(root).Any(), "A partial model file remained after the timeout.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

static async Task StalledResponseHeadersTimeOutAsync()
{
    var root = CreateTemporaryDirectory("stalled-headers");
    try
    {
        using var handler = new StallingHandler();
        using var service = new WhisperDownloadService(
            root,
            handler,
            new WhisperDownloadPolicy(TimeSpan.FromMilliseconds(80)));

        var exception = await ThrowsAsync<WhisperDownloadTimeoutException>(
            () => service.DownloadModelAsync(Model("headers.bin", approximateBytes: 0)));

        Equal("Model", exception.Stage, "header timeout stage");
        Ensure(!DownloadFiles(root).Any(), "A partial file remained after the response-header timeout.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

static async Task CallerCancellationRemainsCancellationAsync()
{
    var root = CreateTemporaryDirectory("cancelled");
    try
    {
        using var handler = new DelegateHandler(_ => Response(
            new StreamContent(new PacedStream([1, 2, 3], TimeSpan.Zero, chunkSize: 3, stallAtEnd: true))));
        using var service = new WhisperDownloadService(
            root,
            handler,
            new WhisperDownloadPolicy(TimeSpan.FromSeconds(5)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await ThrowsAsync<OperationCanceledException>(
            () => service.DownloadModelAsync(Model("cancelled.bin", approximateBytes: 0), cancellationToken: cancellation.Token));

        Ensure(cancellation.IsCancellationRequested, "The caller cancellation token was not the source of cancellation.");
        Ensure(!DownloadFiles(root).Any(), "A partial model file remained after caller cancellation.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

static async Task TimedOutDownloadCanBeRetriedAsync()
{
    var root = CreateTemporaryDirectory("retry");
    try
    {
        var payload = new byte[] { 10, 20, 30, 40 };
        using var handler = new DelegateHandler(requestNumber => requestNumber == 1
            ? Response(new StreamContent(new PacedStream([1], TimeSpan.Zero, chunkSize: 1, stallAtEnd: true)))
            : Response(new ByteArrayContent(payload)));
        using var service = new WhisperDownloadService(
            root,
            handler,
            new WhisperDownloadPolicy(TimeSpan.FromMilliseconds(80)));
        var model = Model("retry.bin", payload.Length);

        await ThrowsAsync<WhisperDownloadTimeoutException>(() => service.DownloadModelAsync(model));
        var path = await service.DownloadModelAsync(model);

        SequenceEqual(payload, await File.ReadAllBytesAsync(path), "retried model bytes");
        Equal(2, handler.RequestCount, "request count after retry");
        Ensure(!DownloadFiles(root).Any(), "A partial model file remained after the successful retry.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

static async Task InAppSetupInstallsOfficialAssetsSafelyAsync()
{
    var root = CreateTemporaryDirectory("official-assets");
    try
    {
        using var handler = new OfficialAssetHandler();
        using var downloads = new WhisperDownloadService(root, handler);
        var worker = await downloads.InstallLatestWindowsWorkerAsync();
        Ensure(File.Exists(worker.ExecutablePath), "The worker executable was not installed.");
        Equal("v-test", worker.Version, "worker version");
        Ensure(File.Exists(Path.Combine(Path.GetDirectoryName(worker.ExecutablePath)!, "whisper.dll")), "The worker library was not installed.");

        var model = await downloads.DownloadModelAsync(Model("ggml-test.bin", 1024));
        Ensure(File.Exists(model), "The model was not installed.");
        Equal(1024L, new FileInfo(model).Length, "model length");

        var vad = await downloads.DownloadVadModelAsync();
        Ensure(File.Exists(vad), "The VAD model was not installed.");
        Equal(WhisperDownloadService.VadFileName, Path.GetFileName(vad), "VAD file name");
        Ensure(handler.ReleaseRequested && handler.WorkerRequested && handler.ModelRequested && handler.VadRequested,
            "The setup did not request every official asset.");
    }
    finally
    {
        DeleteTemporaryDirectory(root);
    }
}

static WhisperModelCatalogItem Model(string fileName, long approximateBytes)
    => new("test", "Test model", fileName, $"https://huggingface.co/ggml-org/test/resolve/main/{fileName}", approximateBytes);

static IEnumerable<string> DownloadFiles(string root)
    => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*.download", SearchOption.AllDirectories)
        : [];

static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException exception)
    {
        return exception;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void Ensure(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'.");
}

static void SequenceEqual(byte[] expected, byte[] actual, string label)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"{label}: downloaded bytes did not match the response.");
}

static string CreateTemporaryDirectory(string name)
{
    var path = Path.Combine(Path.GetTempPath(), "RadioVaultTranscriptionTests", $"{name}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(path);
    return path;
}

static void DeleteTemporaryDirectory(string path)
{
    try
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
    catch
    {
    }
}

static HttpResponseMessage Response(HttpContent content)
    => new(HttpStatusCode.OK) { Content = content };

sealed class DelegateHandler(Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestCount++;
        return Task.FromResult(responseFactory(RequestCount));
    }
}

sealed class StallingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The stalled request unexpectedly resumed.");
    }
}

sealed class PacedStream(
    byte[] data,
    TimeSpan delay,
    int chunkSize,
    bool stallAtEnd = false) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= data.Length)
        {
            if (stallAtEnd) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
        var count = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _position);
        data.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class OfficialAssetHandler : HttpMessageHandler
{
    private readonly byte[] _workerArchive;
    private readonly string _workerDigest;

    public OfficialAssetHandler()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "Release/whisper-cli.exe", [1, 2, 3, 4]);
            WriteEntry(archive, "Release/whisper.dll", [5, 6, 7, 8]);
        }
        _workerArchive = stream.ToArray();
        _workerDigest = Convert.ToHexString(SHA256.HashData(_workerArchive)).ToLowerInvariant();
    }

    public bool ReleaseRequested { get; private set; }
    public bool WorkerRequested { get; private set; }
    public bool ModelRequested { get; private set; }
    public bool VadRequested { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri ?? throw new InvalidOperationException("A request URI is required.");
        if (uri.Host == "api.github.com")
        {
            ReleaseRequested = true;
            var json = JsonSerializer.Serialize(new
            {
                tag_name = "v-test",
                assets = new[]
                {
                    new
                    {
                        name = "whisper-bin-x64.zip",
                        browser_download_url = "https://github.com/ggml-org/whisper.cpp/releases/download/v-test/worker.zip",
                        digest = $"sha256:{_workerDigest}",
                        size = _workerArchive.Length
                    }
                }
            });
            return Task.FromResult(Response(new StringContent(json, Encoding.UTF8, "application/json")));
        }
        if (uri.Host == "github.com")
        {
            WorkerRequested = true;
            return Task.FromResult(Response(new ByteArrayContent(_workerArchive)));
        }
        if (uri.Host == "huggingface.co" && uri.AbsolutePath.Contains("whisper-vad", StringComparison.OrdinalIgnoreCase))
        {
            VadRequested = true;
            return Task.FromResult(Response(new ByteArrayContent(Enumerable.Repeat((byte)9, 512).ToArray())));
        }
        if (uri.Host == "huggingface.co")
        {
            ModelRequested = true;
            return Task.FromResult(Response(new ByteArrayContent(Enumerable.Repeat((byte)10, 1024).ToArray())));
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path);
        using var target = entry.Open();
        target.Write(content);
    }

    private static HttpResponseMessage Response(HttpContent content)
        => new(HttpStatusCode.OK) { Content = content };
}
