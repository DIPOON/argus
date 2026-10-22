# Argus protocol v3

2026-10-09. JSON over HTTP + WebSocket, UTF-8. 좌표 단위는 월드 단위, 시간은 서버 초, 각도는 라디안이다. 서버는 좌표를 4096분의 1 단위 고정소수점 정수로 계산하고, 좌표와 몸 반지름은 소수 둘째 자리로 반올림해 보낸다. 딱 절반이면 0에서 먼 쪽으로 간다(0.125는 0.13). 4096분의 1 단위에는 0.125처럼 소수 셋째 자리가 딱 5인 값이 실제로 있다. 원점은 왼쪽 위다. X는 오른쪽, Y는 아래쪽이다. 서버는 1/30초 틱으로 진행하며, 시간 필드는 세 가지다.

- **틱 시각과 틱 수**: `now`, `elapsed`, `nextPulse`, 기술의 `startedAt`·`contactAt`·`activeUntil`·`followupUntil`·`endsAt`, 효과의 `until`, 안내의 `at`, 대원의 `busy`·`deploy`, 보급의 `cooldown`. 틱을 초로 바꿔 소수 둘째 자리로 반올림해 보낸다. 한 틱은 약 0.033초라 반올림해도 틱끼리 구분된다. 같은 틱의 시각은 `now`와 정확히 같고, 값에 30을 곱해 반올림하면 틱 번호가 된다.
- **규칙 값**: `rules.deploy`, `rules.parryFollowupSeconds`, `rules.skills`의 `contactAfter`·`activeSeconds`·`duration`, `extraction.duration`. 설정한 초를 그대로 보낸다. 서버는 이 기간을 틱으로 올려 세므로, 30을 곱해도 틱 수가 되지 않을 수 있다. 기술의 기간은 모두 `startedAt`부터 잰 초이고 각각 올린다. 예를 들어 `contactAfter` 0.18초는 6틱(0.2초)이다. `parryFollowupSeconds`만은 따로 올리지 않는다. `startedAt`부터 `activeSeconds + parryFollowupSeconds`를 합친 뒤 틱으로 올리고, 그 시각을 기술 객체의 `followupUntil`로 보낸다. `activeUntil`에 반격 시간을 따로 더하면 반격 끝이 한 틱까지 어긋나므로 `followupUntil`을 쓴다. 지금 수치로 센 예는 [전투 명세의 코드 읽기 절](combat.md#코드-읽기)에 있다.
- **탈출 진행도**: `extraction.progress`. 반 틱 단위로 센 값을 초로 바꿔 소수 둘째 자리로 반올림한다. 반 틱 간격(1/60초 ≈ 0.017초)이 반올림 오차(최대 0.005초)의 두 배보다 크므로, 값에 60을 곱해 반올림하면 정확한 반 틱 수가 된다.

`/health`의 `protocol`과 snapshot의 `version`은 3이다.

v2 → v3(2026-10-14): `walls`는 수신자가 아직 받지 못한 판일 때만, `rules`와 `map`은 수신자가 아직 받지 못했을 때만 스냅샷에 담긴다. 빠진 값은 같은 방의 이전 스냅샷 값을 그대로 쓴다. `rules.skills`에 기술별 동작(`guardsFront`, `hasFollowup`, `singleTarget`, `breaksStructures`)이 추가되었고, 적 `id`는 무작위가 되었다.

v3 안에서 추가(2026-10-15): `rules.skills`에 `followupBreaksStructures`가 늘었다. 값이 하나 추가되기만 해서 버전은 그대로 3이다.

v3 안에서 추가(2026-10-16, 버그 제보): 클라이언트 메시지 `report`·`reportNote`와 서버의 답 `reported`·`reportNoted`가 늘었다. 이 메시지를 모르는 서버는 무시하므로 버전은 그대로 3이다.

v3 안에서 추가(2026-10-16): `notices`에 순번 `id`가 늘었다. 기술·효과·안내의 시각도 다른 시간 필드처럼 소수 둘째 자리로 반올림해 보낸다. 이전에는 이 시각들만 반올림 없이 나갔다. 값이 하나 추가되고 정밀도만 바뀌어 버전은 그대로 3이다.

v3 안에서 추가(2026-10-17): 기술 객체에 반격이 끝나는 시각 `followupUntil`이 늘었다. 이전에는 `activeUntil`에 `parryFollowupSeconds`를 더해 반격 끝을 구해야 했다. 서버는 두 기간을 합친 초를 한 번에 틱으로 올리므로, 이렇게 구한 끝은 서버와 한 틱 미만만큼 어긋났다. 값이 하나 추가되기만 해서 버전은 그대로 3이다.

v3 안에서 바뀜(2026-10-18): 대원과 기술 객체의 `aim`을 한 바퀴 65536칸으로 반올림해 보낸다. 서버가 운영체제의 삼각함수 대신 정수 각도로 계산하기 때문이다. 형식은 그대로 라디안이라 버전은 그대로 3이다.

v3 안에서 바뀜(2026-10-20): 서버가 좌표와 거리를 고정소수점 정수로 계산한다. 보내는 형식은 그대로 실수다. 효과의 `x`, `y`도 다른 좌표처럼 소수 둘째 자리로 반올림해 보낸다. 이전에는 효과 좌표만 반올림 없이 나갔다. 좌표와 반지름, 시야 다각형의 정수 좌표는 딱 절반이면 0에서 먼 쪽으로 반올림한다. 이전에는 짝수 쪽으로 갔다(0.125는 0.12). 정밀도와 반올림만 바뀌어 버전은 그대로 3이다.

v3 안에서 추가(2026-10-21): 대원과 적에 예측용 좌표 `rawX`, `rawY`가 늘었다. 고정소수점의 Raw(4096분의 1 단위 정수)를 반올림 없이 보낸다. 공식 클라이언트는 이 값에서 출발해 서버와 같은 고정소수점 계산으로 자기 이동을 예측한다. 값이 추가되기만 해서 버전은 그대로 3이다.

v1 → v2: 총기·탄창·재장전·보조 장비·패시브 필드를 제거하고 네 슬롯의 근접 기술과 마나로 전환했다.

버전 정책: 하위 버전 호환은 유지하지 않는다. 프로토콜 버전이 올라가면 공식 클라이언트를 함께 갱신해 배포한다. 서드파티 클라이언트는 `/health`의 `protocol`과 스냅샷의 `version`을 확인하고, 지원 버전과 다르면 접속을 멈추고 갱신해야 한다. 현재 공식 클라이언트는 `version`이 3이 아니면 접속을 끊고 새로고침을 안내한다.

## 참가와 복구

`POST /api/rooms`, `POST /api/rooms/{code}/join`:

```json
{"name":"대원","slots":["strike","parry","grab","parry"]}
```

방은 모두 생성 작전이라 방마다 맵의 크기와 모양이 다르다(스냅샷의 `map`·`walls`로 받는다). 방을 만들 때 `"layout":"handmade"`를 주면 손으로 만든 40×30 맵의 방이 된다. 정해진 맵이 필요한 검사용이며, 서버 설정 `Argus:Missions:AllowHandmade`가 켜져 있지 않거나 다른 값이면 HTTP 403이다. 참가할 때는 쓰지 않는다.

slots를 생략하면 `["strike","parry","grab","strike"]`. 길이는 정확히 4다. 앞의 세 슬롯은 현재 출시된 해당 계열 기본기로 고정하며 자유 슬롯은 strike / parry / grab 중 하나다. 잘못된 조합은 HTTP 400이다. 이름은 최대 16 UTF-16 문자, 방당 최대 4명, 진행 중 난입 가능, 종료된 방에는 새 참가 불가. 방은 서버 전체에 32개까지이며 가득 차면 방 생성이 HTTP 503이다. 한 주소가 동시에 가질 수 있는 방은 4개이고 넘으면 HTTP 429다. 같은 기계와 사설망에서 온 접속은 이 상한에서 뺀다. IPv6는 /64 대역을 한 주소로 센다. 오류 응답 본문은 `{"error":"…"}`다. 실패하면 장비가 잘못된 경우 400, 참가할 수 없는 방(없음·가득 참·종료)이면 409, 주소별 상한이면 429, 서버가 가득 차면 503이다.

```json
{"room":"ABC234","id":"public-player-id","token":"private-recovery-token"}
```

토큰은 32바이트 난수의 64자리 hex다. 같은 호스트의 `/ws`에 연결한 뒤 8초 내 다음 메시지를 보낸다. HTTPS에서는 WSS를 사용한다.

```json
{"type":"hello","room":"ABC234","token":"private-recovery-token"}
```

잘못된 토큰은 소켓 1008로 종료하며, 종료 응답은 1초까지만 기다린다. 방이 닫히는 등 서버가 먼저 닫을 때는 1000으로 닫는다. 같은 토큰으로 접속하면 기존 연결은 4001로 교체하며 기존 상태를 유지한다. 이전 연결이 종료되어도 새 연결을 끊지 않는다. 클라이언트가 먼저 종료를 보내면 서버도 종료로 답해 정상 종료(1000)로 끝난다. HTTP 참가 후 한 번도 접속하지 않으면 15초, 접속했다가 끊긴 뒤에는 120초 유예 후 자리를 해제한다. 서버 재시작 복구는 지원하지 않는다.

메시지는 텍스트 JSON, 최대 4096바이트, 소켓당 초당 최대 90개다. 서버는 WebSocket permessage-deflate 압축을 협상하며, 지원하지 않는 클라이언트는 압축 없이 받는다. 4096바이트 제한은 압축을 푼 크기에 적용한다. HTTP 참가는 IP당 분당 30개 제한이며 IP는 게스트 신원이 아니다. IPv6는 /64 대역을 한 IP로 센다.

## 입력

```json
{"type":"input","seq":42,"moveX":0.7071,"moveY":0.7071,"aim":1.3,"slot":2}
```

- `seq`: 증가하는 정수, 0~9,000,000,000,000. 과거·중복 순번은 무시한다. 숫자가 아니면 그 입력을 무시한다. 재접속 시 snapshot의 ack보다 큰 값으로 이어간다.
- 공식 클라이언트는 `ack`로 서버 재조정을 한다. 입력을 서버의 틱과 같은 빠르기(평균 초당 30번)로 보내고, 보낸 입력을 순번과 함께 보관한다. 스냅샷이 오면 `ack` 이하의 입력을 버린 뒤, 남은 입력을 내 대원의 `rawX`, `rawY` 위에 하나에 한 틱씩 서버와 같은 고정소수점 계산으로 다시 적용해 그린다. 서버는 틱마다 마지막으로 받은 입력을 쓰므로, 입력이 한 틱에 하나씩 들어가면 예측은 서버와 Raw까지 같다. 같은 틱에 둘이 들어가거나 한 틱 동안 들어가지 않으면 그만큼 어긋나고 다음 스냅샷에서 바로잡힌다. 클라이언트 시각은 서버로 보내지 않으며 서버는 판정에 클라이언트 시각을 쓰지 않는다.
- `moveX`, `moveY`: 유한수, 각 절댓값 1.01 이하. 벡터 길이가 1을 넘으면 서버가 정규화한다. 실제 이동 거리는 서버 시간과 몸·벽 충돌로 결정한다.
- `aim`: 유한 라디안. 현재 방향이며 동작 중에도 변경 가능하다. 서버는 한 바퀴를 65536칸(한 칸은 약 0.0055°)으로 나눈 가장 가까운 방향으로 바꿔 다룬다. 스냅샷의 `aim`도 그 방향을 -π 이상 π 미만의 라디안으로 보낸다.
- `slot`: 정수 0~4. 0은 새 기술 사용 없음. 1~4는 해당 슬롯의 유지 입력이다. 현재 동작이 끝나고 마나가 충분하면 다음 동작을 시작한다. 중간 기술 교체·동작 시간 단축·기본기 중첩은 허용하지 않는다.
- 기술 시작 시 서버가 마나를 소비한다. 실제로 맞히지 않아도 환불하지 않는다. 같은 기술을 여러 메시지로 요청해도 한 틱에 여러 번 발동하지 않는다.
- 입력이 0.35초 이상 없거나 연결이 끊기면 이동과 추가 사용 입력을 중단한다. 이미 시작한 기술의 시간은 계속 흐른다.
- x/y/hp/mana/damage/tier/actions 같은 추가 필드로 서버 상태를 정할 수 없다. 예전 `fire` 필드는 슬롯 밖 사격을 발생시키지 않는다.

```json
{"type":"deploy","x":300,"y":730}
{"type":"loadout","slots":["strike","parry","grab","grab"]}
{"type":"ping","sent":1234.5}
{"type":"report"}
{"type":"reportNote","tick":1234,"text":"적이 벽을 지나간 것 같아요"}
```

`deploy`는 waiting 상태에서 공개된 벽과 겹치지 않는 위치를 선택할 수 있다. 이미 투입 중인 대원이 다시 요청하면 처음 고른 자리와 시간대로 진행하고 오류를 보내지 않는다. 요청 응답으로 숨겨진 적의 점유 여부가 드러나지 않도록 적과의 겹침은 실제 투입 직전에 검사한다. 투입 준비는 5초이고 선택만으로 시야가 생기지 않는다. 도착 시 적이 막고 있으면 원래 지점이 보이는 가장 가까운 빈 자리로 투입하고, 그런 자리가 없으면 캠프로, 캠프도 막혔으면 반지름 640까지 넓혀 찾는다. 실패로 되돌리지 않으므로 투입 결과로 숨겨진 적의 위치를 알 수 없다. 전멸 검사가 증원보다 우선한다.

`loadout`은 최초 투입 시작 전 waiting 상태에서만 허용한다. 사망·증원·재접속으로 다시 바꿀 수 없다. `ping`은 같은 sent의 pong으로 응답하며 클라이언트 시간은 게임 판정에 사용하지 않는다.

`report`는 버그 제보다. 이상한 일을 본 순간 보낸다. 서버는 그 방의 재현용 기록에 지금 틱과 대원을 남기고, 제보한 대원에게만 `{"type":"reported","tick":1234}`로 답한다. `text`를 함께 보내도 된다. 내용을 나중에 적으면 `reportNote`에 답으로 받은 `tick`과 `text`를 담아 보낸다. 서버는 그 대원의 마지막 제보에 한 번만 덧붙이고 `{"type":"reportNoted","tick":1234}`로 답한다. 내용은 제어 문자를 빼고 UTF-16 200단위까지 남기며, 이모지처럼 두 단위로 된 글자는 반으로 자르지 않는다. 게임 상태는 바꾸지 않으며 다른 대원에게 알리지도 않는다. 대원마다 5초에 한 번, 방마다 20개까지 받는다. 기록을 남기지 않거나 기록 쓰기에 실패한 방, 덧붙일 제보가 없거나 이미 덧붙인 경우, 빈 내용에는 `error`로 답한다. 방 코드와 `tick`이 있으면 운영자가 기록에서 그 순간을 그대로 다시 돌려 볼 수 있다.

## 스냅샷

연결 직후 한 번, 이후 10Hz(0.1초 간격)로 스냅샷을 전송한다. 한 번이 한 틱 늦게 나가면 다음 것이 그만큼 일찍 나가 평균 간격을 지킨다. 느린 수신자에게 쌓인 오래된 스냅샷은 버린다.

작전 중 바뀌지 않거나 드물게 바뀌는 `walls`, `rules`, `map`은 필요할 때만 담는다. 서버는 그 정보를 담은 스냅샷을 **실제로 보낸 뒤에야** 수신자가 받았다고 기록한다. 그래서 담긴 스냅샷이 느린 연결에서 버려져도 다음 스냅샷에 다시 담기고, 클라이언트가 처음 받는 스냅샷에는 세 값이 모두 있다. `mapRevision`은 매번 온다. 클라이언트는 이 값이 바뀌면 함께 온 `walls`로 벽을 갱신하고, 세 값이 빠진 스냅샷에서는 이전 값을 그대로 쓴다.

| 필드 | 내용 |
| --- | --- |
| type, version, room, you | snapshot, 3, 방 코드, 수신자 공개 ID |
| now, elapsed | 방 시간 / 첫 투입 이후 임무 시간. 임무 종료 후 elapsed 고정. 둘 다 틱 시각이다(맨 위 시간 필드 참고) |
| phase, result | staging / active / ended, null / success / failure |
| pulse, nextPulse | 전역 피해 회차 / 다음 피해까지 초 |
| map | width, height, cell, columns, rows, campX, campY. 아직 받지 못했을 때만 온다 |
| walls, mapRevision | 행 우선 byte 타일 배열의 Base64 / 파괴 버전. 0 빈 지면, 1 파괴 가능, 2 맵 경계. walls는 그 판을 아직 받지 못했을 때만 온다 |
| rules | deploy, speed, playerRadius, guardHalfAngle, parryFollowupSeconds, skills. 아직 받지 못했을 때만 온다 |
| sight | 팀 시야 다각형 배열. 다각형은 `[x0,y0,x1,y1,…]`이며 좌표는 정수다(딱 절반이면 0에서 먼 쪽으로 반올림) |
| players | 분대 전체 상태 |
| enemies | 팀 시야에 실제 들어온 적과 기술 예고 |
| effects | 시야 안의 효과: id, x, y(소수 둘째 자리로 반올림), kind, until(사라지는 시각, 초) |
| facilities | 공개 목표: id, x, y, hp, maxHp |
| supplies | 공개 보급: id, x, y, objective, collected, cooldown. cooldown은 수신 대원 기준 |
| extraction | x, y, progress, duration, unlocked |
| notices | 최근 안내 최대 6개: id, at(나온 시각, 초), text. id는 방마다 1부터 늘어나는 순번이라 같은 틱에 같은 문구가 와도 다르다. 클라이언트는 id로 새 안내를 가린다 |

`rules.skills`는 기본기별 `id, kind, mana, contactAfter, activeSeconds, duration, range, halfWidth, guardsFront, hasFollowup, singleTarget, breaksStructures, followupBreaksStructures`를 제공한다. kind는 strike / block / channel이다. 뒤의 다섯 값은 기술별 동작이다. `guardsFront`는 먼저 치지 않고 앞에서 들어오는 공격에만 반응함, `hasFollowup`은 유효 시간 뒤 상성 없는 반격, `singleTarget`은 가장 가까운 한 명만 판정, `breaksStructures`는 접촉 중에 시설과 벽을 부숨, `followupBreaksStructures`는 반격이 시설과 벽을 부숨을 뜻한다. 동작은 id가 아니라 이 값으로 정해진다. 이 수치를 클라이언트가 돌려보내도 서버 설정을 바꾸지 않는다.

대원:

```text
id, name, slots, state(waiting/deploying/alive), connected, hasDeployed,
x, y, rawX, rawY, aim, hp, maxHp, mana, maxMana, busy(남은 초), actions,
deploy(남은 초), kills, deaths, ack, landingX, landingY
```

`x`, `y`는 그리기용으로 소수 둘째 자리로 반올림한 좌표이고, `rawX`, `rawY`는 예측용으로 반올림하지 않은 Raw(4096분의 1 단위 정수, `x` × 4096)다. 적도 같다.

적:

```text
id, x, y, rawX, rawY, kind(grunt/breaker/warden), name, radius, hp, maxHp,
tiers([타격, 방어, 채널]), action(null 또는 기술 객체)
```

적 `id`는 무작위이며 순서나 크기에 뜻이 없다. 순번으로 정하면 보이는 id 사이의 빈 번호로 안개 속에서 생성된 적의 수가 드러나기 때문이다.

기술 객체:

```text
id, skill, kind, slot, tier, phase,
aim, startedAt, contactAt, activeUntil, followupUntil, endsAt, range, halfWidth
```

시간 필드는 서버 `now`와 같은 절대 시간이며 틱 시각이다(맨 위 시간 필드 참고). 반격은 `activeUntil`부터 `followupUntil` 전까지다. 반격이 없는 기술과 적의 행동은 두 값이 같다. `phase`는 telegraph / active / followup / recovery / interrupted. 플레이어의 후속 반격은 `kind: "none"`이며 RPS가 없다. 적의 telegraph에는 이미 상성이 있고 recovery에는 없다. `slot`은 플레이어 1~4, 적은 0이다. 미래 표적 ID나 숨겨진 적의 좌표는 기술 객체에 포함하지 않는다.

상성·합산·피해 규칙은 [전투 명세](combat.md)에 있다. 클라이언트는 모양·진행률·예고를 그리며 판정 결과를 결정하지 않는다. `effects.kind`는 break / partial / draw / guard / hit / hurt / heal / deploy이며 표시용이다.

시야에서 사라진 적은 죽었거나 시야를 벗어났을 수 있다. 현재 목록에 없는 적의 위치와 기술 예고를 이전 스냅샷으로 계속 보여주지 않는다. 사망·대기 대원은 추가 시야를 제공하지 않는다. 공개 맵·시설·보급 상태는 별개다.

거절된 투입과 제보에는 `{ "type":"error", "message":"…" }`가 올 수 있다. 입력·장비 거절마다 오류를 보내지는 않으므로 다음 스냅샷으로 확인한다. 오류 안내와 `pong`, `reported`, `reportNoted`는 스냅샷과 다른 통로로 보내며, 느린 연결에서 스냅샷을 버릴 때도 버리지 않는다. 스냅샷 JSON의 속성 순서는 정해져 있지 않으므로 이름으로 읽는다.
