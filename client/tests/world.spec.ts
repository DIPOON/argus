import { test, expect } from '@playwright/test';
import { createServer, type ViteDevServer } from 'vite';
import type { Container, Text } from 'pixi.js';
import type { Control, Position, Snapshot } from '../src/types';

// 실제 World와 Pixi 객체를 검사한다. 테스트용 접근점은 아래 임시 페이지에만 있다.
interface TestWorld {
  update(snapshot: Snapshot): void;
  draw(dt: number): void;
  canStand(position: Position, radius: number): boolean;
  control: Control;
  move(position: Position, dx: number, dy: number): Position;
  recordInput(seq: number, x: number, y: number, at?: number): void;
  replay(from: Position, speed: number, now: number): Position;
  pending: unknown[];
  mePosition(): Position | undefined;
  labelsById: Map<string, Text>;
  labels: Container;
}

declare global {
  interface Window {
    testWorld: TestWorld;
  }
}

let server: ViteDevServer;
let url: string;

test.beforeAll(async () => {
  server = await createServer({
    configFile: false,
    server: { host: '127.0.0.1', port: 0 },
    plugins: [{
      name: 'world-test-page',
      configureServer(devServer) {
        devServer.middlewares.use('/__world_test', (_request, response) => {
          response.setHeader('Content-Type', 'text/html');
          response.end(`<!doctype html><html><body>
            <div id="host" style="width:1000px;height:800px"></div>
            <script type="module">
              import { World } from '/src/world.ts';
              const world = new World();
              await world.init(document.getElementById('host'));
              world.app.ticker.stop();
              window.testWorld = world;
            </script>
          </body></html>`);
        });
      },
    }],
  });
  await server.listen();
  url = server.resolvedUrls!.local[0] + '__world_test';
});

test.afterAll(async () => {
  await server.close();
});

test.beforeEach(async ({ page }) => {
  await page.goto(url);
  await page.waitForFunction(() => window.testWorld !== undefined);
});

function snapshot(): Snapshot {
  const walls = new Uint8Array(40 * 30);
  walls[10 * 40 + 10] = 1;
  return {
    type: 'snapshot', version: 2, room: 'ROOM01', you: 'pilot', now: 10, elapsed: 5,
    phase: 'active', result: null, pulse: 0, nextPulse: 595,
    walls: Buffer.from(walls).toString('base64'), mapRevision: 0,
    extraction: { x: 1080, y: 1130, progress: 0, duration: 20, unlocked: false },
    map: { width: 1920, height: 1440, cell: 48, columns: 40, rows: 30, campX: 300, campY: 730 },
    rules: { deploy: 5, speed: 180, playerRadius: 13, guardHalfAngle: Math.PI / 3, parryFollowupSeconds: .18, skills: [] },
    players: [{
      id: 'pilot', name: 'Pilot', x: 400, y: 730, slots: ['strike', 'parry', 'grab', 'strike'],
      state: 'alive', connected: true, hasDeployed: true, aim: 0, hp: 6, maxHp: 6,
      mana: 12, maxMana: 24, busy: 0, actions: [], deploy: 0, kills: 0, deaths: 0,
      ack: 0, landingX: 400, landingY: 730,
    }],
    enemies: [], facilities: [], supplies: [], effects: [], sight: [], notices: [],
  };
}

function elite(id: number): Snapshot['enemies'][number] {
  return { id, kind: 'breaker', name: '분쇄자', x: 460, y: 730, hp: 2, maxHp: 2, radius: 22,
    tiers: [2, 1, 1], action: null };
}

test('120 enemy replacements release labels, including when changing rooms', async ({ page }) => {
  const state = snapshot();
  const result = await page.evaluate(({ state, enemy }) => {
    const world = window.testWorld;
    world.update(structuredClone(state)); world.draw(1 / 60);
    const baseline = world.labelsById.size;
    const oldPlayerLabel = world.labelsById.get('player-pilot')!;
    const labels: Text[] = [];
    let maximum = baseline;
    for (let i = 0; i < 120; i++) {
      state.enemies = [{ ...enemy, id: 1000 + i }];
      world.update(structuredClone(state)); world.draw(1 / 60);
      labels.push(world.labelsById.get(`enemy-type-${1000 + i}`)!);
      maximum = Math.max(maximum, world.labelsById.size);
    }
    state.enemies = [];
    world.update(structuredClone(state)); world.draw(1 / 60);
    const afterRemoval = world.labelsById.size;
    state.room = 'ROOM02'; state.you = 'new-pilot'; state.players[0].id = state.you;
    world.update(structuredClone(state)); world.draw(1 / 60);
    return { baseline, maximum, afterRemoval, afterRoomChange: world.labelsById.size,
      children: world.labels.children.length, destroyed: labels.every(label => label.destroyed && label.parent === null),
      oldPlayerDestroyed: oldPlayerLabel.destroyed && oldPlayerLabel.parent === null };
  }, { state, enemy: elite(1000) });
  expect(result.maximum).toBe(result.baseline + 1);
  expect(result.afterRemoval).toBe(result.baseline);
  expect(result.afterRoomChange).toBe(result.baseline);
  expect(result.children).toBe(result.baseline);
  expect(result.destroyed).toBe(true);
  expect(result.oldPlayerDestroyed).toBe(true);
});

test('enemy tells refresh color and disappear immediately with lost visibility', async ({ page }) => {
  const state = snapshot();
  state.enemies = [elite(900)];
  state.enemies[0].action = { id: 901, skill: 'strike', kind: 'strike', slot: 0, tier: 2, phase: 'telegraph',
    aim: Math.PI, startedAt: 9.5, contactAt: 11, activeUntil: 11.25, endsAt: 12, range: 82, halfWidth: 24 };
  const result = await page.evaluate(state => {
    const world = window.testWorld;
    world.update(structuredClone(state)); world.draw(1 / 60);
    const label = world.labelsById.get('enemy-action-900')!;
    const first = { text: label.text, color: label.style.fill };
    state.enemies[0].action!.kind = 'channel';
    state.enemies[0].action!.tier = 1;
    world.update(structuredClone(state)); world.draw(1 / 60);
    const changed = { text: label.text, color: label.style.fill };
    state.enemies = [];
    world.update(structuredClone(state)); world.draw(1 / 60);
    return { first, changed, removed: !world.labelsById.has('enemy-action-900'), destroyed: label.destroyed };
  }, state);
  expect(result.first).toEqual({ text: '타격 Ⅱ · 대응 2', color: 0xf28068 });
  expect(result.changed).toEqual({ text: '잡기 Ⅰ', color: 0xc0a1f5 });
  expect(result.removed).toBe(true); expect(result.destroyed).toBe(true);
});

test('prediction blocks enemy bodies while allowing teammate overlap', async ({ page }) => {
  const state = snapshot();
  state.enemies = [elite(900)];
  const result = await page.evaluate(state => {
    const world = window.testWorld;
    world.update(structuredClone(state));
    const blocked = world.move(state.players[0], 200, 0);
    state.enemies = [];
    state.players.push({ ...state.players[0], id: 'ally', x: 430 });
    world.update(structuredClone(state));
    const through = world.move(state.players[0], 200, 0);
    return { blocked, through };
  }, state);
  expect(result.blocked.x).toBeLessThanOrEqual(425);
  expect(result.blocked.x).toBeGreaterThan(400);
  expect(result.through.x).toBe(600);
});

test('reconciliation replays unacknowledged input on top of the server position', async ({ page }) => {
  const state = snapshot();
  const result = await page.evaluate(state => {
    const world = window.testWorld;
    const interval = 1000 / 30;
    world.update(structuredClone(state));
    // 1/30초마다 오른쪽 입력 6개를 보냈고 서버는 아직 하나도 처리하지 않았다.
    for (let seq = 1; seq <= 6; seq++) world.recordInput(seq, 1, 0, 1000 + (seq - 1) * interval);
    const now = 1000 + 6 * interval;
    const ahead = world.replay(state.players[0], 180, now);
    // 서버가 입력 4까지 처리했다는 스냅샷이 오면 그 이하는 버리고 새 서버 위치에서 다시 계산한다.
    state.players[0] = { ...state.players[0], x: 424, ack: 4 };
    world.update(structuredClone(state));
    const left = world.pending.length;
    const after = world.replay(state.players[0], 180, now);
    return { ahead, left, after };
  }, state);
  // 확인받지 않은 입력 0.2초 분량(180 × 0.2 = 36단위)만큼 서버 위치 400보다 앞이다.
  expect(result.ahead.x).toBeCloseTo(436, 1);
  expect(result.left).toBe(2);
  // 서버가 예측과 같은 곳에 있으면 확인이 와도 그리는 위치가 튀지 않는다.
  expect(result.after.x).toBeCloseTo(436, 1);
});

test('the drawn position follows the reconciled position instead of the lagging server position', async ({ page }) => {
  const state = snapshot();
  const drawn = await page.evaluate(state => {
    const world = window.testWorld;
    world.update(structuredClone(state));
    const now = performance.now();
    // 0.2초 동안 오른쪽으로 보낸 입력을 서버가 아직 처리하지 않은 상태다.
    for (let i = 0; i < 6; i++) world.recordInput(i + 1, 1, 0, now - 200 + i * 1000 / 30);
    // 현재 입력을 0으로 두어 프레임마다의 이동을 빼고 보정 목표만 본다.
    world.control = { x: 0, y: 0, aim: 0, slot: 0 };
    for (let frame = 0; frame < 30; frame++) world.draw(1 / 60);
    return world.mePosition()!;
  }, state);
  // 예전 방식은 서버 위치 400에 머물렀다. 재조정하면 입력 0.2초 분량인 436 근처로 간다.
  expect(drawn.x).toBeGreaterThan(430);
  expect(drawn.x).toBeLessThan(445);
});
