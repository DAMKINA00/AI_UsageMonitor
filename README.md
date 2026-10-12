# AI 사용량 모니터

이 PC에서 돌아가는 **Claude**(데스크톱 앱·Claude Code)와 **Codex**(데스크톱 앱·CLI)의
작업 상태와 남은 사용량을 한 화면에서 보여 주는 Windows 트레이 앱입니다.
카드 옆에서는 두 캐릭터(클짱·짚짱)가 상태에 따라 움직입니다.

실행 파일: `publish\AIUsageMonitor.exe` (.NET 10 데스크톱 런타임 필요 — 이미 설치되어 있음)

## 보여 주는 것

| | Claude | Codex |
|---|---|---|
| 앱 실행 여부 | `claude.exe` 프로세스 | Store판 `ChatGPT.exe`(OpenAI.Codex) / 직접 설치판 `Codex.exe` |
| 세션·스레드 상태 | `~/.claude/sessions/<pid>.json` (작업 중 / 승인 대기 / 대기) | `~/.codex/sessions/**/rollout-*.jsonl` 의 `task_started`/`task_complete` |
| 사용률 | claude.ai 로그인 후 사용량 화면과 같은 데이터 (+ 터미널 CLI 상태줄) | `codex app-server` 의 `account/rateLimits/read` (+ 로그의 `rate_limits`) |
| 기타 | 플랜(Max 5x/20x) | 플랜(Pro 5x/20x), 크레딧 잔액, 무료 리셋권 |
| 세션 클릭 | Claude 앱에서 그 세션 열기 (`claude://`) | Codex 앱에서 그 스레드 열기 (`codex://threads/…`, Store판으로 연결) |

- 카드에는 한도를 하나만 보여 줍니다: 평소엔 **주간 한도**, 5시간 한도가 90% 이상이면 그 자리에 5시간 한도.
- 창 크기는 고정이고, 세션이 많아지면 카드 안 목록만 스크롤됩니다.
- Codex 스레드 상태는 실제 작업 이벤트 시각으로 판단합니다. Codex 앱이 켜질 때 모든 스레드 로그에 덧붙이는
  `thread_settings_applied` 는 활동으로 치지 않습니다.
- 로컬 파일은 3초마다, Codex 서버 조회는 2분마다, claude.ai 조회는 3분마다 갱신합니다.
- 트레이 아이콘의 두 막대(왼쪽 Claude, 오른쪽 Codex)는 가장 많이 쓴 한도의 사용률입니다.
- 사용률이 80%·95%를 넘으면 알림을 띄웁니다(리셋 주기마다 한 번).

## Claude 사용량 보기

Claude 카드의 **claude.ai 로그인** 버튼(또는 트레이 메뉴)을 누르면 앱 전용 브라우저 창이 열립니다.
한 번 로그인하면 창이 자동으로 닫히고 이후 계속 사용량을 읽습니다.

- 세션은 `%LOCALAPPDATA%\AIUsageMonitor\WebView2` 프로필 안에만 저장되며, 앱이 쿠키를 꺼내 쓰지 않습니다.
- 구글 로그인은 내장 브라우저를 막는 경우가 있어 **이메일 로그인**을 권장합니다.
- 트레이 메뉴 **Claude 로그아웃**으로 이 프로필의 세션을 지울 수 있습니다.

데스크톱 앱 세션은 상태줄(statusLine)을 실행하지 않아서 이 방식을 씁니다.
터미널에서 `claude` CLI를 쓸 때는 `helpers\claude_statusline.py` 가 공식 `rate_limits` 값을
`%LOCALAPPDATA%\AIUsageMonitor\claude\rate_limits.json` 에 남기고, 앱이 더 최신 값을 골라 씁니다.
(`~/.claude/settings.json` 의 `statusLine` 항목. 원래 설정은 `settings.json.bak-usage-monitor` 에 백업)

## 캐릭터 연출

Claude 카드 옆에는 **클짱**, Codex 카드 옆에는 **짚짱**(GPT)이 섭니다. 디시인사이드 AI 활용 갤러리에서
밈으로 쓰이는 의인화 캐릭터의 특징(머리색·장식·의상)을 글로 묘사해 새로 그린 팬아트입니다.
원작 캐릭터에는 따로 창작자가 있으니 **개인 용도로만** 쓰고, 이 앱을 배포하거나 공개할 때는 원작자 허락을 받으세요.

그림체는 참고 MV처럼 단순한 플랫 채색에 약 3.5등신으로 데포르메했고, 동작도 MV의 손짓 연출을 따랐습니다.
한 캐릭터의 포즈는 모두 같은 캔버스·같은 축척이라 동작이 바뀌어도 몸 크기가 변하지 않습니다. 두 캐릭터 모두 카드 오른쪽에 섭니다.

| 상황 | 연출 |
|---|---|
| 작업 중 | 선 채로 일하는 3프레임 루프 — 짚짱은 홀로그램 화면을 보며 홀로그램 키보드 타이핑, 클짱은 아날로그 수첩에 메모 (가끔 눈 깜빡임) |
| 작업 중 + 사용률 70% 이상 | 손이 더 빨라지고 땀방울 |
| 작업 중 + 90% 이상, 한도 도달 | 울며 패닉, 눈물 |
| 승인·입력 대기 | 화면을 손가락으로 콕 가리키기 |
| 쉬는 중 | 독서와 차(클짱) / 게임(짚짱), 가끔 서서 대기 |
| 쉬는 중 + 70% 이상 / 90% 이상 | 지친 표정·땀 / 바닥에 털썩 엎어짐 |
| 앱 꺼짐, 30분 이상 활동 없음 | 서서 졸기 |
| 사용률 증가 | 머리 위에 "+3%" 팝 |
| 한도 리셋 | 점프하며 반짝이 (15분 동안) |

효과음 글자는 쓰지 않고, 땀방울·눈물과 사용률 상승 "+N%", 리셋 반짝이만 표시합니다.
말풍선은 몇 초 보였다가 사라지고 잠시 뒤 다른 대사가 나옵니다. 대사는 AI활용갤 밈(명령하지마라, 그정돈아님, 어이가 없군요,
인간 싫어, 귀하게 자란 ○○가 이런 일을…, 클로드는 하던데 등)을 섞어 상태·캐릭터마다 여러 개씩 있습니다.
클짱은 정중한 척하는 존댓말, 짚짱은 건방진 반말입니다 (`UI/Mood.cs`).
트레이 메뉴 **캐릭터 표시**로 끌 수 있습니다. 창이 숨겨지면 애니메이션도 멈춥니다.

이미지는 로컬 ComfyUI의 Qwen Image 2.1로 만들었습니다(`art/`). 기본 디자인은 `art/ref/`,
포즈 프롬프트는 `art/poses.py`, 작업 중 프레임은 `art/work.py` 이고, `art/build_assets.py` 가 여백 제거와 흰 스티커 테두리를 입혀
`Assets/Characters/` 로 넣습니다. 그 다음 다시 빌드하세요.

## 빌드

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

진단: `AIUsageMonitor.exe --render-png 화면.png` — 12초 뒤 창을 PNG로 저장하고 종료합니다.
로그: `%LOCALAPPDATA%\AIUsageMonitor\monitor.log`

## 회귀 검증

```bash
dotnet run --project tests/AIUsageMonitor.Regression/AIUsageMonitor.Regression.csproj -c Release
```

실제 계정이나 서버에 접속하지 않고 합성 데이터로 한도 병합, 조회 실패 안내, 초기화 실패 후 재시도,
세션 클릭 대상 갱신, 로그 증분 읽기와 오래된 스레드 제외를 검사합니다. 임시 로그는 별도 폴더에 만들며
사용자의 세션 파일과 로그인 프로필은 변경하지 않습니다.
