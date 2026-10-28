// 서버가 대원을 움직이는 계산(Match.Input·UpdatePlayer·MoveBody·CanOccupy, BattleMap.CanStand)을 옮긴 것이다.
// 클라이언트 예측은 보낸 입력 하나를 한 틱으로 쳐서 이 계산으로 움직인다. 같은 입력이 같은 틱에 들어가면 서버와 Raw까지 같은 위치가 된다.
// 클라이언트는 숨은 적을 모르고 적이 다음 틱에 어디로 갈지도 모르므로, 그런 몸에 막혀 생긴 차이는 서버 위치로 보정된다.
// 서버의 계산을 바꾸면 이 파일도 함께 고치고 대조 자료(client/tests/fixed-vectors.json)를 다시 만든다.
import {
  type FixedVector, add, ceilingDivide, clamp, distanceSquared, divideInt, divideVectorInt, floorDivide, fromDouble, fromInt,
  length, lengthSquared, multiply, multiplyVectorInt, normalize, one, scaleVector, subtract, subtractVector,
} from './fixed';

// 벽 판정에 쓰는 맵이다. 칸 값은 0 빈 땅, 1·2 벽, 3 분대가 아직 보지 못한 칸(unknownTile)이고, 맵 밖은 모두 벽이다.
export interface Terrain { tiles: Uint8Array; columns: number; rows: number; cell: number }

// 분대가 아직 보지 못한 칸이다(BattleMap.UnknownTile). 클라이언트는 그 칸이 벽인지 모른다.
export const unknownTile = 3;

// 대원을 막는 몸(살아 있는 적)이다. 좌표와 반지름 모두 Raw다.
export interface Body { x: number; y: number; radius: number }

// 서버의 틱 빈도다(Rules.TicksPerSecond). 클라이언트는 입력을 한 틱에 하나씩 보내고, 예측도 입력 하나를 한 틱으로 친다.
// 입력 주기(main.ts)와 그리기 보간(world.ts)도 이 값을 쓴다. 하나만 달라도 예측의 전제가 깨진다.
export const ticksPerSecond = 30;
export const tickMilliseconds = 1000 / ticksPerSecond;
// 새 입력이 없으면 서버는 마지막 입력을 계속 쓰다가, 받은 틱부터 이만큼 쓴 뒤 멈춘다(Match.InputTimeoutTicks - 1).
export const inputHoldTicks = 10;
// 몸끼리 딱 붙은 자리를 겹친 것으로 보지 않게 거리의 제곱에 두는 여유다(Match.TouchTolerance, Raw 4).
const touchTolerance = 4;
// 이동을 나누는 걸음의 길이다(MoveBody의 4단위).
const stride = 4;

// 보지 못한 칸은 빈 땅으로 본다. 서버도 투입 요청에는 본 칸의 벽만으로 답한다(BattleMap.CanStand의 known).
// 서버는 칸의 어느 부분이든 보이면 본 칸으로 치고, 대원이 반 칸을 움직일 때마다 다시 둘러본다. 그래서 대원 몸이 닿는 칸은
// 거의 늘 본 칸이고, 예측이 보지 못한 벽을 지나가는 일은 모퉁이를 막 돌 때처럼 드물다. 그런 차이는 다음 스냅샷의 서버 위치로 보정된다.
export function solid(terrain: Terrain, x: number, y: number): boolean {
  if (x < 0 || y < 0 || x >= terrain.columns || y >= terrain.rows) return true;
  const tile = terrain.tiles[y * terrain.columns + x];
  return tile !== 0 && tile !== unknownTile;
}

// 그 자리의 칸을 분대가 보았는지 본다(Match.Explored). 맵 밖이면 거짓이다.
export function known(terrain: Terrain, position: FixedVector): boolean {
  const x = floorDivide(position.x, terrain.cell);
  const y = floorDivide(position.y, terrain.cell);
  if (x < 0 || y < 0 || x >= terrain.columns || y >= terrain.rows) return false;
  return terrain.tiles[y * terrain.columns + x] !== unknownTile;
}

// 그 자리에 몸이 설 수 있는지 본다(BattleMap.CanStand). 몸에 닿는 칸마다 칸 사각형에서 몸의 중심에 가장 가까운 점을 구해
// 반지름보다 가까우면 막힌다.
export function canStand(terrain: Terrain, position: FixedVector, radius: number): boolean {
  const width = fromInt(terrain.columns * terrain.cell);
  const height = fromInt(terrain.rows * terrain.cell);
  if (position.x < radius || position.y < radius || position.x > subtract(width, radius) || position.y > subtract(height, radius)) return false;
  const minX = floorDivide(subtract(position.x, radius), terrain.cell);
  const maxX = floorDivide(add(position.x, radius), terrain.cell);
  const minY = floorDivide(subtract(position.y, radius), terrain.cell);
  const maxY = floorDivide(add(position.y, radius), terrain.cell);
  const radiusSquared = multiply(radius, radius);
  for (let y = minY; y <= maxY; y++) {
    for (let x = minX; x <= maxX; x++) {
      if (!solid(terrain, x, y)) continue;
      const nearest = {
        x: clamp(position.x, fromInt(x * terrain.cell), fromInt((x + 1) * terrain.cell)),
        y: clamp(position.y, fromInt(y * terrain.cell), fromInt((y + 1) * terrain.cell)),
      };
      if (distanceSquared(nearest, position) < radiusSquared) return false;
    }
  }
  return true;
}

// 그 자리에 투입할 수 있는지 서버의 Deploy와 같은 순서로 본다. 분대가 본 칸이어야 하고(unseen), 본 칸의 벽만으로 몸이
// 들어가야 한다(blocked). 지금 보이지 않는 적과 보지 못한 벽은 서버가 착지할 때 보고 가까운 빈 자리로 옮긴다.
export function deployable(terrain: Terrain, position: FixedVector, radius: number): 'ok' | 'unseen' | 'blocked' {
  if (!known(terrain, position)) return 'unseen';
  return canStand(terrain, position, radius) ? 'ok' : 'blocked';
}

// 대원이 그 자리를 차지할 수 있는지 본다(Match.CanOccupy의 대원 쪽). 대원끼리는 통과하고 살아 있는 적에게만 막힌다.
export function canOccupy(terrain: Terrain, bodies: readonly Body[], position: FixedVector, radius: number): boolean {
  if (!canStand(terrain, position, radius)) return false;
  for (const body of bodies) {
    const minimum = add(radius, body.radius);
    if (distanceSquared(position, body) < subtract(multiply(minimum, minimum), touchTolerance)) return false;
  }
  return true;
}

// delta만큼 움직이되 4단위보다 길면 나눠서 조금씩 움직인다(Match.MoveBody). 걸음마다 가로와 세로를 따로 움직여
// 막힌 쪽만 멈추고 벽을 따라 미끄러진다. i번째 걸음은 (delta × i / 걸음 수)에서 앞 걸음까지의 양을 뺀 만큼이라
// 걸음을 다 더하면 정확히 delta다.
export function moveBody(terrain: Terrain, bodies: readonly Body[], from: FixedVector, delta: FixedVector, radius: number): FixedVector {
  const steps = Math.max(1, ceilingDivide(length(delta), stride));
  let planned: FixedVector = { x: 0, y: 0 };
  let position = from;
  for (let i = 1; i <= steps; i++) {
    const reach = divideVectorInt(multiplyVectorInt(delta, i), steps);
    const step = subtractVector(reach, planned);
    planned = reach;
    let next = { x: add(position.x, step.x), y: position.y };
    if (canOccupy(terrain, bodies, next, radius)) position = next;
    next = { x: position.x, y: add(position.y, step.y) };
    if (canOccupy(terrain, bodies, next, radius)) position = next;
  }
  return position;
}

// 보낸 이동 입력을 서버처럼 바꾼다(Match.Input). 길이가 1보다 길면 길이 1로 줄인다.
export function moveDirection(moveX: number, moveY: number): FixedVector {
  const move = { x: fromDouble(moveX), y: fromDouble(moveY) };
  if (lengthSquared(move) > one) return normalize(move);
  return move;
}

// 한 틱에 움직이는 거리다. 1/30을 곱하지 않고 30으로 나눈다(Match._playerStep).
export const playerStep = (speed: number) => divideInt(fromDouble(speed), ticksPerSecond);

// 입력 하나로 한 틱 움직인 자리다(Match.UpdatePlayer).
export function stepPlayer(terrain: Terrain, bodies: readonly Body[], position: FixedVector, moveX: number, moveY: number,
  speed: number, radius: number): FixedVector {
  const delta = scaleVector(moveDirection(moveX, moveY), playerStep(speed));
  return moveBody(terrain, bodies, position, delta, radius);
}
