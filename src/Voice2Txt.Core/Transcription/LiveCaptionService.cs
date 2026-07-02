using Voice2Txt.Core.Abstractions;
using Voice2Txt.Core.Storage;

namespace Voice2Txt.Core.Transcription;

/// <summary>
/// 녹음 중 실시간 자막 워커.
/// 16kHz mono 16-bit PCM 청크를 받아 N초씩 모아 → 임시 WAV → (전용)Whisper → TextProduced.
/// 변환용 엔진과 분리된 전용 엔진/모델을 사용해 서로 간섭하지 않는다.
/// </summary>
public sealed class LiveCaptionService : IDisposable
{
    private const int SampleRate = 16_000;
    private const int BytesPerSample = 2;

    private readonly ITranscriber _engine = new WhisperNetTranscriber(); // 라이브 전용
    private readonly int _windowSeconds;
    private readonly int _chunkBytes;
    private readonly int _threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    private readonly List<byte> _buffer = new();
    private readonly object _lock = new();

    private string _modelPath = "";
    private CancellationTokenSource? _cts;
    private Task? _runner;
    private TaskCompletionSource? _trigger;

    /// <summary>새 자막 텍스트(한 청크 결과). 캡처 외 스레드에서 발생.</summary>
    public event EventHandler<string>? TextProduced;

    public LiveCaptionService(int windowSeconds = 5)
    {
        _windowSeconds = Math.Clamp(windowSeconds, 2, 30);
        _chunkBytes = SampleRate * BytesPerSample * _windowSeconds;
    }

    public void Start(string modelPath)
    {
        if (_runner is not null) return;
        _modelPath = modelPath;
        _cts = new CancellationTokenSource();
        _ = _engine.WarmupAsync(modelPath, _cts.Token); // 모델 미리 로드
        _runner = Task.Run(() => RunAsync(_cts.Token));
    }

    public void AddPcm(ReadOnlyMemory<byte> chunk)
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        lock (_lock)
        {
            _buffer.AddRange(chunk.ToArray());
            if (_buffer.Count >= _chunkBytes)
                _trigger?.TrySetResult();
        }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _trigger?.TrySetResult();
        try { if (_runner is not null) await _runner.ConfigureAwait(false); }
        catch { /* 정리 중 예외 무시 */ }
        _runner = null;
        lock (_lock) _buffer.Clear();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var noProgress = new Progress<TranscribeProgress>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _trigger = new TaskCompletionSource();
                try { await _trigger.Task.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                byte[]? data = null;
                lock (_lock)
                {
                    if (_buffer.Count >= _chunkBytes)
                    {
                        data = _buffer.GetRange(0, _chunkBytes).ToArray();
                        _buffer.RemoveRange(0, _chunkBytes);
                    }
                }
                if (data is null) continue;

                var tempWav = Path.Combine(StoragePaths.StagingDir, $"live_{Guid.NewGuid():N}.wav");
                try
                {
                    WriteWav(tempWav, data);
                    var options = new TranscribeOptions(_modelPath, "ko", _threads);
                    var result = await _engine.TranscribeAsync(tempWav, options, noProgress, ct).ConfigureAwait(false);
                    var text = result.FullText.Trim();
                    if (text.Length > 0)
                        TextProduced?.Invoke(this, text);
                }
                catch (OperationCanceledException) { break; }
                catch { /* 한 청크 실패는 건너뜀 */ }
                finally { TryDelete(tempWav); }
            }
        }
        catch (OperationCanceledException) { /* 정상 종료 */ }
    }

    private static void WriteWav(string path, byte[] pcm)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        short channels = 1, bits = 16;
        int byteRate = SampleRate * channels * bits / 8;
        short blockAlign = (short)(channels * bits / 8);

        bw.Write(new[] { 'R', 'I', 'F', 'F' });
        bw.Write(36 + pcm.Length);
        bw.Write(new[] { 'W', 'A', 'V', 'E' });
        bw.Write(new[] { 'f', 'm', 't', ' ' });
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(SampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bits);
        bw.Write(new[] { 'd', 'a', 't', 'a' });
        bw.Write(pcm.Length);
        bw.Write(pcm);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 무시 */ }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        (_engine as IDisposable)?.Dispose();
    }
}
