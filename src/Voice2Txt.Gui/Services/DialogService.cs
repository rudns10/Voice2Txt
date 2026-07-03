using Microsoft.UI.Xaml.Controls;
using Voice2Txt.Gui.Views;
using Windows.Storage.Pickers;

namespace Voice2Txt.Gui.Services;

/// <inheritdoc />
public sealed class DialogService : IDialogService
{
    public async Task<SaveRecordingRequest?> ShowSaveRecordingDialogAsync(SaveRecordingRequest defaults)
    {
        var root = RequireXamlRoot();
        var dialog = new SaveRecordingDialog(defaults) { XamlRoot = root };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? dialog.GetRequest() : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string primaryText = "확인", string closeText = "취소")
    {
        // 버튼 배치: 취소=왼쪽(Primary, 안전 기본), 실행(삭제 등)=오른쪽(Secondary).
        // Secondary로 두면 Escape/바깥 클릭은 None → 실행 안 됨(오삭제 방지).
        var hasCancel = !string.IsNullOrEmpty(closeText);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = closeText,
            SecondaryButtonText = primaryText,
            DefaultButton = hasCancel ? ContentDialogButton.Primary : ContentDialogButton.Secondary,
            XamlRoot = RequireXamlRoot()
        };
        return await dialog.ShowAsync() == ContentDialogResult.Secondary;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string extension, string typeName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName
        };
        picker.FileTypeChoices.Add(typeName, new List<string> { extension });

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickAudioFileAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            ViewMode = PickerViewMode.List
        };
        foreach (var ext in new[] { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac" })
            picker.FileTypeFilter.Add(ext);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PromptTextAsync(string title, string initialValue, string placeholder)
    {
        var box = new TextBox
        {
            Text = initialValue,
            PlaceholderText = placeholder,
            SelectionStart = initialValue?.Length ?? 0
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "확인",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RequireXamlRoot()
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? box.Text?.Trim() : null;
    }

    private static Microsoft.UI.Xaml.XamlRoot RequireXamlRoot()
        => App.MainWindow?.Content?.XamlRoot
           ?? throw new InvalidOperationException("메인 창이 아직 준비되지 않았습니다.");
}
