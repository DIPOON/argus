export type Position = { x: number; y: number };
export type Skill = 'strike' | 'parry' | 'grab';
export type Affinity = 'strike' | 'block' | 'channel' | 'none';
export const skillNames: Record<Skill, string> = { strike: '근접 타격', parry: '받아치기', grab: '잡기' };
export const affinityNames: Record<Affinity, string> = { strike: '타격', block: '방어', channel: '잡기', none: '반격' };
export interface SkillDefinition {
  id: Skill; kind: Affinity; mana: number; contactAfter: number; activeSeconds: number;
  duration: number; range: number; halfWidth: number;
  // 기술별 동작. 그림도 id가 아니라 이 속성으로 정한다.
  guardsFront: boolean; hasFollowup: boolean; singleTarget: boolean; breaksStructures: boolean;
  followupBreaksStructures: boolean;
}
export interface CombatAction {
  id: number; skill: string; kind: Affinity; slot: number; tier: number;
  phase: 'telegraph' | 'active' | 'followup' | 'recovery' | 'interrupted';
  aim: number; startedAt: number; contactAt: number; activeUntil: number; followupUntil: number; endsAt: number;
  range: number; halfWidth: number;
}
// x·y는 그리기용(소수 둘째 자리로 반올림)이고, rawX·rawY는 예측용 고정소수점 Raw(4096분의 1 단위 정수)다.
export interface Player extends Position {
  rawX: number; rawY: number;
  id: string; name: string; slots: Skill[]; state: 'waiting' | 'deploying' | 'alive';
  connected: boolean; hasDeployed: boolean; aim: number; hp: number; maxHp: number;
  mana: number; maxMana: number; busy: number; actions: CombatAction[];
  deploy: number; kills: number; deaths: number; ack: number; landingX: number; landingY: number;
}
export interface Enemy extends Position {
  rawX: number; rawY: number;
  id: number; kind: 'grunt' | 'breaker' | 'warden'; name: string; radius: number;
  hp: number; maxHp: number; tiers: number[]; action: CombatAction | null;
}
export interface Snapshot {
  type: 'snapshot'; version: number; room: string; you: string; now: number; elapsed: number;
  phase: 'staging' | 'active' | 'ended'; result: 'success' | 'failure' | null;
  // 벽 배열(base64)이다. 칸마다 0 빈 땅, 1 부술 수 있는 벽, 2 부술 수 없는 벽, 3 분대가 아직 보지 못한 칸이다(protocol v5).
  // 판 번호는 벽이 부서지거나 분대가 새 칸을 볼 때 바뀐다.
  walls: string; mapRevision: number;
  // 탈출 지점은 분대가 그 칸을 보기 전까지 null이다. 시설과 보급도 본 칸에 있는 것만 온다(protocol v5).
  extraction: (Position & { progress: number; duration: number; unlocked: boolean }) | null;
  map: { width: number; height: number; cell: number; columns: number; rows: number; campX: number; campY: number };
  rules: { deploy: number; speed: number; playerRadius: number; guardHalfAngle: number; parryFollowupSeconds: number; skills: SkillDefinition[] };
  players: Player[]; enemies: Enemy[];
  facilities: (Position & { id: number; hp: number; maxHp: number })[];
  // kind는 보급 종류(시설 자리 상자도 field)이고 uses는 남은 횟수다. 다 쓴 상자는 0, 부목표 상자는 1에서 회수하면 0이다.
  supplies: (Position & { id: number; kind: 'camp' | 'field' | 'objective'; objective: boolean; collected: boolean; uses: number; cooldown: number })[];
  effects: (Position & { id: number; kind: string; until: number })[];
  sight: number[][]; notices: { id: number; at: number; text: string }[];
}
// 서버가 보내는 그대로의 스냅샷. 벽은 바뀔 때만, 규칙과 맵은 접속 직후에만 담긴다(protocol v3부터).
export type SnapshotWire = Omit<Snapshot, 'walls' | 'rules' | 'map'> & Partial<Pick<Snapshot, 'walls' | 'rules' | 'map'>>;
export interface Guest { room: string; id: string; token: string }
export interface Control { x: number; y: number; aim: number; slot: number }
