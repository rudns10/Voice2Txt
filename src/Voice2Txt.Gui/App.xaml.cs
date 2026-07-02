using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Voice2Txt.Core;
using Voice2Txt.Core.Abstractions;
using Voice2Txt.Core.Audio;
using Voice2Txt.Core.Storage;
using Voice2Txt.Core.Transcription;
using Voice2Txt.Gui.Services;
using Voice2Txt.Gui.ViewModels;
using Voice2Txt.Gui.Views;

namespace Voice2Txt.Gui;

/// <summary>애플리케이션 진입점. DI 컨테이너를 구성하고 메인 창을 띄운다.</summary>
public partial class App : Application
{
    /// <summary>전역 서비스 컨테이너. View/ViewModel은 여기서 의존성을 주입받는다.</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    /// <summary>현재 메인 창(다이얼로그 XamlRoot/창 핸들 용).</summary>
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        // 크래시 진단: 처리 안 된 예외를 파일로 기록하고 앱이 바로 닫히지 않게 한다.
        UnhandledException += (_, e) =>
        {
            LogCrash("UI", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("AppDomain", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("Task", e.Exception);
            e.SetObserved();
        };

        Services = ConfigureServices();
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = System.IO.Path.Combine(StoragePaths.AppDataDir, "crash.log");
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] ({source})\n{ex}\n\n";
            System.IO.File.AppendAllText(path, line);
        }
        catch { /* 로깅 실패는 무시 */ }
    }

    /// <summary>DI 구성. 엔진 구현체는 절대 직접 new 하지 않고 여기서 등록한다.</summary>
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core 서비스
        services.AddSingleton<IAppInfo, AppInfo>();
        services.AddSingleton<IRecorder, NAudioRecorder>();
        services.AddSingleton<IRecordingStore>(_ => new SqliteRecordingStore());
        services.AddSingleton<IModelManager, WhisperModelManager>();
        // 정식 엔진(Whisper.net, Vulkan→CPU). MVP로 되돌리려면 WhisperCppProcessTranscriber로 교체.
        services.AddSingleton<ITranscriber, WhisperNetTranscriber>();
        // 실시간 자막 전용(자체 엔진 보유)
        services.AddSingleton<LiveCaptionService>();

        // UI 서비스
        services.AddSingleton<IDialogService, DialogService>();

        // ViewModels
        services.AddTransient<MainViewModel>();

        // Views / Windows
        services.AddTransient<MainView>();
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StoragePaths.CleanStaging(); // 이전 종료로 남은 임시 파일 정리
        MainWindow = Services.GetRequiredService<MainWindow>();
        MainWindow.Activate();
    }
}
