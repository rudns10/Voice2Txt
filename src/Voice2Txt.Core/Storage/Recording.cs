namespace Voice2Txt.Core.Storage;

/// <summary>녹음 1건의 메타데이터.</summary>
public sealed class Recording
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>표시 이름.</summary>
    public string Name { get; set; } = "";

    /// <summary>생성 시각.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>녹음 길이.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>WAV 파일 경로.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>변환(STT) 완료 여부.</summary>
    public bool IsTranscribed { get; set; }

    /// <summary>transcript 저장 경로(W4에서 사용). 없으면 null.</summary>
    public string? TranscriptPath { get; set; }
}
