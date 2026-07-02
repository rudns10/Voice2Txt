using Voice2Txt.Core.Abstractions;
using Voice2Txt.Core.Storage;

namespace Voice2Txt.Core.Transcription;

/// <summary>
/// ggml 모델을 관리한다. 우선순위:
///   1) %LOCALAPPDATA%\Voice2Txt\models  (다운로드/사용자)
///   2) &lt;앱 폴더&gt;\models             (배포 시 동봉, 읽기 전용)
/// 둘 다 없으면 (1)로 다운로드. 모델은 1회 확보 후 오프라인 동작.
/// </summary>
public sealed class WhisperModelManager : IModelManager
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static string LocalPath(WhisperModel m) => Path.Combine(StoragePaths.ModelsDir, m.FileName);
    private static string BundledPath(WhisperModel m) => Path.Combine(AppContext.BaseDirectory, "models", m.FileName);

    private static bool Has(string path, WhisperModel m)
        => File.Exists(path) && (m.SizeBytes <= 0 || new FileInfo(path).Length == m.SizeBytes);

    /// <summary>이미 확보된 모델 경로(로컬 우선, 없으면 동봉). 없으면 null.</summary>
    private static string? FindExisting(WhisperModel m)
    {
        var local = LocalPath(m);
        if (Has(local, m)) return local;
        var bundled = BundledPath(m);
        if (Has(bundled, m)) return bundled;
        return null;
    }

    /// <summary>사용할 모델 경로(없으면 다운로드 대상=로컬 경로).</summary>
    public string GetModelPath(WhisperModel model) => FindExisting(model) ?? LocalPath(model);

    public bool IsDownloaded(WhisperModel model) => FindExisting(model) is not null;

    public async Task<string> EnsureModelAsync(
        WhisperModel model,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var existing = FindExisting(model);
        if (existing is not null)
            return existing;

        var path = LocalPath(model);
        var tempPath = path + ".part";
        try
        {
            using var resp = await Http.GetAsync(
                model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? model.SizeBytes;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long received = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    progress?.Report(new DownloadProgress(received, total > 0 ? total : null));
                }
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(tempPath, path);
            return path;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 무시 */ }
    }
}
