namespace TheRadioVault.Transcription.Models;

public sealed class WhisperCppEngineSettings
{
    public string ExecutablePath { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string VadModelPath { get; set; } = "";
    public string DiarizationSegmentationModelPath { get; set; } = "";
    public string DiarizationEmbeddingModelPath { get; set; } = "";
    public string DefaultLanguage { get; set; } = "auto";
    public int Threads { get; set; }
    public bool UseGpu { get; set; } = true;
    public bool UseVoiceActivityDetection { get; set; }
    public bool EnableMultiSpeakerDiarization { get; set; }
    public double DiarizationClusteringThreshold { get; set; } = 0.9;
    public bool UseArchiveContext { get; set; } = true;

    public WhisperCppEngineSettings Clone() => new()
    {
        ExecutablePath = ExecutablePath,
        ModelPath = ModelPath,
        VadModelPath = VadModelPath,
        DiarizationSegmentationModelPath = DiarizationSegmentationModelPath,
        DiarizationEmbeddingModelPath = DiarizationEmbeddingModelPath,
        DefaultLanguage = DefaultLanguage,
        Threads = Threads,
        UseGpu = UseGpu,
        UseVoiceActivityDetection = UseVoiceActivityDetection,
        EnableMultiSpeakerDiarization = EnableMultiSpeakerDiarization,
        DiarizationClusteringThreshold = DiarizationClusteringThreshold,
        UseArchiveContext = UseArchiveContext
    };

    public string ModelId => string.IsNullOrWhiteSpace(ModelPath)
        ? ""
        : Path.GetFileNameWithoutExtension(ModelPath);

    public bool MultiSpeakerDiarizationModelsAvailable
        => File.Exists(DiarizationSegmentationModelPath) && File.Exists(DiarizationEmbeddingModelPath);

    public void DisableUnsupportedFeatures()
    {
        DiarizationClusteringThreshold = Math.Clamp(DiarizationClusteringThreshold, 0.1, 1.5);
    }
}

public sealed record WhisperModelCatalogItem(
    string Id,
    string DisplayName,
    string FileName,
    string DownloadUrl,
    long ApproximateBytes)
{
    public string SizeDisplay => ApproximateBytes <= 0
        ? ""
        : ApproximateBytes >= 1024L * 1024 * 1024
            ? $"{ApproximateBytes / (1024d * 1024 * 1024):0.0} GB"
            : $"{ApproximateBytes / (1024d * 1024):0} MB";

    public string Display => string.IsNullOrWhiteSpace(SizeDisplay)
        ? DisplayName
        : $"{DisplayName} · {SizeDisplay}";
}

public static class WhisperModelCatalog
{
    public static IReadOnlyList<WhisperModelCatalogItem> Items { get; } = new[]
    {
        new WhisperModelCatalogItem(
            "tiny.en",
            "Tiny English — fastest test model",
            "ggml-tiny.en.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin",
            75L * 1024 * 1024),
        new WhisperModelCatalogItem(
            "base.en",
            "Base English — recommended starting point",
            "ggml-base.en.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin",
            142L * 1024 * 1024),
        new WhisperModelCatalogItem(
            "small.en",
            "Small English — higher accuracy",
            "ggml-small.en.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin",
            466L * 1024 * 1024),
    };
}
