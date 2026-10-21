import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import {
  ceilingDivide, divide, divideInt, floorDivide, fromDouble, length, lengthSquared, multiply, normalize, roundedSqrt,
} from '../src/fixed';
import { stepPlayer, type Body, type Terrain } from '../src/movement';

// 서버(C#)가 계산해 적어 둔 대조 자료다. tests/Argus.Server.Tests의 ClientParityTests가 만들고, 서버 검사가 지금 계산과 같은지 본다.
// 여기서는 클라이언트의 같은 계산이 Raw까지 같은 값을 내는지 본다. 화면이 필요 없어 브라우저 없이 돈다.
interface Scenario {
  name: string; speed: number; radius: number; start: [number, number]; bodies: number[][];
  ticks: [number, number, number, number][];
}
interface Vectors {
  multiply: number[][]; divide: number[][]; divideInt: number[][]; floorDivide: number[][]; ceilingDivide: number[][];
  sqrt: number[][]; fromDouble: number[][]; vectors: number[][]; throws: [string, number, number][];
  map: { cell: number; columns: number; rows: number; walls: string };
  scenarios: Scenario[];
}

const vectors = JSON.parse(readFileSync(new URL('./fixed-vectors.json', import.meta.url), 'utf8')) as Vectors;

// 어긋난 행을 모두 모아 한꺼번에 보여 준다.
function differences(name: string, rows: number[][], inputs: number, compute: (...values: number[]) => number[]) {
  const found: string[] = [];
  for (const row of rows) {
    const expected = row.slice(inputs);
    const actual = compute(...row.slice(0, inputs));
    if (actual.some((value, i) => value !== expected[i])) found.push(`${name}(${row.slice(0, inputs).join(', ')}) = ${actual.join(', ')}, 서버는 ${expected.join(', ')}`);
  }
  return found;
}

test('fixed-point arithmetic gives the same raw values as the server', () => {
  const found = [
    ...differences('multiply', vectors.multiply, 2, (a, b) => [multiply(a, b)]),
    ...differences('divide', vectors.divide, 2, (a, b) => [divide(a, b)]),
    ...differences('divideInt', vectors.divideInt, 2, (a, b) => [divideInt(a, b)]),
    ...differences('floorDivide', vectors.floorDivide, 2, (raw, divisor) => [floorDivide(raw, divisor)]),
    ...differences('ceilingDivide', vectors.ceilingDivide, 2, (raw, divisor) => [ceilingDivide(raw, divisor)]),
    ...differences('sqrt', vectors.sqrt, 1, value => [roundedSqrt(value)]),
    ...differences('fromDouble', vectors.fromDouble, 1, value => [fromDouble(value)]),
    ...differences('vector', vectors.vectors, 2, (x, y) => {
      const normal = normalize({ x, y });
      return [lengthSquared({ x, y }), length({ x, y }), normal.x, normal.y];
    }),
  ];
  expect(found).toEqual([]);
  // 자료가 비어 통과하지 않게 행 수도 본다.
  expect(vectors.multiply.length + vectors.divide.length + vectors.fromDouble.length).toBeGreaterThan(500);
});

test('calculations the server refuses throw here too', () => {
  const operations: Record<string, (a: number, b: number) => unknown> = {
    multiply, divide, divideInt, floorDivide, ceilingDivide,
    length: (x, y) => length({ x, y }),
    fromDouble: value => fromDouble(value),
  };
  const silent: string[] = [];
  for (const [name, a, b] of vectors.throws) {
    const operation = operations[name];
    if (!operation) throw new Error(`대조 자료의 연산 ${name}을 모릅니다`);
    try {
      const value = operation(a, b);
      silent.push(`${name}(${a}, ${b}) = ${JSON.stringify(value)}`);
    } catch {
      // 서버처럼 예외를 냈다.
    }
  }
  expect(silent).toEqual([]);
  expect(vectors.throws.length).toBeGreaterThan(0);
});

test('movement steps to the same raw position as the server, tick by tick', () => {
  const terrain: Terrain = {
    tiles: Uint8Array.from(Buffer.from(vectors.map.walls, 'base64')),
    columns: vectors.map.columns, rows: vectors.map.rows, cell: vectors.map.cell,
  };
  const found: string[] = [];
  for (const scenario of vectors.scenarios) {
    const bodies: Body[] = scenario.bodies.map(([x, y, radius]) => ({ x, y, radius }));
    let position = { x: scenario.start[0], y: scenario.start[1] };
    scenario.ticks.forEach(([moveX, moveY, x, y], tick) => {
      position = stepPlayer(terrain, bodies, position, moveX, moveY, scenario.speed, scenario.radius);
      if (position.x !== x || position.y !== y) found.push(`${scenario.name} ${tick}틱: (${position.x}, ${position.y}), 서버는 (${x}, ${y})`);
    });
  }
  // 첫 차이만 보면 원인을 찾기 쉽다. 한 틱이 어긋나면 뒤의 틱도 따라 어긋난다.
  expect(found.slice(0, 5)).toEqual([]);
  expect(vectors.scenarios.length).toBeGreaterThan(5);
});
