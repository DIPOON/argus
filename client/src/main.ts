import './style.css';
import { World } from './world';
import type { Guest, Player, Snapshot } from './types';

const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;
const escape = (value: string) => value.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);
const time = (seconds: number) => `${Math.floor(Math.max(0, seconds) / 60).toString().padStart(2, '0')}:${Math.floor(Math.max(0, seconds) % 60).toString().padStart(2, '0')}`;

$('app').innerHTML = `
  <header class="topbar">
    <a class="brand" href="/" aria-label="ARGUS 홈"><span class="brand-mark">△</span> ARGUS <span class="version">FIELD TEST / 01</span></a>
    <div class="top-right"><span id="connection" class="connection"><i></i> 투입 대기</span><span class="build-tag">CO-OP PVE · 1–4</span></div>
  </header>
  <main id="lobby" class="lobby">
    <section class="briefing">
      <div class="eyebrow"><span class="dash"></span> OPERATION 001</div>
      <h1>침묵의<br><span>전초기지.</span></h1>
      <p class="lead">적 시설을 무력화하고, 함께 탈출하세요.<br>한 구역. 하나의 분대. 마지막까지 살아남는 작전.</p>
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
      <div class="field-label loadout-title">장비 구성 <span>기본 장비</span></div>
      <div class="equipment"><div><span class="weapon-icon">⌁</span><div><b>돌격 소총</b><small>30발 · 예비탄 ∞ · 재장전 2초</small></div><span class="slot">01</span></div><div><span class="weapon-icon">◉</span><div><b>파편 수류탄</b><small>3개 · 폭발 피해 · 벽 파괴</small></div><span class="slot">02</span></div></div>
      <fieldset class="passives"><legend class="field-label">패시브 선택</legend><label><input type="radio" name="passive" value="vitality" checked/><span><b>＋ 강화 체력</b><small>체력 100 → 130</small></span></label><label><input type="radio" name="passive" value="mobility"/><span><b>↗ 기동력</b><small>이동속도 +22%</small></span></label></fieldset>
      <button id="create" class="primary">새 작전 시작 <span>↗</span></button>
      <div class="or"><span></span> 초대받았다면 <span></span></div>
      <div class="join-row"><input id="room-input" aria-label="방 코드" placeholder="6자리 방 코드" maxlength="6" autocomplete="off"/><button id="join" class="secondary">참가</button></div>
      <p id="lobby-error" class="error" role="alert"></p>
      <p class="entry-note"><span>◌</span> 투입 준비 5초 · 아군 사격 주의 · 전원 사망 시 실패</p>
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
      <div class="side-bottom"><div class="section-label">FIELD NOTES</div><p>밝은 구역은 분대의 공유 시야입니다. 어두운 곳에도 적이 있을 수 있습니다.</p><p class="friendly">아군에게도 피해가 들어갑니다.</p><button id="leave" class="text-button">작전에서 나가기 ↗</button></div>
    </aside>
    <section id="battlefield" class="battlefield" aria-label="작전 지도">
      <div id="canvas-host" class="canvas-host"></div>
      <div class="map-top"><span class="map-title"><i></i><span id="view-label">전술 지도 / 투입 위치 선택</span></span><div class="timers"><span>경과 <b id="elapsed">00:00</b></span><span id="pulse-label">전역 충격 <b id="pulse">10:00</b></span></div></div>
      <div id="toast" class="toast" role="status" hidden></div>
      <div id="deploy-panel" class="deploy-panel"><div><span class="eyebrow" id="deploy-kicker">READY TO DEPLOY</span><h3 id="deploy-title">투입할 위치를 선택하세요</h3><p id="deploy-description">지도의 빈 지면을 클릭하세요. 선택 전에는 계속 대기합니다.</p></div><div class="deploy-actions"><button id="camp" class="secondary">캠프 선택</button><button id="deploy" class="primary" disabled>위치 선택 필요</button></div></div>
      <div id="combat-hud" class="combat-hud" hidden><div class="health-block"><span id="my-name">대원</span><div><b id="hp">130</b><small id="max-hp">/ 130 HP</small></div><div class="health-bar"><i id="health-fill"></i></div></div><div class="ammo-block"><span id="weapon-state">돌격 소총</span><div><b id="ammo">30</b><small>/ 30</small><em>∞</em></div><div class="reload-bar"><i id="reload-fill"></i></div></div><div class="grenade-block"><span>수류탄 <kbd>G</kbd></span><b id="grenades">● ● ●</b></div></div>
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
let fire = false;
let reload = false;
let grenade = false;
let lastNotice = '';
let toastTimer = 0;
const keys = new Set<string>();
let mouse = { x: 0, y: 0, known: false };

function me(): Player | undefined { return snapshot?.players.find(p => p.id === snapshot?.you); }
function send(value: object) { if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(value)); }
function status(text: string, connected = false) { $('connection').innerHTML = `<i class="${connected ? 'online' : ''}"></i> ${escape(text)}`; }
function toast(text: string) {
  $('toast').textContent = text; $('toast').hidden = false;
  window.clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => { $('toast').hidden = true; }, 3500);
}
function clearInput() { keys.clear(); fire = reload = grenade = false; if (world) world.control = { x: 0, y: 0, aim: world.control.aim, fire: false }; }

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
      body: JSON.stringify({ name: $<HTMLInputElement>('callsign').value || '대원', passive: document.querySelector<HTMLInputElement>('input[name="passive"]:checked')!.value }),
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
    canvas.addEventListener('pointermove', event => { mouse = { x: event.clientX, y: event.clientY, known: true }; });
    canvas.addEventListener('pointerdown', event => {
      if (event.button !== 0 || !world) return;
      mouse = { x: event.clientX, y: event.clientY, known: true };
      if (me()?.state === 'waiting' && snapshot?.phase !== 'ended') {
        const point = world.screenToWorld(event.clientX, event.clientY);
        if (!world.canStand(point)) { toast('벽을 피해 빈 지면을 선택하세요.'); return; }
        world.landing = point; updateUi();
      } else if (me()?.state === 'alive') fire = true;
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
    if (message.type === 'error') { toast(message.message); return; }
    if (message.type !== 'snapshot') return;
    snapshot = message as Snapshot;
    sequence = Math.max(sequence, (me()?.ack ?? -1) + 1);
    status('분대 연결됨', true);
    world!.update(snapshot);
    updateUi();
    const notice = snapshot.notices.at(-1);
    const noticeKey = notice ? `${notice.at}:${notice.text}` : '';
    if (notice && noticeKey !== lastNotice) { lastNotice = noticeKey; toast(notice.text); }
  });
  ws.addEventListener('close', event => {
    if (socket !== ws || !guest) return;
    clearInput();
    if (event.code === 1008) { leave(); $('lobby-error').textContent = '참가 정보가 만료되었습니다. 새 작전에 참가해 주세요.'; return; }
    if (event.code === 4001) { leave(); $('lobby-error').textContent = '같은 대원이 다른 창에서 접속했습니다. 이 창에서는 별도 대원으로 참가할 수 있습니다.'; return; }
    status('재접속 중');
    toast('연결을 복구하고 있습니다. 캐릭터와 임무는 계속 진행됩니다.');
    reconnectTimer = window.setTimeout(connect, 1500);
  });
}

function leave() {
  guest = undefined; snapshot = undefined; lastNotice = ''; sequence = 0;
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
  $('pulse').textContent = time(s.nextPulse);
  $('pulse-label').classList.toggle('danger', s.nextPulse < 30);
  $('primary-count').textContent = `${s.facilities.filter(f => f.hp <= 0).length}/3`;
  $('secondary-count').textContent = `${s.supplies.filter(f => f.objective && f.collected).length}/2`;
  $('facility-list').innerHTML = s.facilities.map(f => `<span class="${f.hp <= 0 ? 'done' : ''}">${f.hp <= 0 ? '✓' : '◇'} 0${f.id}</span>`).join('');
  $('extract-note').textContent = s.extraction.unlocked ? `구역 안에서 ${s.extraction.duration}초 확보 · ${s.extraction.progress.toFixed(1)}초` : '주목표 완료 후 활성화';
  $('extract-progress').style.width = `${Math.min(100, 100 * s.extraction.progress / s.extraction.duration)}%`;
  $('team-count').textContent = `${s.players.length} / 4`;
  $('roster').innerHTML = s.players.map((member, index) => `<div class="member ${member.id === s.you ? 'self' : ''}"><span class="member-number color-${index}">0${index + 1}</span><div><b>${escape(member.name)}${member.id === s.you ? ' <small>나</small>' : ''}</b><span>${!member.connected ? '연결 끊김' : member.state === 'alive' ? `${Math.ceil(member.hp)} HP` : member.state === 'deploying' ? `투입까지 ${member.deploy.toFixed(1)}초` : member.hasDeployed ? '증원 대기' : '투입 대기'}</span></div><i class="${member.state === 'alive' ? 'alive-dot' : ''}"></i></div>`).join('');
  $('deploy-panel').hidden = alive || ended;
  $('combat-hud').hidden = !alive || ended;
  $('view-label').textContent = alive ? '분대 공유 시야 / SECTOR 07' : '전술 지도 / 투입 위치 선택';
  $('controls').textContent = alive ? 'WASD 이동 · 마우스 사격 · R 재장전 · G 수류탄 · M 전체 지도' : '클릭 위치 선택 · 캠프는 권장 투입 지점';
  if (!alive && !ended) {
    const preparing = p.state === 'deploying';
    $('deploy-kicker').textContent = preparing ? 'DEPLOYMENT IN PROGRESS' : p.hasDeployed ? 'REINFORCEMENT' : 'READY TO DEPLOY';
    $('deploy-title').textContent = preparing ? `${p.deploy.toFixed(1)}초 후 투입합니다` : p.hasDeployed ? '증원할 위치를 선택하세요' : '투입할 위치를 선택하세요';
    $('deploy-description').textContent = preparing ? '투입이 완료될 때까지 해당 위치의 시야는 열리지 않습니다.' : '빈 지면을 클릭하세요. 어두운 곳의 안전은 보장되지 않습니다.';
    $<HTMLButtonElement>('camp').disabled = preparing;
    $<HTMLButtonElement>('deploy').disabled = preparing || !world?.landing;
    $('deploy').textContent = preparing ? '투입 준비 중…' : world?.landing ? '5초 후 투입 ↗' : '위치 선택 필요';
  }
  if (alive) {
    $('my-name').textContent = p.name;
    $('hp').textContent = Math.ceil(p.hp).toString(); $('max-hp').textContent = `/ ${p.maxHp} HP`;
    $('health-fill').style.width = `${100 * p.hp / p.maxHp}%`;
    $('health-fill').classList.toggle('low', p.hp < p.maxHp * .3);
    $('ammo').textContent = p.ammo.toString().padStart(2, '0');
    $('weapon-state').textContent = p.reload > 0 ? `재장전 · ${p.reload.toFixed(1)}초` : '돌격 소총';
    $('reload-fill').style.width = p.reload > 0 ? `${100 * (1 - p.reload / s.rules.reload)}%` : '0%';
    $('grenades').textContent = Array.from({ length: s.rules.grenades }, (_, i) => i < p.grenades ? '●' : '○').join(' ');
  }
  $('performance').textContent = `${ping} ms · ${world?.fps ?? 60} FPS · 적 ${world?.shownEnemies ?? 0}/${s.enemies.length}`;
  $('result').hidden = !ended;
  if (ended) {
    $('result-title').textContent = s.result === 'success' ? '분대, 복귀 완료.' : '분대 신호 소실.';
    $('result-description').textContent = s.result === 'success' ? '적 시설을 무력화하고 탈출 지점을 확보했습니다.' : '전원이 사망하여 작전이 종료되었습니다.';
    $('result-stats').innerHTML = `<div><b>${time(s.elapsed)}</b><span>작전 시간</span></div><div><b>${s.players.reduce((sum, member) => sum + member.kills, 0)}</b><span>분대 처치</span></div><div><b>${s.supplies.filter(f => f.objective && f.collected).length}/2</b><span>보급품 회수</span></div>`;
    clearInput();
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
  // Separate recommended landing points keep the squad's first shots from hitting overlapping teammates.
  const offsets = [[0, 0], [0, -60], [0, 60], [-60, 0]];
  const index = Math.max(0, snapshot.players.findIndex(p => p.id === snapshot!.you));
  const offset = offsets[index % offsets.length];
  world.landing = { x: snapshot.map.campX + offset[0], y: snapshot.map.campY + offset[1] };
  updateUi();
});
$('deploy').addEventListener('click', () => { if (world?.landing) send({ type: 'deploy', ...world.landing }); });
$('leave').addEventListener('click', leave);
$('again').addEventListener('click', leave);
window.addEventListener('pointerup', () => { fire = false; });
window.addEventListener('blur', clearInput);
document.addEventListener('visibilitychange', () => { if (document.hidden) clearInput(); });
window.addEventListener('keydown', event => {
  if (!guest || event.target instanceof HTMLInputElement) return;
  if (['KeyW', 'KeyA', 'KeyS', 'KeyD', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'KeyR', 'KeyG', 'KeyM', 'Space'].includes(event.code)) event.preventDefault();
  keys.add(event.code);
  if (event.repeat) return;
  if (event.code === 'KeyR') reload = true;
  if (event.code === 'KeyG') grenade = true;
  if (event.code === 'KeyM' && world) world.overview = !world.overview;
});
window.addEventListener('keyup', event => keys.delete(event.code));
window.setInterval(() => {
  if (!world || !snapshot || me()?.state !== 'alive' || snapshot.phase !== 'active') return;
  let x = Number(keys.has('KeyD') || keys.has('ArrowRight')) - Number(keys.has('KeyA') || keys.has('ArrowLeft'));
  let y = Number(keys.has('KeyS') || keys.has('ArrowDown')) - Number(keys.has('KeyW') || keys.has('ArrowUp'));
  const length = Math.hypot(x, y); if (length > 1) { x /= length; y /= length; }
  const p = world.mePosition() ?? me()!;
  const aimAt = mouse.known ? world.screenToWorld(mouse.x, mouse.y) : { x: p.x + 1, y: p.y };
  const aim = Math.atan2(aimAt.y - p.y, aimAt.x - p.x);
  world.control = { x, y, aim, fire };
  send({ type: 'input', seq: sequence++, moveX: x, moveY: y, aim, fire, reload, grenade });
  reload = grenade = false;
}, 1000 / 30);
window.setInterval(() => send({ type: 'ping', sent: performance.now() }), 2000);

try { const saved = sessionStorage.getItem('argus-guest'); if (saved) guest = JSON.parse(saved) as Guest; } catch { sessionStorage.removeItem('argus-guest'); }
if (guest?.room && guest?.token) void openOperation();
