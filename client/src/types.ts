export type Position = { x: number; y: number };
export type Skill = 'strike' | 'parry' | 'grab';
export type Affinity = 'strike' | 'block' | 'channel' | 'none';
export const skillNames: Record<Skill, string> = { strike: '근접 타격', parry: '받아치기', grab: '잡기' };
export const affinityNames: Record<Affinity, string> = { strike: '타격', block: '방어', channel: '잡기', none: '반격' };
export interface SkillDefinition {
  id: Skill; kind: Affinity; mana: number; contactAfter: number; activeSeconds: number;
  duration: number; range: number; halfWidth: number;
}
export interface CombatAction {
  id: number; skill: string; kind: Affinity; slot: number; tier: number;
  phase: 'telegraph' | 'active' | 'followup' | 'recovery' | 'interrupted';
  aim: number; startedAt: number; contactAt: number; activeUntil: number; endsAt: number;
  range: number; halfWidth: number;
}
export interface Player extends Position {
  id: string; name: string; slots: Skill[]; state: 'waiting' | 'deploying' | 'alive';
  connected: boolean; hasDeployed: boolean; aim: number; hp: number; maxHp: number;
  mana: number; maxMana: number; busy: number; actions: CombatAction[];
  deploy: number; kills: number; deaths: number; ack: number; landingX: number; landingY: number;
}
export interface Enemy extends Position {
  id: number; kind: 'grunt' | 'breaker' | 'warden'; name: string; radius: number;
  hp: number; maxHp: number; tiers: number[]; action: CombatAction | null;
}
export interface Snapshot {
  type: 'snapshot'; version: number; room: string; you: string; now: number; elapsed: number;
  phase: 'staging' | 'active' | 'ended'; result: 'success' | 'failure' | null;
  pulse: number; nextPulse: number; walls: string; mapRevision: number;
  extraction: Position & { progress: number; duration: number; unlocked: boolean };
  map: { width: number; height: number; cell: number; columns: number; rows: number; campX: number; campY: number };
  rules: { deploy: number; speed: number; playerRadius: number; guardHalfAngle: number; parryFollowupSeconds: number; skills: SkillDefinition[] };
  players: Player[]; enemies: Enemy[];
  facilities: (Position & { id: number; hp: number; maxHp: number })[];
  supplies: (Position & { id: number; objective: boolean; collected: boolean; cooldown: number })[];
  effects: (Position & { id: number; kind: string; until: number })[];
  sight: number[][]; notices: { at: number; text: string }[];
}
export interface Guest { room: string; id: string; token: string }
export interface Control { x: number; y: number; aim: number; slot: number }
