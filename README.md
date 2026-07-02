# Voice2Txt

완전 오프라인 **음성 → 텍스트(STT)** Windows 데스크톱 앱.
녹음하거나 오디오 파일을 불러와 **Whisper**로 변환하고, 결과를 재생·편집·내보내기(TXT)할 수 있습니다.

- 🔒 **완전 오프라인** — 오디오/텍스트를 외부로 전송하지 않음 (모델만 첫 1회 다운로드)
- 🎙️ 마이크 녹음 + 임의 오디오 파일(mp3/m4a/wav 등) 변환
- ⚡ **Vulkan 가속**(Intel Iris Xe 등 내장 그래픽) — NVIDIA 불필요
- 📝 타임스탬프 transcript, 구간 클릭 재생, TXT 내보내기

---

## 받는 분(팀원)용 — 설치 없이 실행

1. 배포 zip(`Voice2Txt-x.y.z-win-x64.zip`)을 받아 **압축 해제**
2. 폴더 안의 **`Voice2Txt.Gui.exe` 더블클릭**
3. 첫 변환 시 음성인식 모델(약 190MB)을 **1회 자동 다운로드**(인터넷 필요) → 이후 완전 오프라인
   - 단, **`-with-small` 포함된 zip**을 받았다면 모델이 동봉돼 있어 **첫 실행부터 인터넷 없이** 변환됩니다.

> .NET이나 별도 런타임 설치 **불필요** (앱에 모두 포함).
> 서명되지 않은 exe라 첫 실행 시 SmartScreen이 뜨면 **추가 정보 → 실행**.

### 사용법
- **큰 빨강 버튼**(또는 **스페이스바**)으로 녹음 시작/일시정지, **[정지]** 로 종료 후 저장
- 저장 시 **"지금 텍스트로 변환"** 체크하면 바로 변환
- 사이드바 **📂 버튼**으로 기존 오디오 파일 불러와 변환
- 좌하단 **변환 모델**(small/medium) 선택
- 목록 항목 **우클릭 → 이름 수정 / 삭제**
- 결과 화면: 재생(스페이스바), 구간 클릭 재생, **복사 / 편집 / TXT 내보내기**

---

## 개발자용

### 요구 환경
- Windows 10/11 (x64)
- .NET SDK 9
- Visual Studio 2022 (WinUI 3 워크로드) 권장

### 빌드 / 실행
GUI는 self-contained라 **RID(`-r win-x64`)가 필요**합니다.

```powershell
# 실행
dotnet run --project src\Voice2Txt.Gui -r win-x64

# CLI(엔진 검증용)
dotnet run --project src\Voice2Txt.Cli -- transcribe "<오디오경로>" --engine net --model small --lang ko
```

### 배포 패키징
self-contained 게시 + zip 생성 (개발환경 없는 PC에서 exe 더블클릭 실행).
```powershell
pwsh scripts\package.ps1                       # small 모델 동봉 (기본)
pwsh scripts\package.ps1 -Models none          # 모델 미포함(첫 실행 시 다운로드, zip 작음)
pwsh scripts\package.ps1 -Models small,medium  # 둘 다 동봉
pwsh scripts\package.ps1 -Version 0.1.0
```
- `dist\Voice2Txt-<버전>-win-x64.zip` — 모델 미포함(약 120MB), 첫 실행 시 모델 다운로드
- `dist\Voice2Txt-<버전>-win-x64-with-small.zip` — small 동봉(약 300MB), **인터넷 없이 즉시 변환**
- 동봉할 모델은 `%LOCALAPPDATA%\Voice2Txt\models`에 있어야 함(앱에서 1회 변환해 받아두면 됨)

### 프로젝트 구조
```
Voice2Txt.sln
├─ src/
│  ├─ Voice2Txt.Core/   # 도메인: 녹음/변환/저장/내보내기 (UI 비의존)
│  ├─ Voice2Txt.Cli/    # 콘솔: transcribe 명령 (엔진 검증)
│  └─ Voice2Txt.Gui/    # WinUI 3 앱 (MVVM)
├─ scripts/package.ps1  # 배포 zip 생성
└─ tools/whisper-cpp/   # (선택) whisper.cpp 바이너리 — gitignore
```

자세한 아키텍처는 [CLAUDE.md](CLAUDE.md) 참고.

### 기술 스택
WinUI 3 (WindowsAppSDK 1.7) · .NET 9 · CommunityToolkit.Mvvm · NAudio(WASAPI 녹음) ·
Whisper.net 1.9 (Vulkan/CPU) · Microsoft.Data.Sqlite

### 데이터 위치
| 항목 | 경로 |
|------|------|
| 메타데이터 DB | `%LOCALAPPDATA%\Voice2Txt\voice2txt.db` |
| 설정(모델/창) | `%LOCALAPPDATA%\Voice2Txt\voice2txt-settings.json` |
| 모델 | `%LOCALAPPDATA%\Voice2Txt\models\` |
| 녹음/transcript | `내 문서\Voice2Txt\recordings\` |
