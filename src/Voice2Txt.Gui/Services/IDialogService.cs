namespace Voice2Txt.Gui.Services;

/// <summary>저장 다이얼로그 입력/결과.</summary>
public record SaveRecordingRequest(string Name, string Folder, bool TranscribeNow);

/// <summary>ViewModel이 UI에 의존하지 않도록 다이얼로그를 추상화한 서비스.</summary>
public interface IDialogService
{
    /// <summary>저장 다이얼로그를 띄운다. 취소 시 null 반환.</summary>
    Task<SaveRecordingRequest?> ShowSaveRecordingDialogAsync(SaveRecordingRequest defaults);

    /// <summary>확인/취소 다이얼로그. 확인 시 true.</summary>
    Task<bool> ConfirmAsync(string title, string message, string primaryText = "확인", string closeText = "취소");

    /// <summary>파일 저장 위치를 선택한다. 취소 시 null.</summary>
    /// <param name="extension">".txt" 처럼 점 포함 확장자.</param>
    Task<string?> PickSaveFileAsync(string suggestedName, string extension, string typeName);

    /// <summary>변환할 오디오 파일을 선택한다. 취소 시 null.</summary>
    Task<string?> PickAudioFileAsync();

    /// <summary>텍스트 입력 다이얼로그. 취소 시 null.</summary>
    Task<string?> PromptTextAsync(string title, string initialValue, string placeholder);
}
