using Voice2Txt.Core.Transcription;

namespace Voice2Txt.Core.Abstractions;

/// <summary>모델 다운로드 진행률.</summary>
public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0
        ? (double)BytesReceived / TotalBytes.Value * 100
        : null;
}

/// <summary>ggml 모델 파일 관리(존재 확인 + 다운로드).</summary>
public interface IModelManager
{
    /// <summary>모델의 로컬 경로(없어도 경로만 계산).</summary>
    string GetModelPath(WhisperModel model);

    /// <summary>모델이 이미 받아져 있는지.</summary>
    bool IsDownloaded(WhisperModel model);

    /// <summary>모델 경로를 보장한다. 없으면 다운로드 후 경로 반환.</summary>
    Task<string> EnsureModelAsync(
        WhisperModel model,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}
