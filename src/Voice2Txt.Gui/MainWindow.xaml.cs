using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Voice2Txt.Core;
using Voice2Txt.Core.Abstractions;
using Voice2Txt.Core.Storage;
using Voice2Txt.Gui.Views;
using Windows.Graphics;

namespace Voice2Txt.Gui;

/// <summary>메인 창. 커스텀 제목줄 + Mica 배경, DI로 주입받은 MainView 호스팅.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow(IAppInfo appInfo, MainView mainView)
    {
        InitializeComponent();
        Title = $"Voice2Txt v{appInfo.Version}";
        RootContainer.Children.Add(mainView);

        // 창/작업표시줄 아이콘
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (System.IO.File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // Mica 배경 + 콘텐츠를 제목줄까지 확장 + 커스텀 제목줄 영역 지정
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        RestoreWindowPlacement();

        var theme = AppSettingsStore.Load().Theme;
        ApplyTheme(theme);
        (theme switch { "Light" => ThemeLight, "Dark" => ThemeDark, _ => ThemeSystem }).IsChecked = true;

        // 시스템 테마 변경 시(테마=시스템) 캡션 버튼 색도 갱신
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();

        Closed += OnClosed;
    }

    /// <summary>테마 적용("System"|"Light"|"Dark"). 콘텐츠 루트에 RequestedTheme 설정.</summary>
    public void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        UpdateCaptionButtonColors();
    }

    /// <summary>제목줄 캡션 버튼(─ ☐ ✕) 색을 현재 테마에 맞춘다.</summary>
    private void UpdateCaptionButtonColors()
    {
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var tb = AppWindow.TitleBar;

        var fg = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedForegroundColor = fg;
        tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonInactiveForegroundColor = dark
            ? Windows.UI.Color.FromArgb(255, 150, 150, 150)
            : Windows.UI.Color.FromArgb(255, 130, 130, 130);
        tb.ButtonHoverBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(40, 255, 255, 255)
            : Windows.UI.Color.FromArgb(25, 0, 0, 0);
    }

    private void OnThemeSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string theme) return;
        ApplyTheme(theme);
        var s = AppSettingsStore.Load();
        s.Theme = theme;
        AppSettingsStore.Save(s);
    }

    private const int DefaultWidth = 1440;
    private const int DefaultHeight = 920;

    /// <summary>저장된 창 크기/위치 복원(없으면 기본 크기 + 중앙).</summary>
    private void RestoreWindowPlacement()
    {
        var s = AppSettingsStore.Load();
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        int width = System.Math.Min(s.WindowWidth > 0 ? s.WindowWidth : DefaultWidth, work.Width);
        int height = System.Math.Min(s.WindowHeight > 0 ? s.WindowHeight : DefaultHeight, work.Height);

        int x, y;
        bool saved = s.WindowWidth > 0 && s.WindowHeight > 0;
        bool onScreen = saved
            && s.WindowX >= work.X && s.WindowY >= work.Y
            && s.WindowX + width <= work.X + work.Width
            && s.WindowY + height <= work.Y + work.Height;

        if (saved && onScreen)
        {
            x = s.WindowX;
            y = s.WindowY;
        }
        else
        {
            x = work.X + (work.Width - width) / 2;
            y = work.Y + (work.Height - height) / 2;
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    /// <summary>종료 시: 녹음 중이면 안전 정리 + 창 크기/위치 저장.</summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        // 녹음 중 종료 → 마이크 장치 안전 해제
        try { (App.Services.GetService(typeof(IRecorder)) as IRecorder)?.Cancel(); }
        catch { /* 무시 */ }

        var s = AppSettingsStore.Load();
        s.WindowWidth = AppWindow.Size.Width;
        s.WindowHeight = AppWindow.Size.Height;
        s.WindowX = AppWindow.Position.X;
        s.WindowY = AppWindow.Position.Y;
        AppSettingsStore.Save(s);
    }
}
