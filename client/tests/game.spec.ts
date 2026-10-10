import { test, expect, type Page } from '@playwright/test';
import type { Snapshot } from '../src/types';

function observe(page: Page) {
  let snapshot: Snapshot | undefined;
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('websocket', ws => ws.on('framereceived', frame => {
    try { const data = JSON.parse(frame.payload.toString()); if (data.type === 'snapshot') snapshot = data; } catch { }
  }));
  return { get state() { return snapshot; }, get player() { return snapshot?.players.find(p => p.id === snapshot?.you); }, errors };
}

test('four guests deploy, collect mana, cast while moving and reconnect with the same resources', async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 1440, height: 960 } });
  const pages = await Promise.all(Array.from({ length: 4 }, () => context.newPage()));
  const observed = pages.map(observe);
  const page = pages[0], me = observed[0];
  try {
    await Promise.all(pages.map(p => p.goto('/')));
    await page.screenshot({ path: 'test-results/argus-rps-lobby.png', fullPage: true });
    await page.getByLabel('호출명').fill('Alpha');
    await page.getByRole('button', { name: '새 작전 시작' }).click();
    await expect(page.locator('#room-code')).not.toHaveText('------');
    const code = await page.locator('#room-code').innerText();
    await expect.poll(() => me.player?.state).toBe('waiting');
    await expect(page.locator('#deploy')).toBeDisabled();
    for (let i = 1; i < 4; i++) {
      await pages[i].bringToFront();
      await pages[i].getByLabel('호출명').fill(`Pilot ${i + 1}`);
      await pages[i].getByLabel('방 코드').fill(code);
      await pages[i].getByRole('button', { name: '참가', exact: true }).click();
      await expect(pages[i].locator('#room-code')).toHaveText(code);
    }
    for (const p of pages) {
      await p.getByRole('button', { name: '캠프 선택' }).click();
    }
    const start = Date.now();
    await Promise.all(pages.map(p => p.locator('#deploy').click()));
    await expect.poll(() => me.state?.players.filter(p => p.state === 'alive').length, { timeout: 10000 }).toBe(4);
    expect(Date.now() - start).toBeGreaterThanOrEqual(4800);
    expect(me.player?.mana).toBe(0);
    expect(me.player?.slots).toEqual(['strike', 'parry', 'grab', 'strike']);
    await page.bringToFront();
    await page.keyboard.down('KeyD'); await page.keyboard.down('KeyS');
    await page.waitForTimeout(470);
    await page.keyboard.up('KeyD'); await page.keyboard.up('KeyS');
    await expect.poll(() => me.player?.mana).toBe(24);
    const canvas = (await page.locator('canvas').boundingBox())!;
    await page.mouse.move(canvas.x + canvas.width * .15, canvas.y + canvas.height / 2);
    const x = me.player!.x;
    await page.keyboard.down('KeyA');
    await page.mouse.down();
    await expect.poll(() => me.player?.mana).toBeLessThan(24);
    await expect.poll(() => me.player?.x).toBeLessThan(x - 15);
    await page.mouse.up(); await page.keyboard.up('KeyA');
    await expect(page.locator('#mana')).toHaveText(String(me.player!.mana));
    await page.waitForTimeout(150);
    const before = me.player!;
    await page.reload();
    await expect(page.locator('#room-code')).toHaveText(code);
    await expect.poll(() => me.player?.id).toBe(before.id);
    expect(me.player!.mana).toBe(before.mana);
    await expect(page.locator('#skill-bar .skill-slot')).toHaveCount(4);
    await page.screenshot({ path: 'test-results/argus-rps-operation.png' });
    for (const result of observed) expect(result.errors).toEqual([]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)).toBe(false);
  } finally { await context.close(); }
});

test('all four input bindings use slots, consume mana on whiffs and keep the free slot after refresh', async ({ page, request }) => {
  const invalid = await request.post('/api/rooms', { data: { slots: ['strike', 'parry', 'grab', 'turret'] } });
  expect(invalid.status()).toBe(400);
  const me = observe(page);
  await page.goto('/');
  await page.locator('input[name="free-slot"][value="grab"]').check();
  await page.getByRole('button', { name: '새 작전 시작' }).click();
  await expect.poll(() => me.player?.slots[3]).toBe('grab');
  await page.getByRole('button', { name: '캠프 선택' }).click();
  await page.locator('#deploy').click();
  await expect.poll(() => me.player?.state, { timeout: 8000 }).toBe('alive');
  await expect(page.locator('#combat-status')).toContainText('마나가 없습니다');
  await page.keyboard.down('KeyD'); await page.keyboard.down('KeyS');
  await page.waitForTimeout(470);
  await page.keyboard.up('KeyD'); await page.keyboard.up('KeyS');
  await expect.poll(() => me.player?.mana).toBe(24);
  const canvas = (await page.locator('canvas').boundingBox())!;
  await page.mouse.move(canvas.x + canvas.width * .05, canvas.y + canvas.height / 2);
  for (const [key, slot, skill] of [['Digit1', 1, 'strike'], ['Digit2', 2, 'parry'], ['Space', 3, 'grab'], ['KeyQ', 4, 'grab']] as const) {
    await expect.poll(() => me.player?.busy).toBe(0);
    const mana = me.player!.mana;
    await page.keyboard.down(key);
    await expect.poll(() => me.player?.actions.at(-1)?.slot).toBe(slot);
    await page.keyboard.up(key);
    expect(me.player!.actions.at(-1)!.skill).toBe(skill);
    expect(me.player!.mana).toBe(mana - 1);
  }
  const before = me.player!;
  await page.reload();
  await expect.poll(() => me.player?.id).toBe(before.id);
  expect(me.player!.slots[3]).toBe('grab');
  expect(me.player!.mana).toBe(before.mana);
  expect(me.errors).toEqual([]);
});

test('wire protocol rejects forged state, invalid slots, replay and stolen identity; late join works', async ({ request }) => {
  const response = await request.post('/api/rooms', { data: { name: 'Wire test' } });
  const guest = await response.json();
  const url = (process.env.ARGUS_URL ?? 'http://127.0.0.1:5077').replace(/^http/, 'ws') + '/ws';
  const bad = new WebSocket(url);
  bad.addEventListener('open', () => bad.send(JSON.stringify({ type: 'hello', room: guest.room, token: 'wrong' })));
  expect(await new Promise<number>(resolve => bad.addEventListener('close', event => resolve(event.code)))).toBe(1008);
  const ws = new WebSocket(url);
  let snapshot: Snapshot | undefined;
  ws.addEventListener('message', event => { const message = JSON.parse(String(event.data)); if (message.type === 'snapshot') snapshot = message; });
  await new Promise<void>(resolve => ws.addEventListener('open', () => resolve()));
  const send = (value: object) => ws.send(JSON.stringify(value));
  let replacement: WebSocket | undefined;
  let newcomer: WebSocket | undefined;
  try {
    send({ type: 'hello', room: guest.room, token: guest.token });
    await expect.poll(() => snapshot?.phase).toBe('staging');
    send({ type: 'deploy', x: 300, y: 730 });
    const self = () => snapshot?.players.find(p => p.id === guest.id);
    await expect.poll(() => self()?.state, { timeout: 8000 }).toBe('alive');
    send({ type: 'input', seq: 0, moveX: 0, moveY: 0, aim: 0, slot: 0, x: 1300, y: 1200, hp: 999, mana: 999, damage: 999 });
    await expect.poll(() => self()?.ack).toBe(0);
    expect(self()!.x).toBe(300); expect(self()!.mana).toBe(0); expect(self()!.hp).toBe(6);
    send({ type: 'input', seq: 1, moveX: 100, moveY: 0, aim: 0, slot: 0 });
    send({ type: 'input', seq: 1, moveX: 0, moveY: 0, aim: 0, slot: 9 });
    send({ type: 'input', seq: 1, moveX: 0, moveY: 0, aim: 0, slot: 2.5 });
    send({ type: 'input', seq: 1, moveX: 0, moveY: 0, aim: 0, slot: 'strike' });
    send({ type: 'input', seq: 0, moveX: 1, moveY: 0, aim: 0, slot: 0 });
    send({ type: 'loadout', slots: ['strike', 'parry', 'grab', 'grab'] });
    send({ type: 'deploy', x: 1000, y: 700 });
    send({ type: 'input', seq: 2, moveX: 0, moveY: 0, aim: 0, slot: 1 });
    await expect.poll(() => self()?.ack).toBe(2);
    expect(self()!.x).toBe(300); expect(self()!.slots[3]).toBe('strike'); expect(self()!.mana).toBe(0);
    expect(self()!.actions).toHaveLength(0);
    const joins = [];
    for (let i = 0; i < 4; i++) joins.push(await request.post(`/api/rooms/${guest.room}/join`, { data: { name: 'guest' } }));
    expect(joins.map(r => r.status())).toEqual([200, 200, 200, 409]);
    const late = await joins[0].json();
    newcomer = new WebSocket(url);
    newcomer.addEventListener('open', () => newcomer!.send(JSON.stringify({ type: 'hello', room: late.room, token: late.token })));
    await expect.poll(() => snapshot?.players.find(p => p.id === late.id)?.connected).toBe(true);
    newcomer.send(JSON.stringify({ type: 'deploy', x: 300, y: 730 }));
    await expect.poll(() => snapshot?.players.find(p => p.id === late.id)?.state, { timeout: 8000 }).toBe('alive');
    expect(snapshot!.players.find(p => p.id === late.id)!.mana).toBe(0);
    const replaced = new Promise<number>(resolve => ws.addEventListener('close', event => resolve(event.code)));
    replacement = new WebSocket(url);
    replacement.addEventListener('open', () => replacement!.send(JSON.stringify({ type: 'hello', room: guest.room, token: guest.token })));
    expect(await replaced).toBe(4001);
  } finally { ws.close(); replacement?.close(); newcomer?.close(); }
});
