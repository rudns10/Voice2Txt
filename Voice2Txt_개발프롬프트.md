# Voice2Txt — Claude Code 개발 프롬프트

> 이 문서를 Claude Code 세션에 그대로 붙여넣어 작업을 시작하세요.
> 한 번에 다 만들지 말고, 아래 "단계별 빌드 계획"을 **한 단계씩** 진행합니다.

---

## 0. 프로젝트 개요

오프라인 음성→텍스트(STT) **Windows 데스크톱 애플리케이션**을 만든다.

- **흐름**: 녹음 → 저장 → 오프라인 변환(Whisper) → 텍스트 결과 → 내보내기(TXT/SRT)
- **목적**: 갤럭시/아이폰 기본 녹음기의 STT를 PC에서, **완전 오프라인**으로 제공하고, 결과물을 자유롭게 내보내며, 향후 사내 시스템과 연동한다.
- **차별점**: 클라우드 전송 없음 / PC에서 동작 / 임의 오디오 파일도 변환 / 로컬 REST로 사내 연동 가능.

---

## 1. 기술 스택 & 제약

- **UI**: WinUI 3 (.NET 8), MVVM 패턴 (`CommunityToolkit.Mvvm`)
- **녹음**: `NAudio` (WASAPI 캡처) → **16kHz / mono / 16-bit WAV** 로 저장 (Whisper 입력 규격)
- **변환 엔진 (MVP)**: whisper.cpp 실행파일을 **외부 프로세스로 호출** (Vulkan 빌드 사용)
- **변환 엔진 (정식)**: `Whisper.net` 1.9.x + `Whisper.net.Runtime.Vulkan` 또는 `Whisper.net.Runtime.OpenVino`
- **로컬 저장**: `Microsoft.Data.Sqlite` (메타데이터) + 파일시스템(오디오/transcript)
- **모델**: ggml `small`(한국어 실사용 최소선) 기본, `medium` 선택지, **양자화(q5) 우선**
- **타깃 하드웨어**: Windows 11, Intel Core i5-1340P + Iris Xe 내장 그래픽 (**NVIDIA 없음** → CUDA 불가, Vulkan/OpenVINO로 가속)

### 반드시 지킬 제약
- 모델 1회 다운로드 후에는 **인터넷 없이 동작**해야 한다.
- 오디오/텍스트를 **외부 클라우드로 전송하는 코드 금지**.
- 변환 엔진은 **추상화 인터페이스 뒤에** 두어 MVP↔정식 구현을 교체 가능하게 한다.

---

## 2. 아키텍처 (4계층)

| 계층 | 역할 | 의존 |
|------|------|------|
| **Core** | 도메인 로직: 녹음 관리, 변환 엔진 추상화, 모델 관리, 데이터 저장. UI/플랫폼 비의존. | 없음 |
| **CLI** | Core를 커맨드라인으로 노출. 테스트·배치·엔진 호출 검증용. | Core |
| **Server** | 로컬 REST API(ASP.NET Core minimal API). 사내 프로그램 연동용. **후순위.** | Core |
| **GUI** | WinUI 3 앱. **Core만 참조**하고 엔진 구현체는 DI로 주입받음. | Core |

### 핵심 추상화
```csharp
public interface ITranscriber
{
    Task<TranscriptResult> TranscribeAsync(
        string wavPath,
        TranscribeOptions options,           // 모델, 언어(ko), 스레드 수 등
        IProgress<TranscribeProgress> progress,
        CancellationToken ct);
}

public record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);
public record TranscriptResult(IReadOnlyList<TranscriptSegment> Segments, string FullText);
public record TranscribeProgress(double Percent, TimeSpan Processed, TimeSpan Total);
```
구현체 2개:
- `WhisperCppProcessTranscriber` — **MVP**. whisper.cpp exe 호출, JSON/SRT 출력 파싱, stdout에서 진행률 추출.
- `WhisperNetTranscriber` — **정식**. Whisper.net 라이브러리, 세그먼트 콜백으로 진행률 보고.

GUI는 `ITranscriber`만 알고, 어떤 구현이 주입되는지는 모르게 한다.

---

## 3. 프로젝트 구조

```
Voice2Txt.sln
├─ src/
│  ├─ Voice2Txt.Core/         # 도메인, ITranscriber, 모델/DB/녹음 서비스
│  │   ├─ Abstractions/       # ITranscriber, IRecorder, IRecordingStore ...
│  │   ├─ Transcription/      # WhisperCppProcessTranscriber, WhisperNetTranscriber
│  │   ├─ Recording/          # NAudio 기반 녹음 서비스
│  │   ├─ Storage/            # SQLite 메타데이터 + 파일 경로 관리
│  │   └─ Export/             # TxtExporter, SrtExporter
│  ├─ Voice2Txt.Cli/          # 콘솔: transcribe <file> --model small --lang ko
│  ├─ Voice2Txt.Server/       # (후순위) 로컬 REST API
│  └─ Voice2Txt.Gui/          # WinUI 3 앱 (MVVM)
│      ├─ Views/              # 화면 5개 (아래 §4)
│      ├─ ViewModels/
│      └─ Services/           # DI 구성, 다이얼로그 등 UI 서비스
├─ models/                    # ggml-*.bin (gitignore, 첫 실행 시 다운로드)
└─ tools/whisper-cpp/         # MVP용 whisper.cpp Vulkan 바이너리 (gitignore)
```

---

## 4. 화면 명세 (목업 기준 5화면)

창 하나에 **왼쪽 녹음 목록 + 오른쪽 작업 영역**을 고정하고, 상태에 따라 오른쪽만 바뀐다. 제목줄 + 사이드바 + 콘텐츠 영역.

1. **대기 / 메인** — 사이드바(검색 + 녹음 목록), 가운데에 모드 토글(일반 / 음성→텍스트) + 큰 녹음 버튼 + "녹음 시작".
2. **녹음 중** — 타이머(mm:ss), 실시간 파형, [일시정지][정지] 버튼. 음성→텍스트 모드면 실시간 자막 미리보기 영역(후순위).
3. **저장 다이얼로그(모달)** — 파일명 입력, 저장 위치, "지금 텍스트로 변환" 체크박스(오프라인 처리 안내), [취소][저장].
4. **변환 중** — 진행률 바 + 퍼센트 + 경과/총 시간, "Whisper {모델} · 로컬(오프라인) 처리" 표시, [취소].
5. **재생 + 텍스트 결과** — 상단 제목/메타 + 액션 아이콘(복사/내보내기/삭제), 재생 컨트롤(재생 버튼 + 진행 바 + 시간), 타임스탬프 붙은 transcript 패널, 하단 [편집][TXT 내보내기][SRT 내보내기].

---

## 5. 단계별 빌드 계획 (한 단계씩 진행, 각 단계 끝에 커밋)

### W0 — 골격
- 위 구조대로 솔루션/프로젝트 생성, 계층 간 참조 설정.
- 빈 WinUI 3 창이 뜨고 빌드/실행되는 것까지 확인.
- DI 컨테이너 구성(`Microsoft.Extensions.DependencyInjection`).
- **완료 기준**: 앱 실행 시 빈 메인 창 표시, 솔루션 빌드 무오류.

### W1 — 녹음 (엔진 없이 Core+GUI)
- `IRecorder` (NAudio WASAPI) → 16kHz mono WAV 저장.
- "대기/메인" 화면 + "녹음 중" 화면, 녹음/일시정지/정지 동작.
- **완료 기준**: 녹음 버튼 → 타이머·파형 표시 → 정지 시 WAV 파일 생성.

### W2 — 목록 + 저장 + 메타데이터
- SQLite로 녹음 메타데이터(이름, 생성시각, 길이, 파일경로, 변환여부) 저장.
- "저장 다이얼로그" 모달, 사이드바 녹음 목록 + 검색.
- **완료 기준**: 녹음 후 저장 → 목록에 표시 → 재시작해도 목록 유지.

### W3 — 변환 엔진 연결 (MVP)
- `WhisperCppProcessTranscriber` 구현: whisper.cpp Vulkan exe 호출, 모델 경로/언어(ko)/스레드 전달, SRT/JSON 출력 파싱, 진행률 보고.
- "변환 중" 화면, 취소 동작.
- 첫 실행 시 모델 파일 없으면 다운로드 안내(또는 자동 다운로드).
- **완료 기준**: 저장된 WAV 변환 → 진행률 표시 → `TranscriptResult` 생성. 5분 오디오가 small+Vulkan에서 1분 이내 목표.

### W4 — 결과 + 재생 + 내보내기
- "재생 + 텍스트 결과" 화면: 오디오 재생, 타임스탬프 transcript 표시, 단어/구간 클릭 시 해당 위치 재생(가능 범위).
- `TxtExporter`, `SrtExporter`.
- **완료 기준**: 변환 결과 화면 표시 + TXT/SRT 파일로 내보내기 성공.

### W5 — Whisper.net 정식 통합
- `WhisperNetTranscriber` 구현(Whisper.net 1.9.x). `Whisper.net.Runtime.Vulkan` / `Whisper.net.Runtime.OpenVino` 둘 다 시도해 Iris Xe에서 빠른 쪽 선택.
- DI 설정만 바꿔 MVP→정식 교체(인터페이스 유지).
- **완료 기준**: 엔진 구현체 교체 후 동일 흐름 정상 동작, 속도 비교 로그 출력.

### W6 — (선택) Server 계층
- ASP.NET Core minimal API: `POST /transcribe` (오디오 업로드 → transcript 반환).
- Core의 `ITranscriber` 재사용.
- **완료 기준**: 로컬에서 REST로 변환 요청/응답 성공. (사내 연동 시점에 진행)

---

## 6. 코딩 규칙

- **MVVM 엄수**: View에 비즈니스 로직 금지, ViewModel은 Core 서비스만 호출.
- 모든 I/O·변환은 **async + CancellationToken** 지원.
- 의존성은 **생성자 주입(DI)**. 엔진 구현체는 절대 직접 `new` 하지 말 것.
- 변환 엔진은 반드시 `ITranscriber`를 통해서만 사용.
- 한국어 주석 허용. 공개 API에는 XML 문서 주석.
- 예외 처리: 엔진/파일 오류는 사용자에게 알기 쉬운 메시지로 변환, 앱이 죽지 않게.

---

## 7. Claude Code 작업 지침

- 위 **W0부터 순서대로** 진행하고, 각 단계마다 빌드·실행을 확인한 뒤 다음으로.
- 각 단계 시작 전 **무엇을 만들지 한 줄로 요약**하고, 끝나면 변경 파일과 확인 방법을 알려줄 것.
- `ITranscriber` 추상화를 **깨지 말 것**. GUI가 특정 엔진 구현에 직접 의존하면 안 됨.
- 임의로 **네트워크/클라우드 의존성을 추가하지 말 것** (모델 다운로드 제외).
- 모델 파일·바이너리는 커밋하지 말 것(`.gitignore`).
- 큰 결정(라이브러리 추가, 구조 변경)은 진행 전에 먼저 알려줄 것.

---

## 8. 범위 밖 (지금 하지 말 것)

- 화자 분리(speaker diarization)
- 실시간 스트리밍 자막(W2 단계의 미리보기 외 본격 구현)
- 번역·요약 기능
- 모바일/크로스플랫폼
- 30분 분할 병렬 변환 — 이 노트북에선 이득 없음(코어/메모리 한계). 단일 변환 + GPU 가속으로 충분.

---

## 부록 A. whisper.cpp 호출 예 (MVP 참고)

```
whisper-cli.exe -m models/ggml-small-q5_0.bin -f input.wav -l ko -oj -of out
```
- `-oj` JSON 출력(`out.json`) → 세그먼트/타임스탬프 파싱
- `-osrt` 로 SRT 직접 출력도 가능
- 진행률은 stdout의 `[00:00:00.000 --> ...]` 라인을 파싱하거나 총 길이 대비 처리 위치로 계산

## 부록 B. Whisper.net 패키지 (정식 참고)

```xml
<PackageReference Include="Whisper.net" Version="1.9.0" />
<!-- Iris Xe: 아래 둘 중 빠른 쪽 선택 -->
<PackageReference Include="Whisper.net.Runtime.Vulkan" Version="1.9.0" />
<PackageReference Include="Whisper.net.Runtime.OpenVino" Version="1.9.0" />
```
- Vulkan은 Vulkan Runtime, OpenVINO는 OpenVINO Toolkit 사전 설치 필요.
- 여러 런타임을 동시에 참조하면 플랫폼/가용성에 따라 자동 선택됨.

---

## 부록 C. 조정 가능한 가정 (시작 전 확인하면 좋음)

- 프로젝트명 `Voice2Txt` / .NET 8 / WinUI 3 — 변경 가능.
- 녹음 라이브러리 NAudio 가정 — `MediaCapture`로 대체 가능.
- 메타데이터 저장 SQLite 가정 — 단순 JSON 파일로도 시작 가능.
