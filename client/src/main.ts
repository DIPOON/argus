import './style.css';
import { tickMilliseconds } from './movement';
import { World } from './world';
import { applyWallChanges, skillNames, type Guest, type Player, type Snapshot, type SnapshotWire, type Skill } from './types';

const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;
const escape = (value: string) => value.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);
const time = (seconds: number) => `${Math.floor(Math.max(0, seconds) / 60).toString().padStart(2, '0')}:${Math.floor(Math.max(0, seconds) % 60).toString().padStart(2, '0')}`;
const slotKeys = ['좌클릭 / 1', '우클릭 / 2', 'Space / 3', 'Q / 4'];

// FIELD NOTES에 적은 피해 값(부분 대응 1·1, 비김 2·1)은 합의한 규칙(docs/combat.md)의 값이다.
// 서버는 부분 대응 피해 값을 보내지 않으므로, 규칙을 바꾸면 이 문구도 함께 고친다.
$('app').innerHTML = `
  <header class="topbar">
    <a class="brand" href="/" aria-label="ARGUS 홈"><span class="brand-mark">△</span> ARGUS <span class="version">COMBAT TEST / 02</span></a>
    <div class="top-right"><span id="connection" class="connection"><i></i> 투입 대기</span><span class="build-tag">CO-OP PVE · 1–4</span></div>
  </header>
  <main id="lobby" class="lobby">
    <section class="briefing">
      <div class="eyebrow"><span class="dash"></span> OPERATION 001</div>
      <h1>침묵의<br><span>전초기지.</span></h1>
      <p class="lead">적의 예고를 읽고, 동료와 함께 파훼하세요.<br>타격 · 받아치기 · 잡기. 거리를 맞추는 근접 전투.</p>
      <div class="brief-map" aria-hidden="true">
        <svg viewBox="0 0 540 240"><defs><pattern id="grid" width="24" height="24" patternUnits="userSpaceOnUse"><path d="M24 0H0V24" fill="none" stroke="#2a3738" stroke-width="1"/></pattern></defs>
          <rect width="540" height="240" fill="url(#grid)"/><path d="M210 40h235v55m0 45v60H210v-70m0-45V40 M290 90h45v30 M355 150h40" stroke="#75827a" stroke-width="10" fill="none"/>
          <path d="M70 130h175l35-40 90 65-45 45" stroke="#cfe78b" stroke-width="1.5" stroke-dasharray="5 6" fill="none"/>
          <circle cx="70" cy="130" r="31" fill="#dfff80" fill-opacity=".06" stroke="#dfff80" stroke-opacity=".4"/><path d="M61 130h18m-9-9v18" stroke="#dfff80" stroke-width="2"/>
          <g fill="#1b2729" stroke="#ed8d74" stroke-width="2"><rect x="269" y="62" width="22" height="22"/><rect x="364" y="102" width="22" height="22"/><rect x="300" y="162" width="22" height="22"/></g>
          <text x="40" y="189" fill="#9cac9f" font-size="10" font-family="monospace">CAMP</text><text x="357" y="228" fill="#9cac9f" font-size="10" font-family="monospace">SECTOR / 07</text>
        </svg>
        <span class="map-caption">작전 개요도 <span>적 위치 정보 없음</span></span>
      </div>
      <div class="mission-steps"><div><b>01</b><span>적 시설 파괴</span></div><div><b>02</b><span>보급품 회수</span></div><div><b>03</b><span>탈출 지점 확보</span></div></div>
    </section>
    <section class="loadout-panel">
      <div class="eyebrow">DEPLOYMENT PREP</div><h2>분대에 합류하세요</h2><p class="muted">혼자 시작해도, 작전 중 함께해도 됩니다.</p>
      <label class="field-label" for="callsign">호출명 <span>가입 없이 게스트로 참가</span></label>
      <input id="callsign" maxlength="16" autocomplete="nickname" placeholder="대원 이름" value="대원" />
      <div class="basic-slots" aria-label="기본 기술 슬롯">
        <div class="skill-strike"><kbd>1</kbd><b>근접 타격</b><small>잡기를 파훼</small></div>
        <div class="skill-block"><kbd>2</kbd><b>받아치기</b><small>타격을 파훼 · 전방 방어 후 반격</small></div>
        <div class="skill-channel"><kbd>3</kbd><b>잡기</b><small>방어를 파훼 · 단일 대상</small></div>
      </div>
      <fieldset class="equipment-options primary-weapons"><legend class="field-label">4번 자유 슬롯 <span>기본기 중 추가 선택</span></legend>
        <label><input type="radio" name="free-slot" value="strike" checked/><span><b>근접 타격</b><small>Q 또는 4로 사용</small></span></label>
        <label><input type="radio" name="free-slot" value="parry"/><span><b>받아치기</b><small>Q 또는 4로 사용</small></span></label>
        <label><input type="radio" name="free-slot" value="grab"/><span><b>잡기</b><small>Q 또는 4로 사용</small></span></label>
      </fieldset>
      <p class="combat-intro">투입 마나 <b>0</b> · 보급 상자에서 충전 · 기술마다 마나 <b>1</b><br>자연 회복 없음. 빗나가도 마나를 소비합니다.<br>자유 슬롯에는 기본기를 중복 장착하며, 기본기는 한 번에 하나씩 사용합니다.</p>
      <button id="create" class="primary">새 작전 시작 <span>↗</span></button>
      <div class="or"><span></span> 초대받았다면 <span></span></div>
      <div class="join-row"><input id="room-input" aria-label="방 코드" placeholder="6자리 방 코드" maxlength="6" autocomplete="off"/><button id="join" class="secondary">참가</button></div>
      <p id="lobby-error" class="error" role="alert"></p>
      <p class="entry-note"><span>◌</span> 투입 준비 5초 · 아군 피해 없음 · 전원 사망 시 실패</p>
    </section>
    <footer class="lobby-footer"><span>4명까지 함께하는 협동 전투</span><span>PC 브라우저 · 키보드 + 마우스</span></footer>
  </main>
  <main id="operation" class="operation" hidden>
    <aside class="sidebar">
      <div class="eyebrow">OPERATION 001</div><h2>침묵의 전초기지</h2>
      <button id="copy-code" class="room-code" title="친구에게 방 코드를 공유하세요"><span>초대 코드</span><b id="room-code">------</b><span>⧉</span></button>
      <div class="side-section"><div class="section-label">MISSION <span id="mission-state">준비 중</span></div>
        <div class="objective"><span class="objective-icon primary-dot">◇</span><div><b>적 시설 파괴</b><small>구역 내 시설을 모두 무력화</small></div><strong id="primary-count">0/3</strong></div>
        <div id="facility-list" class="facility-list"></div>
        <div class="objective"><span class="objective-icon gold">◇</span><div><b>보급품 회수</b><small>부목표 · 가까이 가서 회수</small></div><strong id="secondary-count">0/2</strong></div>
        <div class="objective"><span class="objective-icon teal">↑</span><div><b>탈출 지점 확보</b><small id="extract-note">주목표 완료 후 활성화</small></div></div>
        <div class="progress"><i id="extract-progress"></i></div>
      </div>
      <div class="side-section"><div class="section-label">SQUAD <span id="team-count">1 / 4</span></div><div id="roster"></div></div>
      <div class="side-bottom"><div class="section-label">FIELD NOTES</div><p>밝은 구역은 분대의 공유 시야입니다. 어두운 곳에도 적이 있을 수 있습니다.</p><p class="combat-legend"><span class="skill-strike">타격</span>은 잡기를 이깁니다.<br><span class="skill-block">방어</span>는 타격을 이깁니다.<br><span class="skill-channel">잡기</span>는 방어를 이깁니다.</p><p>Ⅱ(대응 2)로 표시된 엘리트 행동은 이기는 기술이 둘 겹쳐야 파훼됩니다. 혼자 이기는 기술을 맞히면 나와 엘리트가 1씩 잃고, 같은 기술로 맞서면 나는 2, 엘리트는 1을 잃습니다. 동료와 맞추세요. 타격 예고가 끝날 무렵 받아치세요. 너무 일찍 쓰면 방어가 먼저 끝납니다.</p><button id="report" class="text-button">버그 제보 ↗</button><button id="leave" class="text-button">작전에서 나가기 ↗</button><form id="report-note" class="report-note" hidden><label for="report-text">무엇이 이상했나요? 비워 둬도 제보는 남았습니다.</label><input id="report-text" maxlength="200" autocomplete="off" placeholder="예: 적이 벽을 지나갔어요"><div class="report-actions"><button type="submit" class="secondary">덧붙이기</button><button type="button" id="report-close" class="text-button">닫기</button></div></form></div>
    </aside>
    <section id="battlefield" class="battlefield" aria-label="작전 지도">
      <div id="canvas-host" class="canvas-host"></div>
      <div class="map-top"><span class="map-title"><i></i><span id="view-label">전술 지도 / 투입 위치 선택</span></span><div class="timers"><span>경과 <b id="elapsed">00:00</b></span></div></div>
      <div id="toast" class="toast" role="status" hidden></div>
      <div id="deploy-panel" class="deploy-panel"><div><span class="eyebrow" id="deploy-kicker">READY TO DEPLOY</span><h3 id="deploy-title">투입할 위치를 선택하세요</h3><p id="deploy-description">분대가 본 곳의 빈 지면을 클릭하세요. 선택 전에는 계속 대기합니다.</p></div><div class="deploy-actions"><button id="camp" class="secondary">캠프 선택</button><button id="deploy" class="primary" disabled>위치 선택 필요</button></div></div>
      <div id="combat-hud" class="combat-hud" hidden>
        <div class="vitals"><div><span id="my-name">대원</span><b id="hp">6</b><small id="max-hp">/ 6 HP</small><div class="health-bar"><i id="health-fill"></i></div></div>
        <div><span>마나</span><b id="mana">0</b><small id="max-mana">/ 24</small><div class="mana-bar"><i id="mana-fill"></i></div></div></div>
        <div class="skill-bar" id="skill-bar"></div><p id="combat-status" aria-live="polite"></p>
      </div>
      <div class="map-bottom"><span id="controls">클릭 위치 선택 · 캠프는 권장 투입 지점</span><span id="performance">공유 시야</span></div>
      <div id="result" class="result-overlay" hidden><div class="result-card"><span class="eyebrow">OPERATION COMPLETE</span><h2 id="result-title">작전 종료</h2><p id="result-description"></p><div id="result-stats"></div><button id="again" class="primary">다음 작전 준비 <span>↗</span></button></div></div>
    </section>
  </main>`;

let guest: Guest | undefined;
let snapshot: Snapshot | undefined;
let socket: WebSocket | undefined;
let world: World | undefined;
let connecting = false;
let reconnectTimer = 0;
let sequence = 0;
let ping = 0;
const mouseButtons = new Set<number>();
// 마지막으로 띄운 안내의 순번이다. 0이면 이 방에서 아직 띄운 안내가 없다.
let lastNotice = 0;
// 내용을 덧붙일 버그 제보의 틱이다. 입력란을 닫으면 비운다.
let reportTick: number | null = null;
let toastTimer = 0;
// 알림 줄마다 3.5초 동안 보이고 최대 세 줄까지 쌓인다. 안내가 몰려 와도 앞의 것이 곧바로 지워지지 않는다.
const toastLines: { text: string; until: number }[] = [];
// 투입 요청을 보내고 서버의 답(투입 중 상태나 오류 안내)을 기다리는 중이다. 그동안 투입 버튼을 막아 두 번 보내지 않는다.
let deployRequested = false;
const keys = new Set<string>();
let mouse = { x: 0, y: 0, known: false };
// 동시에 눌린 슬롯 중 가장 나중에 누른 것이 나가도록 누름 순서를 유지한다.
const slotOrder: number[] = [];
// 다시 눌러야 하는 키(staleKeys)는 쥐고 있어도 누른 것으로 치지 않는다. 같은 슬롯의 다른 입력을
// 새로 눌렀다 떼었을 때 쥐고 있던 키로 기술이 이어지지 않게 한다.
const keyHeld = (code: string) => keys.has(code) && !staleKeys.has(code);
function slotHeld(slot: number) {
  if (slot === 1) return mouseButtons.has(0) || keyHeld('Digit1');
  if (slot === 2) return mouseButtons.has(2) || keyHeld('Digit2');
  if (slot === 3) return keyHeld('Space') || keyHeld('Digit3');
  return keyHeld('KeyQ') || keyHeld('Digit4');
}
function slotFromCode(code: string) {
  if (code === 'Digit1') return 1;
  if (code === 'Digit2') return 2;
  if (code === 'Space' || code === 'Digit3') return 3;
  if (code === 'KeyQ' || code === 'Digit4') return 4;
  return 0;
}
function pressSlot(slot: number) {
  const index = slotOrder.indexOf(slot);
  if (index >= 0) slotOrder.splice(index, 1);
  slotOrder.push(slot);
}
// 살아 있지 않을 때 누른 기술 키와, 살아 있는 상태를 벗어날 때 눌려 있던 기술 키다. 떼었다 다시 눌러야 기술이 나간다.
// 그대로 두면 투입을 기다리며 쥔 키가 착지 뒤 마나가 차는 순간부터 기술을 계속 낸다.
const staleKeys = new Set<string>();
// 사망 등으로 살아 있는 상태를 벗어날 때 부른다. 마우스는 syncButtons가 새로 눌린 버튼만 받으므로 비우기만 하면 된다.
function releaseSkills() {
  mouseButtons.clear(); slotOrder.length = 0;
  for (const code of keys) if (slotFromCode(code) > 0) staleKeys.add(code);
}
// 버튼을 겹쳐 누르거나 뗄 때 브라우저는 pointerdown·pointerup 대신 pointermove로만 알린다.
// 그래서 세 이벤트 모두에서 event.buttons(지금 눌린 버튼 비트)로 상태를 다시 맞춘다.
// 직전 이벤트와 비교해 새로 눌린 버튼만 누름으로 본다. 투입 클릭을 쥔 채 살아난 순간 타격이 나가지 않게 한다.
let pointerButtons = 0;
// 입력을 비운 뒤(창을 다녀왔을 때 등) 처음 받은 pointermove·pointerup의 버튼은 비우기 전부터 쥐고 있던 것이다.
// 새 누름으로 치지 않고 기준값으로만 받는다. 0으로 비우면 쥐고 있던 버튼이 돌아온 뒤 첫 움직임에서 새로 누른 것으로
// 보여 기술이 나간다. pointerdown은 아무 버튼도 누르지 않은 상태에서만 오므로(fresh) 그대로 새 누름으로 본다.
let pointerBaseline = false;
function syncButtons(buttons: number, allowPress: boolean, fresh = false) {
  if (pointerBaseline) {
    pointerBaseline = false;
    if (!fresh) { pointerButtons = buttons; return; }
    pointerButtons = 0;
  }
  for (const [button, bit, slot] of [[0, 1, 1], [2, 2, 2]] as const) {
    const pressed = (buttons & bit) !== 0 && (pointerButtons & bit) === 0;
    if (pressed && allowPress && !mouseButtons.has(button)) { mouseButtons.add(button); pressSlot(slot); }
    if ((buttons & bit) === 0) mouseButtons.delete(button);
  }
  pointerButtons = buttons;
}

function me(): Player | undefined { return snapshot?.players.find(p => p.id === snapshot?.you); }
function send(value: object) {
  if (socket?.readyState !== WebSocket.OPEN) return false;
  socket.send(JSON.stringify(value));
  return true;
}
// 입력을 보내고, 서버가 확인할 때까지 화면 예측에 쓰도록 기록한다. 실제로 보낸 입력만 기록한다.
function sendInput(x: number, y: number, aim: number, slot: number) {
  const seq = sequence++;
  if (send({ type: 'input', seq, moveX: x, moveY: y, aim, slot })) world?.recordInput(seq, x, y);
}
function status(text: string, connected = false) { $('connection').innerHTML = `<i class="${connected ? 'online' : ''}"></i> ${escape(text)}`; }
function toast(text: string, merge = true) {
  const now = performance.now();
  const last = toastLines.at(-1);
  // 빈 곳을 여러 번 누르는 것처럼 같은 말을 거듭 띄우면 줄을 늘리지 않고 시간만 늘린다. 안내는 순번이 달라 따로 띄운다.
  if (merge && last?.text === text) last.until = now + 3500;
  else toastLines.push({ text, until: now + 3500 });
  if (toastLines.length > 3) toastLines.splice(0, toastLines.length - 3);
  renderToast();
}
function renderToast() {
  const now = performance.now();
  for (let i = toastLines.length - 1; i >= 0; i--) if (toastLines[i].until <= now) toastLines.splice(i, 1);
  const box = $('toast');
  box.replaceChildren(...toastLines.map(line => Object.assign(document.createElement('div'), { textContent: line.text })));
  box.hidden = toastLines.length === 0;
  window.clearTimeout(toastTimer);
  if (toastLines.length > 0) toastTimer = window.setTimeout(renderToast, Math.min(...toastLines.map(line => line.until)) - now);
}
function clearInput() {
  keys.clear(); mouseButtons.clear(); slotOrder.length = 0; pointerBaseline = true;
  if (world) world.control = { x: 0, y: 0, aim: world.control.aim, slot: 0 };
  // 서버는 작전 중 살아 있는 대원의 입력만 받는다. 30Hz 입력과 같은 조건일 때만 멈춤 입력을 보낸다.
  if (me()?.state === 'alive' && snapshot?.phase === 'active') sendInput(0, 0, world?.control.aim ?? 0, 0);
}

async function enter(code?: string) {
  if (connecting) return;
  const inputCode = code?.trim().toUpperCase();
  if (inputCode !== undefined && !/^[A-Z2-9]{6}$/.test(inputCode)) { $('lobby-error').textContent = '6자리 방 코드를 입력하세요.'; return; }
  connecting = true;
  $('lobby-error').textContent = '';
  $<HTMLButtonElement>('create').disabled = $<HTMLButtonElement>('join').disabled = true;
  try {
    const response = await fetch(inputCode ? `/api/rooms/${inputCode}/join` : '/api/rooms', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        name: $<HTMLInputElement>('callsign').value || '대원',
        slots: ['strike', 'parry', 'grab', document.querySelector<HTMLInputElement>('input[name="free-slot"]:checked')!.value],
      }),
    });
    if (!response.ok) {
      const message = await response.json().catch(() => null);
      throw new Error(message?.error ?? '잠시 뒤 다시 참가해 주세요.');
    }
    guest = await response.json() as Guest;
    sessionStorage.setItem('argus-guest', JSON.stringify(guest));
    await openOperation();
  } catch (error) { $('lobby-error').textContent = error instanceof Error ? error.message : '서버에 연결할 수 없습니다.'; }
  finally { connecting = false; $<HTMLButtonElement>('create').disabled = $<HTMLButtonElement>('join').disabled = false; }
}

async function openOperation() {
  $('lobby').hidden = true; $('operation').hidden = false;
  if (!world) {
    try { world = new World(); await world.init($('canvas-host')); }
    catch { world = undefined; leave(); $('lobby-error').textContent = '그래픽 초기화에 실패했습니다. 브라우저의 하드웨어 가속을 확인해 주세요.'; return; }
    const canvas = world.app.canvas;
    canvas.addEventListener('pointermove', event => {
      mouse = { x: event.clientX, y: event.clientY, known: true };
      syncButtons(event.buttons, me()?.state === 'alive');
    });
    canvas.addEventListener('pointerdown', event => {
      if (!world || (event.button !== 0 && event.button !== 2)) return;
      mouse = { x: event.clientX, y: event.clientY, known: true };
      if (event.button === 0 && me()?.state === 'waiting' && snapshot?.phase !== 'ended') {
        const point = world.screenToWorld(event.clientX, event.clientY);
        syncButtons(event.buttons, false, true);
        // 서버처럼 분대가 본 칸의 빈 지면에만 투입한다.
        const check = world.deployCheck(point);
        if (check === 'unseen') { toast('아직 보지 못한 곳에는 투입할 수 없습니다.'); return; }
        if (check === 'blocked') { toast('벽을 피해 빈 지면을 선택하세요.'); return; }
        world.landing = point; updateUi();
      } else { syncButtons(event.buttons, me()?.state === 'alive', true); }
    });
    canvas.addEventListener('contextmenu', event => event.preventDefault());
  }
  world.app.ticker.start();
  connect();
}

function connect() {
  if (!guest) return;
  window.clearTimeout(reconnectTimer);
  status('연결 중');
  const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws`);
  socket = ws;
  ws.addEventListener('open', () => { ws.send(JSON.stringify({ type: 'hello', room: guest!.room, token: guest!.token })); });
  ws.addEventListener('message', event => {
    if (socket !== ws) return;
    const message = JSON.parse(event.data);
    if (message.type === 'pong') { ping = Math.round(performance.now() - message.sent); return; }
    if (message.type === 'error') { deployRequested = false; toast(message.message); updateUi(); return; }
    // 서버가 제보를 이 방의 기록에 남겼다. 방 코드와 틱을 알려 주면 그 순간을 그대로 다시 돌려 볼 수 있다.
    // 내용을 적을 입력란을 연다. 싸우는 중일 수 있으므로 키보드를 빼앗지 않도록 입력란으로 커서를 옮기지는 않는다.
    if (message.type === 'reported') {
      toast(`제보를 남겼습니다 · 방 ${guest?.room ?? ''} · ${message.tick}틱`, false);
      reportTick = message.tick;
      $('report-note').hidden = false;
      return;
    }
    if (message.type === 'reportNoted') { toast('제보에 내용을 덧붙였습니다.', false); return; }
    if (message.type !== 'snapshot') return;
    if (message.version !== 5) { leave(); $('lobby-error').textContent = '서버와 클라이언트 버전이 다릅니다. 새로고침 후 다시 접속하세요.'; return; }
    // 벽은 바뀔 때만, 규칙과 맵은 접속 직후에만 온다. 빠진 값은 같은 방의 이전 스냅샷에서 이어 쓴다.
    const wire = message as SnapshotWire;
    const previous = snapshot?.room === wire.room ? snapshot : undefined;
    // 벽은 전체가 오거나, 받은 배열에서 바뀐 칸만 온다. 바뀐 칸은 같은 방의 이전 배열에 적용한다.
    let walls = wire.walls ?? previous?.walls;
    if (!wire.walls && wire.wallChanges && walls !== undefined) walls = applyWallChanges(walls, wire.wallChanges);
    const rules = wire.rules ?? previous?.rules, map = wire.map ?? previous?.map;
    // 서버는 실제로 보낼 때까지 다시 담으므로 처음 받은 스냅샷에는 모두 있다. 그래도 빠졌으면 다음 것을 기다린다.
    if (walls === undefined || rules === undefined || map === undefined) return;
    const wasAlive = me()?.state === 'alive';
    const wasEnded = previous?.phase === 'ended';
    snapshot = { ...wire, walls, rules, map };
    if (me()?.state !== 'waiting') deployRequested = false;
    if (wasAlive && me()?.state !== 'alive') releaseSkills();
    // 작전이 끝나는 순간 한 번만 입력을 비운다. 끝난 뒤의 스냅샷마다 비울 필요는 없다.
    if (!wasEnded && snapshot.phase === 'ended') clearInput();
    sequence = Math.max(sequence, (me()?.ack ?? -1) + 1);
    status('분대 연결됨', true);
    world!.update(snapshot);
    updateUi();
    // 순번이 마지막으로 띄운 것보다 큰 안내를 모두 띄운다. 한 방송에 여러 개가 와도, 같은 틱에 같은 문구가 와도 빠뜨리지 않는다.
    // 방에 들어온 직후에는 지난 안내가 최대 여섯 개 함께 오므로 가장 최근 것만 띄운다.
    const fresh = snapshot.notices.filter(notice => notice.id > lastNotice);
    if (fresh.length > 0) {
      for (const notice of lastNotice === 0 ? fresh.slice(-1) : fresh) toast(notice.text, false);
      lastNotice = fresh[fresh.length - 1].id;
    }
  });
  ws.addEventListener('close', event => {
    if (socket !== ws || !guest) return;
    deployRequested = false;
    clearInput();
    if (event.code === 1008) { leave(); $('lobby-error').textContent = '참가 정보가 만료되었습니다. 새 작전에 참가해 주세요.'; return; }
    if (event.code === 4001) { leave(); $('lobby-error').textContent = '같은 대원이 다른 창에서 접속했습니다. 이 창에서는 별도 대원으로 참가할 수 있습니다.'; return; }
    status('재접속 중');
    toast('연결을 복구하고 있습니다. 캐릭터와 임무는 계속 진행됩니다.');
    reconnectTimer = window.setTimeout(connect, 1500);
  });
}

function leave() {
  guest = undefined; snapshot = undefined; lastNotice = 0; sequence = 0;
  closeReportNote();
  sessionStorage.removeItem('argus-guest');
  window.clearTimeout(reconnectTimer);
  socket?.close(); socket = undefined;
  clearInput();
  if (world) { world.landing = undefined; world.overview = false; world.app.ticker.stop(); }
  $('operation').hidden = true; $('lobby').hidden = false; $('result').hidden = true;
  status('투입 대기');
}

function updateUi() {
  const s = snapshot, p = me();
  if (!s || !p) return;
  const alive = p.state === 'alive', ended = s.phase === 'ended';
  $('room-code').textContent = s.room;
  $('mission-state').textContent = ended ? '작전 종료' : s.phase === 'active' ? '진행 중' : '투입 대기';
  $('elapsed').textContent = time(s.elapsed);
  $('primary-count').textContent = `${s.facilities.filter(f => f.hp <= 0).length}/3`;
  $('secondary-count').textContent = `${s.supplies.filter(f => f.objective && f.collected).length}/2`;
  // 시설과 탈출 지점은 분대가 본 것만 온다. 아직 찾지 못한 시설은 자리만 표시한다.
  $('facility-list').innerHTML = s.facilities.map(f => `<span class="${f.hp <= 0 ? 'done' : ''}">${f.hp <= 0 ? '✓' : '◇'} 0${f.id}</span>`).join('')
    + '<span class="unknown">? 미발견</span>'.repeat(Math.max(0, 3 - s.facilities.length));
  const ext = s.extraction;
  $('extract-note').textContent = !ext ? '탈출 지점 미발견 · 지도를 탐색하세요'
    : ext.unlocked ? `구역 안에서 ${ext.duration}초 확보 · ${ext.progress.toFixed(1)}초` : '주목표 완료 후 활성화';
  $('extract-progress').style.width = ext ? `${Math.min(100, 100 * ext.progress / ext.duration)}%` : '0%';
  $('team-count').textContent = `${s.players.length} / 4`;
  $('roster').innerHTML = s.players.map((member, index) => `<div class="member ${member.id === s.you ? 'self' : ''}"><span class="member-number color-${index}">0${index + 1}</span><div><b>${escape(member.name)}${member.id === s.you ? ' <small>나</small>' : ''}</b><span>${!member.connected ? '연결 끊김' : member.state === 'alive' ? `${Math.ceil(member.hp)} HP` : member.state === 'deploying' ? `투입까지 ${member.deploy.toFixed(1)}초` : member.hasDeployed ? '증원 대기' : '투입 대기'}</span><span class="roster-loadout">마나 ${member.mana}/${member.maxMana} · 자유 ${skillNames[member.slots[3]]}</span></div><i class="${member.state === 'alive' ? 'alive-dot' : ''}"></i></div>`).join('');
  $('deploy-panel').hidden = alive || ended;
  $('combat-hud').hidden = !alive || ended;
  $('view-label').textContent = alive ? '분대 공유 시야 / SECTOR 07' : '전술 지도 / 투입 위치 선택';
  $('controls').textContent = alive ? 'WASD 이동 · 좌클릭 타격 · 우클릭 받아치기 · Space 잡기 · Q 자유 슬롯 · M 지도' : '클릭 위치 선택 · 캠프는 권장 투입 지점';
  if (!alive && !ended) {
    const preparing = p.state === 'deploying';
    $('deploy-kicker').textContent = preparing ? 'DEPLOYMENT IN PROGRESS' : p.hasDeployed ? 'REINFORCEMENT' : 'READY TO DEPLOY';
    $('deploy-title').textContent = preparing ? `${p.deploy.toFixed(1)}초 후 투입합니다` : p.hasDeployed ? '증원할 위치를 선택하세요' : '투입할 위치를 선택하세요';
    $('deploy-description').textContent = preparing ? '투입이 완료될 때까지 해당 위치의 시야는 열리지 않습니다.' : '분대가 본 곳의 빈 지면을 클릭하세요. 지금 보이지 않는 곳의 안전은 보장되지 않습니다.';
    $<HTMLButtonElement>('camp').disabled = preparing;
    $<HTMLButtonElement>('deploy').disabled = preparing || deployRequested || !world?.landing;
    $('deploy').textContent = preparing || deployRequested ? '투입 준비 중…' : world?.landing ? '5초 후 투입 ↗' : '위치 선택 필요';
  }
  if (alive) {
    $('my-name').textContent = p.name;
    $('hp').textContent = Math.ceil(p.hp).toString(); $('max-hp').textContent = `/ ${p.maxHp} HP`;
    $('health-fill').style.width = `${100 * p.hp / p.maxHp}%`;
    $('health-fill').classList.toggle('low', p.hp < p.maxHp * .3);
    $('mana').textContent = p.mana.toString();
    $('max-mana').textContent = `/ ${p.maxMana}`;
    $('mana-fill').style.width = `${100 * p.mana / p.maxMana}%`;
    const current = p.actions.at(-1);
    const phaseText = current?.phase === 'interrupted' ? '중단됨' : current?.phase === 'followup' ? '일반 반격' : current?.phase === 'recovery' ? '후딜레이' : current ? skillNames[current.skill as Skill] : '준비';
    $('skill-bar').innerHTML = p.slots.map((skill, index) => {
      const definition = s.rules.skills.find(d => d.id === skill)!;
      const active = current?.slot === index + 1 && current.endsAt > s.now;
      const busy = Math.max(0, Math.min(1, p.busy / (current ? s.rules.skills.find(d => d.id === current.skill)!.duration : 1)));
      return `<div class="skill-slot skill-${definition.kind} ${active ? 'active' : ''} ${p.mana < definition.mana ? 'empty' : ''}" data-slot="${index + 1}"><kbd>${slotKeys[index]}</kbd><b>${skillNames[skill]}</b><small>${active ? phaseText : `마나 ${definition.mana}`}</small><i style="width:${100 * busy}%"></i></div>`;
    }).join('');
    $('combat-status').textContent = p.mana === 0 ? '마나가 없습니다 · 보급 상자에 접근해 충전하세요' : p.busy > 0 ? `${phaseText} · ${p.busy.toFixed(1)}초` : '예고를 읽고 대응하세요 · 기술 사용 중에도 이동 가능';
    $('combat-status').classList.toggle('empty', p.mana === 0);

  }
  $('performance').textContent = `${ping} ms · ${world?.fps ?? 60} FPS · 적 ${world?.shownEnemies ?? 0}/${s.enemies.length}`;
  $('result').hidden = !ended;
  if (ended) {
    $('result-title').textContent = s.result === 'success' ? '분대, 복귀 완료.' : '분대 신호 소실.';
    $('result-description').textContent = s.result === 'success' ? '적 시설을 무력화하고 탈출 지점을 확보했습니다.' : '전원이 사망하여 작전이 종료되었습니다.';
    $('result-stats').innerHTML = `<div><b>${time(s.elapsed)}</b><span>작전 시간</span></div><div><b>${s.players.reduce((sum, member) => sum + member.kills, 0)}</b><span>분대 처치</span></div><div><b>${s.supplies.filter(f => f.objective && f.collected).length}/2</b><span>보급품 회수</span></div>`;
  }
}

$('create').addEventListener('click', () => void enter());
$('join').addEventListener('click', () => void enter($<HTMLInputElement>('room-input').value));
$('room-input').addEventListener('keydown', event => { if (event.key === 'Enter') void enter($<HTMLInputElement>('room-input').value); });
$('copy-code').addEventListener('click', () => {
  if (!guest) return;
  navigator.clipboard?.writeText(guest.room).then(() => toast('초대 코드를 복사했습니다. 친구에게 공유하세요.')).catch(() => toast(`초대 코드: ${guest!.room}`));
  if (!navigator.clipboard) toast(`초대 코드: ${guest.room}`);
});
$('camp').addEventListener('click', () => {
  if (!world || !snapshot) return;
  // 같은 캠프를 추천한다. 아군끼리는 겹칠 수 있다.
  world.landing = { x: snapshot.map.campX, y: snapshot.map.campY };
  updateUi();
});
// 요청을 보내는 즉시 버튼을 막는다. 다음 스냅샷이 오기 전에 다시 눌러도 요청이 두 번 나가지 않는다.
$('deploy').addEventListener('click', () => {
  if (!world?.landing || deployRequested) return;
  if (send({ type: 'deploy', ...world.landing })) { deployRequested = true; updateUi(); }
});
$('leave').addEventListener('click', leave);
// 버그 제보는 누른 순간의 틱을 서버 기록에 찍는다. 내용은 그 뒤에 화면 안 입력란으로 받아 그 제보에 덧붙인다.
// 브라우저 입력 창(prompt)을 쓰면 닫을 때까지 게임이 멈추고, 닫은 순간이 제보 틱으로 찍힌다.
$('report').addEventListener('click', () => {
  if (!send({ type: 'report' })) toast('작전에 연결된 뒤에 제보할 수 있습니다.');
});
// 입력란에서 친 키는 게임 키로 받지 않는다(keydown 처리가 입력란을 건너뛴다). 이모지를 반으로 자르지 않도록 글자 단위로 자른다.
$('report-note').addEventListener('submit', event => {
  event.preventDefault();
  const text = Array.from($<HTMLInputElement>('report-text').value.trim()).slice(0, 200).join('');
  if (text && reportTick !== null) send({ type: 'reportNote', tick: reportTick, text });
  closeReportNote();
});
$('report-close').addEventListener('click', () => closeReportNote());
$('report-text').addEventListener('keydown', event => { if (event.key === 'Escape') closeReportNote(); });
function closeReportNote() {
  const input = $<HTMLInputElement>('report-text');
  input.blur();
  input.value = '';
  $('report-note').hidden = true;
  reportTick = null;
}
$('again').addEventListener('click', leave);
// 캔버스 밖에서 떼거나 움직여도 놓은 버튼은 반영한다. 새로 누르는 것은 캔버스 위에서만 받는다.
window.addEventListener('pointerup', event => syncButtons(event.buttons, false));
window.addEventListener('pointermove', event => { if (event.target !== world?.app.canvas) syncButtons(event.buttons, false); });
window.addEventListener('blur', clearInput);
document.addEventListener('visibilitychange', () => { if (document.hidden) clearInput(); });
window.addEventListener('keydown', event => {
  if (!guest || event.target instanceof HTMLInputElement) return;
  if (['KeyW', 'KeyA', 'KeyS', 'KeyD', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'KeyQ', 'KeyM', 'Space', 'Digit1', 'Digit2', 'Digit3', 'Digit4'].includes(event.code)) event.preventDefault();
  keys.add(event.code);
  const pressed = slotFromCode(event.code);
  // 살아 있지 않을 때 누른 기술 키는 다시 눌러야 한다. 살아 있을 때 새로 누르면(자동 반복이 아니면) 다시 쓸 수 있다.
  if (pressed > 0 && me()?.state !== 'alive') staleKeys.add(event.code);
  else if (pressed > 0 && !event.repeat) staleKeys.delete(event.code);
  // 자동 반복은 누름 순서를 바꾸지 않는다. 다만 창이 포커스를 잃어 순서가 비워진 뒤에도
  // 계속 누르고 있던 키는 자동 반복으로만 들어오므로, 순서에 없을 때는 다시 넣는다.
  if (pressed > 0 && !staleKeys.has(event.code) && (!event.repeat || !slotOrder.includes(pressed))) pressSlot(pressed);
  if (event.repeat) return;
  if (event.code === 'KeyM' && world) world.overview = !world.overview;
});
window.addEventListener('keyup', event => { keys.delete(event.code); staleKeys.delete(event.code); });
function sendControl() {
  if (!world || !snapshot || me()?.state !== 'alive' || snapshot.phase !== 'active') return;
  let x = Number(keys.has('KeyD') || keys.has('ArrowRight')) - Number(keys.has('KeyA') || keys.has('ArrowLeft'));
  let y = Number(keys.has('KeyS') || keys.has('ArrowDown')) - Number(keys.has('KeyW') || keys.has('ArrowUp'));
  const length = Math.hypot(x, y); if (length > 1) { x /= length; y /= length; }
  const p = world.mePosition() ?? me()!;
  const aimAt = mouse.known ? world.screenToWorld(mouse.x, mouse.y) : { x: p.x + 1, y: p.y };
  const aim = Math.atan2(aimAt.y - p.y, aimAt.x - p.x);
  let slot = 0;
  for (const candidate of slotOrder) { if (slotHeld(candidate)) slot = candidate; }
  world.control = { x, y, aim, slot };
  sendInput(x, y, aim, slot);
}
// 입력은 서버의 틱과 같은 빠르기(초당 30번)로 보낸다. 클라이언트 예측이 보낸 입력 하나를 한 틱으로 치기 때문이다.
// setInterval(…, 1000 / 30)은 브라우저가 33ms로 내려 초당 30.3번이 되고 예측이 조금씩 앞서므로, 보낼 시각을 1/30초씩 더해 정한다.
// 타이머가 늦게 깨어 보낼 차례를 놓치면 밀린 입력을 몰아 보내지 않는다. 서버는 그동안 마지막 입력을 계속 쓰므로,
// 예측도 마지막 입력을 놓친 틱만큼 더 쓴 것으로 치고(holdLastInput) 다음 차례부터 다시 1/30초 간격으로 보낸다.
let nextInputAt = performance.now();
function pumpInput() {
  const missed = Math.max(0, Math.floor((performance.now() - nextInputAt) / tickMilliseconds));
  try {
    if (missed > 0) world?.holdLastInput(missed);
    sendControl();
  } finally {
    // 입력이나 예측에서 예외가 나도 다음 차례는 반드시 예약한다. 예약이 끊기면 새로고침할 때까지 입력이 나가지 않는다.
    nextInputAt += (missed + 1) * tickMilliseconds;
    window.setTimeout(pumpInput, Math.max(0, nextInputAt - performance.now()));
  }
}
pumpInput();
window.setInterval(() => send({ type: 'ping', sent: performance.now() }), 2000);

try { const saved = sessionStorage.getItem('argus-guest'); if (saved) guest = JSON.parse(saved) as Guest; } catch { sessionStorage.removeItem('argus-guest'); }
if (guest?.room && guest?.token) void openOperation();
