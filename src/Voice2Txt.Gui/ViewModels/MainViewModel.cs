using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Voice2Txt.Core.Abstractions;
using Voice2Txt.Core.Audio;
using Voice2Txt.Core.Storage;
using Voice2Txt.Core.Transcription;
using Voice2Txt.Gui.Services;

namespace Voice2Txt.Gui.ViewModels;

/// <summary>
/// 메인 화면 ViewModel. 대기↔녹음중 상태, 녹음 명령/타이머/파형,
/// 그리고 저장 다이얼로그 → SQLite 저장 → 사이드바 목록/검색을 관리한다.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const int WaveBarCount = 44;  // 파형 막대 개수
    private const double WaveMinHeight = 3;

    private double _wavePhase;             // 진행성 파동 위상
    private float _level;                  // 입력 레벨(부드럽게 보간)

    private readonly IRecorder _recorder;
    private readonly IRecordingStore _store;
    private readonly IDialogService _dialog;
    private readonly IModelManager _models;
    private readonly ITranscriber _transcriber;
    private readonly LiveCaptionService _live;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;

    private readonly List<RecordingItemViewModel> _all = new();
    private volatile float _lastPeak;
    private CancellationTokenSource? _convertCts;
    private bool _liveActive;

    // 변환 스레드(코어 절반, 2~8)
    private static readonly int ConvertThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>녹음 또는 일시정지 중(작업 영역 = 녹음 화면).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsDetail))]
    [NotifyPropertyChangedFor(nameof(ShowLiveCaptions))]
    private bool _isBusy;

    // ── 실시간 자막 ── (독립 토글로 켜고 끔. 모델 선택과 무관)
    [ObservableProperty]
    private string _liveText = "";

    /// <summary>실시간 자막 사용 여부(사용자 토글, 설정에 저장).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLiveCaptions))]
    private bool _liveCaptions;

    /// <summary>녹음 화면에서 실시간 자막 영역 표시 여부.</summary>
    public bool ShowLiveCaptions => IsBusy && LiveCaptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonText))]
    private bool _isPaused;

    [ObservableProperty]
    private string _elapsedText = "00:00";

    [ObservableProperty]
    private string _statusText = "녹음할 준비가 되었습니다.";

    [ObservableProperty]
    private string _searchText = "";

    // ── 변환 중 화면 ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsDetail))]
    private bool _isConverting;

    // ── 결과/상세 화면 ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsDetail))]
    private RecordingItemViewModel? _selectedItem;

    [ObservableProperty]
    private RecordingDetailViewModel? _detail;

    // ── 일괄 삭제(다중 선택 모드) ──
    /// <summary>다중 선택(일괄 삭제) 모드 여부.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsDetail))]
    private bool _isSelectionMode;

    /// <summary>현재 체크된 항목 수.</summary>
    public int CheckedCount => _all.Count(i => i.IsChecked);

    /// <summary>"삭제 (n)" 버튼 라벨.</summary>
    public string SelectionDeleteLabel => CheckedCount > 0 ? $"삭제 ({CheckedCount})" : "삭제";

    /// <summary>플로팅 바 좌측 안내 텍스트.</summary>
    public string SelectionCountText => CheckedCount > 0 ? $"{CheckedCount}개 선택됨" : "삭제할 항목을 체크하세요";

    /// <summary>현재 보이는 목록이 전부 체크됐는지.</summary>
    public bool IsAllChecked => Recordings.Count > 0 && Recordings.All(i => i.IsChecked);

    /// <summary>"전체 선택 / 선택 해제" 토글 버튼 라벨.</summary>
    public string SelectAllLabel => IsAllChecked ? "선택 해제" : "전체 선택";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConvertProgressText))]
    private double _convertPercent;

    [ObservableProperty]
    private string _convertStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConvertProgressText))]
    private string _convertDetail = "";

    /// <summary>"42% · 00:21 / 00:50" 형태.</summary>
    public string ConvertProgressText =>
        ConvertDetail.Length > 0 ? $"{ConvertPercent:0}% · {ConvertDetail}" : $"{ConvertPercent:0}%";

    /// <summary>선택 가능한 변환 모델 목록.</summary>
    public IReadOnlyList<WhisperModel> AvailableModels { get; } = WhisperModelCatalog.All;

    /// <summary>현재 선택된 변환 모델(설정에 저장됨).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConvertEngineLabel))]
    private WhisperModel _selectedModel = WhisperModelCatalog.Default;

    /// <summary>"Whisper small (q5_1)" 표시.</summary>
    public string ConvertEngineLabel => $"Whisper {SelectedModel.DisplayName}";

    /// <summary>대기 화면(녹음 전) 표시 여부.</summary>
    public bool IsIdle => !IsBusy && !IsConverting && !IsDetail;

    /// <summary>결과/상세 화면 표시 여부. 선택 모드에서는 내비게이션을 억제한다.</summary>
    public bool IsDetail => !IsBusy && !IsConverting && !IsSelectionMode && SelectedItem is not null;

    /// <summary>일시정지/재개 버튼 라벨.</summary>
    public string PauseButtonText => IsPaused ? "재개" : "일시정지";

    /// <summary>파형 막대 목록.</summary>
    public ObservableCollection<WaveBar> WaveBars { get; } = new();

    /// <summary>사이드바에 표시되는(검색 필터 적용된) 녹음 목록.</summary>
    public ObservableCollection<RecordingItemViewModel> Recordings { get; } = new();

    /// <summary>목록이 비었을 때 안내문구 표시 여부.</summary>
    [ObservableProperty]
    private bool _showListPlaceholder;

    /// <summary>목록 안내문구(빈 목록/검색 결과 없음).</summary>
    [ObservableProperty]
    private string _listPlaceholder = "";

    public MainViewModel(
        IRecorder recorder,
        IRecordingStore store,
        IDialogService dialog,
        IModelManager models,
        ITranscriber transcriber,
        LiveCaptionService live)
    {
        _recorder = recorder;
        _store = store;
        _dialog = dialog;
        _models = models;
        _transcriber = transcriber;
        _live = live;

        // 저장된 설정 복원(저장 트리거 방지 위해 백킹필드 직접 설정)
        var settings = AppSettingsStore.Load();
        _selectedModel = WhisperModelCatalog.All.FirstOrDefault(m => m.Key == settings.ModelKey)
                         ?? WhisperModelCatalog.Default;
        _liveCaptions = settings.LiveCaptions;

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _recorder.LevelAvailable += (_, level) => _lastPeak = level.Peak;

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(55);
        _timer.Tick += OnTick;
    }

    /// <summary>시작 시 호출: DB 준비 + 기존 목록 로드.</summary>
    public async Task LoadAsync()
    {
        try
        {
            await _store.InitializeAsync();
            var all = await _store.GetAllAsync();
            _all.Clear();
            foreach (var r in all)
            {
                var item = new RecordingItemViewModel(r);
                item.PropertyChanged += OnItemPropertyChanged;
                _all.Add(item);
            }
            IsSelectionMode = false;   // 목록이 바뀌면 선택 모드 해제
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText = $"목록을 불러오지 못했습니다: {ex.Message}";
        }

        WarmupEngine(); // 모델이 있으면 백그라운드로 미리 로드
    }

    private CancellationTokenSource? _warmupCts;

    /// <summary>선택 모델이 다운로드돼 있으면 백그라운드로 엔진을 미리 로드(첫 변환 가속).</summary>
    private void WarmupEngine()
    {
        if (!_models.IsDownloaded(SelectedModel)) return;
        _warmupCts?.Cancel();
        _warmupCts = new CancellationTokenSource();
        var path = _models.GetModelPath(SelectedModel);
        _ = _transcriber.WarmupAsync(path, _warmupCts.Token);
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordingItemViewModel.IsChecked))
            NotifyCheckedChanged();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedModelChanged(WhisperModel value)
    {
        var s = AppSettingsStore.Load();
        s.ModelKey = value.Key;
        AppSettingsStore.Save(s);
        WarmupEngine(); // 바뀐 모델로 미리 로드
    }

    partial void OnLiveCaptionsChanged(bool value)
    {
        var s = AppSettingsStore.Load();
        s.LiveCaptions = value;
        AppSettingsStore.Save(s);
    }

    /// <summary>목록 선택 해제 → 대기/녹음 메인 화면으로 복귀.</summary>
    [RelayCommand]
    private void NewRecording()
    {
        SelectedItem = null;
        StatusText = "녹음할 준비가 되었습니다.";
    }

    /// <summary>오디오 파일을 불러와 16kHz WAV로 변환·등록 후 바로 STT.</summary>
    /// <summary>가져오기 가능한 오디오 확장자(파일 선택/드래그&드롭 공용).</summary>
    public static readonly string[] SupportedAudioExtensions =
        { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac" };

    [RelayCommand]
    private async Task ImportAudioAsync()
    {
        var source = await _dialog.PickAudioFileAsync();
        if (source is null) return;
        await ImportFileAsync(source);
    }

    /// <summary>주어진 오디오 파일을 16kHz WAV로 변환·등록 후 바로 STT한다(버튼/드래그&드롭 공용).</summary>
    public async Task ImportFileAsync(string source)
    {
        // 녹음/변환 중에는 무시(화면 충돌 방지)
        if (IsBusy || IsConverting) return;

        StatusText = "오디오 가져오는 중…";
        Recording rec;
        try
        {
            var originalName = Path.GetFileNameWithoutExtension(source);
            var targetWav = UniquePath(StoragePaths.DefaultRecordingsDir, SanitizeFileName(originalName));
            // 중복 이름이면 파일명이 "이름 (1)", "이름 (2)"… 로 붙으므로 표시 이름도 그걸 따른다.
            var name = Path.GetFileNameWithoutExtension(targetWav);
            var duration = await AudioImporter.ToWhisperWavAsync(source, targetWav);

            rec = new Recording
            {
                Name = name,
                CreatedAt = DateTimeOffset.Now,
                Duration = duration,
                FilePath = targetWav
            };
            await _store.AddAsync(rec);
            await LoadAsync();
            // 자동 변환하지 않음 — 목록에만 추가. 변환은 사용자가 항목 선택 후 직접.
            StatusText = $"가져옴: {rec.Name} · 변환하려면 목록에서 선택하세요";
        }
        catch (Exception ex)
        {
            StatusText = $"불러오기 실패: {ex.Message}";
        }
    }

    partial void OnSelectedItemChanged(RecordingItemViewModel? value)
    {
        // 이전 상세 정리(재생 중지/리소스 해제)
        Detail?.Dispose();

        // 선택(일괄 삭제) 모드에서는 상세 화면으로 넘어가지 않는다.
        if (value is null || IsSelectionMode)
        {
            Detail = null;
            return;
        }

        var vm = new RecordingDetailViewModel(
            value.Model, _store, _dialog,
            requestConvert: ConvertRecordingAsync,
            onDeleted: OnRecordingDeletedAsync);
        Detail = vm;
        _ = vm.InitializeAsync();
    }

    private async Task OnRecordingDeletedAsync(Recording rec)
    {
        SelectedItem = null;            // 상세 닫기 + Detail dispose
        await LoadAsync();
        StatusText = $"삭제됨: {rec.Name}";
    }

    partial void OnIsSelectionModeChanged(bool value)
    {
        if (value)
            SelectedItem = null;        // 상세 닫고 사이드바로
        else
            ClearChecks();              // 모드 종료 시 체크 해제

        foreach (var i in _all) i.SelectionMode = value;  // 각 항목 체크박스 표시 토글
    }

    private void ClearChecks()
    {
        foreach (var i in _all) i.IsChecked = false;
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(SelectionDeleteLabel));
    }

    /// <summary>선택(일괄 삭제) 모드 진입/종료 토글.</summary>
    [RelayCommand]
    private void ToggleSelectionMode() => IsSelectionMode = !IsSelectionMode;

    /// <summary>체크된 항목을 한 번에 삭제.</summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var targets = _all.Where(i => i.IsChecked).ToList();
        if (targets.Count == 0)
        {
            StatusText = "선택된 항목이 없습니다.";
            return;
        }

        var ok = await _dialog.ConfirmAsync(
            "선택 삭제", $"선택한 {targets.Count}개 녹음을 삭제할까요? 되돌릴 수 없습니다.", "삭제");
        if (!ok) return;

        var n = await DeleteItemsAsync(targets);
        IsSelectionMode = false;
        StatusText = $"{n}개 삭제됨";
    }

    /// <summary>현재 보이는 목록을 전체 선택 ↔ 선택 해제 토글.</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        var check = !IsAllChecked;            // 전부 체크돼 있으면 해제, 아니면 전체 선택
        foreach (var item in Recordings)
            item.IsChecked = check;
        NotifyCheckedChanged();
    }

    /// <summary>여러 항목의 파일 + transcript + 메타데이터를 삭제하고 목록을 새로고침한다. (확인창은 호출부에서 1회)</summary>
    private async Task<int> DeleteItemsAsync(IReadOnlyList<RecordingItemViewModel> items)
    {
        // 삭제 대상 중 상세가 열려 있으면 닫는다(재생 중지).
        if (SelectedItem is { } sel && items.Any(i => i.Id == sel.Id))
            SelectedItem = null;

        var deleted = 0;
        foreach (var item in items)
        {
            try
            {
                TryDelete(item.Model.FilePath);
                if (item.Model.TranscriptPath is { } tp) TryDelete(tp);
                await _store.DeleteAsync(item.Model.Id);
                deleted++;
            }
            catch (Exception ex)
            {
                StatusText = $"일부 삭제 실패: {ex.Message}";
            }
        }

        await LoadAsync();
        return deleted;
    }

    /// <summary>목록 항목 체크 상태가 바뀌면 버튼 라벨/개수를 갱신하기 위해 호출.</summary>
    public void NotifyCheckedChanged()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(SelectionDeleteLabel));
        OnPropertyChanged(nameof(SelectionCountText));
        OnPropertyChanged(nameof(IsAllChecked));
        OnPropertyChanged(nameof(SelectAllLabel));
    }

    /// <summary>목록 항목 이름 수정(메타데이터만 변경, 파일은 유지).</summary>
    public async Task RenameRecordingAsync(RecordingItemViewModel item)
    {
        var newName = await _dialog.PromptTextAsync("이름 수정", item.Name, "녹음 이름");
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;

        item.Model.Name = newName;
        await _store.UpdateAsync(item.Model);

        var keepId = SelectedItem?.Id;
        await LoadAsync();
        if (keepId is not null)
            SelectedItem = _all.FirstOrDefault(i => i.Id == keepId);
        StatusText = $"이름 변경됨: {newName}";
    }

    /// <summary>목록 항목 삭제(파일 + 메타데이터).</summary>
    public async Task DeleteRecordingAsync(RecordingItemViewModel item)
    {
        var ok = await _dialog.ConfirmAsync("삭제", $"'{item.Name}'을(를) 삭제할까요? 되돌릴 수 없습니다.", "삭제");
        if (!ok) return;

        if (SelectedItem?.Id == item.Id)
            SelectedItem = null;        // 상세 닫기 + Detail dispose(재생 중지)

        TryDelete(item.Model.FilePath);
        if (item.Model.TranscriptPath is { } tp) TryDelete(tp);
        await _store.DeleteAsync(item.Model.Id);
        await LoadAsync();
        StatusText = $"삭제됨: {item.Name}";
    }

    private void ApplyFilter()
    {
        var q = SearchText?.Trim() ?? "";
        Recordings.Clear();
        foreach (var item in _all)
        {
            if (q.Length == 0 || item.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                Recordings.Add(item);
        }

        ShowListPlaceholder = Recordings.Count == 0;
        ListPlaceholder = q.Length > 0
            ? "검색 결과가 없습니다."
            : "녹음이 없습니다.\n녹음하거나 오디오 파일을 불러오세요.";
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        ElapsedText = Format(_recorder.Elapsed);

        if (IsPaused) return;

        // 입력 레벨을 부드럽게 따라가고, 위상을 전진시켜 "파도"를 흐르게 한다.
        _level += (_lastPeak - _level) * 0.30f;
        _wavePhase += 0.38;

        // 조용해도 잔잔히 출렁(기본 진폭), 말하면 크게 일렁임. (게인으로 반응 강조)
        double lv = Math.Min(1.0, _level * 3.5f);
        double amp = 6 + lv * 52;
        bool loud = _lastPeak > 0.08f;

        for (int i = 0; i < WaveBars.Count; i++)
        {
            double env = 0.5 + 0.5 * Math.Sin(_wavePhase + i * 0.45);
            var bar = WaveBars[i];
            bar.Height = WaveMinHeight + amp * env;
            bar.Hot = loud && env > 0.55;
        }
    }

    [RelayCommand]
    private void StartRecording()
    {
        try
        {
            WaveBars.Clear();
            for (int i = 0; i < WaveBarCount; i++)
                WaveBars.Add(new WaveBar(WaveMinHeight, false));
            _lastPeak = 0f;
            _level = 0f;
            _wavePhase = 0;
            LiveText = "";

            StartLiveIfEnabled();   // 녹음 시작 전에 PCM 구독 연결

            _recorder.Start();
            IsBusy = true;
            IsPaused = false;
            StatusText = "녹음 중…";
            _timer.Start();
        }
        catch (Exception ex)
        {
            IsBusy = false;
            _ = StopLiveAsync();
            StatusText = $"녹음을 시작할 수 없습니다: {ex.Message}";
        }
    }

    /// <summary>라이브 자막이 켜져있고 모델이 있으면 실시간 변환을 시작한다.</summary>
    private void StartLiveIfEnabled()
    {
        if (!LiveCaptions) return;
        // 실시간 자막도 선택한 변환 모델을 사용(small=빠름 / medium·large=정확하지만 지연 가능)
        var liveModel = SelectedModel;
        if (!_models.IsDownloaded(liveModel))
        {
            StatusText = $"실시간 자막용 모델({liveModel.DisplayName})이 없어 자막은 생략됩니다. (해당 모델로 변환 1회 시 자동 다운로드)";
            return;
        }

        _recorder.PcmAvailable += OnPcm;
        _live.TextProduced += OnLiveText;
        _live.Start(_models.GetModelPath(liveModel));
        _liveActive = true;
    }

    private async Task StopLiveAsync()
    {
        if (!_liveActive) return;
        _liveActive = false;
        _recorder.PcmAvailable -= OnPcm;
        _live.TextProduced -= OnLiveText;
        await _live.StopAsync();
    }

    private void OnPcm(object? sender, ReadOnlyMemory<byte> pcm) => _live.AddPcm(pcm);

    private void OnLiveText(object? sender, string text)
    {
        _dispatcher.TryEnqueue(() =>
            LiveText = string.IsNullOrEmpty(LiveText) ? text : LiveText + " " + text);
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_recorder.State == RecorderState.Recording)
        {
            _recorder.Pause();
            IsPaused = true;
            StatusText = "일시정지됨";
        }
        else if (_recorder.State == RecorderState.Paused)
        {
            _recorder.Resume();
            IsPaused = false;
            StatusText = "녹음 중…";
        }
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        _timer.Stop();
        var duration = _recorder.Elapsed;

        // 1) 캡처 종료 + 변환된 WAV를 임시 폴더에 저장
        var staging = Path.Combine(StoragePaths.StagingDir, $"staging_{Guid.NewGuid():N}.wav");
        StatusText = "처리 중…";
        await StopLiveAsync(); // 실시간 자막 종료
        try
        {
            await _recorder.StopAsync(staging);
        }
        catch (Exception ex)
        {
            StatusText = $"녹음 저장 실패: {ex.Message}";
            ResetToIdle();
            return;
        }

        ResetToIdle();

        // 너무 짧은 녹음(1초 미만)은 저장하지 않고 폐기
        if (duration < TimeSpan.FromSeconds(1))
        {
            TryDelete(staging);
            StatusText = "녹음이 너무 짧아 저장하지 않았습니다 (1초 미만).";
            return;
        }

        // 2) 저장 다이얼로그
        var defaultName = $"녹음 {DateTime.Now:yyyy-MM-dd HH-mm}";
        var req = await _dialog.ShowSaveRecordingDialogAsync(
            new SaveRecordingRequest(defaultName, StoragePaths.DefaultRecordingsDir, false));

        if (req is null)
        {
            TryDelete(staging);
            StatusText = "저장이 취소되었습니다.";
            return;
        }

        // 3) 최종 위치로 이동 + 메타데이터 기록
        try
        {
            Directory.CreateDirectory(req.Folder);
            var finalPath = UniquePath(req.Folder, SanitizeFileName(req.Name));
            File.Move(staging, finalPath);

            var rec = new Recording
            {
                Name = req.Name,
                CreatedAt = DateTimeOffset.Now,
                Duration = duration,
                FilePath = finalPath
            };
            await _store.AddAsync(rec);

            _all.Insert(0, new RecordingItemViewModel(rec));
            ApplyFilter();
            StatusText = $"저장됨: {rec.Name}";

            // 4) "지금 변환" 선택 시 바로 변환
            if (req.TranscribeNow)
                await ConvertRecordingAsync(rec);
        }
        catch (Exception ex)
        {
            StatusText = $"저장 실패: {ex.Message}";
            TryDelete(staging);
        }
    }

    /// <summary>저장된 녹음을 변환한다(모델 없으면 다운로드 → STT → transcript 저장).</summary>
    public async Task ConvertRecordingAsync(Recording rec)
    {
        _convertCts = new CancellationTokenSource();
        var ct = _convertCts.Token;

        IsConverting = true;
        ConvertPercent = 0;
        ConvertStatus = "준비 중…";
        ConvertDetail = "";

        try
        {
            var model = SelectedModel;

            // 1) 모델 보장(없으면 다운로드)
            if (!_models.IsDownloaded(model))
            {
                ConvertStatus = $"모델 다운로드 중… ({model.DisplayName})";
                var dl = new Progress<DownloadProgress>(p =>
                {
                    if (p.Percent is { } pct)
                    {
                        ConvertPercent = pct;
                        ConvertDetail = $"{p.BytesReceived / 1_000_000}MB / {(p.TotalBytes ?? 0) / 1_000_000}MB";
                    }
                });
                await _models.EnsureModelAsync(model, dl, ct);
            }

            var modelPath = _models.GetModelPath(model);

            // 2) 변환
            ConvertStatus = "변환 중…";
            ConvertPercent = 0;
            var stt = new Progress<TranscribeProgress>(p =>
            {
                ConvertPercent = p.Percent;
                ConvertDetail = p.Total > TimeSpan.Zero
                    ? $"{Format(p.Processed)} / {Format(p.Total)}"
                    : "";
            });

            var options = new TranscribeOptions(modelPath, "ko", ConvertThreads);
            // 무거운 네이티브 변환을 백그라운드 스레드에서 실행(UI 멈춤 방지).
            // 진행률(Progress<T>)은 UI 스레드로 자동 마샬링됨.
            var result = await Task.Run(() => _transcriber.TranscribeAsync(rec.FilePath, options, stt, ct), ct);

            // 3) transcript 저장 + 메타데이터 갱신
            var transcriptPath = Path.ChangeExtension(rec.FilePath, ".transcript.json");
            await TranscriptFile.SaveAsync(transcriptPath, result, ct);

            rec.IsTranscribed = true;
            rec.TranscriptPath = transcriptPath;
            await _store.UpdateAsync(rec, ct);

            await LoadAsync(); // 목록 배지 갱신
            StatusText = $"변환 완료: {rec.Name} ({result.Segments.Count}개 구간)";

            // 변환된 항목을 선택해 결과 화면으로 전환
            SelectedItem = _all.FirstOrDefault(i => i.Id == rec.Id);
        }
        catch (OperationCanceledException)
        {
            StatusText = "변환이 취소되었습니다.";
        }
        catch (Exception ex)
        {
            StatusText = $"변환 실패: {ex.Message}";
            await _dialog.ConfirmAsync("변환 실패", ex.Message, "확인", "");
        }
        finally
        {
            IsConverting = false;
            _convertCts?.Dispose();
            _convertCts = null;
        }
    }

    [RelayCommand]
    private void CancelConvert() => _convertCts?.Cancel();

    private void ResetToIdle()
    {
        IsBusy = false;
        IsPaused = false;
        ElapsedText = "00:00";
        WaveBars.Clear();
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "녹음" : name;
    }

    /// <summary>같은 이름이 있으면 (1), (2) … 를 붙여 고유 경로를 만든다.</summary>
    private static string UniquePath(string folder, string baseName)
    {
        var path = Path.Combine(folder, baseName + ".wav");
        int n = 1;
        while (File.Exists(path))
            path = Path.Combine(folder, $"{baseName} ({n++}).wav");
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 무시 */ }
    }
}
