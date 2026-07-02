using CommunityToolkit.Mvvm.ComponentModel;
using Voice2Txt.Core.Storage;

namespace Voice2Txt.Gui.ViewModels;

/// <summary>파형 막대 1개(높이 + 큰 소리 여부). 매 프레임 갱신되므로 observable.</summary>
public sealed partial class WaveBar : ObservableObject
{
    [ObservableProperty] private double _height;
    [ObservableProperty] private bool _hot;

    public WaveBar(double height, bool hot)
    {
        Height = height;
        Hot = hot;
    }
}

/// <summary>사이드바 목록 표시용 래퍼. 원본 메타데이터(<see cref="Model"/>)를 보유.</summary>
public sealed class RecordingItemViewModel
{
    public Recording Model { get; }

    public RecordingItemViewModel(Recording model) => Model = model;

    public string Id => Model.Id;
    public string Name => Model.Name;
    public bool IsTranscribed => Model.IsTranscribed;

    /// <summary>"2026-06-05 14:30 · 03:21" 형태.</summary>
    public string SubTitle => $"{Model.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {FormatDuration(Model.Duration)}";

    private static string FormatDuration(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
}
