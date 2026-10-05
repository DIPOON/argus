# Argus protocol v1

JSON over HTTP + WebSocket. 공식 클라이언트와 서드파티 클라이언트는 같은 프로토콜을 사용한다. UTF-8, 좌표 단위는 월드 단위, 시간은 초, 각도는 라디안이다. 원점은 왼쪽 위, X는 오른쪽, Y는 아래쪽이다.

## 게스트 참가

`POST /api/rooms`는 새 방을 만들고, `POST /api/rooms/{code}/join`은 진행 중인 방에도 참가한다.

```json
{"name":"대원","passive":"vitality"}
```

`passive`는 `vitality` 또는 `mobility`. 생략하면 vitality. 호출명은 최대 16 UTF-16 문자이며 신원이 아니다. 방당 4명까지, 끝난 방에는 새 참가자를 받지 않는다. 응답:

```json
{"room":"ABC234","id":"public-player-id","token":"private-recovery-token"}
```

토큰은 32바이트 난수의 64자리 hex이며 URL, 방 코드, 플레이어 목록에 넣지 않는다. 보유자는 해당 대원으로 재접속할 수 있다. 최초 HTTP 참가도 120초 내 연결하지 않으면 자리를 해제한다. `/health`는 `{ "status": "ok", "protocol": 1 }`을 반환한다.

## 소켓 수립

같은 호스트의 `/ws`에 접속한 뒤 8초 내 첫 메시지를 보낸다. 배포 환경이 HTTPS이면 WSS를 쓴다.

```json
{"type":"hello","room":"ABC234","token":"private-recovery-token"}
```

잘못된 참가 정보는 소켓 코드 1008로 닫는다. 올바른 재접속은 이전 소켓을 교체하며 기존 캐릭터 상태와 입력 순번을 유지한다. 교체된 소켓은 코드 4001로 닫으며 해당 클라이언트는 자동 재접속을 멈춰야 한다. 이전 소켓의 종료가 새 연결을 끊지는 않는다. 토큰은 방과 참가자가 유효한 동안 사용할 수 있다.

메시지는 텍스트 JSON, 최대 4096바이트. 연결당 초당 90개 초과 입력은 연결을 닫는다. 잘못된 메시지 형식도 연결을 닫을 수 있다. HTTP 참가 요청은 IP당 분당 30개 제한을 둔다. IP는 플레이어 신원 판별에 쓰지 않는다.

## 클라이언트 → 서버

### 입력

```json
{"type":"input","seq":42,"moveX":0.7071,"moveY":0.7071,"aim":1.3,"fire":true,"reload":false,"grenade":false}
```

- `seq`: 증가하는 정수, 0 이상 9,000,000,000,000 이하. 재접속 시 마지막 snapshot의 `ack`보다 큰 값으로 이어간다. 같은 순번·과거 순번은 무시한다.
- `moveX`, `moveY`: 유한수, 각 절댓값 1.01 이하. 길이가 1보다 크면 서버가 정규화한다. 클라이언트 시간·위치·속도는 입력받지 않는다.
- `aim`: 유한 라디안. 회전 속도나 클라이언트 조준선 흔들림을 강제하지 않는다.
- `fire`: 유지 입력. 서버 발사 간격과 탄창·재장전 조건을 만족해야 발사한다.
- `reload`, `grenade`: 한 번의 요청에만 true 권장. 연속 요청도 서버가 현재 상태와 자원을 검사한다.
- 입력이 0.35초 이상 없으면 이동·연속 사격을 멈춘다. 이동은 서버 시간에 따라 처리하므로 많은 메시지를 보내도 이동 거리가 늘지 않는다.
- 클라이언트가 임의로 붙인 `hp`, `ammo`, `damage`, `x`, `y` 등은 반영하지 않는다. 전투 피해와 자원은 서버만 변경한다.

### 투입 / 장비

```json
{"type":"deploy","x":300,"y":730}
{"type":"loadout","passive":"mobility"}
```

`deploy`는 waiting 상태에서 맵 안의 충돌 없는 좌표만 허용한다. 최초·난입·증원 모두 요청 수락 시점부터 5초. 선택 위치를 확정해도 시야는 추가되지 않는다. 준비 중에는 위치를 바꿀 수 없다. `loadout`은 첫 투입을 아직 시작하지 않은 waiting 상태에서만 허용한다.

```json
{"type":"ping","sent":1234.5}
```

`pong`은 같은 `sent`를 돌려준다. 클라이언트 자체 시간으로 RTT를 계산할 수 있으며 이 시간값은 게임 판정에 사용하지 않는다.

## 서버 → 클라이언트

상태는 즉시 한 번, 이후 약 10Hz로 전체 snapshot을 보낸다. 느린 수신자는 오래된 전송 대기 상태를 버리고 최신 상태를 받는다. snapshot의 `version`은 1이다.

| 필드 | 내용 |
| --- | --- |
| `type`, `version`, `room`, `you` | snapshot, 버전, 방 코드, 수신자의 공개 ID |
| `now`, `elapsed` | 방 경과 시간, 첫 투입부터 임무 경과 시간. 종료 후 elapsed는 고정 |
| `phase`, `result` | staging / active / ended, null / success / failure |
| `pulse`, `nextPulse` | 전역 피해 회차, 다음 피해까지 남은 시간 |
| `map` | width, height, cell, columns, rows, campX, campY |
| `walls`, `mapRevision` | 행 우선 타일 byte 배열의 Base64, 벽 파괴 버전. 0: 빈 지면, 1: 파괴 가능한 벽, 2: 맵 경계 |
| `rules` | deploy, reload, magazine, grenades, speed |
| `sight` | 시야 다각형의 배열. 각 다각형은 `[x0,y0,x1,y1,…]`. 캠프와 생존 대원별 다각형의 합집합 |
| `players` | 분대 전체 상태. 아래 참고 |
| `enemies` | 팀 시야 안의 적만: id, x, y, kind(melee/ranged), hp |
| `bullets` | 팀 시야 안의 탄환만: id, x, y, vx, vy, hostile |
| `grenades` | 팀 시야 안의 수류탄만: id, x, y, remaining |
| `effects` | 팀 시야 안의 효과만: id, x, y, kind, until |
| `facilities` | 공개된 목표: id, x, y, hp, maxHp |
| `supplies` | 공개된 보급: id, x, y, objective, collected, cooldown |
| `extraction` | x, y, progress, duration, unlocked |
| `notices` | 최근 안내 최대 6개: at, text |

대원 항목:

```text
id, name, passive, state(waiting/deploying/alive), connected, hasDeployed,
x, y, aim, hp, maxHp, ammo, grenades, reload(남은 초), deploy(남은 초),
kills, deaths, ack(마지막 수락 순번), landingX, landingY
```

숨겨진 적은 목록에 존재하지 않는다. 이전 snapshot에는 있었지만 새 snapshot에 없는 적을 계속 움직이게 예측하지 않는다. 적 ID의 부재는 사망과 시야 이탈을 모두 의미할 수 있다. camp와 teammate 시야는 서버의 거리·벽 차폐 검사로 결정하며, 다각형은 그 경계를 근사한 표시 자료다. 지도와 벽 파괴, 목표·보급 진행은 공개 정보로 취급한다.

`effects.kind`는 impact / kill / explosion / deploy / heal. `until`은 서버 `now` 기준 만료 시각이다. 기본적으로 렌더링 자료이며 피해 판정의 근거로 삼지 않는다.

거절된 투입 요청 등은 `{ "type": "error", "message": "…" }`를 받을 수 있다. 입력 요청의 거절마다 에러를 보내지는 않는다. 상태를 변경하는 요청의 성공 여부는 다음 snapshot으로 확인한다.

## 생명주기와 보장 범위

첫 투입 전에는 모두 기다려도 실패하지 않는다. 작전이 시작된 뒤 생존자가 0명이 되면 실패한다. 전멸 판정은 같은 틱의 대기 증원보다 우선한다. 솔로 첫 사망도 이 규칙을 따른다. 사망 후 위치를 고르지 않으면 계속 waiting이다.

연결이 끊긴 생존 캐릭터는 120초 동안 월드에 남아 피격될 수 있다. 복구로 체력·탄약·증원 준비를 초기화하지 않는다. 서버 재시작 시 복구는 지원하지 않는다.

프로토타입에는 장비 예산·해금·보상 메시지가 없다. 구현되지 않은 보상을 안전하게 지급한다고 주장하지 않는다. 인증 없는 신규 게스트의 동일인 판별, 자동 조준 차단도 현재 범위에 포함하지 않는다.
