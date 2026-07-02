# CLAUDE.md

이 저장소에서 작업할 때 참고하는 가이드. (Claude Code / 개발자 공용)

## 개요
오프라인 음성→텍스트 Windows 데스크톱 앱. 흐름: **녹음/파일 → 16kHz mono WAV → Whisper 변환 → transcript → TXT 내보내기**.
원본 요구사항은 [Voice2Txt_개발프롬프트.md](Voice2Txt_개발프롬프트.md), 화면 디자인은 목업 [docs/Voice2Txt_화면목업.html](docs/Voice2Txt_화면목업.html) 5화면 기준.

## 빌드 / 실행 (중요)
- **GUI는 항상 `-r win-x64` 필요** (WindowsAppSDK self-contained). 빼면 빌드/실행 실패.
  - `dotnet run --project src\Voice2Txt.Gui -r win-x64`
- Core/Cli는 RID 불필요: `dotnet build src\Voice2Txt.Cli\Voice2Txt.Cli.csproj`
- 실행 중 빌드 시 exe 잠김 → `taskkill /F /IM Voice2Txt.Gui.exe` 후 빌드.
- 배포: `pwsh scripts\package.ps1` → `dist\*.zip` (게시+압축). 모델 동봉 옵션 `-Models small|medium|none|small,medium` (기본 small).

## 아키텍처 (3계층, Server는 미구현=W6 후순위)
| 프로젝트 | TFM | 역할 |
|----------|-----|------|
| **Voice2Txt.Core** | `net9.0-windows` | 도메인. UI 비의존. 녹음/변환/저장/내보내기/모델 |
| **Voice2Txt.Cli** | `net9.0-windows` | `transcribe` 명령(엔진 헤드리스 검증) |
| **Voice2Txt.Gui** | `net9.0-windows10.0.19041.0` | WinUI 3, MVVM. **Core만 참조**, 구현체는 DI 주입 |

### 핵심 추상화 (Core/Abstractions)
- `ITranscriber` — 변환 엔진. **구현 2개**:
  - `WhisperNetTranscriber` (**정식/기본**, Whisper.net 1.9, Vulkan→CPU 폴백, factory 캐싱)
  - `WhisperCppProcessTranscriber` (MVP, whisper-cli.exe 외부 프로세스)
  - **GUI 기본은 Whisper.net.** 엔진 교체는 [App.xaml.cs](src/Voice2Txt.Gui/App.xaml.cs)의 DI 한 줄만 변경.
- `IRecorder` → `NAudioRecorder` (WASAPI 캡처 → 16k/mono/16-bit WAV)
- `IRecordingStore` → `SqliteRecordingStore` (메타데이터)
- `IModelManager` → `WhisperModelManager` (ggml 모델 다운로드/관리)
- GUI 전용 `IDialogService` → `DialogService` (저장/파일선택/확인/입력 다이얼로그, picker는 창 핸들 초기화 필요)

### 변환 흐름 (MainViewModel.ConvertRecordingAsync)
모델 보장(없으면 다운로드) → **Task.Run으로 STT(UI 비차단)** → transcript JSON 저장 → DB 갱신 → 목록 reload → 결과 화면 전환. 실패 시 에러 다이얼로그.

## 데이터/경로 (StoragePaths)
- `%LOCALAPPDATA%\Voice2Txt\` : `voice2txt.db`, `voice2txt-settings.json`, `models\`, `staging\`
- `내 문서\Voice2Txt\recordings\` : WAV + `*.transcript.json`
- whisper.cpp exe는 `AppContext.BaseDirectory\whisper-cpp\` → 없으면 상위로 올라가며 `tools\whisper-cpp\Release\` 탐색

## 모델 (WhisperModelCatalog / WhisperModelManager)
- 기본 `small-q5_1`(190MB), 선택 `medium-q5_0`(539MB). HF `ggerganov/whisper.cpp`에서 다운로드.
- 동일 ggml 파일을 whisper.cpp/Whisper.net 둘 다 사용.
- **탐색 순서**: `%LOCALAPPDATA%\Voice2Txt\models` → `<앱폴더>\models`(배포 동봉) → 둘 다 없으면 다운로드.
- 배포 시 `package.ps1 -Models`로 모델을 `<앱폴더>\models`에 동봉 가능(폐쇄망/즉시 오프라인).

## 코딩 관례 (개발프롬프트 §6)
- **MVVM 엄수**: View에 비즈니스 로직 금지, ViewModel은 Core/UI서비스만 호출.
- 모든 I/O·변환은 **async + CancellationToken**.
- 의존성은 **생성자 주입(DI)**. 엔진 구현체 직접 `new` 금지(테스트 코드 제외).
- 변환은 반드시 `ITranscriber` 경유. **네트워크/클라우드 의존성 추가 금지**(모델 다운로드 예외).
- 한국어 주석 OK, 공개 API엔 XML 주석.
- 모델/바이너리/`dist`/데이터는 **커밋 금지** (.gitignore).

## WinUI 3 주의점 (겪은 이슈)
- **Window에 x:Bind + 컨버터 직접 사용 불가** → 콘텐츠는 `MainView`(UserControl)로 분리.
- `[ObservableProperty]`는 **필드 기반** 사용(partial property는 생성기 설정 이슈로 미사용). `MVVMTK0045` 경고는 NoWarn 처리.
- 커스텀 제목줄: `ExtendsContentIntoTitleBar` + `SetTitleBar(AppTitleBar)` + `MicaBackdrop`.
- 스페이스바 단축키: 화면 전환 시 포커스를 `MainView`로 옮겨 버튼이 키를 가로채지 않게 함.
- FilePicker/FolderPicker는 unpackaged라 `InitializeWithWindow`로 hwnd 초기화 필요.

## 범위 밖 (개발프롬프트 §8 — 구현하지 말 것)
화자분리 · 실시간 스트리밍 자막 · 번역/요약 · 모바일 · 30분 분할 병렬.

## 남은 작업
- **W6 — Voice2Txt.Server** (로컬 REST `POST /transcribe`, 사내 연동용). 미구현/후순위.
- 코드 서명(SmartScreen 경고 제거)은 선택(인증서 필요).
- 소소한 폴리시: 0초/무음 녹음 방지, 복사 후 피드백 등(미구현).
