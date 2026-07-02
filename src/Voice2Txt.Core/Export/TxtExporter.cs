using Voice2Txt.Core.Abstractions;

namespace Voice2Txt.Core.Export;

/// <summary>transcript를 일반 텍스트(.txt)로 내보낸다.</summary>
public static class TxtExporter
{
    public static async Task SaveAsync(TranscriptResult result, string path, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, result.FullText, ct);
    }
}
