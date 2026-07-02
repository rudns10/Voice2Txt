using NAudio.Wave;
using Voice2Txt.Core.Abstractions;

namespace Voice2Txt.Core.Audio;

/// <summary>
/// NAudio WaveIn 기반 녹음 구현.
/// 마이크를 곧바로 16kHz / mono / 16-bit PCM 으로 캡처(Whisper 입력 규격)하므로
/// 정지 시 별도 리샘플 없이 그대로 저장하고, 실시간 자막용 PCM 청크도 그대로 내보낸다.
/// </summary>
public sealed class NAudioRecorder : IRecorder
{
    private static readonly WaveFormat Format = new(16_000, 16, 1);

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _tempPath;
    private volatile bool _paused;
    private readonly object _gate = new();

    public RecorderState State { get; private set; } = RecorderState.Idle;

    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return _writer?.TotalTime ?? TimeSpan.Zero;
            }
        }
    }

    public event EventHandler<RecordingLevel>? LevelAvailable;
    public event EventHandler<ReadOnlyMemory<byte>>? PcmAvailable;

    public void Start()
    {
        if (State != RecorderState.Idle)
            throw new InvalidOperationException("이미 녹음 중입니다.");

        _tempPath = Path.Combine(Path.GetTempPath(), $"v2t_{Guid.NewGuid():N}.wav");
        _waveIn = new WaveInEvent { WaveFormat = Format, BufferMilliseconds = 100 };
        _writer = new WaveFileWriter(_tempPath, Format);
        _paused = false;

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        _waveIn.StartRecording();
        State = RecorderState.Recording;
    }

    public void Pause()
    {
        if (State != RecorderState.Recording) return;
        _paused = true;
        State = RecorderState.Paused;
    }

    public void Resume()
    {
        if (State != RecorderState.Paused) return;
        _paused = false;
        State = RecorderState.Recording;
    }

    public async Task<string> StopAsync(string outputWavPath, CancellationToken ct = default)
    {
        if (State == RecorderState.Idle)
            throw new InvalidOperationException("녹음 중이 아닙니다.");

        var waveIn = _waveIn ?? throw new InvalidOperationException("캡처가 초기화되지 않았습니다.");

        var stopped = new TaskCompletionSource();
        void Handler(object? s, StoppedEventArgs e) => stopped.TrySetResult();
        waveIn.RecordingStopped += Handler;
        waveIn.StopRecording();
        await stopped.Task.WaitAsync(ct).ConfigureAwait(false);
        waveIn.RecordingStopped -= Handler;

        var tempPath = _tempPath!;
        State = RecorderState.Idle;
        _waveIn = null;
        _tempPath = null;

        // 이미 16kHz/mono/16-bit 이므로 그대로 저장(리샘플 불필요)
        await Task.Run(() =>
        {
            var dir = Path.GetDirectoryName(outputWavPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(tempPath, outputWavPath, overwrite: true);
        }, ct).ConfigureAwait(false);

        TryDelete(tempPath);
        return outputWavPath;
    }

    public void Cancel()
    {
        if (State == RecorderState.Idle) return;
        _paused = false;
        try { _waveIn?.StopRecording(); } catch { /* 무시 */ }
        State = RecorderState.Idle;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_paused) return;

        lock (_gate)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        }

        LevelAvailable?.Invoke(this, new RecordingLevel(ComputePeak(e.Buffer, e.BytesRecorded)));

        // 실시간 자막용 PCM 청크(복사본 — 버퍼 재사용 방지)
        if (PcmAvailable is not null)
        {
            var copy = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
            PcmAvailable.Invoke(this, copy);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }

        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
        }
    }

    /// <summary>16-bit PCM 버퍼의 최대 진폭(0~1).</summary>
    private static float ComputePeak(byte[] buffer, int bytes)
    {
        float peak = 0f;
        for (int i = 0; i + 2 <= bytes; i += 2)
        {
            float abs = Math.Abs(BitConverter.ToInt16(buffer, i) / 32768f);
            if (abs > peak) peak = abs;
        }
        return peak > 1f ? 1f : peak;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 임시 파일 삭제 실패는 무시 */ }
    }
}
