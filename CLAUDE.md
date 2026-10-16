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

서버 검사는 xUnit이 아니라 `tests/Argus.Server.Tests/Program.cs`의 자체 콘솔 하네스다. 필터 인자가 없고 `Main()`이 다섯 개의 `Register` 호출로 모든 검사를 등록한 뒤 순서대로 실행한다. 전체가 수 초면 끝나므로 보통 그대로 돌린다. 하나만 보려면 `Program.cs`의 `Register` 호출을 임시로 줄이거나 해당 `*Tests.cs`의 `Register`에서 `tests.Add` 줄을 임시로 주석 처리한다. **임시 수정을 되돌리고 전체를 다시 통과시킨 뒤 커밋한다.**

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

`RoomHost`(`Networking/RoomHost.cs`)는 싱글턴 `BackgroundService`이고 모든 방을 `ConcurrentDictionary`로 들고 있다. `ExecuteAsync`의 `PeriodicTimer` 하나가 33ms(`TickClock.TimerPeriod`)마다 깨어난다. `PeriodicTimer`는 주기를 밀리초 단위로 내려 쓰므로, `Rules.Step`(1/30초)을 내린 값을 직접 넘기고 검사도 같은 값을 쓴다. 깨어날 때마다 `TickClock`이 시작한 뒤 지난 시간으로 진행할 틱 수를 정하고, 이번 틱의 방 목록을 만든 뒤 `StepRoom`으로 각 방을 그 틱 수만큼 진행한다. 한 틱에 못 미치는 남은 시간은 다음에 깨어날 때로 넘긴다. 깨어나는 시각이 흔들리므로 가끔은(실측 40번에 한 번쯤) 진행할 틱 없이 깨어나고, 그때는 상태가 그대로이므로 방송도 미룬다. 스냅샷은 0.1초마다 보낸다(`PublishSchedule`). 다음 방송 시각은 실제로 깨어난 시각이 아니라 예정 시각에서 0.1초 뒤로 잡는다. 깨어난 시각에서 잡으면 타이머 주기 세 번(99ms)이 0.1초에 못 미쳐 방송이 자주 한 번씩 밀리고 8Hz 안팎이 된다. 방이 둘 이상이면 `Parallel.For`로 **동시에** 진행한다. 방마다 상태와 잠금이 독립이라 가능한 것이며, 아직 부하를 측정한 적은 없다. 한 번 깨어날 때 진행하는 틱은 8틱(약 0.27초, `TickClock.MaxTicksPerWake`)까지이고, 넘친 시간은 게임 시간에서 버린다. 서버가 절전 등으로 오래 멈췄다 깨어났을 때 밀린 시간을 한꺼번에 따라잡으면, 아무도 보지 못하는 사이에 대원이 죽고 작전이 끝나기 때문이다. 연결이 끊긴 대원은 재접속 유예(120초)가 지나면 빠지고, 대원이 아무도 남지 않은 방은 곧바로 지운다. 그래서 방은 보통 마지막 연결이 끊긴 뒤 2분쯤 지나 사라진다. `Room.Expired`의 15분(`IdleRoomGrace`)은 대원이 어떤 이유로 빠지지 않을 때를 대비한 안전장치다. 기준은 `Detach`가 갱신하는 `LastVisit`, 곧 마지막 연결이 끊긴 시각이다. 방을 만들고 첫 대원을 넣기 전의 틈만 2초 기다린다(`EmptyRoomGrace`). 그래서 접속 없이 참가만 한 방은 자리가 풀리는 15초 무렵 사라진다. 한 주소가 동시에 가질 수 있는 방은 4개다(`RoomHost.MaxRoomsPerAddress`, 넘으면 429). 둘 다 빈 방이나 붙잡아 둔 방으로 방 32개 상한을 채우지 못하게 하려는 것이다. 같은 기계와 사설망(10/8, 172.16/12, 192.168/16, 169.254/16, IPv6 fc00::/7·fe80::/10)에서 온 접속은 이 상한에서 뺀다(`AddressKeys.ForRoomLimit`). 개발과 브라우저 검사는 한 주소에서 방을 계속 만들고 끝난 방도 재접속 유예 동안 2분쯤 남는다. 또 Docker로 띄우면 모든 요청이 브리지 주소(172.17.0.1 등)에서 온다. 그래서 이 주소들까지 세면 방 4개가 남은 동안 모든 사용자의 방 생성이 막힌다. 두 제한 모두 IPv6는 /64 대역을 한 주소로 센다. 사용자 한 명이 보통 /64를 통째로 받아 요청마다 주소를 바꿀 수 있어서다. 역방향 프록시 뒤에서는 `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`로 전달 헤더(X-Forwarded-For)를 켜야 사용자마다 따로 센다. 이 설정은 앞단을 모두 믿으므로 프록시가 X-Forwarded-For를 덮어쓰거나 뒤에 덧붙이는 구성에서만 켜고, 서버를 직접 노출할 때는 켜지 않는다. 켜면 클라이언트가 헤더로 주소를 바꿔 제한을 피할 수 있다.

방 상태를 건드리는 모든 경로가 `lock (room.Gate)`를 잡는다. HTTP 참가, WebSocket 접속·입력, 시뮬레이션 루프가 같은 자료구조를 공유하므로 **새 경로를 추가할 때도 이 잠금 안에서 `Match`를 만진다.** 방 삭제(`StepRoom`)도 그 방의 잠금 안에서 일어난다. 그래서 방을 찾은 뒤 잠금을 잡는 경로는 잠금 안에서 `IsRegistered`로 방이 아직 등록되어 있는지 다시 확인한다. `Join`과 `Attach`가 그렇게 한다. `StepRoom`은 진행 중 예외를 잡아 연속 실패를 센다. 30번(약 1초) 연속 실패하면 방을 닫고 연결도 닫는다. 오류는 연속 실패의 첫 번째만 기록한다. 토큰 정리는 대원이 실제로 빠져 `Match.Departures`가 바뀌었을 때만 한다. 연결마다 용량 2의 `BoundedChannel`(`DropOldest`)을 두어 느린 수신자에게는 오래된 스냅샷을 버린다. 오류 안내와 pong은 버려지면 안 되므로 따로 `Peer.Control` 통로(용량 32, 차면 새 메시지를 버림)로 보내고, `Peer.Send`가 스냅샷보다 먼저 보낸다. `Peer.Send`는 두 통로를 함께 기다릴 때 끝나지 않은 기다림을 다음 차례에 다시 쓴다. 차례마다 새로 기다리면 ping을 보내지 않는 연결에서 제어 통로의 기다림이 스냅샷마다 하나씩 쌓인다. 취소되면 기다림의 예외로 끝낸다. 취소된 기다림은 곧바로 끝나므로 넘어가면 헛돈다. 스냅샷 통로가 닫혀 끝날 때도 제어 통로에 남은 메시지는 마저 보낸다. 두 기다림이 함께 깨면 어느 쪽이 먼저인지 정해져 있지 않아, 비우지 않으면 남은 오류 안내를 버린다.

`Program.cs`가 `/health`, `POST /api/rooms`, `POST /api/rooms/{code}/join`, `/ws`를 연결하고 요청 본문 4096바이트 상한과 IP당 분당 30회 참가 제한을 건다. 방 만들기와 참가의 장비 검증과 실패 이유는 `RoomHost`가 정하고(`EntryFailure`), HTTP 처리는 그 값으로 상태 코드(400·409·429·503)만 고른다. 방을 만든 직후 참가에 실패하면 빈 방을 바로 지운다. WebSocket 처리(`HandleWebSocket`)는 연결 오류(`WebSocketException`·`OperationCanceledException`·`JsonException`)만 기록 없이 정리하고, 그 밖의 예외는 결함으로 보고 방 코드와 함께 기록한다. `InvalidOperationException`·`ArgumentException`을 연결 오류로 치면 목록 범위 초과 같은 게임 로직 결함이 기록 없이 사라진다. 그래서 클라이언트가 보낸 JSON 값은 `ValueKind`를 확인한 뒤 읽어, 잘못된 메시지가 이런 예외를 내지 않게 한다. 서버가 먼저 닫을 때(연결 교체는 4001, 방이 닫히는 등 그 밖에는 1000)는 `CloseFirst`가 남은 전송을 마치고 종료 메시지를 보낸 뒤 상대의 종료 응답을 1초까지 기다린다. 기다리는 수신을 먼저 취소하면 .NET이 소켓을 바로 중단(Aborted)하므로, 상대는 정상 종료 대신 1006을 받기 때문이다. 참가 정보가 틀린 연결도 종료 응답은 1초까지만 기다린다. 무기한 기다리면 답하지 않는 클라이언트가 인증 없이 연결을 붙잡아 둘 수 있다. 어느 길로 나가든 `finally`에서 `lifetime`을 취소하고 송수신 작업이 끝나기를 기다린 뒤 나간다. 그러지 않으면 소켓이 해제된 뒤에도 수신 작업이 남는다. 클라이언트가 먼저 닫아 상태가 `CloseReceived`이면 서버도 종료 응답을 보내 닫기를 마친다. 응답 없이 소켓을 해제하면 상대는 정상 종료 대신 1006을 받는다.

### Match — 한 방의 전체 상태

`Match`는 `partial class`로 네 파일에 나뉜다. 어디를 고칠지 먼저 판단한다.

| 파일 | 담당 |
| --- | --- |
| `Game/Match.cs` | 참가·장비·투입·입력 검증, `Step` 진행 순서, 마나·보급·증원·전역 충격·임무 종료, `Visible` |
| `Game/Match.Combat.cs` | 접촉 → 유효 대응 수집 → 완전 파훼 → 부분 대응·비김·패배 → 후속 반격, 시설·벽 파괴 |
| `Game/Match.Movement.cs` | 몸 충돌, 적 생성, 흐름장(flow field) 경로, 적 조향과 예고 시작 |
| `Game/Match.StateHash.cs` | 진행 상태 전체의 요약값(`StateHash`). 결정론 검사와 재현 확인에 쓴다 |

`Match.Step()`은 정확히 한 틱(`Rules.Step`)을 진행하며 진행할 시간을 받지 않는다. 지금 시각 `Now`(초)는 틱 번호 `Tick`에서 바로 구한다. 서버가 늦어지면 `StepRoom`이 `Step()`을 여러 번 부르므로 0.25초 적 공격 창이나 0.55초 방어 창, 몸 충돌을 통째로 건너뛰지 않는다. `CombatTests`의 `LongStep`이 가장 많이 밀린 경우(한 번에 8틱)를 검사한다. 판정을 추가할 때 이 가정을 깨지 않는다. 검사 도우미 `TestTools.Advance`도 정수 틱으로만 진행하며, 한 틱에 못 미치는 나머지는 한 틱으로 올려 센다.

### 결정론

코드·규칙·시드가 같은 두 방에 같은 틱마다 같은 입력을 넣으면 같은 상태가 된다. 서버가 언제 깨어나 한 번에 몇 틱을 진행하든 결과는 같다. `DeterminismTests`가 90초 동안 틱마다 `Match.StateHash()`를 비교해 이를 확인한다.

- **시각은 모두 틱 번호(`long`)로 저장하고 비교한다.** 투입·기술·적 행동·보급 대기·효과·연결 끊김·증원·흐름장의 마감과 안내가 나온 시각이 모두 틱이고, 탈출 진행도는 반 틱 단위로 센다. 초로 정한 기간은 `Rules.ToTicks`로 올려 틱 수로 바꾸고(0.18초는 6틱), 초는 스냅샷으로 보낼 때만 `Rules.ToSeconds`로 바꾼다. 효과와 안내는 모델 객체를 그대로 직렬화하지 않고 전송용 `EffectSnapshot`·`NoticeSnapshot`에 초로 옮겨 담는다. 보내는 시각은 모두 `Snapshot.Seconds`를 거쳐 소수 둘째 자리로 반올림한다. 그래서 같은 틱이면 `now`와 정확히 같은 값이 되고, 30을 곱해 반올림하면 틱 번호로 돌아간다. `LifecycleTests`의 `TimedWireShape`가 효과·안내·기술 시각의 모양과 값을 확인한다. 마감을 `Now + 5`처럼 초로 저장해 비교하면 실수 오차 때문에 같은 5초가 시작 틱에 따라 150틱도 되고 151틱도 된다. 여러 단계로 된 시간(적 행동의 예고·공격 창·회복, 받아치기의 유효 시간·반격)은 단계마다 올림하지 않고 시작부터 잰 초를 한 번에 올림한다. 단계마다 올림하면 끝이 한 틱씩 밀린다.
- 게임 진행에 쓰는 난수는 모두 `Match`의 `SeededRandom` 하나에서 뽑는다. `Game` 코드에서 `Random`, `RandomNumberGenerator`, `DateTime`, `Stopwatch`를 쓰지 않는다.
- `SeededRandom`은 순번마다 시드를 키로 HMAC-SHA256을 계산하는 카운터 방식이다. `System.Random` 같은 일반 생성기는 공개된 적 id 몇 개로 내부 상태를 되짚을 수 있어, 안개 속 생성 위치와 적의 다음 행동이 드러난다. 뽑는 방식을 바꾸면 같은 시드로 기록한 진행을 재현할 수 없다. `SeededSequence` 검사의 고정값이 이를 잡는다.
- 운영 방의 시드는 `Room`이 `RandomNumberGenerator`로 만든 32바이트다. 정수 시드(`new Match(code, rules, 73)`)는 검사와 재현용이다.
- `HashSet`과 `Dictionary`의 순회 순서에 진행을 맡기지 않는다. 지금은 포함 여부 확인에만 쓴다. 순회해야 하면 정렬하거나 `List`를 쓴다.
- 상태 필드를 새로 추가하면 `Match.StateHash`에도 넣는다. 빠뜨리면 그 필드가 갈라져도 검사가 잡지 못한다. 요약에는 방 코드와 규칙도 들어간다. 규칙은 JSON으로 통째로 적으므로 규칙 값을 추가할 때는 따로 챙기지 않아도 된다. 적 기술은 id가 없어 정의 안의 순서로 적는다.
- 남은 한계: 진행 계산은 `float` 벡터와 `Math.Sin`·`Cos`·`Atan2`·`Pow`를 쓴다. 같은 기계에서는 결과가 같지만, 운영체제나 CPU가 다르면 수학 함수 결과의 마지막 자리가 달라질 수 있다. 입력이 도착한 틱도 아직 기록하지 않는다.

### 전투 — 근접 RPS

상성은 **타격 < 방어 < 잡기(채널) < 타격**이고 `Affinity.Beats(response, attack)` 한 곳에만 있다.

주의할 구분: `Affinity.Beats`는 **기술 id가 아니라 `Kind`를 비교한다.** 플레이어 기술 id `strike`/`parry`/`grab`의 `Kind`는 각각 `strike`/`block`/`channel`이다. `parry`를 `"parry"`로 비교하면 조용히 틀린다.

기술별 동작은 id가 아니라 `SkillDefinition`의 속성으로 판단한다. `GuardsFront`(먼저 치지 않고 전방 120°로 들어오는 공격에만 반응), `HasFollowup`(유효 시간 뒤 상성 없는 반격), `SingleTarget`(가장 가까운 한 명만 판정), `BreaksStructures`(접촉 중 시설·벽 파괴), `FollowupBreaksStructures`(반격이 시설·벽 파괴)다. **판정 코드에 `Skill.Id == "parry"` 같은 비교를 다시 넣지 않는다.** 새 기술은 속성만 정하면 같은 판정을 받는다. `CombatTests`의 `SkillFlags`가 id만 다른 기술로 다섯 동작을 모두 확인한다. 클라이언트도 `rules.skills`의 같은 속성으로 방어 호와 반격을 그린다.

적은 조향 방식으로 움직인다. 움직이기 전에 방향을 한 번 정하고 `MoveBody`는 한 번만 부른다. 몸이 직선으로 지나갈 수 있으면 직진하고(`BodyCanPass`: 몸 중심선과 양옆 두 선이 모두 열려 있음), 아니면 흐름장을 따른다. 그 방향에 이웃 적과의 분리 방향(`Separation`)을 더한다. 막혔다고 다른 방향을 거듭 시도하지 않는다. `EncounterTests`의 `CornerSlip`이 사선은 열려 있지만 몸이 모서리에 걸리는 경우를 검사한다. `FlowHeading`은 자기 칸보다 비용이 낮은 이웃이 있을 때만 방향을 준다. 대원이 있는 출발점 칸에서 이웃 칸으로 향하면 오히려 멀어지기 때문이다. 몸 통과 판단의 옆선은 반지름보다 0.5 좁게 긋는다. 벽에 딱 붙은 적의 옆선이 벽 면 위에서 시작하면 거리 0에서 막혔다고 나온다.

- 일반 적(1티어): 승리 시 피해 없이 처치, 비김은 서로 피해 1, 패배는 플레이어만 피해 1. 무행동 적에게는 일반 피해 1.
- 2티어 엘리트 행동은 동시에 유효한 대응 **두 개**가 필요하고 동료의 대응과 합산된다. 완전 파훼는 체력과 무관한 처치이며 같은 공격 범위의 무행동 동료까지 보호한다.
- 적의 예고(telegraph)에는 이미 상성이 있고 회복(recovery) 중에는 없다.
- 받아치기는 전방 `Rules.GuardHalfAngle`를 방어한 뒤 상성 없는(`kind: "none"`) 일반 반격을 낸다. 빈 방어 뒤에도 반격하고, 상성 패배 시에는 취소한다.
- 적 행동의 상성은 그 행동이 향한 범위 안의 대원에게만 적용한다(`InActionArea`: 타격·잡기는 공격 범위, 방어는 밀어치기 범위와 전방 120°). 범위 밖에서 친 측면 공격은 상성 없이 일반 피해 1이고, 그 피해로 적이 쓰러지면 행동이 무효가 된다. 전방 120° 계산은 대원 방어·적 방어의 반격 차단·측면 판정이 모두 `Faces` 하나를 쓴다.
- 잡기 대상은 접촉하는 틱에 가장 가까운 적이다. 준비 동작 중에는 매 틱 다시 고르고 범위에 아무도 없으면 비운다. 접촉한 뒤 대상을 고르거나 판정을 받으면 `TargetLocked`로 고정한다. 비김 피해로 쓰러뜨린 처치는 비긴 대원이 가져간다.

수치와 정의의 위치: `Game/Rules.cs`(기술 비용·유효 시간, `AllowEliteEnemies`, `EnemyWindupMultiplier`, 체력·마나·시야), `Game/SkillDefinition.cs`(`Affinity`, `EnemySkill`, `EnemyDefinition`의 Grunt/Breaker/Warden), `Game/Model.cs`(`Player`·`Enemy`·`PlayerAction`·`EnemyAction` 상태와 중복 판정 기록용 `HashSet`).

첫 작전은 1티어 `Grunt`만 배치·생성한다. `Breaker`와 `Warden`은 정의만 남아 있고 `Rules.AllowEliteEnemies`를 켠 작전에서만 나온다. 처음 배치하는 자리는 `Match.InitialEnemySpots` 한 곳에서 정하고, 작전에 따라 종류만 바꾼다. 자리가 막혀 있으면 생성자가 예외를 낸다(`PlaceInitialEnemy`). `AddEnemy`는 막힌 자리를 아무 표시 없이 건너뛰므로, 처음 배치에 그대로 쓰면 적이 빠져도 알 수 없다. 증원(`SpawnEnemy`)은 막히면 다른 각도를 다시 뽑으므로 그대로 둔다.

### 서버 권위와 시야 — 치트 방지 불변식

이 저장소는 게임 서버의 권위와 치트 방지를 핵심 목표로 삼는다. 아래는 깨뜨리면 안 되는 규칙이다.

- `Snapshot.CreateShared(match)`가 **직렬화 전에** 시야 밖 적과 예고, 효과를 제외한다. 팀 시야는 캠프 반경과 살아 있는 대원의 시야를 합친 것이며 `Match.Visible`에 벽 가림까지 포함된다. 대기·사망 대원은 시야를 주지 않는다. 적 관련 필드를 추가할 때 이 필터를 우회하지 않는다.
- `Match.Deploy`는 **공개된 벽만** 검사한다. 즉시 응답으로 숨은 적의 위치를 탐색할 수 없게 하려는 의도이며, 적과의 겹침은 5초 뒤 실제 도착 직전에 검사한다. 도착 때 막혀 있어도 실패시키지 않고 `Match.FindLanding`이 원래 지점이 보이는 가장 가까운 빈 자리를 고른다. 근처와 캠프가 모두 막히면 반지름 640까지 넓혀 찾아 대원이 투입 중 상태에 갇히지 않게 한다. **결과가 숨은 적에 따라 달라지는 동작은 실패나 거절로 알리지 않는다.** 그 결과가 곧 안개 속을 엿보는 수단이 된다.
- 클라이언트가 보내는 것은 이동 방향·조준 방향·슬롯 번호뿐이다. `Match.Input`이 순번 역행·중복, 비유한수, 범위 초과를 거르고 이동 벡터를 정규화한다. `x`/`hp`/`mana`/`damage` 같은 필드로 상태를 정할 수 없다.
- 맵 구조, 벽 파괴, 시설, 보급, 목표 진행은 공개 정보다.
- 서드파티 클라이언트를 허용하므로 공식 클라이언트에서만 동작하는 장치로 규칙을 강제하지 않는다.
- **보이지 않는 곳에서 일어나는 사건에 공개 순번을 쓰지 않는다.** 효과와 행동의 id는 `Match._nextId` 순번이다. 안개 속 생성처럼 숨은 사건이 같은 순번을 쓰면, 보이는 id 사이의 빈 번호로 그 횟수가 드러난다. 그래서 적 id는 `NewEnemyId`가 방의 시드에서 무작위로 뽑는다.
- **시드를 클라이언트에 보내지 않는다.** 시드를 알면 적이 고를 행동과 안개 속에서 생길 적의 위치를 미리 계산할 수 있다. 시드에서 뽑은 값(적 id 등)은 공개해도 된다. 출력으로 다음 값을 알 수 없는 생성기(`SeededRandom`)이기 때문이다.
- WebSocket 압축(permessage-deflate)을 켜 두었다. **서버가 보내는 메시지에 토큰 같은 비밀을 넣지 않는다.** 비밀이 플레이어 이름처럼 다른 사람이 정할 수 있는 값과 같은 압축 문맥에 들어가면 압축된 크기로 비밀을 추측할 수 있다(CRIME/BREACH). 지금은 숨겨진 적이 직렬화 전에 빠지고 토큰은 HTTP 응답으로만 나가므로 안전하다.

`client/tests/game.spec.ts`의 위조 상태·잘못된 장비·순번 재생·신원 탈취 검사가 이 경계를 지킨다.

### 프로토콜

통신 형식은 [docs/protocol.md](docs/protocol.md)의 protocol v3다. `/health`의 `protocol`과 스냅샷의 `version`이 모두 3이다. `Game/Snapshot.cs`(공개 상태 선별)와 `Game/SnapshotMessage.cs`(전송 자료구조)가 서버 쪽이고, `client/src/types.ts`가 같은 모양을 TypeScript로 옮겨 둔 것이다.

방송할 때 `RoomHost.PublishSnapshots`는 `Snapshot.CreateShared`로 팀 공통 부분(시야 계산 포함)을 한 번만 만들고, 수신자마다 `Snapshot.CreatePersonal`로 `you`, 보급 `cooldown`, 그리고 `walls`·`rules`·`map`을 담을지를 정한다. `walls`는 그 연결이 아직 받지 못한 판일 때만, `rules`·`map`은 아직 받지 못했을 때만 담는다. 받았는지는 **보낸 뒤에** `Peer.MarkSent`가 기록한다(`Peer.Send`). 만들 때 기록하면, 느린 연결에서 그 스냅샷이 버려졌을 때 클라이언트가 바뀐 벽을 영영 받지 못한다. 클라이언트(`main.ts`)와 임무 검사 봇은 빠진 값을 같은 방의 이전 스냅샷에서 이어 쓴다. 직렬화도 공통 부분은 방송마다 한 번만 한다. `Snapshot.SerializeShared`가 공통 부분을 한 번 직렬화하고, `Snapshot.Combine`이 수신자별 `PersonalSnapshot`을 직렬화한 뒤 두 JSON의 바깥 중괄호 안쪽을 원본 배열에서 바로 복사해 쉼표로 잇는다. `SnapshotMessage`에는 수신자별 속성이 없으므로 공통 부분에 수신자별 값이 섞일 수 없다. 연결 직후 한 명에게 보내는 `Snapshot.Create`도 같은 방식으로 만든다. **수신자별 값을 새로 추가하면 `PersonalSnapshot`에 넣는다.** `SnapshotMessage`에 넣으면 모든 수신자에게 같은 값이 간다. `LifecycleTests`의 `SharedSnapshot`이 수신자별 값을 확인하고, 그 값을 뺀 나머지가 공통 부분과 같은지 확인한다.

**전송 형식을 바꾸면 세 곳을 함께 갱신한다**: `SnapshotMessage.cs`, `client/src/types.ts`, `docs/protocol.md`. 이전 버전과는 호환되지 않으므로 서버와 브라우저 빌드를 같이 배포한다. 통신 형식을 깨는 변경은 버전을 올린다.

### 클라이언트

- `client/src/main.ts`: 로비, 방 생성·참가, 네 슬롯 입력, HUD, 재접속. 안내는 서버가 붙인 순번(`id`)으로 새 것을 가려 모두 띄운다. 접속 직후 받은 지난 안내는 가장 최근 것만 띄운다. 알림(`toast`)은 줄마다 3.5초씩 보이고 최대 세 줄까지 쌓인다. 같은 말을 거듭 띄우면 줄을 늘리지 않고 시간만 늘리지만, 안내는 순번이 달라 같은 문구라도 따로 띄운다. 마우스 버튼 상태는 `pointerdown`·`pointermove`·`pointerup`의 `event.buttons`로 다시 맞춘다(`syncButtons`). **버튼을 겹쳐 누르거나 뗄 때 브라우저는 `pointerdown`/`pointerup` 대신 `pointermove`만 보낸다.** `event.button`만 보면 두 번째 버튼이 빠지고 놓은 버튼이 눌린 채로 남는다. 입력 정리(`clearInput`)는 작전 중 살아 있을 때만 서버에 멈춤 입력을 보낸다. 살아 있지 않을 때 누른 기술 키와, 살아 있는 상태를 벗어날 때 눌려 있던 기술 입력은 떼었다 다시 눌러야 나간다(`staleKeys`, `releaseSkills`). 그대로 두면 투입을 기다리며 쥔 키가 착지 뒤 마나가 차는 순간부터 기술을 계속 낸다. `slotHeld`도 이런 키를 누른 것으로 치지 않는다(`keyHeld`). 같은 슬롯의 다른 입력을 새로 눌렀다 떼었을 때 쥐고 있던 키로 기술이 이어지지 않게 하려는 것이다. 투입 버튼은 요청을 보내는 즉시 막고, 서버의 답(투입 중 상태나 오류 안내)이 오거나 연결이 끊기면 푼다(`deployRequested`). 입력을 비운 뒤(`clearInput`) 처음 받은 `pointermove`·`pointerup`의 버튼은 새 누름으로 치지 않고 기준값으로만 받는다(`pointerBaseline`). 0으로 비우면 창을 다녀온 뒤 쥐고 있던 버튼이 첫 움직임에서 새로 누른 것이 된다. `pointerdown`은 아무 버튼도 누르지 않은 상태에서만 오므로 그대로 새 누름으로 본다. 작전이 끝나는 순간에는 한 번만 입력을 비운다. 복구 토큰을 탭별 `sessionStorage`에 둔다.
- `client/src/world.ts`: PixiJS 렌더링, 스냅샷 보간, 이동 예측과 서버 재조정. 하드웨어 WebGL이 없으면 Canvas 렌더러로 내려간다(Playwright는 `--disable-webgl`로 이 경로를 검사한다). 라벨(`Text`)은 쓰지 않게 되면 숨겨 두었다가 다시 쓰고, 64개를 넘는 것만 해제한다. 매번 새로 만드는 비용이 Canvas에서 라벨 하나에 약 30µs였다. **원호는 `arcOnNewPath`로만 그린다.** Pixi는 `fill`·`stroke` 뒤의 다음 경로를 직전 경로의 마지막 점에서 시작한다. 그런데 직전 경로가 원·사각형·다각형 같은 도형으로 끝나면 마지막 점을 계산하지 않고, 읽을 때마다 0으로 되돌리는 임시 점(`Point.shared`)을 그대로 쓴다. 그러면 원호가 지도 원점(왼쪽 위)에서부터 직선으로 이어진다. `arcOnNewPath`는 `beginPath()`로 새 경로를 연 뒤 원호를 그린다. `world.spec.ts`의 원호 검사가 기대하는 원호를 반지름과 중심으로 찾고, 그 앞에 다른 점이 붙지 않았는지 확인한다.
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

이 구성에서는 서버가 모든 요청을 Docker 브리지 주소(172.17.0.1 등)에서 온 것으로 본다. 127.0.0.1에 연 포트는 docker-proxy를 거치고, Docker Desktop도 마찬가지다. 그래서 주소 기준 제한이 사용자를 구분하지 못한다. 브리지 주소는 사설망이라 방 수 상한에서 빠지고, 참가 제한(분당 30회)은 모든 사용자가 함께 쓴다. 지금은 같은 기계에서만 접속하므로 문제가 없다. **공개 배포 전에는 실제 접속 주소가 서버에 닿게 해야 한다.** 앞에 역방향 프록시를 두고 `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`를 켜는 방법이 있다. Linux에서 공개 주소에 포트를 여는 방법도 있는데, 이때는 보통 iptables가 원래 주소를 그대로 넘긴다. 어느 쪽이든 배포 환경에서 서버가 보는 주소를 먼저 확인한다.
