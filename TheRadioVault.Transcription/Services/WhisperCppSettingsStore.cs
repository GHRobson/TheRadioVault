using System.Text.Json;
using System.Text.Json.Nodes;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

public sealed class WhisperCppSettingsStore
{
    public WhisperCppSettingsStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A settings path is required.", nameof(path));
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public WhisperCppEngineSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new WhisperCppEngineSettings();
            var document = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject;
            if (document is null) return new WhisperCppEngineSettings();
            var storedThreshold = document["DiarizationClusteringThreshold"]?.GetValue<double>() ?? 0.9;
            var settings = new WhisperCppEngineSettings
            {
                ExecutablePath = document["ExecutablePath"]?.GetValue<string>() ?? string.Empty,
                ModelPath = document["ModelPath"]?.GetValue<string>() ?? string.Empty,
                VadModelPath = document["VadModelPath"]?.GetValue<string>() ?? string.Empty,
                DiarizationSegmentationModelPath = document["DiarizationSegmentationModelPath"]?.GetValue<string>() ?? string.Empty,
                DiarizationEmbeddingModelPath = document["DiarizationEmbeddingModelPath"]?.GetValue<string>() ?? string.Empty,
                DefaultLanguage = document["DefaultLanguage"]?.GetValue<string>() ?? "auto",
                Threads = Math.Clamp(document["Threads"]?.GetValue<int>() ?? 0, 0, 128),
                UseGpu = document["UseGpu"]?.GetValue<bool>() ?? true,
                UseVoiceActivityDetection = document["UseVoiceActivityDetection"]?.GetValue<bool>() ?? false,
                EnableMultiSpeakerDiarization = document["EnableMultiSpeakerDiarization"]?.GetValue<bool>() ?? false,
                // 0.5 was the original hidden default. Calibration against long-form
                // radio found it grossly over-split voices, so migrate that value.
                DiarizationClusteringThreshold = storedThreshold <= 0.5 ? 0.9 : storedThreshold,
                UseArchiveContext = document["UseArchiveContext"]?.GetValue<bool>() ?? true
            };
            settings.DisableUnsupportedFeatures();
            return settings;
        }
        catch
        {
            return new WhisperCppEngineSettings();
        }
    }

    public void Save(WhisperCppEngineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Clone();
        settings.DisableUnsupportedFeatures();
        var document = new JsonObject
        {
            ["ExecutablePath"] = settings.ExecutablePath.Trim(),
            ["ModelPath"] = settings.ModelPath.Trim(),
            ["VadModelPath"] = settings.VadModelPath.Trim(),
            ["DiarizationSegmentationModelPath"] = settings.DiarizationSegmentationModelPath.Trim(),
            ["DiarizationEmbeddingModelPath"] = settings.DiarizationEmbeddingModelPath.Trim(),
            ["DefaultLanguage"] = string.IsNullOrWhiteSpace(settings.DefaultLanguage) ? "auto" : settings.DefaultLanguage.Trim().ToLowerInvariant(),
            ["Threads"] = Math.Clamp(settings.Threads, 0, 128),
            ["UseGpu"] = settings.UseGpu,
            ["UseVoiceActivityDetection"] = settings.UseVoiceActivityDetection,
            ["EnableMultiSpeakerDiarization"] = settings.EnableMultiSpeakerDiarization,
            ["DiarizationClusteringThreshold"] = settings.DiarizationClusteringThreshold,
            ["UseArchiveContext"] = settings.UseArchiveContext
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path) ?? ".");
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, Path, true);
    }
}
