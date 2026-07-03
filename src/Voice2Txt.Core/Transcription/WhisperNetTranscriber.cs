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

        // 무음 판정용으로 원본 샘플을 한 번 읽어둔다(디코더가 무음에 지어낸 글자 차단).
        var (samples, sampleRate) = ReadSamples(wavPath);

        await using var fs = File.OpenRead(wavPath);
        await foreach (var seg in processor.ProcessAsync(fs, ct))
        {
            var text = seg.Text.Trim();
            if (text.Length == 0) continue;

            // ① 해당 구간의 실제 음량이 무음이면 = 소리가 없는데 뽑아낸 글자 → 버림(공백 처리)
            if (IsSilentRange(samples, sampleRate, seg.Start, seg.End)) continue;

            // ② 무음 환각 정형구("감사합니다"·"자막 제공·광고 포함" 등)도 제거(백업)
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

    // 구간 RMS(16-bit)가 이 값 이하이면 무음으로 간주. 조용한 실제 발화(150~)는 보존하고
    // 사실상 무음(디코더가 억지로 뽑은 구간, ≈0~수십)만 걸러내도록 보수적으로 낮게 설정.
    private const double SilenceRms = 80;

    /// <summary>16k/mono/16-bit WAV의 전체 샘플을 읽는다. 형식이 다르거나 실패하면 빈 배열(게이트 비활성).</summary>
    private static (short[] samples, int rate) ReadSamples(string wavPath)
    {
        try
        {
            using var reader = new WaveFileReader(wavPath);
            var fmt = reader.WaveFormat;
            if (fmt.BitsPerSample != 16 || fmt.Channels != 1)
                return (Array.Empty<short>(), fmt.SampleRate); // 예상 외 형식이면 게이트 미적용
            var bytes = new byte[reader.Length];
            int n = 0, r;
            while (n < bytes.Length && (r = reader.Read(bytes, n, bytes.Length - n)) > 0) n += r;
            var samples = new short[n / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
            return (samples, fmt.SampleRate);
        }
        catch { return (Array.Empty<short>(), 16000); }
    }

    /// <summary>구간 [start,end]의 RMS가 무음 임계 이하이면 true. 샘플이 없으면(못 읽음) false(안전).</summary>
    private static bool IsSilentRange(short[] samples, int rate, TimeSpan start, TimeSpan end)
    {
        if (samples.Length == 0 || rate <= 0) return false;
        int a = Math.Clamp((int)(start.TotalSeconds * rate), 0, samples.Length);
        int b = Math.Clamp((int)(end.TotalSeconds * rate), a, samples.Length);
        long count = b - a;
        if (count <= 0) return false;

        double sumSq = 0;
        for (int i = a; i < b; i++) { double s = samples[i]; sumSq += s * s; }
        return Math.Sqrt(sumSq / count) < SilenceRms;
    }

    // Whisper가 무음/저음량 구간에서 흔히 지어내는 정형구(유튜브 자막 학습 흔적).
    // 전체가 이것(또는 그 반복)일 때만 제거 — 문장 속 진짜 "감사합니다"는 보존.
    private static readonly string[] HallucinationPhrases =
    {
        "감사합니다", "고맙습니다", "수고하셨습니다",
        "시청해주셔서 감사합니다", "시청해 주셔서 감사합니다",
        "구독과 좋아요 부탁드립니다", "구독 좋아요 부탁드립니다",
        "다음 영상에서 만나요", "다음 영상에서 뵙겠습니다", "다음 시간에 만나요",
        "MBC 뉴스 김종국입니다",
    };

    // 회의/메모엔 거의 안 나오는 강한 유튜브 정형구 — 문장 어디에 섞여 있어도 그 구간 통째 제거.
    private static readonly string[] HallucinationContains =
    {
        "광고를 포함", "유료 광고", "시청해 주셔", "시청해주셔",
        "구독과 좋아요", "구독 좋아요", "좋아요 부탁", "자막 제공",
        "다음 영상에서", "다음 시간에 만나", "채널에 오신",
    };

    // 앞뒤에서 벗겨낼 기호(대시/따옴표/문장부호/공백). Whisper가 대화체에 "-"를 자주 붙인다.
    private static readonly char[] TrimChars =
        { '-', '–', '—', '.', ',', '!', '?', '…', ' ', '"', '\'', '·', '~', '(', ')', '[', ']' };

    /// <summary>환각 정형구면 true. 강한 유튜브 문구는 문장에 섞여 있어도, 나머지는 전체 일치일 때만.</summary>
    public static bool IsHallucination(string text)
    {
        var t = text.Trim(TrimChars);
        if (t.Length == 0) return false;

        // 1) 강한 유튜브 정형구가 문장 어디에든 포함되면 제거
        foreach (var m in HallucinationContains)
            if (t.Contains(m, StringComparison.Ordinal)) return true;

        // 2) 전체가 정형구(또는 그 반복)면 제거 — 대화 속 일부는 보존
        foreach (var p in HallucinationPhrases)
        {
            if (t == p) return true;
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
