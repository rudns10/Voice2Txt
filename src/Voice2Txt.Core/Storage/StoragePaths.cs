namespace Voice2Txt.Core.Storage;

/// <summary>앱이 사용하는 로컬 경로 모음. 필요한 폴더는 접근 시 생성한다.</summary>
public static class StoragePaths
{
    /// <summary>%LOCALAPPDATA%\Voice2Txt — DB/임시 파일 보관.</summary>
    public static string AppDataDir =>
        EnsureDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Voice2Txt"));

    /// <summary>SQLite 메타데이터 DB 경로.</summary>
    public static string DbPath => Path.Combine(AppDataDir, "voice2txt.db");

    /// <summary>저장 다이얼로그 전, 변환된 WAV를 임시로 두는 폴더.</summary>
    public static string StagingDir => EnsureDir(Path.Combine(AppDataDir, "staging"));

    /// <summary>다운로드한 ggml 모델 보관 폴더.</summary>
    public static string ModelsDir => EnsureDir(Path.Combine(AppDataDir, "models"));

    /// <summary>저장 위치 기본값: 내 문서\Voice2Txt\recordings.</summary>
    public static string DefaultRecordingsDir =>
        EnsureDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Voice2Txt", "recordings"));

    /// <summary>남아있는 임시 staging 파일을 정리한다(앱 시작 시 호출).</summary>
    public static void CleanStaging()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(StagingDir))
            {
                try { File.Delete(f); } catch { /* 사용 중이면 건너뜀 */ }
            }
        }
        catch { /* 무시 */ }
    }

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
