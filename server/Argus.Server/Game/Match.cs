using System.Numerics;
using System.Security.Cryptography;

namespace Argus.Server.Game;

// One room is one authoritative simulation. Access is serialized by the host's room lock.
public sealed class Match(string code, Rules? rules = null, int? seed = null)
{
    public string Code { get; } = code;
    public Rules Rules { get; } = rules ?? new();
    public BattleMap Map { get; } = new();
    public List<Player> Players { get; } = [];
    public List<Enemy> Enemies { get; } = [];
    public List<Bullet> Bullets { get; } = [];
    public List<Grenade> Grenades { get; } = [];
    public List<Facility> Facilities { get; } = [new(1, new(960, 485)), new(2, new(1210, 695)), new(3, new(990, 920))];
    public List<Supply> Supplies { get; } = [new(1, BattleMap.Camp + new Vector2(70, 40), false), new(2, new(845, 640), true), new(3, new(1150, 835), true), new(4, new(815, 985), false)];
    public List<Effect> Effects { get; } = [];
    public List<Notice> Notices { get; } = [];
    public double Now { get; private set; }
    public double StartedAt { get; private set; } = -1;
    public double? EndedAt { get; private set; }
    public double Elapsed => StartedAt < 0 ? 0 : (EndedAt ?? Now) - StartedAt;
    public string Phase { get; private set; } = "staging";
    public string? Result { get; private set; }
    public double ExtractionProgress { get; private set; }
    public int Pulse { get; private set; }
    public bool ObjectivesComplete => Facilities.All(f => f.Hp <= 0);
    // Only deterministic tests supply a seed. Public clients cannot replay a fixed production spawn sequence.
    private readonly Random? _random = seed is { } value ? new(value) : null;
    private double NextRandom() => _random?.NextDouble() ?? RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue;
    private double _nextSpawn = 1;
    private int _nextId = 100;
    private int[] _flow = new int[BattleMap.Columns * BattleMap.Rows];
    private double _nextFlow;

    public Player? Join(string id, string name)
    {
        if (Players.Count >= Rules.MaxPlayers || Phase == "ended") return null;
        var player = new Player(id, name) { Position = BattleMap.Camp, Hp = 130, Ammo = Rules.Magazine, Grenades = Rules.Grenades };
        Players.Add(player);
        Note($"{name} 합류");
        return player;
    }

    public bool SetLoadout(Player p, string passive)
    {
        if (p.HasDeployed || p.State != "waiting" || passive is not ("vitality" or "mobility")) return false;
        p.Passive = passive;
        p.Hp = p.MaxHp;
        return true;
    }

    public bool Deploy(Player p, double x, double y)
    {
        if (Phase == "ended" || p.State != "waiting" || !double.IsFinite(x) || !double.IsFinite(y)) return false;
        var target = new Vector2((float)x, (float)y);
        if (!Map.CanStand(target, 15)) return false;
        // Committing a landing point grants no vision; only an alive deployed body is an observer.
        p.Landing = target;
        p.DeployAt = Now + Rules.DeploySeconds;
        p.State = "deploying";
        return true;
    }

    public bool Input(Player p, long sequence, double moveX, double moveY, double aim, bool fire, bool reload, bool grenade)
    {
        if (Phase == "ended" || p.State != "alive" || sequence <= p.LastSequence || sequence < 0 || sequence > 9_000_000_000_000 ||
            !double.IsFinite(moveX) || !double.IsFinite(moveY) || !double.IsFinite(aim) || Math.Abs(moveX) > 1.01 || Math.Abs(moveY) > 1.01) return false;
        p.LastSequence = sequence;
        p.LastInputAt = Now;
        p.Move = new((float)moveX, (float)moveY);
        if (p.Move.LengthSquared() > 1) p.Move = Vector2.Normalize(p.Move);
        p.Aim = Math.IEEERemainder(aim, Math.Tau);
        p.Firing = fire;
        p.ReloadRequested |= reload;
        p.GrenadeRequested |= grenade;
        return true;
    }

    public void Disconnect(Player p)
    {
        p.Connected = false;
        p.DisconnectedAt = Now;
        p.Move = Vector2.Zero;
        p.Firing = false;
        p.GrenadeRequested = false;
    }

    public void Step(double dt)
    {
        Now += dt;
        Effects.RemoveAll(e => e.Until < Now);
        foreach (var p in Players.Where(p => p.DisconnectedAt is not null && Now - p.DisconnectedAt.Value > Rules.ReconnectSeconds).ToArray())
        {
            Note($"{p.Name} 연결 복구 시간 만료");
            Players.Remove(p);
        }
        if (Phase == "ended") return;

        // Death/expiry wins over queued respawns and late joins. A waiting guest cannot hold a lost room open.
        if (CheckWipe()) return;
        foreach (var p in Players.Where(p => p.State == "deploying" && p.DeployAt <= Now))
        {
            if (!Map.CanStand(p.Landing, 15)) { p.State = "waiting"; continue; }
            p.Position = p.Landing;
            p.Impulse = Vector2.Zero;
            p.Hp = p.MaxHp;
            p.Ammo = Rules.Magazine;
            p.Grenades = Rules.Grenades;
            p.ReloadUntil = 0;
            p.NextShot = Now;
            p.NextGrenade = Now;
            p.State = "alive";
            p.HasDeployed = true;
            p.Move = Vector2.Zero;
            p.Firing = false;
            p.LastInputAt = -100;
            Effect(p.Position, "deploy", .6);
            if (Phase == "staging") { Phase = "active"; StartedAt = Now; Note("작전 시작 · 시설 3곳을 파괴하세요"); }
        }
        if (Phase != "active") return;

        while (Elapsed >= (Pulse + 1) * Rules.PulseSeconds)
        {
            Pulse++;
            var damage = 60 * Math.Pow(2, Pulse - 1);
            foreach (var p in Players.Where(p => p.State == "alive")) Hurt(p, damage);
            Note($"전역 충격 {Pulse}회 · {damage:0} 피해");
            if (CheckWipe()) return;
        }

        foreach (var p in Players.Where(p => p.State == "alive")) UpdatePlayer(p, dt);
        if (Elapsed >= _nextSpawn && Enemies.Count < Rules.MaxEnemies)
        {
            _nextSpawn = Elapsed + Math.Max(.6, Rules.SpawnInterval - Elapsed / 150);
            for (var i = 0; i < Math.Min(2 + (int)(Elapsed / 90), 7) && Enemies.Count < Rules.MaxEnemies; i++) SpawnEnemy();
        }
        if (Now >= _nextFlow) { _nextFlow = Now + .5; BuildFlow(); }
        UpdateEnemies(dt);
        UpdateBullets(dt);
        UpdateGrenades(dt);
        Enemies.RemoveAll(e => e.Hp <= 0);
        if (CheckWipe()) return;

        if (ObjectivesComplete && Players.Any(p => p.State == "alive" && Vector2.Distance(p.Position, BattleMap.Extraction) < 80))
            ExtractionProgress += dt;
        else ExtractionProgress = Math.Max(0, ExtractionProgress - dt * .5);
        if (ExtractionProgress >= Rules.ExtractionSeconds) Finish("success");
    }

    private bool CheckWipe()
    {
        if (Phase == "active" && !Players.Any(p => p.State == "alive"))
        {
            Finish("failure");
            return true;
        }
        return false;
    }

    private void Finish(string result)
    {
        Phase = "ended";
        EndedAt = Now;
        Result = result;
        Note(result == "success" ? "탈출 성공 · 작전 종료" : "전원 사망 · 작전 실패");
    }

    private void UpdatePlayer(Player p, double dt)
    {
        if (Now - p.LastInputAt > .35) { p.Move = Vector2.Zero; p.Firing = false; }
        var speed = Rules.PlayerSpeed * (p.Passive == "mobility" ? 1.22 : 1);
        p.Position = Map.Move(p.Position, (p.Move * (float)speed + p.Impulse) * (float)dt);
        p.Impulse *= (float)Math.Exp(-9 * dt);
        if (p.ReloadUntil > 0 && Now >= p.ReloadUntil) { p.Ammo = Rules.Magazine; p.ReloadUntil = 0; }
        if ((p.ReloadRequested || p.Ammo == 0) && p.ReloadUntil == 0 && p.Ammo < Rules.Magazine)
            p.ReloadUntil = Now + Rules.ReloadSeconds;
        p.ReloadRequested = false;
        var facing = new Vector2((float)Math.Cos(p.Aim), (float)Math.Sin(p.Aim));
        if (p.Firing && p.ReloadUntil == 0 && p.Ammo > 0 && Now >= p.NextShot)
        {
            p.Ammo--;
            p.NextShot = Now + Rules.ShotInterval;
            // Start at the authoritative body so the muzzle cannot create bullets beyond an intervening wall.
            Bullets.Add(new(++_nextId, p.Position, facing * (float)Rules.BulletSpeed, p.Id, Rules.RifleDamage));
        }
        if (p.GrenadeRequested && p.Grenades > 0 && Now >= p.NextGrenade)
        {
            p.Grenades--;
            p.NextGrenade = Now + .65;
            Grenades.Add(new(++_nextId, p.Position, facing * 360, p.Id));
        }
        p.GrenadeRequested = false;
        foreach (var supply in Supplies)
        {
            if (supply.Collected || supply.AvailableAt > Now || Vector2.Distance(p.Position, supply.Position) > 35) continue;
            if (supply.Objective)
            {
                supply.Collected = true;
                Note($"부목표 보급품 회수 {Supplies.Count(s => s.Objective && s.Collected)}/2");
            }
            else if (p.Hp >= p.MaxHp && p.Grenades >= Rules.Grenades) continue;
            p.Hp = p.MaxHp;
            p.Grenades = Rules.Grenades;
            supply.AvailableAt = Now + 18;
            Effect(p.Position, "heal", .5);
        }
    }

    private void SpawnEnemy()
    {
        var angle = NextRandom() * Math.Tau;
        var position = new Vector2(1080 + (float)Math.Cos(angle) * 680, 710 + (float)Math.Sin(angle) * 580);
        if (!Map.CanStand(position, 12)) return;
        Enemies.Add(new(++_nextId, position, NextRandom() < .22 ? "ranged" : "melee") { NextAttack = Now + 1.5 });
    }

    private void BuildFlow()
    {
        Array.Fill(_flow, int.MaxValue);
        var queue = new Queue<int>();
        foreach (var p in Players.Where(p => p.State == "alive"))
        {
            var index = (int)(p.Position.Y / BattleMap.Cell) * BattleMap.Columns + (int)(p.Position.X / BattleMap.Cell);
            if (index < 0 || index >= _flow.Length || _flow[index] == 0) continue;
            _flow[index] = 0;
            queue.Enqueue(index);
        }
        while (queue.TryDequeue(out var index))
        {
            var x = index % BattleMap.Columns;
            var y = index / BattleMap.Columns;
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (Map.Solid(nx, ny)) continue;
                var next = ny * BattleMap.Columns + nx;
                if (_flow[next] <= _flow[index] + 1) continue;
                _flow[next] = _flow[index] + 1;
                queue.Enqueue(next);
            }
        }
    }

    private void UpdateEnemies(double dt)
    {
        var alive = Players.Where(p => p.State == "alive").ToArray();
        if (alive.Length == 0) return;
        foreach (var e in Enemies)
        {
            if (e.Hp <= 0) continue;
            var target = alive.MinBy(p => Vector2.DistanceSquared(p.Position, e.Position))!;
            var distance = Vector2.Distance(target.Position, e.Position);
            var los = Map.LineOfSight(e.Position, target.Position);
            var heading = target.Position - e.Position;
            if (!los)
            {
                var x = (int)(e.Position.X / BattleMap.Cell);
                var y = (int)(e.Position.Y / BattleMap.Cell);
                var best = int.MaxValue;
                foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                {
                    if (Map.Solid(nx, ny)) continue;
                    var cost = _flow[ny * BattleMap.Columns + nx];
                    if (cost >= best) continue;
                    best = cost;
                    heading = new Vector2((nx + .5f) * BattleMap.Cell, (ny + .5f) * BattleMap.Cell) - e.Position;
                }
            }
            if (heading.LengthSquared() > 1) heading = Vector2.Normalize(heading);
            var desired = e.Kind == "ranged" ? 220 : 25;
            if (distance > desired || !los)
                e.Position = Map.Move(e.Position, heading * (float)((e.Kind == "ranged" ? 65 : 86) * dt) + e.Impulse * (float)dt, 12);
            else e.Position = Map.Move(e.Position, e.Impulse * (float)dt, 12);
            e.Impulse *= (float)Math.Exp(-8 * dt);
            if (!los || Now < e.NextAttack) continue;
            if (e.Kind == "melee" && distance < 32)
            {
                Hurt(target, 11);
                e.NextAttack = Now + .85;
            }
            if (e.Kind == "ranged" && distance > .01 && distance < 360)
            {
                var direction = Vector2.Normalize(target.Position - e.Position);
                Bullets.Add(new(++_nextId, e.Position, direction * 310, null, 16) { Remaining = 2 });
                e.NextAttack = Now + 1.8;
            }
        }
    }

    private void UpdateBullets(double dt)
    {
        foreach (var b in Bullets)
        {
            b.Remaining -= dt;
            var to = b.Position + b.Velocity * (float)dt;
            var (fraction, tile) = Map.RayWall(b.Position, to);
            Player? playerHit = null;
            Enemy? enemyHit = null;
            Facility? facilityHit = null;
            foreach (var p in Players.Where(p => p.State == "alive" && p.Id != b.Owner))
            {
                var hit = BattleMap.RayCircle(b.Position, to, p.Position, 13);
                if (hit >= fraction) continue;
                fraction = hit; playerHit = p; enemyHit = null; facilityHit = null; tile = -1;
            }
            if (b.Owner is not null)
            {
                foreach (var e in Enemies.Where(e => e.Hp > 0))
                {
                    var hit = BattleMap.RayCircle(b.Position, to, e.Position, 13);
                    if (hit >= fraction) continue;
                    fraction = hit; playerHit = null; enemyHit = e; facilityHit = null; tile = -1;
                }
                foreach (var f in Facilities.Where(f => f.Hp > 0))
                {
                    var hit = BattleMap.RayCircle(b.Position, to, f.Position, 30);
                    if (hit >= fraction) continue;
                    fraction = hit; playerHit = null; enemyHit = null; facilityHit = f; tile = -1;
                }
            }
            if (fraction <= 1)
            {
                b.Position = Vector2.Lerp(b.Position, to, (float)fraction);
                if (tile >= 0) Map.Damage(tile, b.Damage);
                if (playerHit is not null) Hurt(playerHit, b.Damage);
                if (enemyHit is not null) DamageEnemy(enemyHit, b.Damage, b.Owner);
                if (facilityHit is not null) DamageFacility(facilityHit, b.Damage);
                Effect(b.Position, "impact", .18);
                b.Remaining = 0;
            }
            else b.Position = to;
        }
        Bullets.RemoveAll(b => b.Remaining <= 0);
    }

    private void UpdateGrenades(double dt)
    {
        foreach (var g in Grenades)
        {
            var next = Map.Move(g.Position, g.Velocity * (float)dt, 5);
            if (Vector2.DistanceSquared(next, g.Position) < 1) g.Velocity *= -.35f;
            g.Position = next;
            g.Velocity *= (float)Math.Exp(-2.1 * dt);
            g.Remaining -= dt;
            if (g.Remaining > 0) continue;
            Explode(g);
        }
        Grenades.RemoveAll(g => g.Remaining <= 0);
    }

    private void Explode(Grenade g)
    {
        // Occlusion is evaluated before breaking walls, so cover protects against this blast.
        foreach (var p in Players.Where(p => p.State == "alive"))
        {
            var distance = Vector2.Distance(p.Position, g.Position);
            if (distance > Rules.GrenadeRadius || !Map.LineOfSight(g.Position, p.Position)) continue;
            Hurt(p, Rules.GrenadeDamage * (1 - distance / Rules.GrenadeRadius));
            p.Impulse += Push(g.Position, p.Position, distance);
        }
        foreach (var e in Enemies.Where(e => e.Hp > 0))
        {
            var distance = Vector2.Distance(e.Position, g.Position);
            if (distance > Rules.GrenadeRadius || !Map.LineOfSight(g.Position, e.Position)) continue;
            DamageEnemy(e, Rules.GrenadeDamage * (1 - distance / Rules.GrenadeRadius), g.Owner);
            e.Impulse += Push(g.Position, e.Position, distance);
        }
        foreach (var f in Facilities.Where(f => f.Hp > 0))
            if (Vector2.Distance(f.Position, g.Position) < Rules.GrenadeRadius && Map.LineOfSight(g.Position, f.Position)) DamageFacility(f, Rules.GrenadeDamage);
        for (var i = 0; i < Map.Tiles.Length; i++)
        {
            if (Map.Tiles[i] != 1) continue;
            var center = new Vector2((i % BattleMap.Columns + .5f) * BattleMap.Cell, (i / BattleMap.Columns + .5f) * BattleMap.Cell);
            if (Vector2.Distance(center, g.Position) < Rules.GrenadeRadius) Map.Damage(i, Rules.GrenadeDamage);
        }
        Effect(g.Position, "explosion", .55);
    }

    private Vector2 Push(Vector2 from, Vector2 to, double distance) => distance < .1 ? Vector2.Zero : Vector2.Normalize(to - from) * (float)(480 * (1 - distance / Rules.GrenadeRadius));

    private void DamageEnemy(Enemy enemy, double damage, string? owner)
    {
        var wasAlive = enemy.Hp > 0;
        enemy.Hp -= damage;
        if (!wasAlive || enemy.Hp > 0) return;
        var p = Players.Find(p => p.Id == owner);
        if (p is not null) p.Kills++;
        Effect(enemy.Position, "kill", .35);
    }

    private void DamageFacility(Facility facility, double damage)
    {
        var wasAlive = facility.Hp > 0;
        facility.Hp = Math.Max(0, facility.Hp - damage);
        if (wasAlive && facility.Hp == 0)
        {
            Effect(facility.Position, "explosion", .8);
            Note($"시설 파괴 {Facilities.Count(f => f.Hp <= 0)}/3");
            if (ObjectivesComplete) Note("주목표 완료 · 탈출 지점을 확보하세요");
        }
    }

    public void Hurt(Player p, double damage)
    {
        if (p.State != "alive" || damage <= 0) return;
        p.Hp = Math.Max(0, p.Hp - damage);
        if (p.Hp > 0) return;
        p.State = "waiting";
        p.Deaths++;
        p.Firing = false;
        p.Move = Vector2.Zero;
        p.ReloadUntil = 0;
        Note($"{p.Name} 사망 · 증원 위치를 선택하세요");
    }

    public bool Visible(Vector2 point)
    {
        if (Vector2.DistanceSquared(BattleMap.Camp, point) <= BattleMap.CampVision * BattleMap.CampVision && Map.LineOfSight(BattleMap.Camp, point)) return true;
        return Players.Any(p => p.State == "alive" && Vector2.DistanceSquared(p.Position, point) <= Rules.Vision * Rules.Vision && Map.LineOfSight(p.Position, point));
    }

    public void Note(string text) { Notices.Add(new(Now, text)); if (Notices.Count > 6) Notices.RemoveAt(0); }
    private void Effect(Vector2 at, string kind, double duration) => Effects.Add(new(++_nextId, at.X, at.Y, kind, Now + duration));
}
