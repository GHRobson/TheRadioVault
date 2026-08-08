using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;

namespace TheRadioVault.Desktop.Avalonia.Diarization;

public sealed class SherpaOnnxVoiceEmbeddingEngine : IVoiceEmbeddingEngine
{
    private const int TargetSampleRate = 16_000;
    private readonly WhisperCppTranscriptionEngine _transcriptionEngine;

    public SherpaOnnxVoiceEmbeddingEngine(WhisperCppTranscriptionEngine transcriptionEngine)
        => _transcriptionEngine = transcriptionEngine ?? throw new ArgumentNullException(nameof(transcriptionEngine));

    public string Id => "sherpa-onnx-speaker-embedding";
    public string DisplayName => "Server remembered voices";
    public string Version => typeof(SpeakerEmbeddingExtractor).Assembly.GetName().Version?.ToString() ?? "unknown";
    public bool IsAvailable => File.Exists(_transcriptionEngine.GetSettings().DiarizationEmbeddingModelPath);
    public int Dimensions
    {
        get
        {
            if (!IsAvailable) return 0;
            using var extractor = CreateExtractor();
            return extractor.Dim;
        }
    }

    public Task<VoiceEmbeddingResult> CreateEmbeddingAsync(VoiceEmbeddingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAvailable)
            throw new InvalidOperationException("Install multi-speaker diarization before using remembered voices.");
        if (!File.Exists(request.AudioPath))
            throw new FileNotFoundException("The source audio for this voice is not available.", request.AudioPath);
        return Task.Run(() => CreateEmbedding(request, cancellationToken), cancellationToken);
    }

    private VoiceEmbeddingResult CreateEmbedding(VoiceEmbeddingRequest request, CancellationToken cancellationToken)
    {
        var samples = ReadMonoSamples(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var extractor = CreateExtractor();
        using var stream = extractor.CreateStream();
        stream.AcceptWaveform(TargetSampleRate, samples);
        stream.InputFinished();
        if (!extractor.IsReady(stream))
            throw new InvalidOperationException("This speaker sample is too short or unclear to remember reliably.");
        var values = extractor.Compute(stream).Select(x => (double)x).ToArray();
        if (values.Length == 0)
            throw new InvalidOperationException("The local voice model did not produce a speaker fingerprint.");
        var durationSeconds = samples.Length / (double)TargetSampleRate;
        var quality = Math.Clamp(durationSeconds / 8d, 0.25, 1d);
        return new VoiceEmbeddingResult(Id, Version, values, quality);
    }

    private SpeakerEmbeddingExtractor CreateExtractor()
    {
        var settings = _transcriptionEngine.GetSettings();
        return new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig
        {
            Model = settings.DiarizationEmbeddingModelPath,
            NumThreads = Math.Max(1, Math.Min(Environment.ProcessorCount, 8)),
            Debug = 0,
            Provider = "cpu"
        });
    }

    private static float[] ReadMonoSamples(VoiceEmbeddingRequest request, CancellationToken cancellationToken)
    {
        using var source = OpenAudio(request.AudioPath);
        var startMs = Math.Clamp(request.StartMs, 0, Math.Max(0, (long)source.TotalTime.TotalMilliseconds - 1));
        var requestedMs = Math.Clamp(request.EndMs - startMs, 1_000, 20_000);
        source.CurrentTime = TimeSpan.FromMilliseconds(startMs);

        ISampleProvider provider = source.ToSampleProvider();
        if (provider.WaveFormat.Channels != 1) provider = new DownmixToMonoSampleProvider(provider);
        if (provider.WaveFormat.SampleRate != TargetSampleRate)
            provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);

        var expected = checked((int)Math.Ceiling(requestedMs * TargetSampleRate / 1000d));
        var samples = new float[expected];
        var readTotal = 0;
        while (readTotal < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = provider.Read(samples, readTotal, Math.Min(32_768, samples.Length - readTotal));
            if (read == 0) break;
            readTotal += read;
        }
        if (readTotal < TargetSampleRate)
            throw new InvalidOperationException("At least one second of clear speech is needed to remember a voice.");
        if (readTotal != samples.Length) Array.Resize(ref samples, readTotal);
        return samples;
    }

    private static WaveStream OpenAudio(string path)
    {
        try { return new AudioFileReader(path); }
        catch { return new MediaFoundationReader(path); }
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
