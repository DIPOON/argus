# Argus protocol v3

2026-10-09. JSON over HTTP + WebSocket, UTF-8. 좌표 단위는 월드 단위, 시간은 서버 초, 각도는 라디안이다. 원점은 왼쪽 위다. X는 오른쪽, Y는 아래쪽이다.

`/health`의 `protocol`과 snapshot의 `version`은 3이다.

v2 → v3(2026-10-14): `walls`는 수신자가 아직 받지 못한 판일 때만, `rules`와 `map`은 수신자가 아직 받지 못했을 때만 스냅샷에 담긴다. 빠진 값은 같은 방의 이전 스냅샷 값을 그대로 쓴다. `rules.skills`에 기술별 동작(`guardsFront`, `hasFollowup`, `singleTarget`, `breaksStructures`)이 추가되었고, 적 `id`는 무작위가 되었다.

v3 안에서 추가(2026-10-15): `rules.skills`에 `followupBreaksStructures`가 늘었다. 값이 하나 추가되기만 해서 버전은 그대로 3이다.

v1 → v2: 총기·탄창·재장전·보조 장비·패시브 필드를 제거하고 네 슬롯의 근접 기술과 마나로 전환했다.

버전 정책: 하위 버전 호환은 유지하지 않는다. 프로토콜 버전이 올라가면 공식 클라이언트를 함께 갱신해 배포한다. 서드파티 클라이언트는 `/health`의 `protocol`과 스냅샷의 `version`을 확인하고, 지원 버전과 다르면 접속을 멈추고 갱신해야 한다. 현재 공식 클라이언트는 `version`이 3이 아니면 접속을 끊고 새로고침을 안내한다.

## 참가와 복구

`POST /api/rooms`, `POST /api/rooms/{code}/join`:

```json
{"name":"대원","slots":["strike","parry","grab","parry"]}
```

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
- 공식 클라이언트는 `ack`로 서버 재조정을 한다. 보낸 입력을 순번과 함께 보관하고, 스냅샷이 오면 `ack` 이하의 입력을 버린 뒤 남은 입력을 서버 위치 위에 다시 적용해 그린다. 클라이언트 시각은 서버로 보내지 않으며 서버는 판정에 클라이언트 시각을 쓰지 않는다.
- `moveX`, `moveY`: 유한수, 각 절댓값 1.01 이하. 벡터 길이가 1을 넘으면 서버가 정규화한다. 실제 이동 거리는 서버 시간과 몸·벽 충돌로 결정한다.
- `aim`: 유한 라디안. 현재 방향이며 동작 중에도 변경 가능하다.
- `slot`: 정수 0~4. 0은 새 기술 사용 없음. 1~4는 해당 슬롯의 유지 입력이다. 현재 동작이 끝나고 마나가 충분하면 다음 동작을 시작한다. 중간 기술 교체·동작 시간 단축·기본기 중첩은 허용하지 않는다.
- 기술 시작 시 서버가 마나를 소비한다. 실제로 맞히지 않아도 환불하지 않는다. 같은 기술을 여러 메시지로 요청해도 한 틱에 여러 번 발동하지 않는다.
- 입력이 0.35초 이상 없거나 연결이 끊기면 이동과 추가 사용 입력을 중단한다. 이미 시작한 기술의 시간은 계속 흐른다.
- x/y/hp/mana/damage/tier/actions 같은 추가 필드로 서버 상태를 정할 수 없다. 예전 `fire` 필드는 슬롯 밖 사격을 발생시키지 않는다.

```json
{"type":"deploy","x":300,"y":730}
{"type":"loadout","slots":["strike","parry","grab","grab"]}
{"type":"ping","sent":1234.5}
```

`deploy`는 waiting 상태에서 공개된 벽과 겹치지 않는 위치를 선택할 수 있다. 이미 투입 중인 대원이 다시 요청하면 처음 고른 자리와 시간대로 진행하고 오류를 보내지 않는다. 요청 응답으로 숨겨진 적의 점유 여부가 드러나지 않도록 적과의 겹침은 실제 투입 직전에 검사한다. 투입 준비는 5초이고 선택만으로 시야가 생기지 않는다. 도착 시 적이 막고 있으면 원래 지점이 보이는 가장 가까운 빈 자리로 투입하고, 그런 자리가 없으면 캠프로, 캠프도 막혔으면 반지름 640까지 넓혀 찾는다. 실패로 되돌리지 않으므로 투입 결과로 숨겨진 적의 위치를 알 수 없다. 전멸 검사가 증원보다 우선한다.

`loadout`은 최초 투입 시작 전 waiting 상태에서만 허용한다. 사망·증원·재접속으로 다시 바꿀 수 없다. `ping`은 같은 sent의 pong으로 응답하며 클라이언트 시간은 게임 판정에 사용하지 않는다.

## 스냅샷

연결 직후 한 번, 이후 10Hz(0.1초 간격)로 스냅샷을 전송한다. 한 번이 한 틱 늦게 나가면 다음 것이 그만큼 일찍 나가 평균 간격을 지킨다. 느린 수신자에게 쌓인 오래된 스냅샷은 버린다.

작전 중 바뀌지 않거나 드물게 바뀌는 `walls`, `rules`, `map`은 필요할 때만 담는다. 서버는 그 정보를 담은 스냅샷을 **실제로 보낸 뒤에야** 수신자가 받았다고 기록한다. 그래서 담긴 스냅샷이 느린 연결에서 버려져도 다음 스냅샷에 다시 담기고, 클라이언트가 처음 받는 스냅샷에는 세 값이 모두 있다. `mapRevision`은 매번 온다. 클라이언트는 이 값이 바뀌면 함께 온 `walls`로 벽을 갱신하고, 세 값이 빠진 스냅샷에서는 이전 값을 그대로 쓴다.

| 필드 | 내용 |
| --- | --- |
| type, version, room, you | snapshot, 3, 방 코드, 수신자 공개 ID |
| now, elapsed | 방 시간 / 첫 투입 이후 임무 시간. 임무 종료 후 elapsed 고정. 서버는 1/30초 틱 단위로만 진행하고, 보낼 때는 소수 둘째 자리로 반올림한다 |
| phase, result | staging / active / ended, null / success / failure |
| pulse, nextPulse | 전역 피해 회차 / 다음 피해까지 초 |
| map | width, height, cell, columns, rows, campX, campY. 아직 받지 못했을 때만 온다 |
| walls, mapRevision | 행 우선 byte 타일 배열의 Base64 / 파괴 버전. 0 빈 지면, 1 파괴 가능, 2 맵 경계. walls는 그 판을 아직 받지 못했을 때만 온다 |
| rules | deploy, speed, playerRadius, guardHalfAngle, parryFollowupSeconds, skills. 아직 받지 못했을 때만 온다 |
| sight | 팀 시야 다각형 배열. 다각형은 `[x0,y0,x1,y1,…]`이며 좌표는 정수다 |
| players | 분대 전체 상태 |
| enemies | 팀 시야에 실제 들어온 적과 기술 예고 |
| effects | 시야 안의 효과: id, x, y, kind, until |
| facilities | 공개 목표: id, x, y, hp, maxHp |
| supplies | 공개 보급: id, x, y, objective, collected, cooldown. cooldown은 수신 대원 기준 |
| extraction | x, y, progress, duration, unlocked |
| notices | 최근 안내 최대 6개: at, text |

`rules.skills`는 기본기별 `id, kind, mana, contactAfter, activeSeconds, duration, range, halfWidth, guardsFront, hasFollowup, singleTarget, breaksStructures, followupBreaksStructures`를 제공한다. kind는 strike / block / channel이다. 뒤의 다섯 값은 기술별 동작이다. `guardsFront`는 먼저 치지 않고 앞에서 들어오는 공격에만 반응함, `hasFollowup`은 유효 시간 뒤 상성 없는 반격, `singleTarget`은 가장 가까운 한 명만 판정, `breaksStructures`는 접촉 중에 시설과 벽을 부숨, `followupBreaksStructures`는 반격이 시설과 벽을 부숨을 뜻한다. 동작은 id가 아니라 이 값으로 정해진다. 이 수치를 클라이언트가 돌려보내도 서버 설정을 바꾸지 않는다.

대원:

```text
id, name, slots, state(waiting/deploying/alive), connected, hasDeployed,
x, y, aim, hp, maxHp, mana, maxMana, busy(남은 초), actions,
deploy(남은 초), kills, deaths, ack, landingX, landingY
```

적:

```text
id, x, y, kind(grunt/breaker/warden), name, radius, hp, maxHp,
tiers([타격, 방어, 채널]), action(null 또는 기술 객체)
```

적 `id`는 무작위이며 순서나 크기에 뜻이 없다. 순번으로 정하면 보이는 id 사이의 빈 번호로 안개 속에서 생성된 적의 수가 드러나기 때문이다.

기술 객체:

```text
id, skill, kind, slot, tier, phase,
aim, startedAt, contactAt, activeUntil, endsAt, range, halfWidth
```

시간 필드는 서버 `now`와 같은 절대 시간이다. `phase`는 telegraph / active / followup / recovery / interrupted. 플레이어의 후속 반격은 `kind: "none"`이며 RPS가 없다. 적의 telegraph에는 이미 상성이 있고 recovery에는 없다. `slot`은 플레이어 1~4, 적은 0이다. 미래 표적 ID나 숨겨진 적의 좌표는 기술 객체에 포함하지 않는다.

상성·합산·피해 규칙은 [전투 명세](combat.md)에 있다. 클라이언트는 모양·진행률·예고를 그리며 판정 결과를 결정하지 않는다. `effects.kind`는 break / partial / draw / guard / hit / hurt / heal / deploy이며 표시용이다.

시야에서 사라진 적은 죽었거나 시야를 벗어났을 수 있다. 현재 목록에 없는 적의 위치와 기술 예고를 이전 스냅샷으로 계속 보여주지 않는다. 사망·대기 대원은 추가 시야를 제공하지 않는다. 공개 맵·시설·보급 상태는 별개다.

거절된 투입에는 `{ "type":"error", "message":"…" }`가 올 수 있다. 입력·장비 거절마다 오류를 보내지는 않으므로 다음 스냅샷으로 확인한다. 오류 안내와 `pong`은 스냅샷과 다른 통로로 보내며, 느린 연결에서 스냅샷을 버릴 때도 버리지 않는다. 스냅샷 JSON의 속성 순서는 정해져 있지 않으므로 이름으로 읽는다.
