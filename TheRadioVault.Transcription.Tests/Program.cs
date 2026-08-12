using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Active model download renews its inactivity deadline", ActiveDownloadRenewsInactivityDeadlineAsync),
    ("Stalled response headers time out", StalledResponseHeadersTimeOutAsync),
    ("Stalled model download times out and removes its partial file", StalledDownloadTimesOutAndCleansUpAsync),
    ("Caller cancellation remains cancellation and removes its partial file", CallerCancellationRemainsCancellationAsync),
    ("Timed-out model download can be retried", TimedOutDownloadCanBeRetriedAsync),
    ("In-app setup installs official transcription assets safely", InAppSetupInstallsOfficialAssetsSafelyAsync),
    ("Active worker output renews its inactivity deadline", ActiveWorkerOutputRenewsInactivityDeadlineAsync),
    ("Stalled worker times out and kills its process tree", StalledWorkerTimesOutAndKillsTreeAsync),
    ("Worker cancellation stays distinct and kills its process tree", WorkerCancellationStaysDistinctAsync),
    ("Whisper engine parses successful worker output and releases pause registration", WhisperEngineCompletesAndReleasesPauseRegistrationAsync),
    ("Whisper engine reports worker crashes and cleans its workspace", WhisperEngineReportsCrashAndCleansWorkspaceAsync),
    ("Whisper engine cleans its workspace after a worker timeout", WhisperEngineCleansWorkspaceAfterTimeoutAsync)
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

static async Task ActiveWorkerOutputRenewsInactivityDeadlineAsync()
{
    var process = new FakeWorkerProcess();
    var runner = new TranscriptionWorkerProcessRunner(new FakeWorkerProcessFactory(process));
    var run = runner.RunAsync(
        WorkerRequest(TimeSpan.FromMilliseconds(300)),
        CancellationToken.None);

    for (var index = 0; index < 6; index++)
    {
        await Task.Delay(75);
        process.EmitOutput($"progress = {index * 10}%");
    }
    process.Complete(0);

    Equal(0, await run, "worker exit code");
    Ensure(!process.KillTreeRequested, "An active worker was incorrectly killed.");
    Ensure(process.Disposed, "The completed worker process was not disposed.");
}

static async Task StalledWorkerTimesOutAndKillsTreeAsync()
{
    var process = new FakeWorkerProcess();
    var runner = new TranscriptionWorkerProcessRunner(new FakeWorkerProcessFactory(process));

    var exception = await ThrowsAsync<WhisperWorkerTimeoutException>(() => runner.RunAsync(
        WorkerRequest(TimeSpan.FromMilliseconds(80)),
        CancellationToken.None));

    Equal(TimeSpan.FromMilliseconds(80), exception.InactivityTimeout, "worker inactivity timeout");
    Ensure(process.KillTreeRequested, "The stalled worker process tree was not killed.");
    Ensure(process.Disposed, "The stalled worker process was not disposed.");
}

static async Task WorkerCancellationStaysDistinctAsync()
{
    var process = new FakeWorkerProcess();
    var runner = new TranscriptionWorkerProcessRunner(new FakeWorkerProcessFactory(process));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

    await ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(
        WorkerRequest(TimeSpan.FromSeconds(5)),
        cancellation.Token));

    Ensure(cancellation.IsCancellationRequested, "The caller token did not initiate worker cancellation.");
    Ensure(process.KillTreeRequested, "The cancelled worker process tree was not killed.");
    Ensure(process.Disposed, "The cancelled worker process was not disposed.");
}

static async Task WhisperEngineCompletesAndReleasesPauseRegistrationAsync()
{
    var fixture = CreateEngineFixture("worker-success");
    try
    {
        var runner = new ControlledWorkerRunner();
        var controller = new RecordingProcessController();
        var engine = new WhisperCppTranscriptionEngine(
            fixture.Settings,
            controller,
            runner,
            new WhisperWorkerPolicy(TimeSpan.FromSeconds(5)));
        var operationId = Guid.NewGuid();
        var request = fixture.Request(operationId);
        var transcription = engine.TranscribeAsync(request, new ImmediateProgress(), CancellationToken.None);

        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(engine.Pause(operationId), "The active worker was not available to pause.");
        Equal(731, controller.LastPausedProcessId, "paused process id");
        runner.CompleteSuccessfully();
        var result = await transcription;

        Equal("Hello Radio Vault", result.FullText, "transcript text");
        Ensure(result.HasWordTimings, "The successful worker transcript lost word timings.");
        Ensure(!engine.Pause(operationId), "The completed worker remained registered as active.");
        Ensure(!Directory.Exists(fixture.WorkingDirectory), "The successful worker workspace was not removed.");
    }
    finally
    {
        fixture.Dispose();
    }
}

static async Task WhisperEngineReportsCrashAndCleansWorkspaceAsync()
{
    var fixture = CreateEngineFixture("worker-crash");
    try
    {
        var runner = new DelegateWorkerRunner((request, _) =>
        {
            request.ProcessStarted?.Invoke(404);
            request.StandardErrorReceived?.Invoke("fatal model failure");
            return Task.FromResult(7);
        });
        var engine = new WhisperCppTranscriptionEngine(fixture.Settings, processRunner: runner);

        var exception = await ThrowsAsync<InvalidOperationException>(() => engine.TranscribeAsync(
            fixture.Request(Guid.NewGuid()),
            new ImmediateProgress(),
            CancellationToken.None));

        Ensure(exception.Message.Contains("code 7", StringComparison.Ordinal), "The worker exit code was not reported.");
        Ensure(exception.Message.Contains("fatal model failure", StringComparison.Ordinal), "The worker diagnostic tail was not reported.");
        Ensure(!Directory.Exists(fixture.WorkingDirectory), "The crashed worker workspace was not removed.");
    }
    finally
    {
        fixture.Dispose();
    }
}

static async Task WhisperEngineCleansWorkspaceAfterTimeoutAsync()
{
    var fixture = CreateEngineFixture("worker-timeout");
    try
    {
        var runner = new DelegateWorkerRunner((request, _) =>
        {
            request.ProcessStarted?.Invoke(505);
            throw new WhisperWorkerTimeoutException(request.InactivityTimeout);
        });
        var controller = new RecordingProcessController();
        var policy = new WhisperWorkerPolicy(TimeSpan.FromMilliseconds(80));
        var engine = new WhisperCppTranscriptionEngine(fixture.Settings, controller, runner, policy);
        var operationId = Guid.NewGuid();

        var exception = await ThrowsAsync<WhisperWorkerTimeoutException>(() => engine.TranscribeAsync(
            fixture.Request(operationId),
            new ImmediateProgress(),
            CancellationToken.None));

        Equal(policy.InactivityTimeout, exception.InactivityTimeout, "engine timeout policy");
        Ensure(!engine.Resume(operationId), "The timed-out worker remained registered as active.");
        Ensure(!Directory.Exists(fixture.WorkingDirectory), "The timed-out worker workspace was not removed.");
    }
    finally
    {
        fixture.Dispose();
    }
}

static TranscriptionWorkerProcessRequest WorkerRequest(TimeSpan timeout)
    => new(
        new System.Diagnostics.ProcessStartInfo("whisper-test"),
        timeout);

static EngineFixture CreateEngineFixture(string name)
{
    var root = CreateTemporaryDirectory(name);
    var executable = Path.Combine(root, OperatingSystem.IsWindows() ? "whisper-test.exe" : "whisper-test");
    var model = Path.Combine(root, "model.bin");
    var audio = Path.Combine(root, "audio.wav");
    File.WriteAllBytes(executable, [1]);
    File.WriteAllBytes(model, [2]);
    File.WriteAllBytes(audio, [3]);
    return new EngineFixture(
        root,
        Path.Combine(root, "work"),
        new WhisperCppEngineSettings
        {
            ExecutablePath = executable,
            ModelPath = model,
            DefaultLanguage = "en"
        },
        audio);
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

sealed class FakeWorkerProcessFactory(FakeWorkerProcess process) : ITranscriptionWorkerProcessFactory
{
    public ITranscriptionWorkerProcess Create(System.Diagnostics.ProcessStartInfo startInfo)
    {
        process.StartInfo = startInfo;
        return process;
    }
}

sealed class FakeWorkerProcess : ITranscriptionWorkerProcess
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<string>? StandardOutputReceived;
    public event Action<string>? StandardErrorReceived;
    public System.Diagnostics.ProcessStartInfo? StartInfo { get; set; }
    public int Id { get; init; } = 731;
    public int ExitCode { get; private set; }
    public bool Started { get; private set; }
    public bool OutputReadStarted { get; private set; }
    public bool KillTreeRequested { get; private set; }
    public bool Disposed { get; private set; }

    public bool Start()
    {
        Started = true;
        return true;
    }

    public void BeginOutputRead() => OutputReadStarted = true;
    public Task WaitForExitAsync() => _exit.Task;
    public void EmitOutput(string line) => StandardOutputReceived?.Invoke(line);
    public void EmitError(string line) => StandardErrorReceived?.Invoke(line);

    public void Complete(int exitCode)
    {
        ExitCode = exitCode;
        _exit.TrySetResult();
    }

    public void TryKillTree()
    {
        KillTreeRequested = true;
        Complete(137);
    }

    public void Dispose() => Disposed = true;
}

sealed class DelegateWorkerRunner(
    Func<TranscriptionWorkerProcessRequest, CancellationToken, Task<int>> run)
    : ITranscriptionWorkerProcessRunner
{
    public Task<int> RunAsync(
        TranscriptionWorkerProcessRequest request,
        CancellationToken cancellationToken)
        => run(request, cancellationToken);
}

sealed class ControlledWorkerRunner : ITranscriptionWorkerProcessRunner
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<int> RunAsync(
        TranscriptionWorkerProcessRequest request,
        CancellationToken cancellationToken)
    {
        request.ProcessStarted?.Invoke(731);
        request.StandardErrorReceived?.Invoke("progress = 12%");
        Started.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        WriteTranscript(request.StartInfo);
        request.StandardOutputReceived?.Invoke("progress = 100%");
        return 0;
    }

    public void CompleteSuccessfully() => _release.TrySetResult();

    private static void WriteTranscript(System.Diagnostics.ProcessStartInfo startInfo)
    {
        var arguments = startInfo.ArgumentList;
        var outputIndex = -1;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--output-file", StringComparison.Ordinal))
            {
                outputIndex = index + 1;
                break;
            }
        }
        if (outputIndex <= 0 || outputIndex >= arguments.Count)
            throw new InvalidOperationException("The worker request did not contain an output prefix.");

        var outputPath = arguments[outputIndex] + ".json";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var json = JsonSerializer.Serialize(new
        {
            result = new { language = "en" },
            transcription = new[]
            {
                new
                {
                    text = "Hello Radio Vault",
                    offsets = new { from = 0, to = 1_000 },
                    tokens = new[]
                    {
                        new { text = "Hello", offsets = new { from = 0, to = 400 }, p = 0.98 },
                        new { text = " Radio", offsets = new { from = 400, to = 700 }, p = 0.97 },
                        new { text = " Vault", offsets = new { from = 700, to = 1_000 }, p = 0.96 }
                    }
                }
            }
        });
        File.WriteAllText(outputPath, json);
    }
}

sealed class RecordingProcessController : ITranscriptionProcessController
{
    public int LastPausedProcessId { get; private set; }
    public int LastResumedProcessId { get; private set; }

    public bool TryPause(int processId)
    {
        LastPausedProcessId = processId;
        return true;
    }

    public bool TryResume(int processId)
    {
        LastResumedProcessId = processId;
        return true;
    }
}

sealed class ImmediateProgress : IProgress<TranscriptionEngineProgress>
{
    public List<TranscriptionEngineProgress> Updates { get; } = [];
    public void Report(TranscriptionEngineProgress value) => Updates.Add(value);
}

sealed class EngineFixture(
    string root,
    string workingDirectory,
    WhisperCppEngineSettings settings,
    string audioPath) : IDisposable
{
    public string Root { get; } = root;
    public string WorkingDirectory { get; } = workingDirectory;
    public WhisperCppEngineSettings Settings { get; } = settings;

    public TranscriptionRequest Request(Guid operationId)
        => new(
            EpisodeId: 42,
            AudioPath: audioPath,
            Language: "en",
            ModelId: "test-model",
            ExpectedDurationMs: 1_000,
            WorkingDirectory: WorkingDirectory,
            OperationId: operationId);

    public void Dispose() => DeleteRoot();

    private void DeleteRoot()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
        }
    }
}
