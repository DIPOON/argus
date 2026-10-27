import { test, expect } from '@playwright/test';
import type { Enemy, Guest, Player, Position, Snapshot } from '../src/types';

// 실제 HTTP 참가와 WebSocket 입력만 사용한다. 서버 상태를 직접 수정하지 않는다.
function route(snapshot: Snapshot, from: Position, goal: Position): Position {
  const { cell, columns, rows } = snapshot.map;
  const walls = Buffer.from(snapshot.walls, 'base64');
  const index = (p: Position) => Math.floor(p.y / cell) * columns + Math.floor(p.x / cell);
  const center = (i: number) => ({ x: (i % columns + .5) * cell, y: (Math.floor(i / columns) + .5) * cell });
  const start = index(from), end = index(goal);
  const previous = new Int32Array(walls.length).fill(-1);
  const queue = [start];
  previous[start] = start;
  for (let head = 0; head < queue.length && previous[end] === -1; head++) {
    const current = queue[head], x = current % columns, y = Math.floor(current / columns);
    for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const nx = x + dx, ny = y + dy, next = ny * columns + nx;
      if (nx < 0 || ny < 0 || nx >= columns || ny >= rows || walls[next] || previous[next] !== -1) continue;
      previous[next] = current;
      queue.push(next);
    }
  }
  if (previous[end] === -1) return from;
  const path = [goal];
  for (let cursor = end; cursor !== start; cursor = previous[cursor]) path.push(center(cursor));
  path.push(center(start));
  // 경로의 모서리를 잘라 갈 때에도 플레이어 몸 전체가 통과할 수 있어야 한다.
  const canWalk = (to: Position) => {
    const steps = Math.max(1, Math.ceil(Math.hypot(to.x - from.x, to.y - from.y) / 6));
    const radius = snapshot.rules.playerRadius + 1;
    for (let step = 1; step <= steps; step++) {
      const x = from.x + (to.x - from.x) * step / steps, y = from.y + (to.y - from.y) * step / steps;
      for (let cy = Math.floor((y - radius) / cell); cy <= Math.floor((y + radius) / cell); cy++) {
        for (let cx = Math.floor((x - radius) / cell); cx <= Math.floor((x + radius) / cell); cx++) {
          if (!walls[cy * columns + cx]) continue;
          const dx = x - Math.max(cx * cell, Math.min((cx + 1) * cell, x));
          const dy = y - Math.max(cy * cell, Math.min((cy + 1) * cell, y));
          if (dx * dx + dy * dy < radius * radius) return false;
        }
      }
    }
    return true;
  };
  return path.find(canWalk) ?? from;
}

// 분대가 아직 보지 못한 칸이다(protocol v5). route와 clear는 0이 아닌 칸을 모두 막힌 것으로 보므로, 보지 못한 칸으로는 길을 내지 않는다.
const unknownTile = 3;

// 갈 곳(시설·탈출 지점)을 아직 보지 못했을 때 둘러볼 칸이다. from에서 본 빈 땅으로만 걸어 닿는 칸 가운데 보지 못한 칸과 맞닿은 칸
// (본 곳의 경계)을 고른다. 손으로 만든 맵의 시설은 맵 가운데 둘레에 있으므로 맵 가운데에 가장 가까운 경계로 간다.
// 경계 칸에 서면 맞닿은 칸이 모두 보이므로(벽이면 앞면이 보인다) 같은 경계에 머물지 않는다.
function frontier(snapshot: Snapshot, from: Position): Position | undefined {
  const { cell, columns, rows, width, height } = snapshot.map;
  const walls = Buffer.from(snapshot.walls, 'base64');
  const start = Math.floor(from.y / cell) * columns + Math.floor(from.x / cell);
  const reached = new Uint8Array(walls.length);
  const queue = [start];
  reached[start] = 1;
  let best: Position | undefined;
  let bestDistance = Infinity;
  for (let head = 0; head < queue.length; head++) {
    const current = queue[head], x = current % columns, y = Math.floor(current / columns);
    let edge = false;
    for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const nx = x + dx, ny = y + dy, next = ny * columns + nx;
      if (nx < 0 || ny < 0 || nx >= columns || ny >= rows) continue;
      if (walls[next] === unknownTile) edge = true;
      if (walls[next] || reached[next]) continue;
      reached[next] = 1;
      queue.push(next);
    }
    if (!edge) continue;
    const center = { x: (x + .5) * cell, y: (y + .5) * cell };
    const distance = Math.hypot(center.x - width / 2, center.y - height / 2);
    if (distance < bestDistance) { best = center; bestDistance = distance; }
  }
  return best;
}

function clear(snapshot: Snapshot, from: Position, to: Position) {
  const walls = Buffer.from(snapshot.walls, 'base64');
  const steps = Math.max(1, Math.ceil(Math.hypot(to.x - from.x, to.y - from.y) / 2));
  for (let step = 0; step <= steps; step++) {
    const x = Math.floor((from.x + (to.x - from.x) * step / steps) / snapshot.map.cell);
    const y = Math.floor((from.y + (to.y - from.y) * step / steps) / snapshot.map.cell);
    if (walls[y * snapshot.map.columns + x]) return false;
  }
  return true;
}

function counterSlot(enemy: Enemy, snapshot: Snapshot, player: Player) {
  if (player.busy > 0 || player.mana === 0) return 0;
  const action = enemy.action;
  if (!action || snapshot.now >= action.activeUntil) return 1;
  if (action.kind === 'block') return 3;
  if (action.kind === 'channel') return 1;
  // 받아치기를 너무 일찍 쓰면 적 공격이 도달하기 전에 방어가 끝난다.
  if (action.contactAt - snapshot.now < .38) return 2;
  return 0;
}

test('a squad counters enemies and completes the mission using only public gameplay commands', async ({ request }, testInfo) => {
  // 봇은 손으로 만든 맵의 지형에 맞춰 짰으므로 그 맵을 고른다. 서버 설정 Argus:Missions:AllowHandmade가 켜져 있어야 한다.
  const first = await request.post('/api/rooms', { data: { name: 'Squad 1', layout: 'handmade' } });
  expect(first.ok()).toBeTruthy();
  const guests: Guest[] = [await first.json()];
  for (let i = 1; i < 4; i++) {
    const response = await request.post(`/api/rooms/${guests[0].room}/join`, { data: { name: `Squad ${i + 1}` } });
    expect(response.ok()).toBeTruthy();
    guests.push(await response.json());
  }
  const url = (process.env.ARGUS_URL ?? 'http://127.0.0.1:5077').replace(/^http/, 'ws') + '/ws';
  const clients = await Promise.all(guests.map(async guest => {
    const ws = new WebSocket(url);
    const client = { ws, snapshot: undefined as Snapshot | undefined, seq: 0, lastDeploy: 0 };
    ws.addEventListener('message', event => {
      const value = JSON.parse(String(event.data));
      if (value.type !== 'snapshot') return;
      // 벽·규칙·맵은 바뀌거나 처음일 때만 온다(protocol v3부터). 빠진 값은 이전 스냅샷에서 이어 쓴다.
      const previous = client.snapshot;
      client.snapshot = { ...value, walls: value.walls ?? previous?.walls, rules: value.rules ?? previous?.rules, map: value.map ?? previous?.map };
    });
    await new Promise<void>(resolve => ws.addEventListener('open', () => resolve()));
    ws.send(JSON.stringify({ type: 'hello', room: guest.room, token: guest.token }));
    return client;
  }));
  let controller: ReturnType<typeof setInterval> | undefined;
  const history: object[] = [];
  const usedSlots = new Set<number>();
  const effects = new Set<string>();
  try {
    await expect.poll(() => clients.every(client => client.snapshot)).toBe(true);
    // 캠프 상자 자리에 투입해 각자 마나를 채운 뒤 함께 움직인다. 들판 상자는 상자마다 두 번만 채워 넷이 한 상자로 채울 수 없다.
    // 캠프 상자도 분대가 함께 네 번이라 처음 투입에서 다 쓴다. 쓰러졌다가 다시 투입한 대원은 아래에서 가까운 상자로 채우러 간다.
    const campSupply = clients[0].snapshot!.supplies.find(supply => supply.kind === 'camp')!;
    const landing = { x: campSupply.x, y: campSupply.y };
    for (const client of clients) client.ws.send(JSON.stringify({ type: 'deploy', ...landing }));
    await expect.poll(() => clients[0].snapshot?.players.filter(player => player.state === 'alive').length, { timeout: 8000 }).toBe(4);
    expect(clients[0].snapshot!.players.every(player => player.mana === 24)).toBe(true);
    let lastRecord = 0;
    controller = setInterval(() => {
      const shared = clients[0].snapshot!;
      if (shared.phase !== 'active') return;
      const leader = shared.players.find(player => player.state === 'alive');
      if (!leader) return;
      // 시설과 탈출 지점은 분대가 본 것만 온다. 시설을 다 부수기 전에는 아직 보지 못한 시설을, 다 부순 뒤에는 탈출 지점을 찾아 둘러본다.
      const facility = shared.facilities.find(target => target.hp > 0);
      const destroyed = shared.facilities.filter(target => target.hp <= 0).length;
      const known = facility ?? (destroyed === 3 ? shared.extraction ?? undefined : undefined);
      const exploring = known ? undefined : frontier(shared, leader);
      const goal = known ?? exploring ?? leader;
      const danger = shared.enemies.filter(enemy => clear(shared, leader, enemy))
        .sort((a, b) => Math.hypot(a.x - leader.x, a.y - leader.y) - Math.hypot(b.x - leader.x, b.y - leader.y))[0];
      const fight = danger && Math.hypot(danger.x - leader.x, danger.y - leader.y) < 125;
      for (const client of clients) {
        const snapshot = client.snapshot!;
        const player = snapshot.players.find(p => p.id === snapshot.you)!;
        for (const effect of snapshot.effects) effects.add(effect.kind);
        for (const action of player.actions) usedSlots.add(action.slot);
        if (player.state === 'waiting' && Date.now() - client.lastDeploy > 1000) {
          client.lastDeploy = Date.now();
          client.ws.send(JSON.stringify({ type: 'deploy', ...landing }));
        }
        if (player.state !== 'alive') continue;
        let target: Position = goal;
        let destination: Position = goal;
        let slot = 0;
        let stop = facility ? 62 : exploring ? 8 : 30;
        // 마나가 없으면 싸울 수 없으므로(counterSlot) 남은 횟수가 있고 내 대기 시간이 끝난 가장 가까운 상자로 간다.
        const refill = player.mana === 0 ? snapshot.supplies
          .filter(supply => supply.uses > 0 && supply.cooldown === 0)
          .sort((a, b) => Math.hypot(a.x - player.x, a.y - player.y) - Math.hypot(b.x - player.x, b.y - player.y))[0] : undefined;
        if (refill) {
          target = refill;
          destination = refill;
          stop = 8;
        } else if (fight) {
          target = danger;
          destination = danger;
          stop = danger.radius + 36;
          if (Math.hypot(player.x - danger.x, player.y - danger.y) < stop + 8 && clear(snapshot, player, danger)) {
            slot = counterSlot(danger, snapshot, player);
          }
        } else if (facility && Math.hypot(player.x - facility.x, player.y - facility.y) < 75 && clear(snapshot, player, facility)) {
          slot = player.busy === 0 ? 1 : 0;
        }
        const distance = Math.hypot(destination.x - player.x, destination.y - player.y);
        const next = route(snapshot, player, destination);
        const dx = next.x - player.x, dy = next.y - player.y, length = Math.hypot(dx, dy);
        const move = distance > stop && length > 2;
        client.ws.send(JSON.stringify({ type: 'input', seq: client.seq++, moveX: move ? dx / length : 0,
          moveY: move ? dy / length : 0, aim: Math.atan2(target.y - player.y, target.x - player.x), slot }));
      }
      if (shared.now - lastRecord > 1) {
        lastRecord = shared.now;
        history.push({ now: shared.now, facilities: shared.facilities.map(f => f.hp), extraction: shared.extraction?.progress,
          players: shared.players.map(p => ({ state: p.state, x: p.x, y: p.y, hp: p.hp, mana: p.mana })), enemies: shared.enemies });
      }
    }, 50);
    await expect.poll(() => clients[0].snapshot?.facilities.filter(facility => facility.hp === 0).length, { timeout: 30000 }).toBe(3);
    await expect.poll(() => clients[0].snapshot?.result, { timeout: 45000 }).toBe('success');
    expect(clients[0].snapshot!.extraction!.progress).toBeGreaterThanOrEqual(clients[0].snapshot!.extraction!.duration);
    expect(clients[0].snapshot!.players.reduce((kills, player) => kills + player.kills, 0)).toBeGreaterThan(0);
    expect(effects.has('break')).toBe(true);
    expect(usedSlots.has(1)).toBe(true);
  } finally {
    if (controller) clearInterval(controller);
    for (const client of clients) client.ws.close();
    await testInfo.attach('mission-history', { body: JSON.stringify(history), contentType: 'application/json' });
  }
});
