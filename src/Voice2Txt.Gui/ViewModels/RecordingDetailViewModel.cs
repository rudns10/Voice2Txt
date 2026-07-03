using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Voice2Txt.Core.Abstractions;
using Voice2Txt.Core.Export;
using Voice2Txt.Core.Storage;
using Voice2Txt.Core.Transcription;
using Voice2Txt.Gui.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Voice2Txt.Gui.ViewModels;

/// <summary>transcript 한 구간(표시용). 재생 중 현재 구간 강조를 위해 observable.</summary>
public sealed partial class SegmentRow : ObservableObject
{
    public string TimeLabel { get; }
    public string Text { get; }
    public TimeSpan Start { get; }
    public TimeSpan End { get; }

    [ObservableProperty] private bool _isActive;

    public SegmentRow(string timeLabel, string text, TimeSpan start, TimeSpan end)
    {
        TimeLabel = timeLabel;
        Text = text;
        Start = start;
        End = end;
    }
}

/// <summary>결과 화면 ViewModel: 재생 + 타임스탬프 transcript + 내보내기/복사/삭제/변환.</summary>
public partial class RecordingDetailViewModel : ObservableObject, IDisposable
{
    private readonly Recording _rec;
    private readonly IRecordingStore _store;
    private readonly IDialogService _dialog;
    private readonly Func<Recording, Task> _requestConvert;
    private readonly Func<Recording, Task> _onDeleted;
    private readonly DispatcherQueue _dispatcher;

    private readonly MediaPlayer _player = new();
    private readonly DispatcherQueueTimer _posTimer;
    private TranscriptResult? _transcript;
    private bool _suppressSeek;
    private bool _mediaReady;

    private SegmentRow? _activeSegment;
    /// <summary>현재 재생 위치의 구간이 바뀌면 발생(자동 스크롤용).</summary>
    public event EventHandler<SegmentRow>? ActiveSegmentChanged;

    public string Title => _rec.Name;
    public string Meta => $"{_rec.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {FormatClock(_rec.Duration)}";

    public bool IsTranscribed => _rec.IsTranscribed;
    public bool NotTranscribed => !_rec.IsTranscribed;

    public ObservableCollection<SegmentRow> Segments { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayGlyph))]
    private bool _isPlaying;

    /// <summary>재생/일시정지 아이콘(Segoe MDL2: E769 일시정지 / E768 재생).</summary>
    public string PlayGlyph => ((char)(IsPlaying ? 0xE769 : 0xE768)).ToString();

    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private string _positionText = "00:00 / 00:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnlyView))]
    [NotifyPropertyChangedFor(nameof(ShowTranscriptList))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyResult))]
    private bool _isEditing;

    [ObservableProperty] private string _editText = "";

    /// <summary>복사 직후 잠깐 표시되는 피드백("복사됨").</summary>
    [ObservableProperty] private string _copyFeedback = "";

    public bool IsReadOnlyView => !IsEditing;

    /// <summary>transcript 리스트 표시(변환됨 + 구간 있음 + 편집 아님).</summary>
    public bool ShowTranscriptList => IsTranscribed && !IsEditing && Segments.Count > 0;

    /// <summary>변환됐지만 인식된 텍스트가 없을 때(무음 등) 안내 표시.</summary>
    public bool ShowEmptyResult => IsTranscribed && !IsEditing && Segments.Count == 0;

    public RecordingDetailViewModel(
        Recording rec,
        IRecordingStore store,
        IDialogService dialog,
        Func<Recording, Task> requestConvert,
        Func<Recording, Task> onDeleted)
    {
        _rec = rec;
        _store = store;
        _dialog = dialog;
        _requestConvert = requestConvert;
        _onDeleted = onDeleted;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _player.MediaOpened += OnMediaOpened;
        _player.MediaEnded += OnMediaEnded;
        _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;

        _posTimer = _dispatcher.CreateTimer();
        _posTimer.Interval = TimeSpan.FromMilliseconds(200);
        _posTimer.Tick += OnPosTick;
    }

    /// <summary>미디어 로드 + (변환됐다면) transcript 로드.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_rec.FilePath);
            _player.Source = MediaSource.CreateFromStorageFile(file);
        }
        catch { /* 파일 없음 등 → 재생만 비활성 */ }

        if (_rec.IsTranscribed && _rec.TranscriptPath is { } tp && File.Exists(tp))
        {
            _transcript = await TranscriptFile.LoadAsync(tp);
            Segments.Clear();
            foreach (var s in _transcript.Segments)
                Segments.Add(new SegmentRow(
                    $"{(int)s.Start.TotalMinutes:00}:{s.Start.Seconds:00}", s.Text, s.Start, s.End));
            EditText = _transcript.FullText;
            OnPropertyChanged(nameof(ShowTranscriptList));
            OnPropertyChanged(nameof(ShowEmptyResult));
        }
    }

    // ── 재생 ──
    [RelayCommand]
    private void PlayPause()
    {
        if (!_mediaReady) return;
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            _player.Pause();
        else
        {
            _player.Play();
            _posTimer.Start();
        }
    }

    [RelayCommand]
    private void OpenInFolder()
    {
        try
        {
            if (!File.Exists(_rec.FilePath)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_rec.FilePath}\"",
                UseShellExecute = true
            });
        }
        catch { /* 무시 */ }
    }

    [RelayCommand]
    private void SeekToSegment(SegmentRow row)
    {
        if (!_mediaReady) return;
        _player.PlaybackSession.Position = row.Start;
        _player.Play();
        _posTimer.Start();
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _mediaReady = true;
            DurationSeconds = Math.Max(1, _player.PlaybackSession.NaturalDuration.TotalSeconds);
            UpdatePositionText();
        });
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _posTimer.Stop();
            _player.PlaybackSession.Position = TimeSpan.Zero;
            IsPlaying = false;
            _suppressSeek = true;
            PositionSeconds = 0;
            _suppressSeek = false;
            UpdatePositionText();
            ClearActiveSegment();
        });
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            IsPlaying = sender.PlaybackState == MediaPlaybackState.Playing;
            if (!IsPlaying) _posTimer.Stop();
        });
    }

    private void OnPosTick(DispatcherQueueTimer sender, object args)
    {
        var pos = _player.PlaybackSession.Position;
        _suppressSeek = true;
        PositionSeconds = pos.TotalSeconds;
        _suppressSeek = false;
        UpdatePositionText();
        UpdateActiveSegment(pos);
    }

    /// <summary>현재 재생 위치에 해당하는 구간을 강조하고, 바뀌면 스크롤 이벤트 발생.</summary>
    private void UpdateActiveSegment(TimeSpan pos)
    {
        if (Segments.Count == 0) return;

        SegmentRow? active = null;
        foreach (var s in Segments)
        {
            if (pos >= s.Start && pos < s.End) { active = s; break; }
            if (pos >= s.Start) active = s; // 구간 사이 공백이면 직전 구간 유지
        }

        if (ReferenceEquals(active, _activeSegment)) return;

        if (_activeSegment is not null) _activeSegment.IsActive = false;
        _activeSegment = active;
        if (active is not null)
        {
            active.IsActive = true;
            ActiveSegmentChanged?.Invoke(this, active);
        }
    }

    private void ClearActiveSegment()
    {
        if (_activeSegment is not null) _activeSegment.IsActive = false;
        _activeSegment = null;
    }

    partial void OnPositionSecondsChanged(double value)
    {
        if (_suppressSeek || !_mediaReady) return;
        // 사용자가 슬라이더를 움직인 경우 → 탐색
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(value);
        UpdatePositionText();
    }

    private void UpdatePositionText()
        => PositionText = $"{FormatClock(TimeSpan.FromSeconds(PositionSeconds))} / {FormatClock(TimeSpan.FromSeconds(DurationSeconds))}";

    // ── 액션 ──
    [RelayCommand]
    private async Task CopyAsync()
    {
        if (_transcript is null) return;
        var pkg = new DataPackage();
        pkg.SetText(_transcript.FullText);
        Clipboard.SetContent(pkg);

        CopyFeedback = "복사됨";
        await Task.Delay(1500);
        CopyFeedback = "";
    }

    [RelayCommand]
    private async Task ExportTxtAsync()
    {
        if (_transcript is null) return;
        var path = await _dialog.PickSaveFileAsync(_rec.Name, ".txt", "텍스트 파일");
        if (path is not null) await TxtExporter.SaveAsync(_transcript, path);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var ok = await _dialog.ConfirmAsync("삭제", $"'{_rec.Name}'을(를) 삭제할까요? 되돌릴 수 없습니다.", "삭제");
        if (!ok) return;

        StopAndRelease();
        TryDelete(_rec.FilePath);
        if (_rec.TranscriptPath is { } tp) TryDelete(tp);
        await _store.DeleteAsync(_rec.Id);
        await _onDeleted(_rec);
    }

    [RelayCommand]
    private async Task ConvertAsync() => await _requestConvert(_rec);

    [RelayCommand]
    private void ToggleEdit()
    {
        if (!IsEditing)
        {
            // 편집은 "한 줄 = 한 구간"으로 시작(저장 시 타임스탬프와 다시 짝지음)
            EditText = _transcript is { Segments.Count: > 0 }
                ? string.Join("\n", _transcript.Segments.Select(s => s.Text))
                : (_transcript?.FullText ?? "");
            IsEditing = true;
        }
    }

    /// <summary>편집을 취소하고 저장돼 있던 내용으로 되돌린다(구간/타임스탬프 유지).</summary>
    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (_transcript is null || _rec.TranscriptPath is not { } tp)
        {
            IsEditing = false;
            return;
        }

        // 편집된 각 줄을 원래 구간의 타임스탬프와 순서대로 다시 짝짓는다(타임스탬프 보존).
        // WinUI TextBox는 줄바꿈을 '\r'로 두므로 '\r'·'\n' 모두로 분리한다.
        var oldSegs = _transcript.Segments;
        var lines = EditText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var newSegs = new List<TranscriptSegment>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i < oldSegs.Count)
                newSegs.Add(new TranscriptSegment(oldSegs[i].Start, oldSegs[i].End, lines[i]));
            else
            {
                // 원래보다 줄이 늘어난 경우: 직전 구간 끝 시각을 이어 붙임
                var start = newSegs.Count > 0 ? newSegs[^1].End : TimeSpan.Zero;
                newSegs.Add(new TranscriptSegment(start, start, lines[i]));
            }
        }

        _transcript = new TranscriptResult(newSegs, string.Join(" ", lines));
        await TranscriptFile.SaveAsync(tp, _transcript);

        RebuildSegmentsView();  // 타임스탬프 목록(첫 번째 화면)으로 복귀
        IsEditing = false;
    }

    /// <summary>_transcript 구간으로 화면 목록을 다시 만든다(타임스탬프 포함).</summary>
    private void RebuildSegmentsView()
    {
        ClearActiveSegment();
        Segments.Clear();
        if (_transcript is null) return;
        foreach (var s in _transcript.Segments)
            Segments.Add(new SegmentRow(
                $"{(int)s.Start.TotalMinutes:00}:{s.Start.Seconds:00}", s.Text, s.Start, s.End));
        OnPropertyChanged(nameof(ShowTranscriptList));
        OnPropertyChanged(nameof(ShowEmptyResult));
    }

    private void StopAndRelease()
    {
        _posTimer.Stop();
        try { _player.Pause(); } catch { }
        _player.Source = null;
    }

    public void Dispose()
    {
        _posTimer.Stop();
        _player.MediaOpened -= OnMediaOpened;
        _player.MediaEnded -= OnMediaEnded;
        _player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
        try { _player.Pause(); } catch { }
        _player.Dispose();
    }

    private static string FormatClock(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
