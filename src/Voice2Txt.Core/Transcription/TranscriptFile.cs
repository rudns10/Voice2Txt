using System.Text.Json;
using Voice2Txt.Core.Abstractions;

namespace Voice2Txt.Core.Transcription;

/// <summary>변환 결과를 JSON 파일로 저장/로드(W4 결과 화면·내보내기에서 사용).</summary>
public static class TranscriptFile
{
    private sealed record SegmentDto(double StartMs, double EndMs, string Text);
    private sealed record TranscriptDto(IReadOnlyList<SegmentDto> Segments, string FullText);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task SaveAsync(string path, TranscriptResult result, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var dto = new TranscriptDto(
            result.Segments
                .Select(s => new SegmentDto(s.Start.TotalMilliseconds, s.End.TotalMilliseconds, s.Text))
                .ToList(),
            result.FullText);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, dto, Options, ct);
    }

    public static async Task<TranscriptResult> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var dto = await JsonSerializer.DeserializeAsync<TranscriptDto>(stream, Options, ct)
                  ?? new TranscriptDto(Array.Empty<SegmentDto>(), "");

        var segments = dto.Segments
            .Select(s => new TranscriptSegment(
                TimeSpan.FromMilliseconds(s.StartMs),
                TimeSpan.FromMilliseconds(s.EndMs),
                s.Text))
            .ToList();

        return new TranscriptResult(segments, dto.FullText);
    }
}
