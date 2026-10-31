import { Application, Container, Graphics, Text } from 'pixi.js';
import { fromDouble, toDouble, type FixedVector } from './fixed';
import { deployable, inputHoldTicks, stepPlayer, tickMilliseconds, unknownTile, type Body, type Terrain } from './movement';
import { affinityNames, type Affinity, type CombatAction, type Control, type Position, type Snapshot } from './types';

const colors = { floor: 0x141e20, lit: 0x253335, grid: 0x304044, wall: 0x657372, edge: 0x94a29a, unknown: 0x0b1315, lime: 0xdfff80, teal: 0x77cec2, red: 0xf28068, gold: 0xeabd73 };
const affinityColors: Record<Affinity, number> = { strike: 0xf28068, block: 0x77cec2, channel: 0xc0a1f5, none: 0xeabd73 };
const team = [colors.lime, colors.teal, 0xaeb5fa, 0xf2c981];
const unitOutline = { color: 0x111a1a, width: 2.5 };

// 원호는 이 함수로만 그린다. Pixi는 fill·stroke 뒤의 다음 경로를 직전 경로의 마지막 점에서 시작한다.
// 그런데 직전 경로가 원·사각형·다각형 같은 도형으로 끝나면 마지막 점을 계산하지 않고, 읽을 때마다 0으로 되돌리는
// 임시 점(Point.shared)을 그대로 쓴다. 그러면 원호가 지도 원점(왼쪽 위)에서부터 직선으로 이어져 그려진다.
// beginPath로 새 경로를 열면 시작점 없이 원호만 그려진다.
const arcOnNewPath = (g: Graphics, x: number, y: number, radius: number, start: number, end: number) =>
  g.beginPath().arc(x, y, radius, start, end);

// 확인받지 않은 입력을 이만큼(30Hz로 2초 분량)까지 보관한다. 확인이 오지 않는 이상한 상황에서도 끝없이 늘지 않게 한다.
const maxPending = 60;

// 보냈지만 서버가 아직 확인하지 않은 입력이다. ticks는 이 입력이 쓰일 것으로 보는 틱 수다. 보통 1이고, 다음 입력을
// 제때 보내지 못하면(타이머가 늦게 깸) 서버처럼 이 입력을 그만큼 더 쓴 것으로 친다.
type PendingInput = { seq: number; x: number; y: number; at: number; ticks: number };

export class World {
  readonly app = new Application();
  private root = new Container();
  private ground = new Graphics();
  private light = new Graphics();
  // 벽과 분대가 보지 못한 칸은 구역(chunkSize × chunkSize칸)마다 따로 그린다. 분대가 새 칸을 볼 때마다 바뀐 칸이 오므로
  // 바뀐 구역만 다시 그린다. 맵 전체를 매번 다시 그리면 큰 맵에서 초당 여러 번 수천 개의 사각형을 새로 만든다.
  // 보지 못한 칸의 덮개(fog)는 시야(light) 아래, 벽(walls)은 시야 위에 둔다. 지금 보이는 곳은 아직 본 칸으로 오지 않았어도
  // 밝게 보이고, 그 위의 적도 밝은 바닥 위에 그려진다.
  private fog = new Container();
  private walls = new Container();
  private fogChunks: Graphics[] = [];
  private wallChunks: Graphics[] = [];
  private static readonly chunkSize = 8;
  private props = new Graphics();
  private units = new Graphics();
  private labels = new Container();
  private labelsById = new Map<string, Text>();
  // 쓰지 않게 된 라벨을 바로 해제하지 않고 숨겨 두었다가 새 라벨에 다시 쓴다. 상한을 넘는 것만 해제한다.
  private spareLabels: Text[] = [];
  private static readonly spareLimit = 64;
  private snapshot?: Snapshot;
  private previous?: Snapshot;
  private received = 0;
  private interval = 100;
  private tiles = new Uint8Array();
  private revision = -1;
  // 그리는 내 위치(월드 단위)다.
  private predicted?: Position;
  // 보냈지만 서버가 아직 확인하지 않은 내 입력. 서버 재조정에서 서버 위치 위에 다시 적용한다.
  private pending: PendingInput[] = [];
  // 서버가 확인한 마지막 입력 순번과, 보관 상한을 넘어 버린 마지막 입력 순번이다.
  private ack = -1;
  private droppedSeq = -1;
  // 예측에 쓰는 맵과 대원을 막는 몸(시야 안의 살아 있는 적)이다. 좌표는 모두 Raw다.
  private terrain?: Terrain;
  private bodies: Body[] = [];
  // 서버가 보낸 내 위치(Raw)와, 그 위에 남은 입력을 서버와 같은 계산으로 적용한 위치(Raw)다.
  // tip은 입력을 모두 적용한 곳이고, previous는 마지막 입력 하나를 빼고 적용한 곳이다.
  private base?: FixedVector;
  private forecast?: { previous: FixedVector; tip: FixedVector };
  // 서버 위치로 예측을 바로잡을 때 그림이 튀지 않게 남겨 두는 차이(월드 단위)다. 시간이 지나며 0으로 줄어든다.
  private correction: Position = { x: 0, y: 0 };
  private predicting = false;
  private oldState = '';
  private camera = { x: 960, y: 720, zoom: 1 };
  private averageFrame = 16.7;
  private renderBudget = 10000;
  overview = false;
  landing?: Position;
  fps = 60;
  shownEnemies = 0;
  control: Control = { x: 0, y: 0, aim: 0, slot: 0 };

  async init(host: HTMLElement) {
    await this.app.init({
      resizeTo: host, background: '#10191b', antialias: true,
      resolution: Math.min(devicePixelRatio, 2), autoDensity: true,
      // Use native 2D drawing when hardware WebGL is unavailable, including software-only browsers.
      preference: ['webgl', 'canvas'], failIfMajorPerformanceCaveat: true,
    });
    host.appendChild(this.app.canvas);
    this.app.stage.addChild(this.root);
    this.root.addChild(this.ground, this.fog, this.light, this.walls, this.props, this.units, this.labels);
    this.app.ticker.add(t => this.draw(Math.min(t.deltaMS / 1000, .05)));
  }

  update(snapshot: Snapshot) {
    const now = performance.now();
    const before = this.target(now);
    const changedRoom = snapshot.room !== this.snapshot?.room;
    this.previous = changedRoom ? undefined : this.snapshot;
    this.interval = Math.max(50, Math.min(250, performance.now() - this.received));
    this.snapshot = snapshot;
    this.received = performance.now();
    if (changedRoom || this.revision !== snapshot.mapRevision) {
      this.revision = snapshot.mapRevision;
      const before = changedRoom ? undefined : this.tiles;
      this.tiles = Uint8Array.from(atob(snapshot.walls), c => c.charCodeAt(0));
      if (changedRoom) this.drawGround();
      this.drawTiles(before);
    }
    this.terrain = { tiles: this.tiles, columns: snapshot.map.columns, rows: snapshot.map.rows, cell: snapshot.map.cell };
    // 살아 있는 적만 대원을 막는다. 스냅샷에는 시야 안의 적만 오므로, 숨은 적이나 그 사이 움직인 적과의 차이는 서버 위치로 보정된다.
    this.bodies = snapshot.enemies.filter(enemy => enemy.hp > 0)
      .map(enemy => ({ x: enemy.rawX, y: enemy.rawY, radius: fromDouble(enemy.radius) }));
    const me = snapshot.players.find(p => p.id === snapshot.you);
    this.base = me ? { x: me.rawX, y: me.rawY } : undefined;
    this.ack = me?.ack ?? -1;
    const reset = me !== undefined && (me.state !== this.oldState || changedRoom);
    if (me && reset) {
      this.predicted = { x: me.x, y: me.y };
      // 투입·사망·방 이동으로 위치가 새로 정해지면 그 전에 보낸 입력은 의미가 없다.
      this.pending = [];
      this.droppedSeq = -1;
      this.correction = { x: 0, y: 0 };
    }
    this.oldState = me?.state ?? '';
    // ack 이하의 입력은 서버가 이미 처리해 서버 위치에 반영되어 있다.
    if (me) this.pending = this.pending.filter(input => input.seq > me.ack);
    // 서버 위치와 막는 몸이 새로 왔으니 남은 입력 전체를 다시 걷는다.
    this.resimulate();
    // 서버 위치가 예측과 다르면 그리는 목표가 바뀐다. 그 차이를 보정 차이로 옮겨 그림이 튀지 않게 하고 천천히 줄인다.
    if (!reset) this.keepDrawn(before, now);
    this.light.clear();
    for (const polygon of snapshot.sight) this.light.poly(polygon).fill(colors.lit);
  }

  screenToWorld(clientX: number, clientY: number): Position {
    const rect = this.app.canvas.getBoundingClientRect();
    return { x: (clientX - rect.left - this.root.x) / this.camera.zoom, y: (clientY - rect.top - this.root.y) / this.camera.zoom };
  }

  mePosition() { return this.predicted; }

  // 보낸 입력을 서버가 확인할 때까지 보관한다. 시각은 화면 계산에만 쓰며 서버로 보내지 않는다.
  recordInput(seq: number, x: number, y: number, at = performance.now()) {
    const before = this.target(at);
    const input = { seq, x, y, at, ticks: 1 };
    this.pending.push(input);
    if (this.pending.length > maxPending) {
      // 버리는 입력은 서버가 이미 처리했지만 아직 확인이 오지 않았을 수 있다. 빼고 다시 걸으면 그 틱만큼 뒤처지므로,
      // 서버가 버린 입력까지 확인해 줄 때까지 예측을 끄고 서버 위치를 그린다.
      this.droppedSeq = this.pending.shift()!.seq;
      this.resimulate();
    } else {
      // 새 입력은 마지막 예측 위치에서 한 틱만 더 걸으면 된다.
      this.advance(input, 1);
    }
    // 직전 입력이 한 틱을 다 채우기 전에 새 입력이 오면 목표가 앞으로 건너뛴다. 그 차이도 보정 차이로 옮긴다.
    this.keepDrawn(before, at);
  }

  // 타이머가 늦게 깨어 missed틱 동안 입력을 보내지 못했다. 서버는 그동안 마지막 입력을 계속 쓰므로 예측도 그만큼 더 쓴다.
  // 서버처럼 입력 하나는 inputHoldTicks틱까지만 쓴다. 그 뒤에는 서버도 대원을 멈춘다.
  holdLastInput(missed: number, at = performance.now()) {
    const last = this.pending[this.pending.length - 1];
    if (!last || missed <= 0) return;
    const extra = Math.min(missed, inputHoldTicks - last.ticks);
    if (extra <= 0) return;
    const before = this.target(at);
    last.ticks += extra;
    this.advance(last, extra);
    this.keepDrawn(before, at);
  }

  // 서버 재조정: 서버가 보낸 내 위치(Raw)에서 출발해, 서버가 아직 처리하지 않은 입력을 보낸 순서대로 다시 적용한다.
  // 입력 하나는 보통 한 틱이다. 계산은 서버와 같은 고정소수점이라, 서버가 입력을 같은 틱에 받고 막는 몸이 그대로면
  // 서버와 Raw까지 같은 곳에 이른다. 서버가 같은 틱에 입력 둘을 받거나(나중 것만 쓴다) 한 틱 동안 입력을 받지 못하면
  // (앞 입력을 한 틱 더 쓴다) 그만큼 어긋나고, 다음 스냅샷에서 서버 위치로 바로잡힌다.
  private resimulate() {
    if (!this.snapshot || !this.base || !this.terrain || this.droppedSeq > this.ack) { this.forecast = undefined; return; }
    this.forecast = { previous: this.base, tip: this.base };
    for (const input of this.pending) this.advance(input, input.ticks);
  }

  // 마지막 예측 위치(tip)에서 input을 ticks틱 더 쓴다. previous는 마지막 한 틱 전의 위치로, 그리기 보간에 쓴다.
  private advance(input: PendingInput, ticks: number) {
    const s = this.snapshot;
    if (!s || !this.terrain || !this.forecast) return;
    const radius = fromDouble(s.rules.playerRadius);
    let { previous, tip } = this.forecast;
    for (let i = 0; i < ticks; i++) {
      previous = tip;
      tip = stepPlayer(this.terrain, this.bodies, tip, input.x, input.y, s.rules.speed, radius);
    }
    this.forecast = { previous, tip };
  }

  // 목표가 바뀐 만큼을 보정 차이로 옮겨, 그리는 위치가 바뀌기 전 자리에서 이어지게 한다.
  private keepDrawn(before: Position | undefined, at: number) {
    const after = this.target(at);
    if (before && after) this.correct(before, after);
  }

  // 그릴 목표 위치(월드 단위)다. 마지막 입력은 보낸 뒤 한 틱 동안 나아가는 것으로 이어 그린다.
  // 예측하지 않을 때(살아 있지 않거나 스냅샷이 끊겼을 때)는 서버 위치다.
  private target(now: number): Position | undefined {
    if (!this.base) return undefined;
    if (!this.predicting || !this.forecast) return { x: toDouble(this.base.x), y: toDouble(this.base.y) };
    const { previous, tip } = this.forecast;
    const last = this.pending[this.pending.length - 1];
    const progress = last ? Math.min(1, Math.max(0, (now - last.at) / tickMilliseconds)) : 1;
    return { x: toDouble(previous.x + (tip.x - previous.x) * progress), y: toDouble(previous.y + (tip.y - previous.y) * progress) };
  }

  // 목표가 before에서 after로 바뀐 만큼을 보정 차이로 옮긴다. 차이가 아주 크면(순간 이동) 바로 따라간다.
  private correct(before: Position, after: Position) {
    this.correction = { x: this.correction.x + before.x - after.x, y: this.correction.y + before.y - after.y };
    if (Math.hypot(this.correction.x, this.correction.y) > 100) this.correction = { x: 0, y: 0 };
  }

  // 그 자리에 투입할 수 있는지 서버의 Deploy와 같은 계산(movement.ts의 deployable)과 대원 반지름으로 본다.
  // 분대가 본 칸이어야 하고(unseen), 본 칸의 벽만으로 몸이 들어가야 한다(blocked). 서버처럼 아주 먼 좌표는 바로 거절한다.
  deployCheck(p: Position, radius = this.snapshot?.rules.playerRadius ?? 13): 'ok' | 'unseen' | 'blocked' {
    if (!this.terrain || !Number.isFinite(p.x) || !Number.isFinite(p.y) || Math.abs(p.x) > 1e6 || Math.abs(p.y) > 1e6) return 'blocked';
    return deployable(this.terrain, { x: fromDouble(p.x), y: fromDouble(p.y) }, fromDouble(radius));
  }

  private drawAction(g: Graphics, p: Position, action: CombatAction, now: number, player: boolean) {
    if (now >= action.endsAt || action.phase === 'interrupted') return;
    let activeUntil = action.activeUntil;
    let kind = action.kind;
    // 기술별 그림은 id가 아니라 서버가 보낸 기술 속성으로 정한다. 적의 행동에는 기술 정의가 없다.
    const definition = player ? this.snapshot!.rules.skills.find(d => d.id === action.skill) : undefined;
    // 반격은 서버가 정한 followupUntil까지다. 규칙 값으로 다시 계산하면 서버의 틱 올림과 어긋난다.
    // 반격이 없는 기술과 적의 행동은 followupUntil이 activeUntil과 같아 반격 구간이 비어 있다.
    const followup = now >= activeUntil && now < action.followupUntil;
    if (followup) { kind = 'none'; activeUntil = action.followupUntil; }
    if (now >= activeUntil) return;
    const color = affinityColors[kind];
    const telegraph = now < action.contactAt;
    // 둘레 기술은 조준 방향 없이 대원 둘레의 원에 닿는다. 방어는 모든 방향을 막으므로 고리 하나로 그린다.
    if (definition?.around) {
      if (definition.guardsFront && !followup) {
        g.circle(p.x, p.y, 28).stroke({ color, width: 5 });
        return;
      }
      g.circle(p.x, p.y, action.range).fill({ color, alpha: telegraph ? .08 : .28 }).stroke({ color, width: 1.5, alpha: .8 });
      return;
    }
    const aim = action.aim;
    const f = { x: Math.cos(aim), y: Math.sin(aim) };
    if (definition?.guardsFront && !followup) {
      const half = this.snapshot!.rules.guardHalfAngle;
      arcOnNewPath(g, p.x, p.y, 28, aim - half, aim + half).stroke({ color, width: 5 });
      g.moveTo(p.x, p.y).lineTo(p.x + Math.cos(aim - half) * 28, p.y + Math.sin(aim - half) * 28)
        .moveTo(p.x, p.y).lineTo(p.x + Math.cos(aim + half) * 28, p.y + Math.sin(aim + half) * 28).stroke({ color, width: 1, alpha: .4 });
      return;
    }
    const width = action.halfWidth;
    const end = { x: p.x + f.x * action.range, y: p.y + f.y * action.range };
    g.poly([
      p.x - f.y * width, p.y + f.x * width, end.x - f.y * width, end.y + f.x * width,
      end.x + f.y * width, end.y - f.x * width, p.x + f.y * width, p.y - f.x * width,
    ]).fill({ color, alpha: telegraph ? .08 : .28 }).stroke({ color, width: action.tier === 2 ? 3 : 1.5, alpha: .8 });
    if (telegraph && !player) {
      const fraction = Math.max(0, Math.min(1, (now - action.startedAt) / (action.contactAt - action.startedAt)));
      const radius = 32;
      arcOnNewPath(g, p.x, p.y, radius, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * fraction).stroke({ color, width: 3 });
    }
    if (kind === 'channel') {
      for (const sign of [-1, 1]) {
        const x = end.x - f.y * width * sign, y = end.y + f.x * width * sign;
        g.circle(x, y, 5).fill(color).stroke(unitOutline);
      }
    }
  }

  private drawGround() {
    const s = this.snapshot!;
    const g = this.ground.clear();
    g.rect(0, 0, s.map.width, s.map.height).fill(colors.floor);
    for (let x = 0; x <= s.map.width; x += s.map.cell) g.moveTo(x, 0).lineTo(x, s.map.height);
    for (let y = 0; y <= s.map.height; y += s.map.cell) g.moveTo(0, y).lineTo(s.map.width, y);
    g.stroke({ color: colors.grid, width: 1, alpha: .28 });
  }

  // before는 직전에 그린 벽 배열이다. 없거나 크기가 다르면(방이 바뀜) 모든 구역을 새로 만들고, 아니면 칸 값이 바뀐 구역만 다시 그린다.
  private drawTiles(before?: Uint8Array) {
    const s = this.snapshot!;
    const { columns, rows, cell } = s.map;
    const size = World.chunkSize;
    const across = Math.ceil(columns / size), down = Math.ceil(rows / size);
    const rebuild = !before || before.length !== this.tiles.length || this.wallChunks.length !== across * down;
    if (rebuild) {
      for (const chunk of [...this.fogChunks, ...this.wallChunks]) chunk.destroy();
      this.fogChunks = Array.from({ length: across * down }, () => this.fog.addChild(new Graphics()));
      this.wallChunks = Array.from({ length: across * down }, () => this.walls.addChild(new Graphics()));
    }
    for (let cy = 0; cy < down; cy++) {
      for (let cx = 0; cx < across; cx++) {
        const x0 = cx * size, y0 = cy * size, x1 = Math.min(columns, x0 + size), y1 = Math.min(rows, y0 + size);
        let changed = rebuild;
        for (let y = y0; y < y1 && !changed; y++) {
          for (let x = x0; x < x1 && !changed; x++) changed = before![y * columns + x] !== this.tiles[y * columns + x];
        }
        if (!changed) continue;
        const f = this.fogChunks[cy * across + cx].clear();
        const w = this.wallChunks[cy * across + cx].clear();
        for (let y = y0; y < y1; y++) {
          // 보지 못한 칸은 가로로 이어진 칸을 한 사각형으로 묶어 어둡게 덮는다. 덮개는 시야 아래에 있어 지금 보이는 곳은 밝게 남는다.
          let run = -1;
          for (let x = x0; x <= x1; x++) {
            const unknown = x < x1 && this.tiles[y * columns + x] === unknownTile;
            if (unknown && run < 0) run = x;
            if (!unknown && run >= 0) { f.rect(run * cell, y * cell, (x - run) * cell, cell).fill(colors.unknown); run = -1; }
          }
          for (let x = x0; x < x1; x++) {
            const tile = this.tiles[y * columns + x];
            if (tile !== 1 && tile !== 2) continue;
            const px = x * cell, py = y * cell;
            w.rect(px + 3, py + 5, cell - 3, cell - 3).fill({ color: 0x000000, alpha: .3 });
            w.rect(px + 1, py + 1, cell - 2, cell - 2).fill(tile === 2 ? 0x303b3e : colors.wall);
            if (tile === 1) w.moveTo(px + 3, py + 2).lineTo(px + cell - 3, py + 2).stroke({ color: colors.edge, width: 2 });
          }
        }
      }
    }
  }

  private label(key: string, text: string, x: number, y: number, color: number, size = 12) {
    let label = this.labelsById.get(key);
    if (!label) {
      label = this.spareLabels.pop();
      if (!label) {
        label = new Text({ text, style: { fontFamily: 'sans-serif', fontSize: size, fontWeight: '600', fill: color } });
        label.anchor.set(.5, .5);
        this.labels.addChild(label);
      }
      this.labelsById.set(key, label);
    }
    label.text = text;
    label.style.fill = color;
    label.style.fontSize = size;
    label.position.set(x, y);
    label.visible = true;
  }

  private removeUnusedLabels() {
    // 이번 프레임에 쓰지 않은 라벨은 목록에서 뺀다. 적의 사망·시야 이탈과 방 전환으로 사라진 라벨도 여기서 정리된다.
    // 상한까지는 숨긴 채 모아 두었다가 다시 쓰고, 넘는 것은 Pixi에서 떼어 해제해 라벨 수가 끝없이 늘지 않게 한다.
    for (const [key, label] of this.labelsById) {
      if (label.visible) continue;
      this.labelsById.delete(key);
      if (this.spareLabels.length < World.spareLimit) {
        this.spareLabels.push(label);
        continue;
      }
      this.labels.removeChild(label);
      label.destroy({ style: true });
    }
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
      // 서버 위치는 왕복 지연만큼 과거 입력의 결과다. 그대로 따라가면 화면의 내 캐릭터가 입력보다 늦으므로
      // 확인받지 않은 입력을 다시 적용한 곳을 목표로 삼는다.
      const now = performance.now();
      const active = me.state === 'alive' && s.phase === 'active' && now - this.received < 500;
      if (active !== this.predicting) {
        // 예측을 켜거나 끄면 목표가 바뀐다. 그 차이도 보정 차이로 옮겨 그림이 튀지 않게 한다.
        const before = this.predicted;
        this.predicting = active;
        const after = this.target(now);
        if (before && after) { this.correction = { x: 0, y: 0 }; this.correct(before, after); }
      }
      const target = this.target(now);
      if (target) {
        const keep = Math.max(0, 1 - dt * 12);
        this.correction = { x: this.correction.x * keep, y: this.correction.y * keep };
        this.predicted = { x: target.x + this.correction.x, y: target.y + this.correction.y };
      }
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
    // 탈출 지점은 분대가 그 칸을 본 뒤에야 온다.
    const ext = s.extraction;
    if (ext) {
      g.circle(ext.x, ext.y, 80).fill({ color: colors.teal, alpha: ext.unlocked ? .12 : .025 }).stroke({ color: colors.teal, alpha: ext.unlocked ? .9 : .25, width: 2 });
      if (ext.progress > 0) arcOnNewPath(g, ext.x, ext.y, 80, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * ext.progress / ext.duration).stroke({ color: colors.lime, width: 7 });
      this.label('extract', ext.unlocked ? '↑ 탈출 · 구역 확보' : '↑ 탈출 지점', ext.x, ext.y + 102, colors.teal, 15);
    }
    for (const f of s.facilities) {
      const color = f.hp > 0 ? colors.red : 0x64706a;
      g.rect(f.x - 27, f.y - 27, 54, 54).fill(0x1a2325).stroke({ color, width: 3 });
      g.moveTo(f.x - 18, f.y - 18).lineTo(f.x + 18, f.y + 18).moveTo(f.x + 18, f.y - 18).lineTo(f.x - 18, f.y + 18).stroke({ color, width: 3 });
      if (f.hp > 0) g.rect(f.x - 27, f.y - 39, 54 * f.hp / f.maxHp, 4).fill(color);
      this.label(`facility-${f.id}`, f.hp > 0 ? `0${f.id} · 적 시설` : `0${f.id} · 파괴 완료`, f.x, f.y + 47, color, 14);
    }
    for (const supply of s.supplies) {
      // 회수한 부목표 상자와 다 쓴 상자(남은 횟수 0)는 그리지 않는다.
      if (supply.collected || supply.uses === 0) continue;
      const color = supply.cooldown > 0 ? 0x829388 : supply.objective ? colors.gold : colors.teal;
      g.roundRect(supply.x - 12, supply.y - 12, 24, 24, 3).fill(0x1a2325).stroke({ color, width: 2 });
      g.moveTo(supply.x - 6, supply.y).lineTo(supply.x + 6, supply.y).moveTo(supply.x, supply.y - 6).lineTo(supply.x, supply.y + 6).stroke({ color, width: 2 });
      // 남은 횟수는 늘 보인다. 내 대기 시간은 대원마다 따로 세므로, 대기 중에도 횟수를 가리지 않고 함께 보인다.
      const name = supply.kind === 'camp' ? '캠프 보급' : '보급';
      const text = supply.objective ? '보급품 회수'
        : supply.cooldown > 0 ? `${name} · ${supply.uses}회 · ${Math.ceil(supply.cooldown)}s` : `${name} · ${supply.uses}회`;
      this.label(`supply-${supply.id}`, text, supply.x, supply.y + 30, color, 12);
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
    // 현재 공개된 적만 그린다. 시야에서 빠진 적과 기술 예고는 즉시 제거한다.
    const now = s.now + elapsed;
    for (const enemy of enemies) {
      const p = interpolate(enemy, oldEnemies.get(enemy.id));
      const r = enemy.radius;
      const action = enemy.action;
      if (action) this.drawAction(u, p, action, now, false);
      const color = enemy.kind === 'warden' ? colors.teal : enemy.kind === 'breaker' ? colors.red : 0xd2b49b;
      if (enemy.kind === 'warden') {
        u.poly([p.x, p.y - r, p.x + r, p.y - r * .4, p.x + r * .8, p.y + r * .7,
          p.x, p.y + r, p.x - r * .8, p.y + r * .7, p.x - r, p.y - r * .4]).fill(color).stroke(unitOutline);
        u.rect(p.x - 10, p.y - 10, 20, 20).stroke({ color: 0x172222, width: 3 });
      } else if (enemy.kind === 'breaker') {
        u.poly([p.x - r, p.y - r, p.x, p.y - r * .6, p.x + r, p.y - r,
          p.x + r, p.y + r * .6, p.x, p.y + r, p.x - r, p.y + r * .6]).fill(color).stroke(unitOutline);
      } else {
        u.circle(p.x, p.y, r).fill(color).stroke(unitOutline);
      }
      const aim = action?.aim ?? Math.atan2((me?.y ?? p.y) - p.y, (me?.x ?? p.x) - p.x);
      u.circle(p.x + Math.cos(aim) * r * .7, p.y + Math.sin(aim) * r * .7, 3).fill(0x172222);
      if (enemy.maxHp > 1) {
        this.label(`enemy-type-${enemy.id}`, `${enemy.name} · 타${enemy.tiers[0]} 방${enemy.tiers[1]} 잡${enemy.tiers[2]}`, p.x, p.y + r + 17, color, 11);
        for (let i = 0; i < enemy.maxHp; i++) {
          u.rect(p.x - enemy.maxHp * 5 + i * 10, p.y - r - 10, 7, 4).fill(i < enemy.hp ? color : 0x394342);
        }
      }
      if (action && now < action.activeUntil) {
        const suffix = action.tier === 2 ? 'Ⅱ · 대응 2' : 'Ⅰ';
        this.label(`enemy-action-${enemy.id}`, `${affinityNames[action.kind]} ${suffix}`, p.x, p.y - r - 26, affinityColors[action.kind], 13);
      }
    }
    s.players.forEach((player, index) => {
      if (player.state !== 'alive') return;
      const isMe = player.id === s.you;
      const p = isMe ? this.predicted! : interpolate(player, this.previous?.players.find(old => old.id === player.id));
      const color = team[index % team.length];
      const aim = isMe ? this.control.aim : player.aim;
      for (const action of player.actions) this.drawAction(u, p, { ...action, aim }, now, true);
      // 면과 테두리, 두 손만으로 몸체와 방향을 표시한다.
      for (const sign of [-1, 1]) {
        const x = p.x + Math.cos(aim) * 16 - Math.sin(aim) * 8 * sign;
        const y = p.y + Math.sin(aim) * 16 + Math.cos(aim) * 8 * sign;
        u.circle(x, y, 5).fill(color).stroke(unitOutline);
      }
      u.circle(p.x, p.y, s.rules.playerRadius).fill(color).stroke(unitOutline);
      u.rect(p.x - 16, p.y - 24, 32, 3).fill(0x172222).rect(p.x - 16, p.y - 24, 32 * player.hp / player.maxHp, 3).fill(color);
      this.label(`player-${player.id}`, `${isMe ? '▼ ' : ''}${player.name}${player.connected ? '' : ' · 연결 끊김'}`, p.x, p.y - 42, color, 13);
    });
    for (const effect of s.effects) {
      const remain = Math.max(0, effect.until - (s.now + elapsed));
      if (!remain) continue;
      const text: Record<string, string> = { break: '완전 파훼', partial: '부분 대응', draw: '비김', guard: '방어', hit: '−1', hurt: '피격', heal: '보급 완료', deploy: '투입' };
      const color = effect.kind === 'break' ? colors.lime : effect.kind === 'hurt' || effect.kind === 'draw' ? colors.red : effect.kind === 'partial' ? colors.gold : colors.teal;
      u.circle(effect.x, effect.y, Math.max(3, 30 * (1 - remain / .65))).stroke({ color, alpha: Math.min(1, remain * 2), width: 2 });
      this.label(`effect-${effect.id}`, text[effect.kind] ?? '', effect.x, effect.y - 48 - (1 - remain / .65) * 15, color, 13);

    }
    this.removeUnusedLabels();
  }
}
