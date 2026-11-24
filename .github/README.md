# PokeTokenBar for Windows (개인용 포트)

**한국어** · [English](README.en.md)

[chattymin/PokeTokenBar](https://github.com/chattymin/PokeTokenBar)의 개인용 Windows 포트입니다.
Claude Code 토큰 사용량으로 포켓몬 동료를 키웁니다. macOS 메뉴바 대신 Windows 알림 영역(시스템
트레이)에 표시됩니다.

- **앱 자체**(기능, 스크린샷, macOS 빌드): [원본 레포](https://github.com/chattymin/PokeTokenBar)를
  보세요.
- **Windows 포트**(빌드, 파일 위치, macOS와의 차이): [`windows/README.md`](../windows/README.md)를
  보세요.
- **범위:** Claude Code만 지원합니다. macOS 앱의 다른 사용량 프로바이더는 포팅하지 않았습니다.
  원본 변경별 포팅 현황은 [`windows/PARITY.md`](../windows/PARITY.md)에 있습니다.

## 실행 환경

| 항목 | 내용 |
|---|---|
| OS | Windows 11 x64에서 확인했습니다. Windows 10 x64는 확인하지 않았습니다. |
| .NET | 설치할 필요 없습니다. 런타임이 exe 안에 들어 있습니다. |
| 설치 | 필요 없습니다. `PokeTokenBar.exe` 파일 하나만 원하는 폴더에 두고 실행합니다. |
| Claude Code | 그 PC에서 Claude Code를 써야 사용량이 잡힙니다. `%USERPROFILE%\.claude\projects` 등의 로그를 읽습니다. |
| 데이터 위치 | 세이브·설정·캐시는 `%LOCALAPPDATA%\PokeTokenBar`에 저장됩니다. PC마다 따로이므로, 옮기려면 설정의 세이브 내보내기/가져오기를 씁니다. |

처음 실행하면 WPF 네이티브 라이브러리를 `%TEMP%\.net\PokeTokenBar`에 풀어 씁니다. *로그인 시 실행*과
`poketokenbar://` 프로토콜 등록은 exe 경로를 기록하므로, exe를 둘 폴더를 먼저 정하고 실행하세요.

## 설치

[Releases](https://github.com/Bluewind8791/PokeTokenBar-Win/releases)에서 `PokeTokenBar.exe`를 받아
실행합니다. 앱은 업데이트를 알려 주지 않으니(원본 레포의 릴리스만 확인합니다) 새 버전은 직접 받으세요.

소스에서 빌드하려면(.NET 10 SDK 필요):

```powershell
pwsh windows/scripts/publish.ps1
```

exe는 `windows/artifacts/publish/PokeTokenBar.exe`에 생성됩니다.

## 원본 추적 방식

Windows 코드는 `windows/` 아래에만 있습니다. 나머지는 원본과 똑같이 유지하므로 루트
`README.md`는 원본 프로젝트의 README입니다. 원본 변경은 `main`에 merge하고, Claude Code 스킬
`sync-upstream`(`.claude/skills/sync-upstream/SKILL.md`)으로 `windows/`에 포팅합니다.

이 fork는 원본 프로젝트와 관계가 없습니다. Windows 포트의 문제를 원본 레포에 보고하지 마세요.
