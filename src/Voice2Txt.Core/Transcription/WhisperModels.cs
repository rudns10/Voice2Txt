namespace Voice2Txt.Core.Transcription;

/// <summary>다운로드 가능한 Whisper ggml 모델 정보.</summary>
public sealed record WhisperModel(
    string Key,
    string DisplayName,
    string FileName,
    string DownloadUrl,
    long SizeBytes)
{
    /// <summary>드롭다운 표시용: "small (q5_1) · 190MB".</summary>
    public string MenuLabel => $"{DisplayName} · {SizeBytes / 1_000_000}MB";
}

/// <summary>지원 모델 카탈로그. (Hugging Face: ggerganov/whisper.cpp)</summary>
public static class WhisperModelCatalog
{
    /// <summary>한국어 실사용 최소선. ~190MB.</summary>
    public static readonly WhisperModel Small = new(
        Key: "small-q5_1",
        DisplayName: "small (q5_1)",
        FileName: "ggml-small-q5_1.bin",
        DownloadUrl: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin",
        SizeBytes: 190_085_487);

    /// <summary>정확도 우선 선택지. ~539MB.</summary>
    public static readonly WhisperModel Medium = new(
        Key: "medium-q5_0",
        DisplayName: "medium (q5_0)",
        FileName: "ggml-medium-q5_0.bin",
        DownloadUrl: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium-q5_0.bin",
        SizeBytes: 539_212_467);

    /// <summary>최고 정확도급(turbo, 양자화). ~574MB.</summary>
    public static readonly WhisperModel LargeTurbo = new(
        Key: "large-v3-turbo-q5_0",
        DisplayName: "large-v3-turbo (q5_0)",
        FileName: "ggml-large-v3-turbo-q5_0.bin",
        DownloadUrl: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin",
        SizeBytes: 574_041_195);

    /// <summary>실시간 자막 모드. 응답성을 위해 small 파일을 사용하되 별도 항목으로 노출.</summary>
    public static readonly WhisperModel Live = new(
        Key: "live",
        DisplayName: "실시간 자막 (small)",
        FileName: "ggml-small-q5_1.bin",
        DownloadUrl: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin",
        SizeBytes: 190_085_487);

    public static readonly WhisperModel Default = Small;

    public static IReadOnlyList<WhisperModel> All { get; } = new[] { Small, Medium, LargeTurbo, Live };

    /// <summary>실시간 자막 모드 여부.</summary>
    public static bool IsLive(WhisperModel m) => m.Key == Live.Key;

    public static WhisperModel ByKey(string key)
        => All.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
           ?? throw new ArgumentException($"알 수 없는 모델 키: {key}");
}
