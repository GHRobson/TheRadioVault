using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

public sealed record WhisperDownloadProgress(string Stage, double? Percent, string Message);
public sealed record WhisperWorkerInstallResult(string ExecutablePath, string Version);
public sealed record DiarizationModelInstallResult(string SegmentationModelPath, string EmbeddingModelPath);

public sealed record WhisperDownloadPolicy
{
    public static WhisperDownloadPolicy Default { get; } = new(TimeSpan.FromSeconds(90));

    public WhisperDownloadPolicy(TimeSpan inactivityTimeout)
    {
        if (inactivityTimeout <= TimeSpan.Zero || inactivityTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout), "The download inactivity timeout must be positive and no longer than 49 days.");
        InactivityTimeout = inactivityTimeout;
    }

    public TimeSpan InactivityTimeout { get; }
}

public sealed class WhisperDownloadTimeoutException : TimeoutException
{
    public WhisperDownloadTimeoutException(string stage, TimeSpan inactivityTimeout, Exception innerException)
        : base($"The {stage.ToLowerInvariant()} download stopped making progress for {FormatDuration(inactivityTimeout)}. Check the connection and try again.", innerException)
    {
        Stage = stage;
        InactivityTimeout = inactivityTimeout;
    }

    public string Stage { get; }
    public TimeSpan InactivityTimeout { get; }

    private static string FormatDuration(TimeSpan value)
        => value.TotalSeconds >= 1
            ? $"{value.TotalSeconds:0.#} seconds"
            : $"{value.TotalMilliseconds:0} milliseconds";
}

public sealed class WhisperDownloadService : IDisposable
{
    private static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/ggml-org/whisper.cpp/releases/latest");
    private const int MaximumReleaseMetadataBytes = 1024 * 1024;
    public const string VadFileName = "ggml-silero-v6.2.0.bin";
    public const string VadDownloadUrl = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin";
    public const string DiarizationSegmentationFileName = "sherpa-pyannote-segmentation-3.0-int8.onnx";
    public const string DiarizationSegmentationUrl = "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.int8.onnx";
    public const string DiarizationSegmentationSha256 = "d582f4b4c6b48205de7e0643c57df0df5615a3c176189be3fc461e9d18827b5d";
    public const long DiarizationSegmentationBytes = 1_540_506;
    public const string DiarizationEmbeddingFileName = "nemo-en-titanet-small.onnx";
    public const string DiarizationEmbeddingUrl = "https://huggingface.co/csukuangfj/speaker-embedding-models/resolve/main/nemo_en_titanet_small.onnx";
    public const string DiarizationEmbeddingSha256 = "ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e";
    private readonly HttpClient _http;
    private readonly string _root;
    private readonly WhisperDownloadPolicy _downloadPolicy;
    private bool _disposed;

    public WhisperDownloadService(
        string rootDirectory,
        HttpMessageHandler? handler = null,
        WhisperDownloadPolicy? downloadPolicy = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("A transcription directory is required.", nameof(rootDirectory));
        _root = Path.GetFullPath(rootDirectory);
        _downloadPolicy = downloadPolicy ?? WhisperDownloadPolicy.Default;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RadioVault/0.33");
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<WhisperWorkerInstallResult> InstallLatestWindowsWorkerAsync(
        IProgress<WhisperDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new WhisperDownloadProgress("Worker", null, "Checking the latest stable whisper.cpp release…"));
        using var releaseResponse = await RunNetworkOperationAsync(
            "Worker",
            token => _http.GetAsync(LatestReleaseUri, HttpCompletionOption.ResponseHeadersRead, token),
            cancellationToken).ConfigureAwait(false);
        releaseResponse.EnsureSuccessStatusCode();
        await using var releaseStream = await RunNetworkOperationAsync(
            "Worker",
            token => releaseResponse.Content.ReadAsStreamAsync(token),
            cancellationToken).ConfigureAwait(false);
        using var release = await ReadReleaseMetadataAsync(releaseStream, cancellationToken).ConfigureAwait(false);
        var root = release.RootElement;
        var version = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "latest" : "latest";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The official whisper.cpp release did not contain downloadable assets.");

        JsonElement? selected = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), "whisper-bin-x64.zip", StringComparison.OrdinalIgnoreCase))
            {
                selected = asset.Clone();
                break;
            }
        }
        if (!selected.HasValue) throw new InvalidDataException("The latest stable whisper.cpp release does not include the standard Windows x64 worker.");
        var assetUrl = selected.Value.GetProperty("browser_download_url").GetString() ?? string.Empty;
        ValidateDownloadUri(assetUrl, "github.com");
        var digest = selected.Value.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() ?? string.Empty : string.Empty;
        var expectedSha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : string.Empty;
        if (expectedSha256.Length != 64)
            throw new InvalidDataException("The official whisper.cpp release did not publish a usable SHA-256 digest for the Windows worker.");
        var expectedBytes = selected.Value.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;

        var downloadDirectory = Path.Combine(_root, "Downloads");
        Directory.CreateDirectory(downloadDirectory);
        var archivePath = Path.Combine(downloadDirectory, $"whisper-{Guid.NewGuid():N}.zip.download");
        var extractionPath = Path.Combine(downloadDirectory, $"worker-{Guid.NewGuid():N}");
        try
        {
            await DownloadFileAsync(new Uri(assetUrl), archivePath, expectedBytes, 256L * 1024 * 1024, "Worker", progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new WhisperDownloadProgress("Worker", 100, "Verifying the official worker archive…"));
            var actual = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded whisper.cpp worker did not match its official SHA-256 digest.");

            Directory.CreateDirectory(extractionPath);
            ExtractArchiveSafely(archivePath, extractionPath);
            var executable = Directory.EnumerateFiles(extractionPath, "whisper-cli.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (executable is null) throw new InvalidDataException("The official worker archive did not contain whisper-cli.exe.");
            var versionFolder = SanitizeDirectoryName(version);
            var installDirectory = Path.Combine(_root, "Worker", versionFolder);
            if (Directory.Exists(installDirectory))
            {
                var existing = Directory.EnumerateFiles(installDirectory, "whisper-cli.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (existing is not null)
                {
                    progress?.Report(new WhisperDownloadProgress("Worker", 100, $"whisper.cpp {version} is already installed."));
                    return new WhisperWorkerInstallResult(existing, version);
                }
                installDirectory = Path.Combine(_root, "Worker", $"{versionFolder}-{Guid.NewGuid():N}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(installDirectory)!);
            Directory.Move(extractionPath, installDirectory);
            var installedExecutable = Directory.EnumerateFiles(installDirectory, "whisper-cli.exe", SearchOption.AllDirectories).First();
            progress?.Report(new WhisperDownloadProgress("Worker", 100, $"Installed whisper.cpp {version}."));
            return new WhisperWorkerInstallResult(installedExecutable, version);
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(extractionPath);
        }
    }

    public Task<string> DownloadModelAsync(
        WhisperModelCatalogItem model,
        IProgress<WhisperDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        return DownloadKnownModelAsync(model.DownloadUrl, model.FileName, model.ApproximateBytes, "Model", progress, cancellationToken);
    }

    public Task<string> DownloadVadModelAsync(
        IProgress<WhisperDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => DownloadKnownModelAsync(VadDownloadUrl, VadFileName, 0, "VAD", progress, cancellationToken);

    public async Task<DiarizationModelInstallResult> DownloadDiarizationModelsAsync(
        IProgress<WhisperDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var segmentation = await DownloadKnownModelAsync(
            DiarizationSegmentationUrl,
            DiarizationSegmentationFileName,
            DiarizationSegmentationBytes,
            "Speaker segmentation",
            progress,
            cancellationToken,
            DiarizationSegmentationSha256).ConfigureAwait(false);
        var embedding = await DownloadKnownModelAsync(
            DiarizationEmbeddingUrl,
            DiarizationEmbeddingFileName,
            40_300_000,
            "Voice embeddings",
            progress,
            cancellationToken,
            DiarizationEmbeddingSha256).ConfigureAwait(false);
        return new DiarizationModelInstallResult(segmentation, embedding);
    }

    private async Task<string> DownloadKnownModelAsync(
        string url,
        string fileName,
        long approximateBytes,
        string stage,
        IProgress<WhisperDownloadProgress>? progress,
        CancellationToken cancellationToken,
        string expectedSha256 = "")
    {
        ValidateDownloadUri(url, "huggingface.co");
        var modelDirectory = Path.Combine(_root, "Models");
        Directory.CreateDirectory(modelDirectory);
        var destination = Path.Combine(modelDirectory, fileName);
        if (File.Exists(destination) && new FileInfo(destination).Length > 0
            && (string.IsNullOrWhiteSpace(expectedSha256)
                || string.Equals(await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false), expectedSha256, StringComparison.OrdinalIgnoreCase)))
        {
            progress?.Report(new WhisperDownloadProgress(stage, 100, $"{fileName} is already downloaded."));
            return destination;
        }
        var temporary = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            await DownloadFileAsync(new Uri(url), temporary, approximateBytes, 8L * 1024 * 1024 * 1024, stage, progress, cancellationToken).ConfigureAwait(false);
            if (new FileInfo(temporary).Length == 0) throw new InvalidDataException($"The downloaded {stage.ToLowerInvariant()} file is empty.");
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                progress?.Report(new WhisperDownloadProgress(stage, 100, $"Verifying {fileName}â€¦"));
                var actualSha256 = await ComputeSha256Async(temporary, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The downloaded {stage.ToLowerInvariant()} model did not match its published SHA-256 digest.");
            }
            File.Move(temporary, destination, true);
            progress?.Report(new WhisperDownloadProgress(stage, 100, $"Downloaded {fileName}."));
            return destination;
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private async Task DownloadFileAsync(
        Uri uri,
        string destination,
        long expectedBytes,
        long maximumBytes,
        string stage,
        IProgress<WhisperDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await RunNetworkOperationAsync(
            stage,
            token => _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedBytes;
        if (total > maximumBytes) throw new InvalidDataException($"The {stage.ToLowerInvariant()} download is unexpectedly large.");
        await using var source = await RunNetworkOperationAsync(
            stage,
            token => response.Content.ReadAsStreamAsync(token),
            cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await RunNetworkOperationAsync(
                stage,
                token => source.ReadAsync(buffer.AsMemory(), token).AsTask(),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            copied += read;
            if (copied > maximumBytes) throw new InvalidDataException($"The {stage.ToLowerInvariant()} download exceeded its safety limit.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            var percent = total > 0 ? Math.Clamp(copied * 100d / total, 0, 100) : (double?)null;
            progress?.Report(new WhisperDownloadProgress(stage, percent, $"Downloading {stage.ToLowerInvariant()} · {FormatBytes(copied)}{(total > 0 ? $" of {FormatBytes(total)}" : string.Empty)}"));
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (expectedBytes > 0 && copied < expectedBytes * 0.75)
            throw new InvalidDataException($"The downloaded {stage.ToLowerInvariant()} file is smaller than expected.");
    }

    private async Task<T> RunNetworkOperationAsync<T>(
        string stage,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var inactivityCancellation = new CancellationTokenSource(_downloadPolicy.InactivityTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            inactivityCancellation.Token);
        try
        {
            return await operation(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && inactivityCancellation.IsCancellationRequested)
        {
            throw new WhisperDownloadTimeoutException(stage, _downloadPolicy.InactivityTimeout, exception);
        }
    }

    private async Task<JsonDocument> ReadReleaseMetadataAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await RunNetworkOperationAsync(
                "Worker",
                token => source.ReadAsync(chunk.AsMemory(), token).AsTask(),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumReleaseMetadataBytes)
                throw new InvalidDataException("The whisper.cpp release metadata is unexpectedly large.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void ExtractArchiveSafely(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The worker archive contained an unsafe path.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ValidateDownloadUri(string value, string expectedHost)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The download address is not an approved official HTTPS source.");
    }

    private static string SanitizeDirectoryName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Where(character => !invalid.Contains(character)).ToArray()).Trim();
        return clean.Length == 0 ? "latest" : clean;
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GB"
            : bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):0} MB"
            : $"{bytes / 1024d:0} KB";

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
