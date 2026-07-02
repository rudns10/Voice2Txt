using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Voice2Txt.Core.Audio;

/// <summary>
/// 임의 오디오 파일(mp3/m4a/wav/wma 등)을 Whisper 입력 규격
/// (16kHz / mono / 16-bit WAV)으로 변환한다. Windows Media Foundation으로 디코드.
/// </summary>
public static class AudioImporter
{
    private const int TargetSampleRate = 16_000;

    /// <summary>변환 후 원본 길이를 반환.</summary>
    public static Task<TimeSpan> ToWhisperWavAsync(string sourcePath, string targetPath, CancellationToken ct = default)
        => Task.Run(() => Convert(sourcePath, targetPath), ct);

    private static TimeSpan Convert(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"오디오 파일이 없습니다: {sourcePath}");

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var reader = new MediaFoundationReader(sourcePath); // wav/mp3/m4a/aac/wma 디코드
        var duration = reader.TotalTime;

        ISampleProvider source = reader.ToSampleProvider();
        if (source.WaveFormat.Channels > 1)
            source = source.ToMono();
        if (source.WaveFormat.SampleRate != TargetSampleRate)
            source = new WdlResamplingSampleProvider(source, TargetSampleRate);

        WaveFileWriter.CreateWaveFile16(targetPath, source);
        return duration;
    }
}
