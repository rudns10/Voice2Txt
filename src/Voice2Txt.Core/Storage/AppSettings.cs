using System.Text.Json;
using Voice2Txt.Core.Transcription;

namespace Voice2Txt.Core.Storage;

/// <summary>사용자 설정(영속). 현재는 변환 모델 선택만.</summary>
public sealed class AppSettings
{
    public string ModelKey { get; set; } = WhisperModelCatalog.Default.Key;

    /// <summary>테마: "System" | "Light" | "Dark".</summary>
    public string Theme { get; set; } = "System";

    // 창 크기/위치(0이면 미설정 → 기본값 사용)
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public int WindowX { get; set; }
    public int WindowY { get; set; }
}

/// <summary>설정을 JSON 파일로 로드/저장한다.</summary>
public static class AppSettingsStore
{
    private static string Path => System.IO.Path.Combine(StoragePaths.AppDataDir, "voice2txt-settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
        }
        catch { /* 손상 시 기본값 */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(settings, Options)); }
        catch { /* 무시 */ }
    }
}
