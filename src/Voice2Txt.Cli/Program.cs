using Voice2Txt.Core;
using Voice2Txt.Core.Abstractions;
using Voice2Txt.Core.Transcription;

// 사용법:
//   Voice2Txt.Cli                              → 정보 출력
//   Voice2Txt.Cli transcribe <wav> [--model small|medium] [--lang ko] [--threads N]
//   엔진/모델 다운로드/변환을 GUI 없이 검증하는 용도.

IAppInfo appInfo = new AppInfo();

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine($"Voice2Txt CLI v{appInfo.Version} — {appInfo.Description}");
    Console.WriteLine("사용법: transcribe <wav> [--model small|medium] [--lang ko] [--threads N]");
    return 0;
}

if (args[0] == "transcribe")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("오류: 입력 WAV 경로가 필요합니다.");
        return 1;
    }

    var wav = args[1];
    var modelKey = GetOption(args, "--model", "small");
    var lang = GetOption(args, "--lang", "ko");
    var engine = GetOption(args, "--engine", "cpp");   // cpp | net
    var threads = int.TryParse(GetOption(args, "--threads", "4"), out var t) ? t : 4;

    // small / medium / large(-v3-turbo) 등 카탈로그 키(또는 접두어)로 선택
    var model = WhisperModelCatalog.All.FirstOrDefault(m =>
                    m.Key.StartsWith(modelKey, StringComparison.OrdinalIgnoreCase))
                ?? WhisperModelCatalog.Small;

    var modelManager = new WhisperModelManager();
    ITranscriber transcriber = engine.Equals("net", StringComparison.OrdinalIgnoreCase)
        ? new WhisperNetTranscriber()
        : new WhisperCppProcessTranscriber();

    Console.WriteLine($"엔진: {engine} / 모델: {model.DisplayName} / 언어: {lang} / 스레드: {threads}");

    if (!modelManager.IsDownloaded(model))
    {
        Console.WriteLine($"모델 다운로드 중… ({model.SizeBytes / 1_000_000}MB)");
        var dlProgress = new Progress<DownloadProgress>(p =>
        {
            if (p.Percent is { } pct)
                Console.Write($"\r  {pct,5:0.0}%   ");
        });
        await modelManager.EnsureModelAsync(model, dlProgress);
        Console.WriteLine("\r  완료        ");
    }

    var modelPath = modelManager.GetModelPath(model);
    var options = new TranscribeOptions(modelPath, lang, threads);

    var sttProgress = new Progress<TranscribeProgress>(p =>
        Console.Write($"\r변환 중… {p.Percent,5:0.0}%   "));

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await transcriber.TranscribeAsync(wav, options, sttProgress, CancellationToken.None);
    sw.Stop();

    Console.WriteLine($"\r변환 완료 ({sw.Elapsed.TotalSeconds:0.0}s)        ");
    if (engine.Equals("net", StringComparison.OrdinalIgnoreCase))
        Console.WriteLine($"Whisper.net 로드 런타임: {WhisperNetTranscriber.RuntimeInfo}");
    Console.WriteLine(new string('-', 50));
    foreach (var seg in result.Segments)
        Console.WriteLine($"[{seg.Start:hh\\:mm\\:ss} → {seg.End:hh\\:mm\\:ss}] {seg.Text}");
    Console.WriteLine(new string('-', 50));
    Console.WriteLine($"전체 {result.Segments.Count}개 구간, {result.FullText.Length}자");
    return 0;
}

Console.Error.WriteLine($"알 수 없는 명령: {args[0]}");
return 1;

static string GetOption(string[] args, string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
