import { Application, Container, Graphics, Text } from 'pixi.js';
import type { Control, Position, Snapshot } from './types';

const colors = { floor: 0x141e20, lit: 0x253335, grid: 0x304044, wall: 0x657372, edge: 0x94a29a, lime: 0xdfff80, teal: 0x77cec2, red: 0xf28068, gold: 0xeabd73 };
const team = [colors.lime, colors.teal, 0xaeb5fa, 0xf2c981];
const unitOutline = { color: 0x111a1a, width: 2.5 };

export class World {
  readonly app = new Application();
  private root = new Container();
  private ground = new Graphics();
  private light = new Graphics();
  private walls = new Graphics();
  private props = new Graphics();
  private units = new Graphics();
  private labels = new Container();
  private labelsById = new Map<string, Text>();
  private snapshot?: Snapshot;
  private previous?: Snapshot;
  private received = 0;
  private interval = 100;
  private tiles = new Uint8Array();
  private revision = -1;
  private predicted?: Position;
  private oldState = '';
  private camera = { x: 960, y: 720, zoom: 1 };
  private averageFrame = 16.7;
  private renderBudget = 10000;
  overview = false;
  landing?: Position;
  fps = 60;
  shownEnemies = 0;
  control: Control = { x: 0, y: 0, aim: 0, fire: false };

  async init(host: HTMLElement) {
    await this.app.init({
      resizeTo: host, background: '#10191b', antialias: true,
      resolution: Math.min(devicePixelRatio, 2), autoDensity: true,
      // Use native 2D drawing when hardware WebGL is unavailable, including software-only browsers.
      preference: ['webgl', 'canvas'], failIfMajorPerformanceCaveat: true,
    });
    host.appendChild(this.app.canvas);
    this.app.stage.addChild(this.root);
    this.root.addChild(this.ground, this.light, this.walls, this.props, this.units, this.labels);
    this.app.ticker.add(t => this.draw(Math.min(t.deltaMS / 1000, .05)));
  }

  update(snapshot: Snapshot) {
    const changedRoom = snapshot.room !== this.snapshot?.room;
    this.previous = changedRoom ? undefined : this.snapshot;
    this.interval = Math.max(50, Math.min(250, performance.now() - this.received));
    this.snapshot = snapshot;
    this.received = performance.now();
    if (changedRoom || this.revision !== snapshot.mapRevision) {
      this.revision = snapshot.mapRevision;
      this.tiles = Uint8Array.from(atob(snapshot.walls), c => c.charCodeAt(0));
      this.drawMap();
    }
    const me = snapshot.players.find(p => p.id === snapshot.you);
    if (me && (me.state !== this.oldState || changedRoom)) this.predicted = { x: me.x, y: me.y };
    this.oldState = me?.state ?? '';
    this.light.clear();
    for (const polygon of snapshot.sight) this.light.poly(polygon).fill(colors.lit);
  }

  screenToWorld(clientX: number, clientY: number): Position {
    const rect = this.app.canvas.getBoundingClientRect();
    return { x: (clientX - rect.left - this.root.x) / this.camera.zoom, y: (clientY - rect.top - this.root.y) / this.camera.zoom };
  }

  mePosition() { return this.predicted; }

  canStand(p: Position, radius = 15) {
    const m = this.snapshot?.map;
    if (!m || p.x < radius || p.y < radius || p.x > m.width - radius || p.y > m.height - radius) return false;
    for (let y = Math.floor((p.y - radius) / m.cell); y <= Math.floor((p.y + radius) / m.cell); y++) {
      for (let x = Math.floor((p.x - radius) / m.cell); x <= Math.floor((p.x + radius) / m.cell); x++) {
        if (this.tiles[y * m.columns + x] === 0) continue;
        const dx = p.x - Math.max(x * m.cell, Math.min((x + 1) * m.cell, p.x));
        const dy = p.y - Math.max(y * m.cell, Math.min((y + 1) * m.cell, p.y));
        if (dx * dx + dy * dy < radius * radius) return false;
      }
    }
    return true;
  }

  private move(p: Position, dx: number, dy: number): Position {
    const next = { ...p };
    if (this.canStand({ x: p.x + dx, y: p.y }, 13)) next.x += dx;
    if (this.canStand({ x: next.x, y: p.y + dy }, 13)) next.y += dy;
    return next;
  }

  private drawMap() {
    const s = this.snapshot!;
    const g = this.ground.clear();
    g.rect(0, 0, s.map.width, s.map.height).fill(colors.floor);
    for (let x = 0; x <= s.map.width; x += s.map.cell) g.moveTo(x, 0).lineTo(x, s.map.height);
    for (let y = 0; y <= s.map.height; y += s.map.cell) g.moveTo(0, y).lineTo(s.map.width, y);
    g.stroke({ color: colors.grid, width: 1, alpha: .28 });
    const w = this.walls.clear();
    this.tiles.forEach((tile, index) => {
      if (!tile) return;
      const x = (index % s.map.columns) * s.map.cell, y = Math.floor(index / s.map.columns) * s.map.cell;
      w.rect(x + 3, y + 5, s.map.cell - 3, s.map.cell - 3).fill({ color: 0x000000, alpha: .3 });
      w.rect(x + 1, y + 1, s.map.cell - 2, s.map.cell - 2).fill(tile === 2 ? 0x303b3e : colors.wall);
      if (tile === 1) w.moveTo(x + 3, y + 2).lineTo(x + s.map.cell - 3, y + 2).stroke({ color: colors.edge, width: 2 });
    });
  }

  private label(key: string, text: string, x: number, y: number, color: number, size = 12) {
    let label = this.labelsById.get(key);
    if (!label) {
      label = new Text({ text, style: { fontFamily: 'sans-serif', fontSize: size, fontWeight: '600', fill: color } });
      label.anchor.set(.5, .5);
      this.labelsById.set(key, label);
      this.labels.addChild(label);
    }
    label.text = text;
    label.position.set(x, y);
    label.visible = true;
  }

  private draw(dt: number) {
    const s = this.snapshot;
    if (!s) return;
    // Keep an unfocused companion window useful without rendering it at the foreground frame rate.
    const focused = document.hasFocus();
    this.app.ticker.maxFPS = focused ? 60 : 10;
    this.averageFrame += (this.app.ticker.deltaMS - this.averageFrame) * .02;
    this.fps = Math.round(1000 / this.averageFrame);
    if (focused && this.fps < 35 && s.enemies.length > 40) this.renderBudget = Math.max(30, this.shownEnemies - 1);
    if (focused && this.fps > 52) this.renderBudget = Math.min(10000, this.renderBudget + 1);
    const me = s.players.find(p => p.id === s.you);
    const overview = this.overview || me?.state !== 'alive' || s.phase === 'ended';
    const elapsed = Math.min(.2, (performance.now() - this.received) / 1000);
    if (me) {
      let next = this.predicted ?? { x: me.x, y: me.y };
      const speed = s.rules.speed * (me.passive === 'mobility' ? 1.22 : 1);
      const active = me.state === 'alive' && s.phase === 'active' && performance.now() - this.received < 500;
      const dx = active ? this.control.x * speed : 0, dy = active ? this.control.y * speed : 0;
      next = this.move(next, dx * dt, dy * dt);
      const target = this.move(me, dx * elapsed, dy * elapsed);
      const distance = Math.hypot(target.x - next.x, target.y - next.y);
      const correction = distance > 100 ? 1 : Math.min(1, dt * 12);
      this.predicted = { x: next.x + (target.x - next.x) * correction, y: next.y + (target.y - next.y) * correction };
    }
    const width = this.app.screen.width, height = this.app.screen.height;
    const zoom = overview ? Math.min((width - 70) / s.map.width, (height - 120) / s.map.height) : Math.min(1.15, Math.max(.65, Math.min(width / 1050, height / 740)));
    this.camera.zoom = zoom;
    this.camera.x = overview ? s.map.width / 2 : this.predicted?.x ?? s.map.campX;
    this.camera.y = overview ? s.map.height / 2 : this.predicted?.y ?? s.map.campY;
    this.camera.x = Math.max(Math.min(width / zoom / 2, s.map.width / 2), Math.min(s.map.width - width / zoom / 2, this.camera.x));
    this.camera.y = Math.max(Math.min(height / zoom / 2, s.map.height / 2), Math.min(s.map.height - height / zoom / 2, this.camera.y));
    this.root.scale.set(zoom);
    this.root.position.set(width / 2 - this.camera.x * zoom, height / 2 - this.camera.y * zoom);
    this.labelsById.forEach(label => { label.visible = false; });
    const g = this.props.clear();
    g.circle(s.map.campX, s.map.campY, 78).fill({ color: colors.teal, alpha: .06 }).stroke({ color: colors.teal, width: 1, alpha: .5 });
    g.rect(s.map.campX - 20, s.map.campY - 20, 40, 40).stroke({ color: colors.teal, width: 2 });
    g.moveTo(s.map.campX - 12, s.map.campY).lineTo(s.map.campX + 12, s.map.campY).moveTo(s.map.campX, s.map.campY - 12).lineTo(s.map.campX, s.map.campY + 12).stroke({ color: colors.teal, width: 2 });
    this.label('camp', 'CAMP · 권장 투입', s.map.campX, s.map.campY + 105, colors.teal, 15);
    const ext = s.extraction;
    g.circle(ext.x, ext.y, 80).fill({ color: colors.teal, alpha: ext.unlocked ? .12 : .025 }).stroke({ color: colors.teal, alpha: ext.unlocked ? .9 : .25, width: 2 });
    if (ext.progress > 0) g.arc(ext.x, ext.y, 80, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * ext.progress / ext.duration).stroke({ color: colors.lime, width: 7 });
    this.label('extract', ext.unlocked ? '↑ 탈출 · 구역 확보' : '↑ 탈출 지점', ext.x, ext.y + 102, colors.teal, 15);
    for (const f of s.facilities) {
      const color = f.hp > 0 ? colors.red : 0x64706a;
      g.rect(f.x - 27, f.y - 27, 54, 54).fill(0x1a2325).stroke({ color, width: 3 });
      g.moveTo(f.x - 18, f.y - 18).lineTo(f.x + 18, f.y + 18).moveTo(f.x + 18, f.y - 18).lineTo(f.x - 18, f.y + 18).stroke({ color, width: 3 });
      if (f.hp > 0) g.rect(f.x - 27, f.y - 39, 54 * f.hp / f.maxHp, 4).fill(color);
      this.label(`facility-${f.id}`, f.hp > 0 ? `0${f.id} · 적 시설` : `0${f.id} · 파괴 완료`, f.x, f.y + 47, color, 14);
    }
    for (const supply of s.supplies) {
      if (supply.collected || supply.cooldown > 0) continue;
      const color = supply.objective ? colors.gold : colors.teal;
      g.roundRect(supply.x - 12, supply.y - 12, 24, 24, 3).fill(0x1a2325).stroke({ color, width: 2 });
      g.moveTo(supply.x - 6, supply.y).lineTo(supply.x + 6, supply.y).moveTo(supply.x, supply.y - 6).lineTo(supply.x, supply.y + 6).stroke({ color, width: 2 });
      this.label(`supply-${supply.id}`, supply.objective ? '보급품 회수' : '보급', supply.x, supply.y + 30, color, 12);
    }
    const landing = me?.state === 'deploying' ? { x: me.landingX, y: me.landingY } : me?.state === 'waiting' ? this.landing : undefined;
    if (landing && s.phase !== 'ended') {
      g.circle(landing.x, landing.y, 30 + Math.sin(performance.now() / 240) * 3).stroke({ color: colors.lime, width: 2 });
      g.moveTo(landing.x - 40, landing.y).lineTo(landing.x + 40, landing.y).moveTo(landing.x, landing.y - 40).lineTo(landing.x, landing.y + 40).stroke({ color: colors.lime, alpha: .5, width: 1 });
      this.label('landing', me?.state === 'deploying' ? `${me.deploy.toFixed(1)}s` : '선택한 투입 위치', landing.x, landing.y - 55, colors.lime, 16);
    }
    const u = this.units.clear();
    const alpha = Math.min(1, elapsed * 1000 / this.interval);
    const interpolate = (p: Position, old?: Position) => old ? { x: old.x + (p.x - old.x) * alpha, y: old.y + (p.y - old.y) * alpha } : p;
    const oldEnemies = new Map(this.previous?.enemies.map(e => [e.id, e]));
    let enemies = s.enemies;
    if (enemies.length > this.renderBudget) {
      const at = this.predicted ?? { x: s.map.campX, y: s.map.campY };
      enemies = [...enemies].sort((a, b) => Math.hypot(a.x - at.x, a.y - at.y) - Math.hypot(b.x - at.x, b.y - at.y)).slice(0, this.renderBudget);
    }
    this.shownEnemies = enemies.length;
    // Iterate the current visible set only: an enemy removed by server visibility never lingers in interpolation.
    for (const enemy of enemies) {
      const p = interpolate(enemy, oldEnemies.get(enemy.id));
      if (enemy.kind === 'ranged') {
        u.poly([p.x, p.y - 14, p.x + 13, p.y, p.x, p.y + 14, p.x - 13, p.y]).fill(colors.gold).stroke(unitOutline);
      } else {
        u.rect(p.x - 12, p.y - 12, 24, 24).fill(colors.red).stroke(unitOutline);
      }
    }
    s.players.forEach((player, index) => {
      if (player.state !== 'alive') return;
      const isMe = player.id === s.you;
      const p = isMe ? this.predicted! : interpolate(player, this.previous?.players.find(old => old.id === player.id));
      const color = team[index % team.length];
      const aim = isMe ? this.control.aim : player.aim;
      const forward = { x: Math.cos(aim), y: Math.sin(aim) };
      const side = { x: -forward.y * 4, y: forward.x * 4 };
      // A flat disk and a short rectangular barrel keep direction readable with two simple shapes.
      u.poly([
        p.x + forward.x * 8 + side.x, p.y + forward.y * 8 + side.y,
        p.x + forward.x * 24 + side.x, p.y + forward.y * 24 + side.y,
        p.x + forward.x * 24 - side.x, p.y + forward.y * 24 - side.y,
        p.x + forward.x * 8 - side.x, p.y + forward.y * 8 - side.y,
      ]).fill(color).stroke(unitOutline);
      u.circle(p.x, p.y, 13).fill(color).stroke(unitOutline);
      u.rect(p.x - 16, p.y - 24, 32, 3).fill(0x172222).rect(p.x - 16, p.y - 24, 32 * player.hp / player.maxHp, 3).fill(color);
      this.label(`player-${player.id}`, `${isMe ? '▼ ' : ''}${player.name}${player.connected ? '' : ' · 연결 끊김'}`, p.x, p.y - 40, color, 13);
    });
    for (const b of s.bullets) {
      const p = interpolate(b, this.previous?.bullets.find(old => old.id === b.id));
      const length = Math.hypot(b.vx, b.vy) || 1;
      u.moveTo(p.x - b.vx / length * 15, p.y - b.vy / length * 15).lineTo(p.x, p.y).stroke({ color: b.hostile ? colors.red : colors.lime, width: 3, cap: 'round' });
    }
    for (const g of s.grenades) u.circle(g.x, g.y, 6).fill(colors.gold).stroke({ color: 0xffffff, width: 1 });
    for (const effect of s.effects) {
      const remain = Math.max(0, effect.until - (s.now + elapsed));
      if (!remain) continue;
      const big = effect.kind === 'explosion';
      const radius = big ? (1 - remain / .8) * 140 + 10 : (1 - remain / .6) * 30 + 2;
      const color = effect.kind === 'heal' || effect.kind === 'deploy' ? colors.teal : colors.gold;
      u.circle(effect.x, effect.y, Math.max(2, radius)).fill({ color, alpha: remain * .15 }).stroke({ color, alpha: Math.min(1, remain * 2), width: big ? 3 : 2 });
    }
  }
}
