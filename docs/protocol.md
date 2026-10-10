# Argus protocol v2

2026-10-09. JSON over HTTP + WebSocket, UTF-8. 좌표 단위는 월드 단위, 시간은 서버 초, 각도는 라디안이다. 원점은 왼쪽 위다. X는 오른쪽, Y는 아래쪽이다.

v1의 총기·탄창·재장전·보조 장비·패시브 필드를 제거하고 네 슬롯의 근접 기술과 마나로 전환했다. 서버와 클라이언트를 함께 갱신한다. `/health`의 `protocol`과 snapshot의 `version`은 2다.

## 참가와 복구

`POST /api/rooms`, `POST /api/rooms/{code}/join`:

```json
{"name":"대원","slots":["strike","parry","grab","parry"]}
```

slots를 생략하면 `["strike","parry","grab","strike"]`. 길이는 정확히 4다. 앞의 세 슬롯은 현재 출시된 해당 계열 기본기로 고정하며 자유 슬롯은 strike / parry / grab 중 하나다. 잘못된 조합은 HTTP 400이다. 이름은 최대 16 UTF-16 문자, 방당 최대 4명, 진행 중 난입 가능, 종료된 방에는 새 참가 불가.

```json
{"room":"ABC234","id":"public-player-id","token":"private-recovery-token"}
```

토큰은 32바이트 난수의 64자리 hex다. 같은 호스트의 `/ws`에 연결한 뒤 8초 내 다음 메시지를 보낸다. HTTPS에서는 WSS를 사용한다.

```json
{"type":"hello","room":"ABC234","token":"private-recovery-token"}
```

잘못된 토큰은 소켓 1008로 종료한다. 같은 토큰으로 접속하면 기존 연결은 4001로 교체하며 기존 상태를 유지한다. 이전 연결이 종료되어도 새 연결을 끊지 않는다. HTTP 참가 후 미접속과 연결 끊김 모두 120초 유예 후 자리를 해제한다. 서버 재시작 복구는 지원하지 않는다.

메시지는 텍스트 JSON, 최대 4096바이트, 소켓당 초당 최대 90개다. HTTP 참가는 IP당 분당 30개 제한이며 IP는 게스트 신원이 아니다.

## 입력

```json
{"type":"input","seq":42,"moveX":0.7071,"moveY":0.7071,"aim":1.3,"slot":2}
```

- `seq`: 증가하는 정수, 0~9,000,000,000,000. 과거·중복 순번은 무시한다. 재접속 시 snapshot의 ack보다 큰 값으로 이어간다.
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

`deploy`는 waiting 상태에서 공개된 벽과 겹치지 않는 위치를 선택할 수 있다. 요청 응답으로 숨겨진 적의 점유 여부가 드러나지 않도록 적과의 겹침은 실제 투입 직전에 검사한다. 투입 준비는 5초이고 선택만으로 시야가 생기지 않는다. 도착 시 적이 막고 있으면 waiting으로 돌린다. 전멸 검사가 증원보다 우선한다.

`loadout`은 최초 투입 시작 전 waiting 상태에서만 허용한다. 사망·증원·재접속으로 다시 바꿀 수 없다. `ping`은 같은 sent의 pong으로 응답하며 클라이언트 시간은 게임 판정에 사용하지 않는다.

## 스냅샷

연결 직후 한 번, 이후 약 10Hz로 전체 스냅샷을 전송한다. 느린 수신자에게 쌓인 오래된 스냅샷은 버린다.

| 필드 | 내용 |
| --- | --- |
| type, version, room, you | snapshot, 2, 방 코드, 수신자 공개 ID |
| now, elapsed | 방 시간 / 첫 투입 이후 임무 시간. 임무 종료 후 elapsed 고정 |
| phase, result | staging / active / ended, null / success / failure |
| pulse, nextPulse | 전역 피해 회차 / 다음 피해까지 초 |
| map | width, height, cell, columns, rows, campX, campY |
| walls, mapRevision | 행 우선 byte 타일 배열의 Base64 / 파괴 버전. 0 빈 지면, 1 파괴 가능, 2 맵 경계 |
| rules | deploy, speed, playerRadius, guardHalfAngle, parryFollowupSeconds, skills |
| sight | 팀 시야 다각형 배열. 다각형은 `[x0,y0,x1,y1,…]` |
| players | 분대 전체 상태 |
| enemies | 팀 시야에 실제 들어온 적과 기술 예고 |
| effects | 시야 안의 효과: id, x, y, kind, until |
| facilities | 공개 목표: id, x, y, hp, maxHp |
| supplies | 공개 보급: id, x, y, objective, collected, cooldown. cooldown은 수신 대원 기준 |
| extraction | x, y, progress, duration, unlocked |
| notices | 최근 안내 최대 6개: at, text |

`rules.skills`는 기본기별 `id, kind, mana, contactAfter, activeSeconds, duration, range, halfWidth`를 제공한다. kind는 strike / block / channel이다. 이 수치를 클라이언트가 돌려보내도 서버 설정을 바꾸지 않는다.

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

기술 객체:

```text
id, skill, kind, slot, tier, phase,
aim, startedAt, contactAt, activeUntil, endsAt, range, halfWidth
```

시간 필드는 서버 `now`와 같은 절대 시간이다. `phase`는 telegraph / active / followup / recovery / interrupted. 플레이어의 후속 반격은 `kind: "none"`이며 RPS가 없다. 적의 telegraph에는 이미 상성이 있고 recovery에는 없다. `slot`은 플레이어 1~4, 적은 0이다. 미래 표적 ID나 숨겨진 적의 좌표는 기술 객체에 포함하지 않는다.

상성·합산·피해 규칙은 [전투 명세](combat.md)에 있다. 클라이언트는 모양·진행률·예고를 그리며 판정 결과를 결정하지 않는다. `effects.kind`는 break / partial / draw / guard / hit / hurt / heal / deploy이며 표시용이다.

시야에서 사라진 적은 죽었거나 시야를 벗어났을 수 있다. 현재 목록에 없는 적의 위치와 기술 예고를 이전 스냅샷으로 계속 보여주지 않는다. 사망·대기 대원은 추가 시야를 제공하지 않는다. 공개 맵·시설·보급 상태는 별개다.

거절된 투입에는 `{ "type":"error", "message":"…" }`가 올 수 있다. 입력·장비 거절마다 오류를 보내지는 않으므로 다음 스냅샷으로 확인한다.
