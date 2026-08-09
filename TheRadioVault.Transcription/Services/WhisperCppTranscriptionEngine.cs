using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

/// <summary>
/// Runs an externally installed whisper.cpp command-line worker. Radio Vault owns
/// the durable job, cancellation and transcript storage lifecycle while the native
/// worker remains replaceable and isolated behind ITranscriptionEngine.
/// </summary>
public sealed class WhisperCppTranscriptionEngine : ITranscriptionEngine, IPausableTranscriptionEngine
{
    private static readonly Regex ProgressRegex = new(@"(?:progress\s*=\s*|\b)(?<value>\d{1,3})%", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly object _settingsGate = new();
    private readonly ITranscriptionProcessController? _processController;
    private readonly ConcurrentDictionary<Guid, int> _activeProcesses = new();
    private WhisperCppEngineSettings _settings;
    private string _version = "external";

    public WhisperCppTranscriptionEngine(WhisperCppEngineSettings settings, ITranscriptionProcessController? processController = null)
    {
        _settings = settings?.Clone() ?? throw new ArgumentNullException(nameof(settings));
        _settings.DisableUnsupportedFeatures();
        _processController = processController;
        RefreshVersion();
    }

    public string Id => "whisper.cpp";
    public string DisplayName => "whisper.cpp server worker";
    public string Version => _version;
    public bool IsAvailable
    {
        get
        {
            var settings = GetSettings();
            return File.Exists(settings.ExecutablePath) && File.Exists(settings.ModelPath);
        }
    }
    public bool SupportsWordTimings => true;
    public bool SupportsSpeakerDiarization => false;
    public string AvailabilityMessage
    {
        get
        {
            var settings = GetSettings();
            if (string.IsNullOrWhiteSpace(settings.ExecutablePath)) return "Choose a whisper.cpp worker in Settings → Transcription.";
            if (!File.Exists(settings.ExecutablePath)) return "The configured whisper.cpp executable cannot be found.";
            if (string.IsNullOrWhiteSpace(settings.ModelPath)) return "Choose or download a Whisper model in Settings → Transcription.";
            if (!File.Exists(settings.ModelPath)) return "The configured Whisper model cannot be found.";
            if (settings.UseVoiceActivityDetection && !File.Exists(settings.VadModelPath)) return "Voice activity detection is enabled but its VAD model cannot be found.";
            return $"Ready with {Path.GetFileName(settings.ModelPath)}";
        }
    }

    public WhisperCppEngineSettings GetSettings()
    {
        lock (_settingsGate) return _settings.Clone();
    }

    public void Configure(WhisperCppEngineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Clone();
        normalized.DisableUnsupportedFeatures();
        lock (_settingsGate) _settings = normalized;
        RefreshVersion();
    }

    public bool Pause(Guid operationId)
        => _processController is not null
           && _activeProcesses.TryGetValue(operationId, out var processId)
           && _processController.TryPause(processId);

    public bool Resume(Guid operationId)
        => _processController is not null
           && _activeProcesses.TryGetValue(operationId, out var processId)
           && _processController.TryResume(processId);

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<TranscriptionEngineProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        var settings = GetSettings();
        Validate(settings, request);

        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? Path.Combine(Path.GetTempPath(), "RadioVault", "Transcription", Guid.NewGuid().ToString("N"))
            : request.WorkingDirectory;
        Directory.CreateDirectory(workingDirectory);
        var outputPrefix = Path.Combine(workingDirectory, "transcript");
        var stderrTail = new Queue<string>();
        var stdoutTail = new Queue<string>();
        var runtimeProbe = new WorkerRuntimeProbe();
        var stopwatch = Stopwatch.StartNew();

        progress.Report(new TranscriptionEngineProgress(0, "Starting local Whisper worker", 0, request.EffectiveDurationMs));

        using var process = new Process
        {
            StartInfo = BuildStartInfo(settings, request, outputPrefix),
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            AddTail(stdoutTail, e.Data);
            runtimeProbe.Observe(e.Data);
            ReportProgressLine(e.Data, request, progress);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            AddTail(stderrTail, e.Data);
            runtimeProbe.Observe(e.Data);
            ReportProgressLine(e.Data, request, progress);
        };

        if (!process.Start()) throw new InvalidOperationException("whisper.cpp could not be started.");
        if (request.OperationId != Guid.Empty)
        {
            _activeProcesses[request.OperationId] = process.Id;
            process.Exited += (_, _) => _activeProcesses.TryRemove(request.OperationId, out _);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The worker may have exited between the cancellation signal and Kill.
            }
        });

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(workingDirectory);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var diagnostic = string.Join(Environment.NewLine, SnapshotTail(stderrTail).Concat(SnapshotTail(stdoutTail)).TakeLast(20));
            TryDeleteDirectory(workingDirectory);
            throw new InvalidOperationException(
                $"whisper.cpp exited with code {process.ExitCode}." +
                (diagnostic.Length == 0 ? "" : Environment.NewLine + diagnostic));
        }

        var outputPath = outputPrefix + ".json";
        if (!File.Exists(outputPath))
        {
            outputPath = Directory.EnumerateFiles(workingDirectory, "*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault() ?? "";
        }
        if (outputPath.Length == 0 || !File.Exists(outputPath))
        {
            var diagnostic = string.Join(Environment.NewLine, SnapshotTail(stderrTail).Concat(SnapshotTail(stdoutTail)).TakeLast(20));
            TryDeleteDirectory(workingDirectory);
            throw new InvalidDataException(
                "whisper.cpp completed without producing its full JSON transcript." +
                (diagnostic.Length == 0 ? "" : Environment.NewLine + diagnostic));
        }

        progress.Report(new TranscriptionEngineProgress(98, "Reading timed transcript", request.EffectiveDurationMs, request.EffectiveDurationMs));
        try
        {
            var result = await ParseResultAsync(
                outputPath,
                settings,
                request,
                runtimeProbe,
                stopwatch.Elapsed,
                cancellationToken).ConfigureAwait(false);
            progress.Report(new TranscriptionEngineProgress(100, "Local transcription complete", request.EffectiveDurationMs, request.EffectiveDurationMs));
            return result;
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    private ProcessStartInfo BuildStartInfo(
        WhisperCppEngineSettings settings,
        TranscriptionRequest request,
        string outputPrefix)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = settings.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(settings.ExecutablePath) ?? Environment.CurrentDirectory
        };

        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(settings.ModelPath);
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(request.AudioPath);
        startInfo.ArgumentList.Add("--output-json-full");
        startInfo.ArgumentList.Add("--output-file");
        startInfo.ArgumentList.Add(outputPrefix);
        startInfo.ArgumentList.Add("--print-progress");
        startInfo.ArgumentList.Add("--split-on-word");
        startInfo.ArgumentList.Add("--max-len");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--language");
        startInfo.ArgumentList.Add(NormalizeLanguage(request.Language, settings.DefaultLanguage));

        if (settings.Threads > 0)
        {
            startInfo.ArgumentList.Add("--threads");
            startInfo.ArgumentList.Add(settings.Threads.ToString(CultureInfo.InvariantCulture));
        }
        if (!settings.UseGpu) startInfo.ArgumentList.Add("--no-gpu");
        var inputOffsetMs = request.InputOffsetMs ?? request.StartMs;
        if (inputOffsetMs > 0)
        {
            startInfo.ArgumentList.Add("--offset-t");
            startInfo.ArgumentList.Add(inputOffsetMs.ToString(CultureInfo.InvariantCulture));
        }
        if (request.DurationMs is > 0)
        {
            startInfo.ArgumentList.Add("--duration");
            startInfo.ArgumentList.Add(request.DurationMs.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(request.ContextPrompt))
        {
            startInfo.ArgumentList.Add("--prompt");
            startInfo.ArgumentList.Add(request.ContextPrompt.Length <= 1200 ? request.ContextPrompt : request.ContextPrompt[..1200]);
        }
        if (request.UseVoiceActivityDetection && settings.UseVoiceActivityDetection && File.Exists(settings.VadModelPath))
        {
            startInfo.ArgumentList.Add("--vad");
            startInfo.ArgumentList.Add("--vad-model");
            startInfo.ArgumentList.Add(settings.VadModelPath);
        }

        return startInfo;
    }

    private async Task<TranscriptionResult> ParseResultAsync(
        string path,
        WhisperCppEngineSettings settings,
        TranscriptionRequest request,
        WorkerRuntimeProbe runtimeProbe,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var language = ReadString(root, "result", "language");
        if (string.IsNullOrWhiteSpace(language)) language = NormalizeLanguage(request.Language, settings.DefaultLanguage);

        if (!root.TryGetProperty("transcription", out var transcription) || transcription.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The whisper.cpp JSON did not contain a transcription array.");

        var units = new List<RawTranscriptUnit>();
        foreach (var sourceSegment in transcription.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawText = sourceSegment.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? "" : "";
            var contentKind = TranscriptQualityProcessor.DetectKind(rawText);
            var text = rawText.Replace("[SPEAKER_TURN]", "", StringComparison.OrdinalIgnoreCase).Trim();
            var (startMs, endMs) = ReadOffsets(sourceSegment, request.StartMs, request.InputOffsetMs);
            var words = ReadWords(sourceSegment, request.StartMs, request.InputOffsetMs, string.Empty);

            if (text.Length > 0 || words.Count > 0)
            {
                units.Add(new RawTranscriptUnit(
                    startMs,
                    Math.Max(startMs, endMs),
                    text,
                    string.Empty,
                    string.Empty,
                    words,
                    contentKind));
            }

        }

        // Word-level output is ideal for accurate seeking, but displaying one row
        // per word would make a long radio show unusable. Group adjacent units into
        // readable phrases while retaining every timed word underneath.
        var segments = TranscriptQualityProcessor.Process(GroupUnits(units));
        var fullText = string.Join(Environment.NewLine, segments.Select(x => x.Text));
        var durationMs = request.ExpectedDurationMs is > 0
            ? request.ExpectedDurationMs.Value
            : segments.Count == 0
                ? request.StartMs + (request.DurationMs ?? 0)
                : segments.Max(x => x.EndMs);
        var modelId = string.IsNullOrWhiteSpace(request.ModelId) ? settings.ModelId : request.ModelId;
        var effectiveAudioDuration = request.DurationMs ?? (segments.Count == 0 ? request.EffectiveDurationMs : Math.Max(0, segments.Max(x => x.EndMs) - request.StartMs));
        var speedMultiplier = elapsed.TotalSeconds <= 0 ? 0 : effectiveAudioDuration / 1000d / elapsed.TotalSeconds;
        var metadata = JsonSerializer.Serialize(new
        {
            worker = "whisper.cpp",
            workerVersion = Version,
            modelFile = Path.GetFileName(settings.ModelPath),
            language,
            rangeStartMs = request.StartMs,
            rangeDurationMs = request.DurationMs,
            speakerDiarization = "separate multi-speaker pass",
            voiceActivityDetection = request.UseVoiceActivityDetection && settings.UseVoiceActivityDetection,
            backend = runtimeProbe.Backend,
            processingDurationMs = (long)elapsed.TotalMilliseconds,
            audioDurationMs = effectiveAudioDuration,
            speedMultiplier = Math.Round(speedMultiplier, 2),
            contextTerms = string.IsNullOrWhiteSpace(request.ContextPrompt) ? 0 : request.ContextPrompt.Split(',', StringSplitOptions.RemoveEmptyEntries).Length,
            qualityProcessor = "alpha4"
        });

        return new TranscriptionResult(
            language,
            fullText.Trim(),
            durationMs,
            segments,
            Id,
            Version,
            modelId,
            HasWordTimings: segments.Any(x => x.Words?.Count > 0),
            HasSpeakerDiarization: false,
            MetadataJson: metadata);
    }


    private static IReadOnlyList<TranscriptSegment> GroupUnits(IReadOnlyList<RawTranscriptUnit> units)
    {
        if (units.Count == 0) return Array.Empty<TranscriptSegment>();

        var result = new List<TranscriptSegment>();
        var text = new StringBuilder();
        var words = new List<TranscriptWord>();
        long startMs = 0;
        long endMs = 0;
        string speaker = "";
        string speakerKey = "";
        var contentKind = TranscriptContentKind.Speech;
        var hasPending = false;

        void Flush()
        {
            if (!hasPending) return;
            var cleanText = text.ToString().Trim();
            if (cleanText.Length > 0 || words.Count > 0)
            {
                var confidenceValues = words
                    .Where(x => x.Confidence.HasValue)
                    .Select(x => x.Confidence!.Value)
                    .ToList();
                result.Add(new TranscriptSegment(
                    result.Count,
                    startMs,
                    Math.Max(startMs, endMs),
                    cleanText,
                    speaker,
                    confidenceValues.Count == 0 ? null : confidenceValues.Average(),
                    words.ToList(),
                    speakerKey,
                    ContentKind: contentKind));
            }
            text.Clear();
            words.Clear();
            hasPending = false;
        }

        foreach (var unit in units)
        {
            var speakerChanged = hasPending && !string.Equals(speakerKey, unit.SpeakerKey, StringComparison.Ordinal);
            var contentChanged = hasPending && contentKind != unit.ContentKind;
            var largeGap = hasPending && unit.StartMs - endMs > 1500;
            var phraseAlreadyLong = hasPending && (endMs - startMs >= 12000 || text.Length >= 220);
            if (speakerChanged || contentChanged || largeGap || phraseAlreadyLong) Flush();

            if (!hasPending)
            {
                startMs = unit.StartMs;
                endMs = unit.EndMs;
                speaker = unit.Speaker;
                speakerKey = unit.SpeakerKey;
                contentKind = unit.ContentKind;
                hasPending = true;
            }

            AppendTranscriptText(text, unit.Text);
            words.AddRange(unit.Words);
            endMs = Math.Max(endMs, unit.EndMs);

            var phraseDuration = endMs - startMs;
            var endsSentence = unit.Text.TrimEnd().EndsWith('.')
                               || unit.Text.TrimEnd().EndsWith('?')
                               || unit.Text.TrimEnd().EndsWith('!');
            if ((endsSentence && phraseDuration >= 2500) || phraseDuration >= 15000 || text.Length >= 260)
                Flush();
        }

        Flush();
        return result;
    }

    private static void AppendTranscriptText(StringBuilder builder, string value)
    {
        var clean = value.Trim();
        if (clean.Length == 0) return;
        var punctuation = clean[0] is '.' or ',' or '?' or '!' or ':' or ';' or ')' or ']' or '}';
        if (builder.Length > 0 && !punctuation && !char.IsWhiteSpace(builder[^1])) builder.Append(' ');
        builder.Append(clean);
    }

    private static IReadOnlyList<TranscriptWord> ReadWords(
        JsonElement segment,
        long rangeStartMs,
        long? workerInputOffsetMs,
        string speakerKey)
    {
        if (!segment.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Array)
            return Array.Empty<TranscriptWord>();

        var result = new List<TranscriptWord>();
        foreach (var token in tokens.EnumerateArray())
        {
            var text = token.TryGetProperty("text", out var value) ? value.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text) || IsSpecialToken(text)) continue;
            var (start, end) = ReadOffsets(token, rangeStartMs, workerInputOffsetMs);
            if (end <= start) continue;
            double? confidence = null;
            if (token.TryGetProperty("p", out var probability) && probability.TryGetDouble(out var parsed)) confidence = parsed;
            result.Add(new TranscriptWord(start, end, text, confidence, speakerKey));
        }
        return result;
    }

    private static (long StartMs, long EndMs) ReadOffsets(
        JsonElement element,
        long rangeStartMs,
        long? workerInputOffsetMs)
    {
        if (!element.TryGetProperty("offsets", out var offsets) || offsets.ValueKind != JsonValueKind.Object)
            return (rangeStartMs, rangeStartMs);
        var start = offsets.TryGetProperty("from", out var from) && from.TryGetInt64(out var parsedStart) ? parsedStart : 0;
        var end = offsets.TryGetProperty("to", out var to) && to.TryGetInt64(out var parsedEnd) ? parsedEnd : start;

        return WhisperTimestampMapper.Map(start, end, rangeStartMs, workerInputOffsetMs);
    }

    private static string ReadString(JsonElement root, string objectName, string propertyName)
    {
        if (!root.TryGetProperty(objectName, out var nested) || nested.ValueKind != JsonValueKind.Object) return "";
        return nested.TryGetProperty(propertyName, out var value) ? value.GetString() ?? "" : "";
    }

    private static void ReportProgressLine(
        string line,
        TranscriptionRequest request,
        IProgress<TranscriptionEngineProgress> progress)
    {
        var match = ProgressRegex.Match(line);
        if (!match.Success || !double.TryParse(match.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return;
        value = Math.Clamp(value, 0, 100);
        var total = request.EffectiveDurationMs;
        var processed = total > 0 ? (long)Math.Round(total * value / 100d) : 0;
        progress.Report(new TranscriptionEngineProgress(value, $"Transcribing locally · {value:0}%", processed, total));
    }

    private void RefreshVersion()
    {
        var settings = GetSettings();
        if (!File.Exists(settings.ExecutablePath))
        {
            _version = "external";
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = settings.ExecutablePath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                _version = "external";
                return;
            }
            if (!process.WaitForExit(2500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                _version = "external";
                return;
            }
            var output = (process.StandardOutput.ReadToEnd() + " " + process.StandardError.ReadToEnd()).Trim();
            var versionMatch = Regex.Match(output, @"(?<!\d)(?<version>\d+\.\d+(?:\.\d+)?)(?!\d)");
            _version = versionMatch.Success ? versionMatch.Groups["version"].Value : "external";
        }
        catch
        {
            _version = "external";
        }
    }

    private static void Validate(WhisperCppEngineSettings settings, TranscriptionRequest request)
    {
        if (!File.Exists(settings.ExecutablePath)) throw new FileNotFoundException("The configured whisper.cpp executable cannot be found.", settings.ExecutablePath);
        if (!File.Exists(settings.ModelPath)) throw new FileNotFoundException("The configured Whisper model cannot be found.", settings.ModelPath);
        if (!File.Exists(request.AudioPath)) throw new FileNotFoundException("The broadcast audio file cannot be found.", request.AudioPath);
        if (request.StartMs < 0) throw new ArgumentOutOfRangeException(nameof(request), "The transcription start must not be negative.");
        if (request.DurationMs is <= 0) throw new ArgumentOutOfRangeException(nameof(request), "A range duration must be greater than zero.");
        if (request.UseVoiceActivityDetection && settings.UseVoiceActivityDetection && !File.Exists(settings.VadModelPath))
            throw new FileNotFoundException("Voice activity detection is enabled but its model cannot be found.", settings.VadModelPath);
    }

    private static string NormalizeLanguage(string requested, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(requested) ? fallback : requested;
        return string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
    }

    private static bool IsSpecialToken(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("[_", StringComparison.Ordinal)
               || trimmed.StartsWith("[SPEAKER_", StringComparison.OrdinalIgnoreCase)
               || trimmed is "[BLANK_AUDIO]" or "[MUSIC]";
    }

    private static void AddTail(Queue<string> tail, string line)
    {
        lock (tail)
        {
            tail.Enqueue(line);
            while (tail.Count > 40) tail.Dequeue();
        }
    }

    private static IReadOnlyList<string> SnapshotTail(Queue<string> tail)
    {
        lock (tail) return tail.ToArray();
    }

    private sealed record RawTranscriptUnit(
        long StartMs,
        long EndMs,
        string Text,
        string Speaker,
        string SpeakerKey,
        IReadOnlyList<TranscriptWord> Words,
        TranscriptContentKind ContentKind);

    private sealed class WorkerRuntimeProbe
    {
        private readonly object _gate = new();
        public string Backend { get; private set; } = "Unknown";

        public void Observe(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var value = line.ToLowerInvariant();
            var detected = value.Contains("cuda") ? "CUDA"
                : value.Contains("metal") ? "Metal"
                : value.Contains("vulkan") ? "Vulkan"
                : value.Contains("openvino") ? "OpenVINO"
                : value.Contains("cpu backend") || value.Contains("ggml-cpu") ? "CPU"
                : "";
            if (detected.Length == 0) return;
            lock (_gate) Backend = detected;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Working files are harmless and can be cleaned on the next launch.
        }
    }
}
