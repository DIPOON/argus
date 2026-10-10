import './style.css';
import { World } from './world';
import { skillNames, type Guest, type Player, type Snapshot, type Skill } from './types';

const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;
const escape = (value: string) => value.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);
const time = (seconds: number) => `${Math.floor(Math.max(0, seconds) / 60).toString().padStart(2, '0')}:${Math.floor(Math.max(0, seconds) % 60).toString().padStart(2, '0')}`;
const slotKeys = ['좌클릭 / 1', '우클릭 / 2', 'Space / 3', 'Q / 4'];

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
      <div class="side-bottom"><div class="section-label">FIELD NOTES</div><p>밝은 구역은 분대의 공유 시야입니다. 어두운 곳에도 적이 있을 수 있습니다.</p><p class="combat-legend"><span class="skill-strike">타격</span>은 잡기를 이깁니다.<br><span class="skill-block">방어</span>는 타격을 이깁니다.<br><span class="skill-channel">잡기</span>는 방어를 이깁니다.</p><p>첫 작전에는 1티어 적만 등장합니다. 타격 예고가 끝날 무렵 받아치세요. 너무 일찍 쓰면 방어가 먼저 끝납니다.</p><button id="leave" class="text-button">작전에서 나가기 ↗</button></div>
    </aside>
    <section id="battlefield" class="battlefield" aria-label="작전 지도">
      <div id="canvas-host" class="canvas-host"></div>
      <div class="map-top"><span class="map-title"><i></i><span id="view-label">전술 지도 / 투입 위치 선택</span></span><div class="timers"><span>경과 <b id="elapsed">00:00</b></span><span id="pulse-label">전역 충격 <b id="pulse">10:00</b></span></div></div>
      <div id="toast" class="toast" role="status" hidden></div>
      <div id="deploy-panel" class="deploy-panel"><div><span class="eyebrow" id="deploy-kicker">READY TO DEPLOY</span><h3 id="deploy-title">투입할 위치를 선택하세요</h3><p id="deploy-description">지도의 빈 지면을 클릭하세요. 선택 전에는 계속 대기합니다.</p></div><div class="deploy-actions"><button id="camp" class="secondary">캠프 선택</button><button id="deploy" class="primary" disabled>위치 선택 필요</button></div></div>
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
function clearInput() { keys.clear(); mouseButtons.clear(); if (world) world.control = { x: 0, y: 0, aim: world.control.aim, slot: 0 }; send({ type: 'input', seq: sequence++, moveX: 0, moveY: 0, aim: world?.control.aim ?? 0, slot: 0 }); }

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
    canvas.addEventListener('pointermove', event => { mouse = { x: event.clientX, y: event.clientY, known: true }; });
    canvas.addEventListener('pointerdown', event => {
      if (!world || (event.button !== 0 && event.button !== 2)) return;
      mouse = { x: event.clientX, y: event.clientY, known: true };
      if (event.button === 0 && me()?.state === 'waiting' && snapshot?.phase !== 'ended') {
        const point = world.screenToWorld(event.clientX, event.clientY);
        if (!world.canStand(point)) { toast('벽을 피해 빈 지면을 선택하세요.'); return; }
        world.landing = point; updateUi();
      } else if (me()?.state === 'alive') { mouseButtons.add(event.button); }
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
    if (message.version !== 2) { leave(); $('lobby-error').textContent = '서버와 클라이언트 버전이 다릅니다. 새로고침 후 다시 접속하세요.'; return; }
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
  $('roster').innerHTML = s.players.map((member, index) => `<div class="member ${member.id === s.you ? 'self' : ''}"><span class="member-number color-${index}">0${index + 1}</span><div><b>${escape(member.name)}${member.id === s.you ? ' <small>나</small>' : ''}</b><span>${!member.connected ? '연결 끊김' : member.state === 'alive' ? `${Math.ceil(member.hp)} HP` : member.state === 'deploying' ? `투입까지 ${member.deploy.toFixed(1)}초` : member.hasDeployed ? '증원 대기' : '투입 대기'}</span><span class="roster-loadout">마나 ${member.mana}/${member.maxMana} · 자유 ${skillNames[member.slots[3]]}</span></div><i class="${member.state === 'alive' ? 'alive-dot' : ''}"></i></div>`).join('');
  $('deploy-panel').hidden = alive || ended;
  $('combat-hud').hidden = !alive || ended;
  $('view-label').textContent = alive ? '분대 공유 시야 / SECTOR 07' : '전술 지도 / 투입 위치 선택';
  $('controls').textContent = alive ? 'WASD 이동 · 좌클릭 타격 · 우클릭 받아치기 · Space 잡기 · Q 자유 슬롯 · M 지도' : '클릭 위치 선택 · 캠프는 권장 투입 지점';
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
  // 같은 캠프를 추천한다. 아군끼리는 겹칠 수 있다.
  world.landing = { x: snapshot.map.campX, y: snapshot.map.campY };
  updateUi();
});
$('deploy').addEventListener('click', () => { if (world?.landing) send({ type: 'deploy', ...world.landing }); });
$('leave').addEventListener('click', leave);
$('again').addEventListener('click', leave);
window.addEventListener('pointerup', event => { mouseButtons.delete(event.button); });
window.addEventListener('blur', clearInput);
document.addEventListener('visibilitychange', () => { if (document.hidden) clearInput(); });
window.addEventListener('keydown', event => {
  if (!guest || event.target instanceof HTMLInputElement) return;
  if (['KeyW', 'KeyA', 'KeyS', 'KeyD', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'KeyQ', 'KeyM', 'Space', 'Digit1', 'Digit2', 'Digit3', 'Digit4'].includes(event.code)) event.preventDefault();
  keys.add(event.code);
  if (event.repeat) return;
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
  let slot = 0;
  if (mouseButtons.has(0) || keys.has('Digit1')) slot = 1;
  if (mouseButtons.has(2) || keys.has('Digit2')) slot = 2;
  if (keys.has('Space') || keys.has('Digit3')) slot = 3;
  if (keys.has('KeyQ') || keys.has('Digit4')) slot = 4;
  world.control = { x, y, aim, slot };
  send({ type: 'input', seq: sequence++, moveX: x, moveY: y, aim, slot });
}, 1000 / 30);
window.setInterval(() => send({ type: 'ping', sent: performance.now() }), 2000);

try { const saved = sessionStorage.getItem('argus-guest'); if (saved) guest = JSON.parse(saved) as Guest; } catch { sessionStorage.removeItem('argus-guest'); }
if (guest?.room && guest?.token) void openOperation();
