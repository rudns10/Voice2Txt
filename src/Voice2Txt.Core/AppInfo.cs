namespace Voice2Txt.Core;

/// <summary>앱 기본 정보. W0에서 DI 연결 확인용으로 사용.</summary>
public interface IAppInfo
{
    string Version { get; }
    string Description { get; }
}

/// <inheritdoc />
public sealed class AppInfo : IAppInfo
{
    public string Version => "0.0.1";
    public string Description => "오프라인 음성→텍스트 (Whisper)";
}
