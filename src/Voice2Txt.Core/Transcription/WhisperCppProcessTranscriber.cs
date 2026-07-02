using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;
using Voice2Txt.Core.Abstractions;
using Voice2Txt.Core.Storage;

namespace Voice2Txt.Core.Transcription;

/// <summary>
/// [MVP] whisper.cpp(whisper-cli.exe)를 외부 프로세스로 호출하는 변환기.
/// JSON(-oj) 출력으로 세그먼트/타임스탬프를 파싱하고, stderr의 진행률(-pp)을 보고한다.
/// </summary>
public sealed partial class WhisperCppProcessTranscriber : ITranscriber
{
    private readonly string _cliPath;

    public WhisperCppProcessTranscriber(string? whisperCliPath = null)
    {
        _cliPath = whisperCliPath ?? ResolveCliPath();
    }

    [GeneratedRegex(@"progress\s*=\s*(\d+)\s*%", RegexOptions.IgnoreCase)]
    private static partial Regex ProgressRegex();

    /// <summary>외부 프로세스 방식이라 미리 로드할 상태가 없음(no-op).</summary>
    public Task WarmupAsync(string modelPath, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<TranscriptResult> TranscribeAsync(
        string wavPath,
        TranscribeOptions options,
        IProgress<TranscribeProgress> progress,
        CancellationToken ct)
    {
        if (!File.Exists(_cliPath))
            throw new FileNotFoundException($"whisper-cli.exe를 찾을 수 없습니다: {_cliPath}");
        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException($"모델 파일이 없습니다: {options.ModelPath}");
        if (!File.Exists(wavPath))
            throw new FileNotFoundException($"오디오 파일이 없습니다: {wavPath}");

        var total = TryGetDuration(wavPath);
        var outBase = Path.Combine(StoragePaths.StagingDir, $"stt_{Guid.NewGuid():N}");
        var jsonPath = outBase + ".json";

        var psi = new ProcessStartInfo
        {
            FileName = _cliPath,
            WorkingDirectory = Path.GetDirectoryName(_cliPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(options.ModelPath);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(wavPath);
        psi.ArgumentList.Add("-l"); psi.ArgumentList.Add(options.Language);
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(options.Threads.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-oj");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add(outBase);
        psi.ArgumentList.Add("-pp");

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            var m = ProgressRegex().Match(e.Data);
            if (m.Success && double.TryParse(m.Groups[1].Value, out var pct))
            {
                var processed = total > TimeSpan.Zero
                    ? TimeSpan.FromTicks((long)(total.Ticks * pct / 100.0))
                    : TimeSpan.Zero;
                progress.Report(new TranscribeProgress(pct, processed, total));
            }
        };

        if (!process.Start())
            throw new InvalidOperationException("whisper-cli 프로세스를 시작하지 못했습니다.");

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            TryDelete(jsonPath);
            throw;
        }

        if (process.ExitCode != 0)
        {
            TryDelete(jsonPath);
            var tail = Tail(stderr.ToString(), 600);
            throw new InvalidOperationException($"변환에 실패했습니다 (코드 {process.ExitCode}).\n{tail}");
        }

        progress.Report(new TranscribeProgress(100, total, total));

        var result = ParseJson(jsonPath);
        TryDelete(jsonPath);
        return result;
    }

    private static TranscriptResult ParseJson(string jsonPath)
    {
        using var stream = File.OpenRead(jsonPath);
        using var doc = JsonDocument.Parse(stream);

        var segments = new List<TranscriptSegment>();
        var full = new StringBuilder();

        if (doc.RootElement.TryGetProperty("transcription", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in arr.EnumerateArray())
            {
                var text = seg.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                text = text.Trim();
                if (text.Length == 0) continue;

                TimeSpan from = TimeSpan.Zero, to = TimeSpan.Zero;
                if (seg.TryGetProperty("offsets", out var off))
                {
                    if (off.TryGetProperty("from", out var f)) from = TimeSpan.FromMilliseconds(f.GetInt64());
                    if (off.TryGetProperty("to", out var to2)) to = TimeSpan.FromMilliseconds(to2.GetInt64());
                }

                segments.Add(new TranscriptSegment(from, to, text));
                if (full.Length > 0) full.Append(' ');
                full.Append(text);
            }
        }

        return new TranscriptResult(segments, full.ToString());
    }

    /// <summary>실행 위치(앱 출력) 또는 상위 폴더의 repo tools/에서 whisper-cli.exe를 찾는다.</summary>
    private static string ResolveCliPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "whisper-cpp", "whisper-cli.exe")
        };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            candidates.Add(Path.Combine(dir.FullName, "tools", "whisper-cpp", "Release", "whisper-cli.exe"));
            dir = dir.Parent;
        }

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
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

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* 무시 */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 무시 */ }
    }

    private static string Tail(string text, int maxChars)
        => text.Length <= maxChars ? text : text[^maxChars..];
}
