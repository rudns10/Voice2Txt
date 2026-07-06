using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Voice2Txt.Gui.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Voice2Txt.Gui.Views;

/// <summary>메인 화면(사이드바 + 작업 영역). ViewModel은 DI로 주입.</summary>
public sealed partial class MainView : UserControl
{
    public MainViewModel ViewModel { get; }

    public MainView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        IsTabStop = true; // 스페이스 단축키용 중립 포커스 대상
        Loaded += OnLoaded;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private RecordingDetailViewModel? _hookedDetail;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 화면 상태가 바뀌면 포커스를 뷰로 옮겨, 버튼이 스페이스를 가로채지 않게 한다.
        if (e.PropertyName is nameof(MainViewModel.IsBusy)
            or nameof(MainViewModel.IsPaused)
            or nameof(MainViewModel.IsDetail)
            or nameof(MainViewModel.IsConverting)
            or nameof(MainViewModel.IsIdle))
        {
            Focus(FocusState.Programmatic);
        }

        // 선택(일괄 삭제) 모드에서는 행 클릭 선택을 끈다(체크박스로만 선택).
        if (e.PropertyName == nameof(MainViewModel.IsSelectionMode))
        {
            RecordingList.SelectionMode = ViewModel.IsSelectionMode
                ? ListViewSelectionMode.None
                : ListViewSelectionMode.Single;
        }

        // 상세 VM이 바뀌면 현재 구간 자동 스크롤 이벤트를 다시 연결
        if (e.PropertyName == nameof(MainViewModel.Detail))
        {
            if (_hookedDetail is not null)
                _hookedDetail.ActiveSegmentChanged -= OnActiveSegmentChanged;
            _hookedDetail = ViewModel.Detail;
            if (_hookedDetail is not null)
                _hookedDetail.ActiveSegmentChanged += OnActiveSegmentChanged;
        }
    }

    private void OnActiveSegmentChanged(object? sender, SegmentRow row)
    {
        // 재생 중인 구간을 화면에 보이도록 스크롤
        SegmentList.ScrollIntoView(row);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void OnSegmentClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SegmentRow row)
            ViewModel.Detail?.SeekToSegmentCommand.Execute(row);
    }

    private void OnSpaceRecord(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // 텍스트 입력/콤보가 포커스면 스페이스는 그쪽으로(여기로 안 옴).
        if (ViewModel.IsBusy)
        {
            // 녹음 중: 일시정지/재개 토글 (완전 정지는 [정지] 버튼)
            ViewModel.TogglePauseCommand.Execute(null);
            args.Handled = true;
        }
        else if (ViewModel.IsIdle)
        {
            ViewModel.StartRecordingCommand.Execute(null);
            args.Handled = true;
        }
        else if (ViewModel.IsDetail && ViewModel.Detail is { } detail)
        {
            // 결과 화면: 재생/일시정지
            detail.PlayPauseCommand.Execute(null);
            args.Handled = true;
        }
    }

    private async void OnRenameItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecordingItemViewModel item)
            await ViewModel.RenameRecordingAsync(item);
    }

    private async void OnDeleteItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecordingItemViewModel item)
            await ViewModel.DeleteRecordingAsync(item);
    }

    // ── 오디오 파일 드래그&드롭 가져오기 ──
    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "오디오 가져오기";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        // 비동기 가져오기 동안 드래그 소스가 차단되지 않도록 deferral 사용
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var files = items.OfType<StorageFile>()
                .Where(f => MainViewModel.SupportedAudioExtensions
                    .Contains(f.FileType.ToLowerInvariant()))
                .Select(f => f.Path)
                .ToList();

            if (files.Count == 0)
            {
                ViewModel.StatusText = "지원하는 오디오 파일이 아닙니다 (wav·mp3·m4a·aac·wma·flac).";
                return;
            }

            foreach (var path in files)
                await ViewModel.ImportFileAsync(path);
        }
        finally { deferral.Complete(); }
    }
}
