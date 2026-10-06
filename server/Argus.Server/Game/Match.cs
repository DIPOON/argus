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
    public List<Turret> Turrets { get; } = [];
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

    public bool SetLoadout(Player p, string passive, string? weapon = null, string? secondary = null)
    {
        weapon ??= p.Weapon;
        secondary ??= p.Secondary;
        if (Phase == "ended" || p.HasDeployed || p.State != "waiting" || passive is not ("vitality" or "mobility") ||
            !Rules.ValidWeapon(weapon) || !Rules.ValidSecondary(secondary)) return false;
        p.Passive = passive;
        p.Weapon = weapon;
        p.Secondary = secondary;
        p.Hp = p.MaxHp;
        p.Ammo = Rules.Weapon(weapon).Magazine;
        p.Grenades = secondary == "grenade" ? Rules.Grenades : 0;
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

    public bool Input(Player p, long sequence, double moveX, double moveY, double aim, bool fire, bool reload, bool secondary)
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
        p.SecondaryRequested |= secondary;
        return true;
    }

    public void Disconnect(Player p)
    {
        p.Connected = false;
        p.DisconnectedAt = Now;
        p.Move = Vector2.Zero;
        p.Firing = false;
        p.SecondaryRequested = false;
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
        Turrets.RemoveAll(t => t.Hp <= 0 || t.ExpiresAt <= Now || !Players.Any(p => p.Id == t.Owner));
        if (Phase == "ended") return;

        // Death/expiry wins over queued respawns and late joins. A waiting guest cannot hold a lost room open.
        if (CheckWipe()) return;
        foreach (var p in Players.Where(p => p.State == "deploying" && p.DeployAt <= Now))
        {
            if (!Map.CanStand(p.Landing, 15)) { p.State = "waiting"; continue; }
            p.Position = p.Landing;
            p.Impulse = Vector2.Zero;
            p.Hp = p.MaxHp;
            p.Ammo = Rules.Weapon(p.Weapon).Magazine;
            p.Grenades = p.Secondary == "grenade" ? Rules.Grenades : 0;
            p.ReloadUntil = 0;
            p.NextShot = Now;
            p.NextGrenade = Now;
            p.NextTurret = Now;
            p.State = "alive";
            p.HasDeployed = true;
            p.Move = Vector2.Zero;
            p.Firing = false;
            p.ReloadRequested = p.SecondaryRequested = false;
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
        UpdateTurrets();
        UpdateBullets(dt);
        UpdateGrenades(dt);
        Enemies.RemoveAll(e => e.Hp <= 0);
        Turrets.RemoveAll(t => t.Hp <= 0);
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
        var weapon = Rules.Weapon(p.Weapon);
        if (Now - p.LastInputAt > .35) { p.Move = Vector2.Zero; p.Firing = false; }
        var speed = Rules.PlayerSpeed * (p.Passive == "mobility" ? 1.22 : 1);
        p.Position = Map.Move(p.Position, (p.Move * (float)speed + p.Impulse) * (float)dt);
        p.Impulse *= (float)Math.Exp(-9 * dt);
        if (p.ReloadUntil > 0 && Now >= p.ReloadUntil) { p.Ammo = weapon.Magazine; p.ReloadUntil = 0; }
        if ((p.ReloadRequested || p.Ammo == 0) && p.ReloadUntil == 0 && p.Ammo < weapon.Magazine)
            p.ReloadUntil = Now + weapon.ReloadSeconds;
        p.ReloadRequested = false;
        var facing = new Vector2((float)Math.Cos(p.Aim), (float)Math.Sin(p.Aim));
        if (p.Firing && p.ReloadUntil == 0 && p.Ammo > 0 && Now >= p.NextShot)
        {
            p.Ammo--;
            p.NextShot = Now + weapon.ShotInterval;
            // Start at the authoritative body so the muzzle cannot create bullets beyond an intervening wall.
            for (var i = 0; i < weapon.Pellets; i++)
            {
                var angle = p.Aim + (weapon.Pellets == 1 ? 0 : ((double)i / (weapon.Pellets - 1) - .5) * weapon.Spread);
                var direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                Bullets.Add(new(++_nextId, p.Position, direction * (float)weapon.BulletSpeed, p.Id, weapon.Damage)
                {
                    Kind = p.Weapon, Remaining = weapon.Range / weapon.BulletSpeed, EnemyHitsLeft = weapon.EnemyHits
                });
            }
        }
        if (p.SecondaryRequested && p.Secondary == "grenade" && p.Grenades > 0 && Now >= p.NextGrenade)
        {
            p.Grenades--;
            p.NextGrenade = Now + .65;
            Grenades.Add(new(++_nextId, p.Position, facing * 360, p.Id));
        }
        if (p.SecondaryRequested && p.Secondary == "turret" && Now >= p.NextTurret)
            PlaceTurret(p, facing);
        p.SecondaryRequested = false;
        foreach (var supply in Supplies)
        {
            if (supply.Collected || supply.AvailableAt > Now || Vector2.Distance(p.Position, supply.Position) > 35) continue;
            if (supply.Objective)
            {
                supply.Collected = true;
                Note($"부목표 보급품 회수 {Supplies.Count(s => s.Objective && s.Collected)}/2");
            }
            else if (p.Hp >= p.MaxHp && (p.Secondary != "grenade" || p.Grenades >= Rules.Grenades)) continue;
            p.Hp = p.MaxHp;
            p.Grenades = p.Secondary == "grenade" ? Rules.Grenades : 0;
            supply.AvailableAt = Now + 18;
            Effect(p.Position, "heal", .5);
        }
    }

    private void PlaceTurret(Player owner, Vector2 facing)
    {
        var at = owner.Position + facing * Rules.TurretPlacementDistance;
        if (!Map.CanStand(at, 14) || !Map.LineOfSight(owner.Position, at) ||
            Turrets.Any(t => t.Owner != owner.Id && t.Hp > 0 && Vector2.DistanceSquared(t.Position, at) < 28 * 28)) return;
        // Respawns restore equipment, but replacing a turret never increases its owner's active count.
        Turrets.RemoveAll(t => t.Owner == owner.Id);
        Turrets.Add(new(++_nextId, at, owner.Id, Rules.TurretHealth, Now + Rules.TurretLifetime) { Aim = owner.Aim });
        owner.NextTurret = Now + Rules.TurretCooldown;
        Effect(at, "deploy", .6);
    }

    private void UpdateTurrets()
    {
        foreach (var turret in Turrets)
        {
            if (turret.Hp <= 0 || turret.ExpiresAt <= Now || Now < turret.NextShot) continue;
            // A turret does not grant vision or track enemies hidden from the squad.
            var target = Enemies.Where(e => e.Hp > 0 && Vector2.DistanceSquared(e.Position, turret.Position) <= Rules.TurretRange * Rules.TurretRange)
                .OrderBy(e => Vector2.DistanceSquared(e.Position, turret.Position))
                .FirstOrDefault(e => Visible(e.Position) && Map.LineOfSight(turret.Position, e.Position));
            if (target is null) continue;
            var direction = target.Position - turret.Position;
            if (direction.LengthSquared() < .001f) continue;
            direction = Vector2.Normalize(direction);
            turret.Aim = Math.Atan2(direction.Y, direction.X);
            turret.NextShot = Now + Rules.TurretShotInterval;
            Bullets.Add(new(++_nextId, turret.Position, direction * 900, turret.Owner, Rules.TurretDamage)
            {
                Kind = "turret", SourceTurret = turret.Id, Remaining = Rules.TurretRange / 900
            });
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
        var goals = Players.Where(p => p.State == "alive").Select(p => p.Position)
            .Concat(Turrets.Where(t => t.Hp > 0).Select(t => t.Position));
        foreach (var position in goals)
        {
            var index = (int)(position.Y / BattleMap.Cell) * BattleMap.Columns + (int)(position.X / BattleMap.Cell);
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
            var turret = Turrets.Where(t => t.Hp > 0).MinBy(t => Vector2.DistanceSquared(t.Position, e.Position));
            if (turret is not null && Vector2.DistanceSquared(turret.Position, e.Position) >= Vector2.DistanceSquared(target.Position, e.Position)) turret = null;
            var targetPosition = turret?.Position ?? target.Position;
            var distance = Vector2.Distance(targetPosition, e.Position);
            var los = Map.LineOfSight(e.Position, targetPosition);
            var heading = targetPosition - e.Position;
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
                if (turret is not null) DamageTurret(turret, 11);
                else Hurt(target, 11);
                e.NextAttack = Now + .85;
            }
            if (e.Kind == "ranged" && distance > .01 && distance < 360)
            {
                var direction = Vector2.Normalize(targetPosition - e.Position);
                Bullets.Add(new(++_nextId, e.Position, direction * 310, null, 16) { Remaining = 2, Kind = "enemy" });
                e.NextAttack = Now + 1.8;
            }
        }
    }

    private void UpdateBullets(double dt)
    {
        foreach (var b in Bullets)
        {
            if (b.Remaining <= 0) continue;
            // Limit the last movement segment to the projectile's actual remaining range.
            var to = b.Position + b.Velocity * (float)Math.Min(dt, b.Remaining);
            b.Remaining -= dt;
            var (fraction, tile) = Map.RayWall(b.Position, to);
            Player? playerHit = null;
            Facility? facilityHit = null;
            Turret? turretHit = null;
            foreach (var p in Players.Where(p => p.State == "alive" && (p.Id != b.Owner || b.SourceTurret is not null)))
            {
                var hit = BattleMap.RayCircle(b.Position, to, p.Position, 13);
                if (hit >= fraction) continue;
                fraction = hit; playerHit = p; tile = -1;
            }
            foreach (var turret in Turrets.Where(t => t.Hp > 0 && t.Id != b.SourceTurret))
            {
                var hit = BattleMap.RayCircle(b.Position, to, turret.Position, 14);
                if (hit >= fraction) continue;
                fraction = hit; turretHit = turret; playerHit = null; tile = -1;
            }
            if (b.Owner is not null)
            {
                foreach (var f in Facilities.Where(f => f.Hp > 0))
                {
                    var hit = BattleMap.RayCircle(b.Position, to, f.Position, 30);
                    if (hit >= fraction) continue;
                    fraction = hit; playerHit = null; turretHit = null; facilityHit = f; tile = -1;
                }

                // Resolve enemy hits in travel order, up to the first wall or non-enemy body.
                // Remember hit IDs across ticks so one piercing bullet never damages a body twice.
                var hits = Enemies.Where(e => e.Hp > 0 && !b.HitEnemies.Contains(e.Id))
                    .Select(e => (Enemy: e, Fraction: BattleMap.RayCircle(b.Position, to, e.Position, 13)))
                    .Where(hit => hit.Fraction <= 1 && hit.Fraction < fraction)
                    .OrderBy(hit => hit.Fraction).ToArray();
                var stopped = false;
                foreach (var hit in hits)
                {
                    DamageEnemy(hit.Enemy, b.Damage, b.Owner);
                    b.HitEnemies.Add(hit.Enemy.Id);
                    var at = Vector2.Lerp(b.Position, to, (float)hit.Fraction);
                    Effect(at, "impact", .18);
                    if (--b.EnemyHitsLeft > 0) continue;
                    b.Position = at;
                    b.Remaining = 0;
                    stopped = true;
                    break;
                }
                if (stopped) continue;
            }
            if (fraction <= 1)
            {
                b.Position = Vector2.Lerp(b.Position, to, (float)fraction);
                if (tile >= 0) Map.Damage(tile, b.Damage);
                if (playerHit is not null) Hurt(playerHit, b.Damage);
                if (turretHit is not null) DamageTurret(turretHit, b.Damage);
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
        foreach (var turret in Turrets.Where(t => t.Hp > 0))
        {
            var distance = Vector2.Distance(turret.Position, g.Position);
            if (distance <= Rules.GrenadeRadius && Map.LineOfSight(g.Position, turret.Position))
                DamageTurret(turret, Rules.GrenadeDamage * (1 - distance / Rules.GrenadeRadius));
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

    private void DamageTurret(Turret turret, double damage)
    {
        var wasAlive = turret.Hp > 0;
        turret.Hp = Math.Max(0, turret.Hp - damage);
        if (wasAlive && turret.Hp == 0) Effect(turret.Position, "kill", .35);
    }

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
