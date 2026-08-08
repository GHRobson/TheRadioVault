using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Desktop.Avalonia.Transcription;

public sealed class NAudioTranscriptionAudioPreparer : ITranscriptionAudioPreparer
{
    private const int TargetSampleRate = 16_000;
    private static readonly HashSet<string> ConversionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4a", ".m4b", ".aac", ".wma", ".mp4"
    };

    public Task<PreparedTranscriptionAudio> PrepareAsync(
        string sourcePath,
        long startMs,
        long? durationMs,
        string workingDirectory,
        IProgress<TranscriptionEngineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!ConversionExtensions.Contains(Path.GetExtension(sourcePath)))
            return Task.FromResult(new PreparedTranscriptionAudio(sourcePath));
        return Task.Run(() => ConvertRange(sourcePath, startMs, durationMs, workingDirectory, progress, cancellationToken), cancellationToken);
    }

    private static PreparedTranscriptionAudio ConvertRange(
        string sourcePath,
        long startMs,
        long? durationMs,
        string workingDirectory,
        IProgress<TranscriptionEngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workingDirectory);
        var destination = Path.Combine(workingDirectory, "prepared-audio.wav");
        progress?.Report(new TranscriptionEngineProgress(0, "Preparing M4A audio for transcription"));

        try
        {
            using var source = OpenAudio(sourcePath);
            if (startMs >= source.TotalTime.TotalMilliseconds)
                throw new InvalidOperationException("The selected start point is beyond the end of the audio file.");
            source.CurrentTime = TimeSpan.FromMilliseconds(startMs);
            ISampleProvider provider = source.ToSampleProvider();
            if (provider.WaveFormat.Channels != 1) provider = new DownmixToMonoSampleProvider(provider);
            if (provider.WaveFormat.SampleRate != TargetSampleRate)
                provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);

            var availableMs = Math.Max(0, source.TotalTime.TotalMilliseconds - startMs);
            var selectedMs = durationMs.HasValue ? Math.Min(availableMs, durationMs.Value) : availableMs;
            var expectedSamples = Math.Max(1L, (long)Math.Ceiling(selectedMs * TargetSampleRate / 1000d));
            using var writer = new WaveFileWriter(destination, new WaveFormat(TargetSampleRate, 16, 1));
            var buffer = new float[65_536];
            long written = 0;
            while (written < expectedSamples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wanted = (int)Math.Min(buffer.Length, expectedSamples - written);
                var read = provider.Read(buffer, 0, wanted);
                if (read == 0) break;
                writer.WriteSamples(buffer, 0, read);
                written += read;
                var percent = Math.Clamp(written * 100d / expectedSamples, 0, 100);
                progress?.Report(new TranscriptionEngineProgress(percent, $"Preparing audio · {percent:0}%"));
            }
            if (written == 0) throw new InvalidDataException("The selected M4A audio could not be decoded.");
            progress?.Report(new TranscriptionEngineProgress(100, "Audio prepared for local transcription"));
            return new PreparedTranscriptionAudio(destination, InputOffsetMs: 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException("Radio Vault could not prepare this M4A audio for transcription.", exception);
        }
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
