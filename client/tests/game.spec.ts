import { test, expect, type Page } from '@playwright/test';
import type { Snapshot } from '../src/types';

test('four browser guests, five-second entry, shooting, reload and refresh recovery', async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 1440, height: 960 } });
  // sessionStorage isolates guest identity per tab; four simultaneous software-rendered windows overload CI's GPU fallback.
  const pages = await Promise.all(Array.from({ length: 4 }, () => context.newPage()));
  const errors: string[] = [];
  const latest = new Map<Page, Snapshot>();
  for (const page of pages) {
    page.on('pageerror', error => errors.push(error.message));
    page.on('websocket', ws => ws.on('framereceived', frame => {
      try { const data = JSON.parse(frame.payload.toString()); if (data.type === 'snapshot') latest.set(page, data); } catch { }
    }));
  }
  const page = pages[0];
  try {
    await Promise.all(pages.map(p => p.goto('/')));
    await expect(page.getByRole('heading', { name: '분대에 합류하세요' })).toBeVisible();
    await page.screenshot({ path: 'test-results/argus-lobby.png', fullPage: true });
    await page.getByLabel('호출명').fill('Alpha');
    await page.getByRole('button', { name: '새 작전 시작' }).click();
    await expect(page.locator('#room-code')).not.toHaveText('------');
    await expect(page.locator('canvas')).toBeVisible();
    const code = await page.locator('#room-code').innerText();
    const self = () => latest.get(page)?.players.find(p => p.id === latest.get(page)?.you);
    await expect.poll(() => self()?.state).toBe('waiting');
    await expect(page.locator('#deploy')).toBeDisabled();
    const join = async (i: number) => {
      await pages[i].bringToFront();
      await pages[i].getByLabel('호출명').fill(`Pilot ${i + 1}`);
      await pages[i].getByLabel('방 코드').fill(code);
      await pages[i].getByRole('button', { name: '참가', exact: true }).click();
      await expect(pages[i].locator('#room-code')).toHaveText(code);
    };
    // Initialize all renderers before the clock starts; late joining is exercised by the protocol test.
    for (let i = 1; i < 4; i++) await join(i);
    for (const other of pages.slice(1)) {
      await other.bringToFront();
      await other.getByRole('button', { name: '캠프 선택' }).click();
    }
    await page.bringToFront();
    await page.getByRole('button', { name: '캠프 선택' }).click();
    const started = Date.now();
    await page.locator('#deploy').click();
    for (const other of pages.slice(1)) { await other.bringToFront(); await other.locator('#deploy').click(); }
    await page.bringToFront();
    await expect.poll(() => self()?.state).toBe('deploying');
    await expect.poll(() => self()?.state, { timeout: 8000 }).toBe('alive');
    expect(Date.now() - started).toBeGreaterThanOrEqual(4800);
    await expect(page.locator('#team-count')).toHaveText('4 / 4');
    await expect.poll(() => latest.get(page)?.players.filter(p => p.state === 'alive').length, { timeout: 10000 }).toBe(4);
    await page.bringToFront();
    const originalX = self()!.x;
    await page.keyboard.down('KeyD');
    await page.waitForTimeout(450);
    await page.keyboard.up('KeyD');
    await expect.poll(() => self()!.x).toBeGreaterThan(originalX + 35);
    const canvas = await page.locator('canvas').boundingBox();
    await page.mouse.move(canvas!.x + canvas!.width * .75, canvas!.y + canvas!.height / 2);
    await page.mouse.down();
    await expect.poll(() => self()!.ammo).toBeLessThan(29);
    await page.mouse.up();
    await page.keyboard.press('KeyR');
    await expect.poll(() => self()!.reload).toBeGreaterThan(0);
    await expect.poll(() => self()!.ammo, { timeout: 4000 }).toBe(30);
    await page.mouse.down();
    await expect.poll(() => self()!.ammo).toBeLessThan(30);
    await page.mouse.up();
    await page.waitForTimeout(200);
    const before = self()!;
    await page.reload();
    await expect(page.locator('#room-code')).toHaveText(code);
    await expect.poll(() => self()?.id).toBe(before.id);
    await expect.poll(() => self()?.state).toBe('alive');
    expect(self()!.ammo).toBe(before.ammo);
    await page.bringToFront();
    await page.screenshot({ path: 'test-results/argus-operation.png' });
    expect(errors).toEqual([]);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
    expect(overflow).toBe(false);
  } finally { await context.close(); }
});

test('wire protocol rejects invented position/resources, invalid motion and stolen identity', async ({ request }) => {
  const response = await request.post('/api/rooms', { data: { name: 'Wire test', passive: 'vitality' } });
  expect(response.ok()).toBeTruthy();
  const guest = await response.json();
  const url = (process.env.ARGUS_URL ?? 'http://127.0.0.1:5077').replace(/^http/, 'ws') + '/ws';
  const bad = new WebSocket(url);
  bad.addEventListener('open', () => bad.send(JSON.stringify({ type: 'hello', room: guest.room, token: 'wrong' })));
  const closeCode = await new Promise<number>(resolve => bad.addEventListener('close', event => resolve(event.code)));
  expect(closeCode).toBe(1008);
  const ws = new WebSocket(url);
  let snapshot: Snapshot | undefined;
  ws.addEventListener('message', event => { const message = JSON.parse(String(event.data)); if (message.type === 'snapshot') snapshot = message; });
  await new Promise<void>(resolve => ws.addEventListener('open', () => resolve()));
  const send = (value: object) => ws.send(JSON.stringify(value));
  send({ type: 'hello', room: guest.room, token: guest.token });
  await expect.poll(() => snapshot?.phase).toBe('staging');
  send({ type: 'deploy', x: 300, y: 730 });
  const self = () => snapshot?.players.find(p => p.id === guest.id);
  await expect.poll(() => self()?.state, { timeout: 8000 }).toBe('alive');
  send({ type: 'input', seq: 0, moveX: 0, moveY: 0, aim: 0, fire: false, x: 1300, y: 1200, hp: 99999, ammo: 99999, damage: 99999 });
  await expect.poll(() => self()?.ack).toBe(0);
  expect(self()!.x).toBe(300); expect(self()!.y).toBe(730); expect(self()!.hp).toBe(130); expect(self()!.ammo).toBe(30);
  send({ type: 'input', seq: 1, moveX: 100, moveY: 0, aim: 0 });
  send({ type: 'input', seq: 0, moveX: 1, moveY: 0, aim: 0 });
  send({ type: 'loadout', passive: 'mobility' });
  send({ type: 'deploy', x: 1000, y: 700 });
  // A later acknowledged valid packet proves the prior frames have passed through the receiver.
  send({ type: 'input', seq: 2, moveX: 0, moveY: 0, aim: 0 });
  await expect.poll(() => self()?.ack).toBe(2);
  expect(self()!.x).toBe(300); expect(self()!.passive).toBe('vitality'); expect(self()!.state).toBe('alive');
  const fifthJoins = [];
  for (let i = 0; i < 4; i++) fifthJoins.push(await request.post(`/api/rooms/${guest.room}/join`, { data: { name: 'guest' } }));
  expect(fifthJoins.map(r => r.status())).toEqual([200, 200, 200, 409]);
  const replaced = new Promise<number>(resolve => ws.addEventListener('close', event => resolve(event.code)));
  const replacement = new WebSocket(url);
  replacement.addEventListener('open', () => replacement.send(JSON.stringify({ type: 'hello', room: guest.room, token: guest.token })));
  expect(await replaced).toBe(4001);
  replacement.close();
});

test('a squad completes the real mission using only public gameplay commands', async ({ request }) => {
  const first = await request.post('/api/rooms', { data: { name: 'Objective 1' } });
  expect(first.ok()).toBeTruthy();
  const guests = [await first.json()];
  for (let i = 1; i < 4; i++) {
    const response = await request.post(`/api/rooms/${guests[0].room}/join`, { data: { name: `Objective ${i + 1}` } });
    expect(response.ok()).toBeTruthy(); guests.push(await response.json());
  }
  const url = (process.env.ARGUS_URL ?? 'http://127.0.0.1:5077').replace(/^http/, 'ws') + '/ws';
  const clients = await Promise.all(guests.map(async guest => {
    const ws = new WebSocket(url);
    const client = { ws, snapshot: undefined as Snapshot | undefined, seq: 0, lastDeploy: 0 };
    ws.addEventListener('message', event => { const value = JSON.parse(String(event.data)); if (value.type === 'snapshot') client.snapshot = value; });
    await new Promise<void>(resolve => ws.addEventListener('open', () => resolve()));
    ws.send(JSON.stringify({ type: 'hello', room: guest.room, token: guest.token }));
    return client;
  }));
  let controller: ReturnType<typeof setInterval> | undefined;
  try {
    await expect.poll(() => clients.every(c => c.snapshot)).toBe(true);
    const ext = clients[0].snapshot!.extraction;
    const positions = [{ x: 1050, y: 485 }, { x: 1300, y: 710 }, { x: 1100, y: 920 }, { x: ext.x, y: ext.y }];
    const exits = [{ x: ext.x - 30, y: ext.y - 30 }, { x: ext.x + 30, y: ext.y - 30 }, { x: ext.x - 30, y: ext.y + 30 }, { x: ext.x + 30, y: ext.y + 30 }];
    clients.forEach((c, i) => c.ws.send(JSON.stringify({ type: 'deploy', ...positions[i] })));
    await expect.poll(() => clients[0].snapshot?.players.filter(p => p.state === 'alive').length, { timeout: 8000 }).toBe(4);
    controller = setInterval(() => {
      clients.forEach((c, index) => {
        const s = c.snapshot!;
        if (s.phase !== 'active') return;
        const p = s.players.find(p => p.id === s.you)!;
        if (p.state === 'waiting' && Date.now() - c.lastDeploy > 1000) {
          c.lastDeploy = Date.now();
          c.ws.send(JSON.stringify({ type: 'deploy', ...(s.extraction.unlocked ? exits[index] : positions[index]) }));
        }
        if (p.state !== 'alive') return;
        const walls = Buffer.from(s.walls, 'base64');
        const clear = (x: number, y: number) => {
          const length = Math.hypot(x - p.x, y - p.y);
          for (let d = 0; d < length; d += 5) {
            const cx = Math.floor((p.x + (x - p.x) * d / length) / s.map.cell);
            const cy = Math.floor((p.y + (y - p.y) * d / length) / s.map.cell);
            if (walls[cy * s.map.columns + cx]) return false;
          }
          return true;
        };
        const danger = s.enemies.filter(e => clear(e.x, e.y)).sort((a, b) => Math.hypot(a.x - p.x, a.y - p.y) - Math.hypot(b.x - p.x, b.y - p.y))[0];
        const facility = s.facilities[index];
        const target = danger && Math.hypot(danger.x - p.x, danger.y - p.y) < 180 ? danger : facility?.hp > 0 ? facility : danger;
        const dx = exits[index].x - p.x, dy = exits[index].y - p.y, distance = Math.hypot(dx, dy);
        const move = s.extraction.unlocked && distance > 8;
        const allyInLine = target && s.players.some(ally => {
          if (ally.id === p.id || ally.state !== 'alive') return false;
          const tx = target.x - p.x, ty = target.y - p.y, lengthSquared = tx * tx + ty * ty;
          const along = ((ally.x - p.x) * tx + (ally.y - p.y) * ty) / lengthSquared;
          return along >= 0 && along < 1 && Math.hypot(p.x + along * tx - ally.x, p.y + along * ty - ally.y) < 20;
        });
        c.ws.send(JSON.stringify({ type: 'input', seq: c.seq++, moveX: move ? dx / distance : 0, moveY: move ? dy / distance : 0, aim: target ? Math.atan2(target.y - p.y, target.x - p.x) : 0, fire: !!target && !allyInLine }));
      });
    }, 50);
    await expect.poll(() => clients[0].snapshot?.facilities.filter(f => f.hp <= 0).length, { timeout: 20000 }).toBe(3);
    await expect.poll(() => clients[0].snapshot?.result, { timeout: 60000 }).toBe('success');
    expect(clients[0].snapshot!.extraction.progress).toBeGreaterThanOrEqual(ext.duration);
  } finally { if (controller) clearInterval(controller); clients.forEach(c => c.ws.close()); }
});
