namespace Voice2Txt.Core.Abstractions;

/// <summary>
/// 음성(WAV) → 텍스트 변환 엔진의 추상화.
/// GUI/CLI는 이 인터페이스만 알고, 구현체(whisper.cpp / Whisper.net)는 DI로 주입받는다.
/// </summary>
public interface ITranscriber
{
    /// <summary>WAV 파일을 텍스트로 변환한다. 모든 I/O는 비동기 + 취소 지원.</summary>
    Task<TranscriptResult> TranscribeAsync(
        string wavPath,
        TranscribeOptions options,
        IProgress<TranscribeProgress> progress,
        CancellationToken ct);

    /// <summary>엔진/모델을 미리 로드해 첫 변환을 빠르게 한다(선택적 최적화, 실패해도 무방).</summary>
    Task WarmupAsync(string modelPath, CancellationToken ct = default);
}

/// <summary>변환 옵션: 모델 경로, 언어, 스레드 수.</summary>
public record TranscribeOptions(string ModelPath, string Language = "ko", int Threads = 4);

/// <summary>타임스탬프가 붙은 transcript 한 구간.</summary>
public record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>변환 결과: 구간 목록 + 전체 텍스트.</summary>
public record TranscriptResult(IReadOnlyList<TranscriptSegment> Segments, string FullText);

/// <summary>변환 진행률 보고.</summary>
public record TranscribeProgress(double Percent, TimeSpan Processed, TimeSpan Total);
