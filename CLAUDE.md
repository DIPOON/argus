# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Argus는 1–4인 협동 PvE 브라우저 게임이다. C# 서버가 모든 판정의 권위를 가지고, TypeScript/PixiJS 클라이언트는 입력 전달과 화면 표시만 담당한다. 문서와 주석, 커밋 메시지는 한국어로 쓴다.

## 명령어

.NET 10 SDK와 Node.js 24 이상이 필요하다. 모든 명령은 저장소 루트에서 실행한다.

```bash
# 클라이언트 의존성 설치
npm --prefix client ci

# 클라이언트 빌드 (tsc --noEmit + vite build)
npm --prefix client run build

# 타입 검사만
npm --prefix client run typecheck

# 서버 실행
dotnet run --project server/Argus.Server --urls http://127.0.0.1:5077

# 서버 단위 검사
dotnet run --project tests/Argus.Server.Tests

# 브라우저 검사 (서버가 5077에서 실행 중이어야 한다)
cd client && npx playwright install --with-deps chromium && npm run test:e2e
```

`vite.config.ts`의 `outDir`은 `server/Argus.Server/wwwroot`다. **클라이언트를 빌드해야 서버가 화면을 제공한다.** `wwwroot`는 git에서 제외되므로 새로 클론한 뒤에는 클라이언트 빌드가 먼저다.

클라이언트 UI 작업 중에는 서버를 5077로 켜 둔 채 `npm --prefix client run dev`를 실행하고 <http://localhost:5173>을 연다. Vite가 `/api`, `/health`, `/ws`를 5077로 전달한다.

두 C# 프로젝트 모두 `TreatWarningsAsErrors`와 `Nullable`이 켜져 있다. 경고 하나로 빌드가 실패한다.

### 검사 하나만 실행하기

서버 검사는 xUnit이 아니라 `tests/Argus.Server.Tests/Program.cs`의 자체 콘솔 하네스다. 필터 인자가 없고 `Main()`이 네 개의 `Register` 호출로 모든 검사를 등록한 뒤 순서대로 실행한다. 전체가 수 초면 끝나므로 보통 그대로 돌린다. 하나만 보려면 `Program.cs`의 `Register` 호출을 임시로 줄이거나 해당 `*Tests.cs`의 `Register`에서 `tests.Add` 줄을 임시로 주석 처리한다. **임시 수정을 되돌리고 전체를 다시 통과시킨 뒤 커밋한다.**

Playwright는 이름으로 고를 수 있다.

```bash
cd client
npx playwright test tests/mission.spec.ts
npx playwright test -g "late join"
ARGUS_URL=http://127.0.0.1:8080 npm run test:e2e   # 다른 포트
```

### root 권한 없이 브라우저 검사 환경 만들기

`playwright install --with-deps`는 apt로 시스템 라이브러리를 설치하므로 root 권한이 필요하다. 그럴 수 없으면 브라우저와 빠진 라이브러리를 따로 받아 환경 변수로 알려 준다. 이 개발 환경(WSL)에서는 `libnss3`, `libnspr4`, `libasound2t64`가 빠져 있었다. `/tmp`는 재시작할 때 지워지므로 유지되는 곳에 둔다.

```bash
D=$HOME/.cache/argus-browser
mkdir -p $D/browsers $D/debs $D/root
(cd client && PLAYWRIGHT_BROWSERS_PATH=$D/browsers npx playwright install --only-shell chromium)
(cd $D/debs && apt-get download libnss3 libnspr4 libasound2t64)
for deb in $D/debs/*.deb; do dpkg -x "$deb" $D/root; done
cd client && PLAYWRIGHT_BROWSERS_PATH=$D/browsers LD_LIBRARY_PATH=$D/root/usr/lib/x86_64-linux-gnu npm run test:e2e
```

## C# 작성 방식

**[AGENTS.md](AGENTS.md)가 이 저장소의 C# 작성 규칙이며 코드를 쓰기 전에 반드시 따른다.** 사용자는 C#을 학습 중이고 축약 문법보다 기본 문법을 선호한다. 요약하면:

- 명시적 타입, 일반 생성자, 중괄호 있는 `if`/`else`와 `for`/`foreach`를 쓴다.
- LINQ 연결, switch 식, 복잡한 패턴 매칭, 삼항 연산자, 식 본문 메서드, 주 생성자(primary constructor), target-typed `new`, 컬렉션 식을 **피한다**.
- 한 줄에 한 동작. 중요한 처리 순서와 이유는 짧은 한국어 주석으로 남긴다.
- 자동 속성, nullable, `async`/`await`, `lock`은 목적이 있으므로 유지한다. 문법을 피하려고 동작을 복잡하게 바꾸지 않는다.
- 기존 검사의 간결한 표현은 유지해도 되지만, 새로 쓰거나 수정하는 검사에는 위 방식을 적용한다.

클라이언트 TypeScript는 이 제약을 받지 않으며 `main.ts`처럼 간결한 화살표 함수와 템플릿 문자열을 쓴다.

## 아키텍처

### 실행 구조

`RoomHost`(`Networking/RoomHost.cs`)는 싱글턴 `BackgroundService`이고 모든 방을 `ConcurrentDictionary`로 들고 있다. `ExecuteAsync`의 `PeriodicTimer` 하나가 `Rules.Step`(1/30초)마다 깨어나 이번 틱의 방 목록을 만든 뒤 `StepRoom`으로 각 방을 진행하고, 0.1초마다 스냅샷을 보낸다. 방이 둘 이상이면 `Parallel.For`로 **동시에** 진행한다. 방마다 상태와 잠금이 독립이라 가능한 것이며, 아직 부하를 측정한 적은 없다. 한 틱에 진행하는 시간은 0.25초(`MaxTickSeconds`)로 자른다. 서버가 절전 등으로 오래 멈췄다 깨어났을 때 밀린 시간을 한꺼번에 따라잡으면, 아무도 보지 못하는 사이에 대원이 죽고 작전이 끝나기 때문이다. 방은 마지막 연결이 끊긴 뒤 15분이 지나면 사라진다(`Room.Expired`). `Detach`가 `LastVisit`를 갱신하므로 기준은 마지막 연결이 끊긴 시각이다. 대원이 아무도 남지 않은 방은 30초 뒤에 지운다. 빈 방으로 방 32개 상한을 막아 두지 못하게 하기 위해서다.

방 상태를 건드리는 모든 경로가 `lock (room.Gate)`를 잡는다. HTTP 참가, WebSocket 접속·입력, 시뮬레이션 루프가 같은 자료구조를 공유하므로 **새 경로를 추가할 때도 이 잠금 안에서 `Match`를 만진다.** 방 삭제(`StepRoom`)도 그 방의 잠금 안에서 일어난다. 그래서 방을 찾은 뒤 잠금을 잡는 경로는 잠금 안에서 `IsRegistered`로 방이 아직 등록되어 있는지 다시 확인한다. `Join`과 `Attach`가 그렇게 한다. 연결마다 용량 2의 `BoundedChannel`(`DropOldest`)을 두어 느린 수신자에게는 오래된 스냅샷을 버린다.

`Program.cs`가 `/health`, `POST /api/rooms`, `POST /api/rooms/{code}/join`, `/ws`를 연결하고 요청 본문 4096바이트 상한과 IP당 분당 30회 참가 제한을 건다.

### Match — 한 방의 전체 상태

`Match`는 `partial class`로 세 파일에 나뉜다. 어디를 고칠지 먼저 판단한다.

| 파일 | 담당 |
| --- | --- |
| `Game/Match.cs` | 참가·장비·투입·입력 검증, `Step`/`StepOnce` 진행 순서, 마나·보급·증원·전역 충격·임무 종료, `Visible` |
| `Game/Match.Combat.cs` | 접촉 → 유효 대응 수집 → 완전 파훼 → 부분 대응·비김·패배 → 후속 반격, 시설·벽 파괴 |
| `Game/Match.Movement.cs` | 몸 충돌, 적 생성, 흐름장(flow field) 경로, 적 조향과 예고 시작 |

`Match.Step(dt)`은 받은 `dt`를 `Rules.Step` 이하 조각으로 **쪼개서** `StepOnce`를 반복 호출한다. 서버 틱이 늦어도 0.25초 적 공격 창이나 0.55초 방어 창, 몸 충돌을 통째로 건너뛰지 않기 위한 설계이며 `CombatTests`의 `LongStep`이 이를 검사한다. 판정을 추가할 때 이 가정을 깨지 않는다.

### 전투 — 근접 RPS

상성은 **타격 < 방어 < 잡기(채널) < 타격**이고 `Affinity.Beats(response, attack)` 한 곳에만 있다.

주의할 구분: `Affinity.Beats`는 **기술 id가 아니라 `Kind`를 비교한다.** 플레이어 기술 id `strike`/`parry`/`grab`의 `Kind`는 각각 `strike`/`block`/`channel`이다. `parry`를 `"parry"`로 비교하면 조용히 틀린다.

기술별 동작은 id가 아니라 `SkillDefinition`의 속성으로 판단한다. `GuardsFront`(먼저 치지 않고 전방 120°로 들어오는 공격에만 반응), `HasFollowup`(유효 시간 뒤 상성 없는 반격), `SingleTarget`(가장 가까운 한 명만 판정), `BreaksStructures`(시설·벽 파괴)다. **판정 코드에 `Skill.Id == "parry"` 같은 비교를 다시 넣지 않는다.** 새 기술은 속성만 정하면 같은 판정을 받는다. `CombatTests`의 `SkillFlags`가 id만 다른 기술로 네 동작을 모두 확인한다. 클라이언트도 `rules.skills`의 같은 속성으로 방어 호와 반격을 그린다.

적은 조향 방식으로 움직인다. 움직이기 전에 방향을 한 번 정하고 `MoveBody`는 한 번만 부른다. 몸이 직선으로 지나갈 수 있으면 직진하고(`BodyCanPass`: 몸 중심선과 양옆 두 선이 모두 열려 있음), 아니면 흐름장을 따른다. 그 방향에 이웃 적과의 분리 방향(`Separation`)을 더한다. 막혔다고 다른 방향을 거듭 시도하지 않는다. `EncounterTests`의 `CornerSlip`이 사선은 열려 있지만 몸이 모서리에 걸리는 경우를 검사한다.

- 일반 적(1티어): 승리 시 피해 없이 처치, 비김은 서로 피해 1, 패배는 플레이어만 피해 1. 무행동 적에게는 일반 피해 1.
- 2티어 엘리트 행동은 동시에 유효한 대응 **두 개**가 필요하고 동료의 대응과 합산된다. 완전 파훼는 체력과 무관한 처치이며 같은 공격 범위의 무행동 동료까지 보호한다.
- 적의 예고(telegraph)에는 이미 상성이 있고 회복(recovery) 중에는 없다.
- 받아치기는 전방 `Rules.GuardHalfAngle`를 방어한 뒤 상성 없는(`kind: "none"`) 일반 반격을 낸다. 빈 방어 뒤에도 반격하고, 상성 패배 시에는 취소한다.

수치와 정의의 위치: `Game/Rules.cs`(기술 비용·유효 시간, `AllowEliteEnemies`, `EnemyWindupMultiplier`, 체력·마나·시야), `Game/SkillDefinition.cs`(`Affinity`, `EnemySkill`, `EnemyDefinition`의 Grunt/Breaker/Warden), `Game/Model.cs`(`Player`·`Enemy`·`PlayerAction`·`EnemyAction` 상태와 중복 판정 기록용 `HashSet`).

첫 작전은 1티어 `Grunt`만 배치·생성한다. `Breaker`와 `Warden`은 정의만 남아 있고 `Rules.AllowEliteEnemies`를 켠 작전에서만 나온다.

### 서버 권위와 시야 — 치트 방지 불변식

이 저장소는 게임 서버의 권위와 치트 방지를 핵심 목표로 삼는다. 아래는 깨뜨리면 안 되는 규칙이다.

- `Snapshot.CreateShared(match)`가 **직렬화 전에** 시야 밖 적과 예고, 효과를 제외한다. 팀 시야는 캠프 반경과 살아 있는 대원의 시야를 합친 것이며 `Match.Visible`에 벽 가림까지 포함된다. 대기·사망 대원은 시야를 주지 않는다. 적 관련 필드를 추가할 때 이 필터를 우회하지 않는다.
- `Match.Deploy`는 **공개된 벽만** 검사한다. 즉시 응답으로 숨은 적의 위치를 탐색할 수 없게 하려는 의도이며, 적과의 겹침은 5초 뒤 실제 도착 직전에 검사한다. 도착 때 막혀 있어도 실패시키지 않고 `Match.FindLanding`이 원래 지점이 보이는 가장 가까운 빈 자리를 고른다. **결과가 숨은 적에 따라 달라지는 동작은 실패나 거절로 알리지 않는다.** 그 결과가 곧 안개 속을 엿보는 수단이 된다.
- 클라이언트가 보내는 것은 이동 방향·조준 방향·슬롯 번호뿐이다. `Match.Input`이 순번 역행·중복, 비유한수, 범위 초과를 거르고 이동 벡터를 정규화한다. `x`/`hp`/`mana`/`damage` 같은 필드로 상태를 정할 수 없다.
- 맵 구조, 벽 파괴, 시설, 보급, 목표 진행은 공개 정보다.
- 서드파티 클라이언트를 허용하므로 공식 클라이언트에서만 동작하는 장치로 규칙을 강제하지 않는다.
- **보이지 않는 곳에서 일어나는 사건에 공개 순번을 쓰지 않는다.** 효과와 행동의 id는 `Match._nextId` 순번이다. 안개 속 생성처럼 숨은 사건이 같은 순번을 쓰면, 보이는 id 사이의 빈 번호로 그 횟수가 드러난다. 그래서 적 id는 `NewEnemyId`가 무작위로 정한다.
- WebSocket 압축(permessage-deflate)을 켜 두었다. **서버가 보내는 메시지에 토큰 같은 비밀을 넣지 않는다.** 비밀이 플레이어 이름처럼 다른 사람이 정할 수 있는 값과 같은 압축 문맥에 들어가면 압축된 크기로 비밀을 추측할 수 있다(CRIME/BREACH). 지금은 숨겨진 적이 직렬화 전에 빠지고 토큰은 HTTP 응답으로만 나가므로 안전하다.

`client/tests/game.spec.ts`의 위조 상태·잘못된 장비·순번 재생·신원 탈취 검사가 이 경계를 지킨다.

### 프로토콜

통신 형식은 [docs/protocol.md](docs/protocol.md)의 protocol v3다. `/health`의 `protocol`과 스냅샷의 `version`이 모두 3이다. `Game/Snapshot.cs`(공개 상태 선별)와 `Game/SnapshotMessage.cs`(전송 자료구조)가 서버 쪽이고, `client/src/types.ts`가 같은 모양을 TypeScript로 옮겨 둔 것이다.

방송할 때 `RoomHost.PublishSnapshots`는 `Snapshot.CreateShared`로 팀 공통 부분(시야 계산 포함)을 한 번만 만들고, 수신자마다 `Snapshot.Personalize`로 `you`, 보급 `cooldown`, 그리고 `walls`·`rules`·`map`을 담을지를 정한 뒤 직렬화한다. `walls`는 그 연결이 아직 받지 못한 판일 때만, `rules`·`map`은 아직 받지 못했을 때만 담는다. 받았는지는 **보낸 뒤에** `Peer.MarkSent`가 기록한다(`Program.Send`). 만들 때 기록하면, 느린 연결에서 그 스냅샷이 버려졌을 때 클라이언트가 바뀐 벽을 영영 받지 못한다. 클라이언트(`main.ts`)와 임무 검사 봇은 빠진 값을 같은 방의 이전 스냅샷에서 이어 쓴다. **수신자별 값을 새로 추가하면 반드시 `Personalize`에서 매번 덮어쓴다.** 같은 메시지 객체를 다음 수신자에게 재사용하므로, 빠뜨리면 앞 수신자의 값이 그대로 전송된다. `LifecycleTests`의 `SharedSnapshot`이 이를 검사한다.

**전송 형식을 바꾸면 세 곳을 함께 갱신한다**: `SnapshotMessage.cs`, `client/src/types.ts`, `docs/protocol.md`. 이전 버전과는 호환되지 않으므로 서버와 브라우저 빌드를 같이 배포한다. 통신 형식을 깨는 변경은 버전을 올린다.

### 클라이언트

- `client/src/main.ts`: 로비, 방 생성·참가, 네 슬롯 입력, HUD, 재접속. 복구 토큰을 탭별 `sessionStorage`에 둔다.
- `client/src/world.ts`: PixiJS 렌더링, 스냅샷 보간, 이동 예측과 서버 재조정. 하드웨어 WebGL이 없으면 Canvas 렌더러로 내려간다(Playwright는 `--disable-webgl`로 이 경로를 검사한다). 라벨(`Text`)은 쓰지 않게 되면 숨겨 두었다가 다시 쓰고, 64개를 넘는 것만 해제한다. 매번 새로 만드는 비용이 Canvas에서 라벨 하나에 약 30µs였다.
- 서버 재조정: `main.ts`의 `sendInput`이 실제로 보낸 입력만 `World.recordInput`으로 기록하고, `update`가 스냅샷의 `ack` 이하를 버린다. `draw`는 서버 위치 위에 남은 입력을 `replay`로 다시 적용한 곳을 보정 목표로 삼는다. **입력을 보내는 새 경로를 만들면 반드시 `sendInput`을 거친다.** 기록되지 않은 입력은 예측에서 빠져 화면이 서버와 어긋난다. 서버는 이 과정에 관여하지 않고, 클라이언트 시각은 화면 계산에만 쓴다.
- 클라이언트는 모양·진행률·예고만 그린다. 판정 결과를 결정하지 않는다.

### 접속과 복구

게스트 참가 후 서버가 32바이트 난수 토큰을 발급하고, 클라이언트는 `/ws`에서 `hello`로 제시한다. 같은 토큰의 새 연결이 기존 연결을 교체한다. 연결이 끊긴 대원은 입력이 멈춘 채 피격 가능한 몸으로 120초(`Rules.ReconnectSeconds`) 남고, 재접속은 체력·마나·장비·기술 시간을 유지한다. HTTP로 참가만 하고 한 번도 접속하지 않은 자리는 `Rules.FirstConnectSeconds`(15초) 뒤에 풀린다. `RoomHost.Join`이 켜고 `Attach`가 끄는 `Player.AwaitingFirstConnection`으로 두 경우를 구분한다. `Connection == 0`으로 판단하면 `RoomHost`를 거치지 않는 검사 대원이 짧은 유예를 받아 기존 검사가 깨진다.

### 운영 범위

메모리 기반 **단일 인스턴스**다. DB도 영구 저장도 없고 서버를 재시작하면 방과 토큰이 사라진다. 여러 복제본 사이에 방 상태를 공유하지 않으므로 수평 확장하려면 방 배정과 연결 라우팅이 먼저 필요하다. 방 32개·방당 적 60마리는 설정 상한이지 부하 보장값이 아니다.

## 문서

| 문서 | 내용 |
| --- | --- |
| [docs/requirements.md](docs/requirements.md) | 합의한 목표와 범위. 무엇을 넣지 않기로 했는지 포함 |
| [docs/combat.md](docs/combat.md) | 확정된 전투 판정 규칙, 시험 수치, 아직 규칙이 아닌 임시 처리 |
| [docs/protocol.md](docs/protocol.md) | protocol v3 통신 형식과 버전별 변경 |
| [docs/verification.md](docs/verification.md) | 날짜별 검증 기록과 실제 확인한 범위 |

`docs/combat.md`는 세 층으로 나뉜다. **합의한 규칙**은 바꾸기 전에 확인을 받고, **시험 설정**의 수치는 자유롭게 조정하며, **규칙으로 확정하지 않은 임시 처리**는 지금 적당히 맞춰 둔 동작이라 바꿔도 규칙 위반이 아니다. 어느 층에 해당하는지 먼저 판단한다. 게임 규칙을 바꾸면 해당 절을 함께 고친다. 검증을 수행했으면 `docs/verification.md`에 날짜와 확인 범위를 남긴다. 가독성 정리처럼 동작을 바꾸지 않아야 하는 작업에서는 게임 규칙과 통신 형식을 보존하고, 서버 검사와 브라우저 검사로 회귀가 없음을 확인한다.

## Docker

`docker compose up --build`는 클라이언트 빌드와 서버 publish를 한 이미지로 묶어 `127.0.0.1:5077`에 노출한다. 컨테이너 내부 포트는 8080이다.
