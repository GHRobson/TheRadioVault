using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;

namespace TheRadioVault.Desktop.Avalonia.Diarization;

public sealed class SherpaOnnxMultiSpeakerDiarizationEngine : IMultiSpeakerDiarizationEngine
{
    private const int TargetSampleRate = 16_000;
    private readonly WhisperCppTranscriptionEngine _transcriptionEngine;

    public SherpaOnnxMultiSpeakerDiarizationEngine(WhisperCppTranscriptionEngine transcriptionEngine)
        => _transcriptionEngine = transcriptionEngine ?? throw new ArgumentNullException(nameof(transcriptionEngine));

    public string Id => "sherpa-onnx-diarization";
    public string DisplayName => "Server multi-speaker diarization";
    public string Version => typeof(OfflineSpeakerDiarization).Assembly.GetName().Version?.ToString() ?? "unknown";

    public bool IsAvailable
    {
        get
        {
            var settings = _transcriptionEngine.GetSettings();
            return File.Exists(settings.DiarizationSegmentationModelPath)
                   && File.Exists(settings.DiarizationEmbeddingModelPath);
        }
    }

    public string AvailabilityMessage
    {
        get
        {
            var settings = _transcriptionEngine.GetSettings();
            if (!File.Exists(settings.DiarizationSegmentationModelPath))
                return "The speaker segmentation model has not been downloaded yet.";
            if (!File.Exists(settings.DiarizationEmbeddingModelPath))
                return "The voice embedding model has not been downloaded yet.";
            return "Multi-speaker diarization is ready.";
        }
    }

    public Task<SpeakerDiarizationResult> DiarizeAsync(
        SpeakerDiarizationRequest request,
        IProgress<TranscriptionEngineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAvailable) throw new InvalidOperationException(AvailabilityMessage);
        if (!File.Exists(request.AudioPath)) throw new FileNotFoundException("The audio file is not available.", request.AudioPath);
        return Task.Run(() => Diarize(request, progress, cancellationToken), cancellationToken);
    }

    private SpeakerDiarizationResult Diarize(
        SpeakerDiarizationRequest request,
        IProgress<TranscriptionEngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        var settings = _transcriptionEngine.GetSettings();
        progress?.Report(new TranscriptionEngineProgress(null, "Preparing audio for speaker analysis"));
        var samples = ReadMonoSamples(request, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = settings.DiarizationSegmentationModelPath;
        config.Segmentation.NumThreads = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
        config.Embedding.Model = settings.DiarizationEmbeddingModelPath;
        config.Embedding.NumThreads = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
        if (request.ExpectedSpeakerCount > 0)
            config.Clustering.NumClusters = request.ExpectedSpeakerCount;
        else
            config.Clustering.Threshold = (float)Math.Clamp(request.ClusteringThreshold, 0.1, 0.9);

        progress?.Report(new TranscriptionEngineProgress(0, "Finding speakers"));
        var diarizer = new OfflineSpeakerDiarization(config);
        try
        {
            var callback = new OfflineSpeakerDiarizationProgressCallback((processed, total, _) =>
            {
                if (cancellationToken.IsCancellationRequested) return 1;
                var percent = total > 0 ? Math.Clamp(processed * 100d / total, 0, 100) : 0;
                progress?.Report(new TranscriptionEngineProgress(percent, $"Finding speakers · {percent:0}%"));
                return 0;
            });
            var nativeSegments = diarizer.ProcessWithCallback(samples, callback, IntPtr.Zero);
            cancellationToken.ThrowIfCancellationRequested();

            var speakerIds = nativeSegments.Select(x => x.Speaker).Distinct().OrderBy(x => x).ToArray();
            var labels = speakerIds.Select((speaker, index) => (speaker, label: $"Speaker {index + 1}"))
                .ToDictionary(x => x.speaker, x => x.label);
            var turns = nativeSegments
                .Where(x => x.End > x.Start)
                .Select(x => new SpeakerDiarizationTurn(
                    request.StartMs + (long)Math.Round(x.Start * 1000d),
                    request.StartMs + (long)Math.Round(x.End * 1000d),
                    $"speaker-{Array.IndexOf(speakerIds, x.Speaker) + 1}",
                    labels[x.Speaker]))
                .OrderBy(x => x.StartMs)
                .ThenBy(x => x.EndMs)
                .ToArray();
            progress?.Report(new TranscriptionEngineProgress(100, $"Found {speakerIds.Length} speaker{(speakerIds.Length == 1 ? "" : "s")}"));
            return new SpeakerDiarizationResult(
                turns,
                speakerIds.Length,
                Id,
                Version,
                Path.GetFileName(settings.DiarizationSegmentationModelPath),
                Path.GetFileName(settings.DiarizationEmbeddingModelPath));
        }
        finally
        {
            (diarizer as IDisposable)?.Dispose();
        }
    }

    private static float[] ReadMonoSamples(
        SpeakerDiarizationRequest request,
        IProgress<TranscriptionEngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var source = OpenAudio(request.AudioPath);
        if (request.StartMs >= source.TotalTime.TotalMilliseconds)
            throw new InvalidOperationException("The selected start point is beyond the end of the audio file.");
        source.CurrentTime = TimeSpan.FromMilliseconds(request.StartMs);

        ISampleProvider provider = source.ToSampleProvider();
        if (provider.WaveFormat.Channels != 1) provider = new DownmixToMonoSampleProvider(provider);
        if (provider.WaveFormat.SampleRate != TargetSampleRate)
            provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);

        var availableMs = Math.Max(0, source.TotalTime.TotalMilliseconds - request.StartMs);
        var requestedMs = request.DurationMs.HasValue ? Math.Min(availableMs, request.DurationMs.Value) : availableMs;
        var expectedSamplesLong = (long)Math.Ceiling(requestedMs * TargetSampleRate / 1000d);
        if (expectedSamplesLong <= 0) throw new InvalidOperationException("The selected audio range is empty.");
        if (expectedSamplesLong > int.MaxValue) throw new InvalidOperationException("The selected audio is too long to analyse in one pass.");

        var samples = new float[(int)expectedSamplesLong];
        var readTotal = 0;
        while (readTotal < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = provider.Read(samples, readTotal, Math.Min(65_536, samples.Length - readTotal));
            if (read == 0) break;
            readTotal += read;
            var percent = readTotal * 100d / samples.Length;
            progress?.Report(new TranscriptionEngineProgress(percent, $"Preparing audio · {percent:0}%"));
        }
        if (readTotal == 0) throw new InvalidOperationException("No audio samples could be decoded.");
        if (readTotal != samples.Length) Array.Resize(ref samples, readTotal);
        return samples;
    }

    private static WaveStream OpenAudio(string path)
    {
        try
        {
            return new AudioFileReader(path);
        }
        catch
        {
            return new MediaFoundationReader(path);
        }
    }

    private sealed class DownmixToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _input = Array.Empty<float>();

        public DownmixToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var wanted = checked(count * _channels);
            if (_input.Length < wanted) _input = new float[wanted];
            var read = _source.Read(_input, 0, wanted);
            var frames = read / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < _channels; channel++) sum += _input[(frame * _channels) + channel];
                buffer[offset + frame] = sum / _channels;
            }
            return frames;
        }
    }
}
