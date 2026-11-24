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

## 빌드

이 fork는 릴리스를 배포하지 않습니다. 소스에서 exe를 빌드합니다(.NET 10 SDK 필요):

```powershell
pwsh windows/scripts/publish.ps1
```

exe는 `windows/artifacts/publish/PokeTokenBar.exe`에 생성됩니다.

## 원본 추적 방식

Windows 코드는 `windows/` 아래에만 있습니다. 나머지는 원본과 똑같이 유지하므로 루트
`README.md`는 원본 프로젝트의 README입니다. 원본 변경은 `main`에 merge하고, Claude Code 스킬
`sync-upstream`(`.claude/skills/sync-upstream/SKILL.md`)으로 `windows/`에 포팅합니다.

이 fork는 원본 프로젝트와 관계가 없습니다. Windows 포트의 문제를 원본 레포에 보고하지 마세요.
