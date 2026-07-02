namespace Voice2Txt.Core.Abstractions;

/// <summary>녹음기 상태.</summary>
public enum RecorderState
{
    Idle,
    Recording,
    Paused
}

/// <summary>실시간 입력 레벨(파형 표시용). Peak는 0.0~1.0.</summary>
public readonly record struct RecordingLevel(float Peak);

/// <summary>
/// 마이크 녹음 추상화. 구현체(NAudio WASAPI)는 DI로 주입.
/// 정지 시 Whisper 입력 규격인 16kHz / mono / 16-bit WAV로 저장한다.
/// </summary>
public interface IRecorder
{
    RecorderState State { get; }

    /// <summary>실제 녹음된(일시정지 제외) 누적 시간.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>입력 레벨 갱신 이벤트(캡처 스레드에서 발생).</summary>
    event EventHandler<RecordingLevel>? LevelAvailable;

    /// <summary>녹음 중 16kHz mono 16-bit PCM 청크(실시간 자막용, 캡처 스레드).</summary>
    event EventHandler<ReadOnlyMemory<byte>>? PcmAvailable;

    /// <summary>기본 캡처 장치로 녹음을 시작한다.</summary>
    void Start();

    /// <summary>녹음을 일시정지한다(캡처는 유지, 기록만 중단).</summary>
    void Pause();

    /// <summary>일시정지된 녹음을 재개한다.</summary>
    void Resume();

    /// <summary>
    /// 녹음을 종료하고 16kHz/mono/16-bit WAV로 저장한 뒤 저장 경로를 반환한다.
    /// </summary>
    Task<string> StopAsync(string outputWavPath, CancellationToken ct = default);

    /// <summary>녹음을 저장하지 않고 즉시 중단·정리한다(앱 종료 등).</summary>
    void Cancel();
}
