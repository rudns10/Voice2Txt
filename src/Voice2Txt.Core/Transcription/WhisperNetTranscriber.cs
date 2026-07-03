using System.Linq;
using NAudio.Wave;
using Voice2Txt.Core.Abstractions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Voice2Txt.Core.Transcription;

/// <summary>
/// [정식] Whisper.net 라이브러리 기반 변환기.
/// 런타임은 Vulkan(가속) → CPU(폴백) 순으로 로드한다. 동일 ggml 모델 사용.
/// 세그먼트 콜백/진행률 핸들러로 진행률을 보고한다.
/// </summary>
public sealed class WhisperNetTranscriber : ITranscriber, IDisposable
{
    private static bool _runtimeConfigured;
    private static readonly object RuntimeGate = new();

    private readonly object _factoryGate = new();
    private WhisperFactory? _factory;
    private string? _factoryModelPath;

    public async Task<TranscriptResult> TranscribeAsync(
        string wavPath,
        TranscribeOptions options,
        IProgress<TranscribeProgress> progress,
        CancellationToken ct)
    {
        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException($"모델 파일이 없습니다: {options.ModelPath}");
        if (!File.Exists(wavPath))
            throw new FileNotFoundException($"오디오 파일이 없습니다: {wavPath}");

        ConfigureRuntimeOnce();

        var total = TryGetDuration(wavPath);
        var factory = GetFactory(options.ModelPath);

        await using var processor = factory.CreateBuilder()
            .WithLanguage(options.Language)
            .WithThreads(options.Threads)
            // beam search: 여러 후보를 비교해 더 정확한 문장 선택(속도는 약간↓)
            .WithBeamSearchSamplingStrategy(b => b.WithBeamSize(5))
            // ── 환각(반복/무음 헛인식) 방지 ──
            // NoContext: 직전(환각) 출력이 다음 입력으로 피드백되는 반복 루프를 차단(핵심 레버)
            .WithNoContext()
            .WithNoSpeechThreshold(0.6f)   // 무음 구간 억제
            .WithEntropyThreshold(2.4f)    // 반복/저엔트로피 출력 억제
            .WithLogProbThreshold(-1.0f)   // 저신뢰 구간 억제
            .Build();

        var segments = new List<TranscriptSegment>();
        string? lastText = null;

        await using var fs = File.OpenRead(wavPath);
        await foreach (var seg in processor.ProcessAsync(fs, ct))
        {
            var text = seg.Text.Trim();
            if (text.Length == 0) continue;

            // 무음 환각 정형구("감사합니다" 등)는 위치 상관없이 통째로 제거 → 공백 처리
            if (IsHallucination(text)) continue;

            // 연속 중복 문장(같은 문구 반복 환각) 제거
            if (string.Equals(text, lastText, StringComparison.Ordinal))
                continue;
            lastText = text;

            segments.Add(new TranscriptSegment(seg.Start, seg.End, text));

            if (total > TimeSpan.Zero)
            {
                var pct = Math.Clamp(seg.End / total * 100, 0, 100);
                progress.Report(new TranscribeProgress(pct, seg.End, total));
            }
        }

        var fullText = string.Join(" ", segments.Select(s => s.Text));

        progress.Report(new TranscribeProgress(100, total, total));
        return new TranscriptResult(segments, fullText);
    }

    // Whisper가 무음/저음량 구간에서 흔히 지어내는 정형구(유튜브 자막 학습 흔적).
    private static readonly string[] HallucinationPhrases =
    {
        "감사합니다", "고맙습니다", "수고하셨습니다",
        "시청해주셔서 감사합니다", "시청해 주셔서 감사합니다",
        "구독과 좋아요 부탁드립니다", "구독 좋아요 부탁드립니다",
        "다음 영상에서 만나요", "다음 영상에서 뵙겠습니다", "다음 시간에 만나요",
        "MBC 뉴스 김종국입니다",
    };

    // 앞뒤에서 벗겨낼 기호(대시/따옴표/문장부호/공백). Whisper가 대화체에 "-"를 자주 붙인다.
    private static readonly char[] TrimChars =
        { '-', '–', '—', '.', ',', '!', '?', '…', ' ', '"', '\'', '·', '~', '(', ')', '[', ']' };

    /// <summary>텍스트 전체가 환각 정형구(또는 그 반복)면 true. 문장 속 일부는 건드리지 않는다.</summary>
    public static bool IsHallucination(string text)
    {
        var t = text.Trim(TrimChars);
        if (t.Length == 0) return false;
        foreach (var p in HallucinationPhrases)
        {
            if (t == p) return true;
            // "감사합니다 감사합니다"처럼 같은 정형구만 반복된 경우도 제거
            if (t.Length > p.Length && t.Replace(p, "").Trim(TrimChars).Length == 0) return true;
        }
        return false;
    }

    public Task WarmupAsync(string modelPath, CancellationToken ct = default)
    {
        if (!File.Exists(modelPath)) return Task.CompletedTask;
        ConfigureRuntimeOnce();
        // FromPath(모델 로드)는 동기·무거움 → 백그라운드에서 미리 캐싱.
        return Task.Run(() =>
        {
            try { GetFactory(modelPath); } catch { /* 워밍업 실패는 무시 */ }
        }, ct);
    }

    /// <summary>현재 선택된 런타임 라이브러리(가능한 경우).</summary>
    public static string RuntimeInfo
    {
        get
        {
            try { return RuntimeOptions.LoadedLibrary?.ToString() ?? "(미로드)"; }
            catch { return "(알 수 없음)"; }
        }
    }

    private static void ConfigureRuntimeOnce()
    {
        if (_runtimeConfigured) return;
        lock (RuntimeGate)
        {
            if (_runtimeConfigured) return;
            // 가속 우선순위: CUDA(NVIDIA) → Vulkan(인텔/AMD) → CPU. 로드 실패 시 다음으로 자동 폴백.
            RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary>
            {
                RuntimeLibrary.Cuda,
                RuntimeLibrary.Vulkan,
                RuntimeLibrary.Cpu
            };
            _runtimeConfigured = true;
        }
    }

    private WhisperFactory GetFactory(string modelPath)
    {
        lock (_factoryGate)
        {
            if (_factory is not null && _factoryModelPath == modelPath)
                return _factory;

            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(modelPath);
            _factoryModelPath = modelPath;
            return _factory;
        }
    }

    private static TimeSpan TryGetDuration(string wavPath)
    {
        try
        {
            using var reader = new WaveFileReader(wavPath);
            return reader.TotalTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    public void Dispose()
    {
        lock (_factoryGate)
        {
            _factory?.Dispose();
            _factory = null;
        }
    }
}
