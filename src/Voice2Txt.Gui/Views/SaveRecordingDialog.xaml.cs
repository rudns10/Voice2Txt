using Microsoft.UI.Xaml.Controls;
using Voice2Txt.Gui.Services;
using Windows.Storage.Pickers;

namespace Voice2Txt.Gui.Views;

/// <summary>녹음 저장 다이얼로그(파일명/위치/변환 여부).</summary>
public sealed partial class SaveRecordingDialog : ContentDialog
{
    public SaveRecordingDialog(SaveRecordingRequest defaults)
    {
        InitializeComponent();
        NameBox.Text = defaults.Name;
        FolderBox.Text = defaults.Folder;
        TranscribeCheck.IsChecked = defaults.TranscribeNow;
    }

    /// <summary>다이얼로그 입력값을 결과로 반환.</summary>
    public SaveRecordingRequest GetRequest()
        => new(NameBox.Text.Trim(), FolderBox.Text.Trim(), TranscribeCheck.IsChecked == true);

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ShowError("파일명을 입력하세요.");
            args.Cancel = true;
            return;
        }
        if (string.IsNullOrWhiteSpace(FolderBox.Text))
        {
            ShowError("저장 위치를 선택하세요.");
            args.Cancel = true;
        }
    }

    private async void OnBrowse(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        // unpackaged 앱: 창 핸들로 picker 초기화 필요
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            FolderBox.Text = folder.Path;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }
}
