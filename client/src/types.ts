export type Position = { x: number; y: number };
export type Weapon = 'rifle' | 'shotgun' | 'piercer';
export type Secondary = 'grenade' | 'turret';
export interface Player extends Position {
  id: string; name: string; passive: string; weapon: Weapon; secondary: Secondary; state: 'waiting' | 'deploying' | 'alive';
  connected: boolean; hasDeployed: boolean; aim: number; hp: number; maxHp: number;
  ammo: number; grenades: number; reload: number; deploy: number; kills: number; deaths: number;
  ack: number; landingX: number; landingY: number;
  magazine: number; reloadSeconds: number; turretCooldown: number;
}
export interface Snapshot {
  type: 'snapshot'; version: number; room: string; you: string; now: number; elapsed: number;
  phase: 'staging' | 'active' | 'ended'; result: 'success' | 'failure' | null;
  pulse: number; nextPulse: number; walls: string; mapRevision: number;
  extraction: Position & { progress: number; duration: number; unlocked: boolean };
  map: { width: number; height: number; cell: number; columns: number; rows: number; campX: number; campY: number };
  rules: { deploy: number; reload: number; magazine: number; grenades: number; speed: number; turretPlacement: number; turretCooldown: number };
  players: Player[];
  enemies: (Position & { id: number; kind: 'melee' | 'ranged'; hp: number })[];
  bullets: (Position & { id: number; vx: number; vy: number; hostile: boolean; kind: Weapon | 'turret' | 'enemy' })[];
  grenades: (Position & { id: number; remaining: number })[];
  turrets: (Position & { id: number; owner: string; aim: number; hp: number; maxHp: number; remaining: number })[];
  facilities: (Position & { id: number; hp: number; maxHp: number })[];
  supplies: (Position & { id: number; objective: boolean; collected: boolean; cooldown: number })[];
  effects: (Position & { id: number; kind: string; until: number })[];
  sight: number[][]; notices: { at: number; text: string }[];
}
export interface Guest { room: string; id: string; token: string }
export interface Control { x: number; y: number; aim: number; fire: boolean }
