using System.Numerics;
using System.Security.Cryptography;

namespace Argus.Server.Game;

// 방 하나의 게임 상태와 판정을 담당한다. RoomHost가 방의 lock을 잡고 호출한다.
public sealed class Match
{
    public string Code { get; }
    public Rules Rules { get; }
    public BattleMap Map { get; } = new BattleMap();
    public List<Player> Players { get; } = new List<Player>();
    public List<Enemy> Enemies { get; } = new List<Enemy>();
    public List<Bullet> Bullets { get; } = new List<Bullet>();
    public List<Grenade> Grenades { get; } = new List<Grenade>();
    public List<Turret> Turrets { get; } = new List<Turret>();
    public List<Facility> Facilities { get; } = new List<Facility>();
    public List<Supply> Supplies { get; } = new List<Supply>();
    public List<Effect> Effects { get; } = new List<Effect>();
    public List<Notice> Notices { get; } = new List<Notice>();
    public double Now { get; private set; }
    public double StartedAt { get; private set; } = -1;
    public double? EndedAt { get; private set; }
    public string Phase { get; private set; } = "staging";
    public string? Result { get; private set; }
    public double ExtractionProgress { get; private set; }
    public int Pulse { get; private set; }

    private readonly Random? _random;
    private double _nextSpawn = 1;
    private int _nextId = 100;
    private readonly int[] _flow = new int[BattleMap.Columns * BattleMap.Rows];
    private double _nextFlow;
    private static readonly int[] NeighborX = new int[] { 1, -1, 0, 0 };
    private static readonly int[] NeighborY = new int[] { 0, 0, 1, -1 };

    public Match(string code, Rules? rules = null, int? seed = null)
    {
        Code = code;
        if (rules == null)
        {
            Rules = new Rules();
        }
        else
        {
            Rules = rules;
        }

        // 테스트에만 고정 시드를 쓴다. 실제 방의 적 생성 순서는 매번 달라진다.
        if (seed.HasValue)
        {
            _random = new Random(seed.Value);
        }

        Facilities.Add(new Facility(1, new Vector2(960, 485)));
        Facilities.Add(new Facility(2, new Vector2(1210, 695)));
        Facilities.Add(new Facility(3, new Vector2(990, 920)));
        Supplies.Add(new Supply(1, BattleMap.Camp + new Vector2(70, 40), false));
        Supplies.Add(new Supply(2, new Vector2(845, 640), true));
        Supplies.Add(new Supply(3, new Vector2(1150, 835), true));
        Supplies.Add(new Supply(4, new Vector2(815, 985), false));
    }

    public double Elapsed
    {
        get
        {
            if (StartedAt < 0)
            {
                return 0;
            }
            double end = Now;
            if (EndedAt.HasValue)
            {
                end = EndedAt.Value;
            }
            return end - StartedAt;
        }
    }

    public bool ObjectivesComplete
    {
        get
        {
            for (int i = 0; i < Facilities.Count; i++)
            {
                if (Facilities[i].Hp <= 0)
                {
                    continue;
                }
                return false;
            }
            return true;
        }
    }

    private double NextRandom()
    {
        if (_random != null)
        {
            return _random.NextDouble();
        }
        return RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue;
    }

    public Player? Join(string id, string name)
    {
        if (Players.Count >= Rules.MaxPlayers || Phase == "ended")
        {
            return null;
        }

        Player player = new Player(id, name);
        player.Position = BattleMap.Camp;
        player.Hp = 130;
        player.Ammo = Rules.Magazine;
        player.Grenades = Rules.Grenades;
        Players.Add(player);
        Note($"{name} 합류");
        return player;
    }

    public bool SetLoadout(Player player, string passive, string? weapon = null, string? secondary = null)
    {
        if (weapon == null)
        {
            weapon = player.Weapon;
        }
        if (secondary == null)
        {
            secondary = player.Secondary;
        }

        if (Phase == "ended" || player.HasDeployed || player.State != "waiting")
        {
            return false;
        }
        if (passive != "vitality" && passive != "mobility")
        {
            return false;
        }
        if (!Rules.ValidWeapon(weapon) || !Rules.ValidSecondary(secondary))
        {
            return false;
        }

        player.Passive = passive;
        player.Weapon = weapon;
        player.Secondary = secondary;
        player.Hp = player.MaxHp;
        player.Ammo = Rules.Weapon(weapon).Magazine;
        player.Grenades = 0;
        if (secondary == "grenade")
        {
            player.Grenades = Rules.Grenades;
        }
        return true;
    }

    public bool Deploy(Player player, double x, double y)
    {
        if (Phase == "ended" || player.State != "waiting" || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return false;
        }

        Vector2 target = new Vector2((float)x, (float)y);
        if (!Map.CanStand(target, 15))
        {
            return false;
        }

        // 위치 선택만으로는 시야가 생기지 않는다. 실제 투입 완료 후에만 시야를 준다.
        player.Landing = target;
        player.DeployAt = Now + Rules.DeploySeconds;
        player.State = "deploying";
        return true;
    }

    public bool Input(Player player, long sequence, double moveX, double moveY, double aim,
        bool fire, bool reload, bool secondary)
    {
        if (Phase == "ended" || player.State != "alive")
        {
            return false;
        }
        if (sequence <= player.LastSequence || sequence < 0 || sequence > 9_000_000_000_000)
        {
            return false;
        }
        if (!double.IsFinite(moveX) || !double.IsFinite(moveY) || !double.IsFinite(aim))
        {
            return false;
        }
        if (Math.Abs(moveX) > 1.01 || Math.Abs(moveY) > 1.01)
        {
            return false;
        }

        player.LastSequence = sequence;
        player.LastInputAt = Now;
        player.Move = new Vector2((float)moveX, (float)moveY);
        if (player.Move.LengthSquared() > 1)
        {
            player.Move = Vector2.Normalize(player.Move);
        }
        player.Aim = Math.IEEERemainder(aim, Math.Tau);
        player.Firing = fire;
        if (reload)
        {
            player.ReloadRequested = true;
        }
        if (secondary)
        {
            player.SecondaryRequested = true;
        }
        return true;
    }

    public void Disconnect(Player player)
    {
        player.Connected = false;
        player.DisconnectedAt = Now;
        player.Move = Vector2.Zero;
        player.Firing = false;
        player.SecondaryRequested = false;
    }

    public void Step(double dt)
    {
        Now += dt;
        RemoveExpiredObjects();
        if (Phase == "ended")
        {
            return;
        }

        // 전멸 판정을 먼저 해야 대기 중인 증원이나 난입이 실패한 임무를 되살리지 않는다.
        if (CheckWipe())
        {
            return;
        }

        CompleteDeployments();
        if (Phase != "active")
        {
            return;
        }

        while (Elapsed >= (Pulse + 1) * Rules.PulseSeconds)
        {
            Pulse++;
            double damage = 60 * Math.Pow(2, Pulse - 1);
            for (int i = 0; i < Players.Count; i++)
            {
                Player player = Players[i];
                if (player.State == "alive")
                {
                    Hurt(player, damage);
                }
            }
            Note($"전역 충격 {Pulse}회 · {damage:0} 피해");
            if (CheckWipe())
            {
                return;
            }
        }

        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State == "alive")
            {
                UpdatePlayer(player, dt);
            }
        }

        if (Elapsed >= _nextSpawn && Enemies.Count < Rules.MaxEnemies)
        {
            _nextSpawn = Elapsed + Math.Max(0.6, Rules.SpawnInterval - Elapsed / 150);
            int spawnCount = Math.Min(2 + (int)(Elapsed / 90), 7);
            for (int i = 0; i < spawnCount && Enemies.Count < Rules.MaxEnemies; i++)
            {
                SpawnEnemy();
            }
        }

        if (Now >= _nextFlow)
        {
            _nextFlow = Now + 0.5;
            BuildFlow();
        }

        UpdateEnemies(dt);
        UpdateTurrets();
        UpdateBullets(dt);
        UpdateGrenades(dt);

        // 뒤에서부터 지우면 아직 검사하지 않은 항목의 인덱스가 바뀌지 않는다.
        for (int i = Enemies.Count - 1; i >= 0; i--)
        {
            if (Enemies[i].Hp <= 0)
            {
                Enemies.RemoveAt(i);
            }
        }
        for (int i = Turrets.Count - 1; i >= 0; i--)
        {
            if (Turrets[i].Hp <= 0)
            {
                Turrets.RemoveAt(i);
            }
        }

        if (CheckWipe())
        {
            return;
        }
        if (ObjectivesComplete && HasPlayerAtExtraction())
        {
            ExtractionProgress += dt;
        }
        else
        {
            ExtractionProgress = Math.Max(0, ExtractionProgress - dt * 0.5);
        }
        if (ExtractionProgress >= Rules.ExtractionSeconds)
        {
            Finish("success");
        }
    }

    private void RemoveExpiredObjects()
    {
        for (int i = Effects.Count - 1; i >= 0; i--)
        {
            if (Effects[i].Until < Now)
            {
                Effects.RemoveAt(i);
            }
        }

        // 안내 메시지는 기존 대원 순서대로 남긴다.
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.DisconnectedAt.HasValue &&
                Now - player.DisconnectedAt.Value > Rules.ReconnectSeconds)
            {
                Note($"{player.Name} 연결 복구 시간 만료");
                Players.RemoveAt(i);
                i--;
            }
        }
        for (int i = Turrets.Count - 1; i >= 0; i--)
        {
            Turret turret = Turrets[i];
            if (turret.Hp <= 0 || turret.ExpiresAt <= Now || FindPlayer(turret.Owner) == null)
            {
                Turrets.RemoveAt(i);
            }
        }
    }

    private void CompleteDeployments()
    {
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State != "deploying" || player.DeployAt > Now)
            {
                continue;
            }
            if (!Map.CanStand(player.Landing, 15))
            {
                player.State = "waiting";
                continue;
            }

            player.Position = player.Landing;
            player.Impulse = Vector2.Zero;
            player.Hp = player.MaxHp;
            player.Ammo = Rules.Weapon(player.Weapon).Magazine;
            player.Grenades = 0;
            if (player.Secondary == "grenade")
            {
                player.Grenades = Rules.Grenades;
            }
            player.ReloadUntil = 0;
            player.NextShot = Now;
            player.NextGrenade = Now;
            player.NextTurret = Now;
            player.State = "alive";
            player.HasDeployed = true;
            player.Move = Vector2.Zero;
            player.Firing = false;
            player.ReloadRequested = false;
            player.SecondaryRequested = false;
            player.LastInputAt = -100;
            Effect(player.Position, "deploy", 0.6);

            if (Phase == "staging")
            {
                Phase = "active";
                StartedAt = Now;
                Note("작전 시작 · 시설 3곳을 파괴하세요");
            }
        }
    }

    private bool CheckWipe()
    {
        if (Phase != "active")
        {
            return false;
        }
        for (int i = 0; i < Players.Count; i++)
        {
            if (Players[i].State == "alive")
            {
                return false;
            }
        }
        Finish("failure");
        return true;
    }

    private bool HasPlayerAtExtraction()
    {
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State == "alive" && Vector2.Distance(player.Position, BattleMap.Extraction) < 80)
            {
                return true;
            }
        }
        return false;
    }

    private void Finish(string result)
    {
        Phase = "ended";
        EndedAt = Now;
        Result = result;
        if (result == "success")
        {
            Note("탈출 성공 · 작전 종료");
        }
        else
        {
            Note("전원 사망 · 작전 실패");
        }
    }

    private void UpdatePlayer(Player player, double dt)
    {
        WeaponDefinition weapon = Rules.Weapon(player.Weapon);
        if (Now - player.LastInputAt > 0.35)
        {
            player.Move = Vector2.Zero;
            player.Firing = false;
        }

        double speed = Rules.PlayerSpeed;
        if (player.Passive == "mobility")
        {
            speed *= 1.22;
        }
        Vector2 movement = (player.Move * (float)speed + player.Impulse) * (float)dt;
        player.Position = Map.Move(player.Position, movement);
        player.Impulse *= (float)Math.Exp(-9 * dt);

        if (player.ReloadUntil > 0 && Now >= player.ReloadUntil)
        {
            player.Ammo = weapon.Magazine;
            player.ReloadUntil = 0;
        }
        if ((player.ReloadRequested || player.Ammo == 0) && player.ReloadUntil == 0 &&
            player.Ammo < weapon.Magazine)
        {
            player.ReloadUntil = Now + weapon.ReloadSeconds;
        }
        player.ReloadRequested = false;

        Vector2 facing = new Vector2((float)Math.Cos(player.Aim), (float)Math.Sin(player.Aim));
        if (player.Firing && player.ReloadUntil == 0 && player.Ammo > 0 && Now >= player.NextShot)
        {
            player.Ammo--;
            player.NextShot = Now + weapon.ShotInterval;
            // 총구를 벽 너머에 내밀어 쏘는 일이 없도록 몸체 위치에서 탄환을 시작한다.
            for (int i = 0; i < weapon.Pellets; i++)
            {
                double angle = player.Aim;
                if (weapon.Pellets != 1)
                {
                    angle += ((double)i / (weapon.Pellets - 1) - 0.5) * weapon.Spread;
                }
                Vector2 direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                _nextId++;
                Bullet bullet = new Bullet(_nextId, player.Position,
                    direction * (float)weapon.BulletSpeed, player.Id, weapon.Damage)
                {
                    Kind = player.Weapon
                };
                bullet.Remaining = weapon.Range / weapon.BulletSpeed;
                bullet.EnemyHitsLeft = weapon.EnemyHits;
                Bullets.Add(bullet);
            }
        }
        if (player.SecondaryRequested && player.Secondary == "grenade" &&
            player.Grenades > 0 && Now >= player.NextGrenade)
        {
            player.Grenades--;
            player.NextGrenade = Now + 0.65;
            _nextId++;
            Grenades.Add(new Grenade(_nextId, player.Position, facing * 360, player.Id));
        }
        if (player.SecondaryRequested && player.Secondary == "turret" && Now >= player.NextTurret)
        {
            PlaceTurret(player, facing);
        }
        player.SecondaryRequested = false;

        for (int i = 0; i < Supplies.Count; i++)
        {
            Supply supply = Supplies[i];
            if (supply.Collected || supply.AvailableAt > Now ||
                Vector2.Distance(player.Position, supply.Position) > 35)
            {
                continue;
            }
            if (supply.Objective)
            {
                supply.Collected = true;
                int collected = 0;
                for (int j = 0; j < Supplies.Count; j++)
                {
                    if (Supplies[j].Objective && Supplies[j].Collected)
                    {
                        collected++;
                    }
                }
                Note($"부목표 보급품 회수 {collected}/2");
            }
            else if (player.Hp >= player.MaxHp &&
                (player.Secondary != "grenade" || player.Grenades >= Rules.Grenades))
            {
                continue;
            }

            player.Hp = player.MaxHp;
            player.Grenades = 0;
            if (player.Secondary == "grenade")
            {
                player.Grenades = Rules.Grenades;
            }
            supply.AvailableAt = Now + 18;
            Effect(player.Position, "heal", 0.5);
        }
    }

    private void PlaceTurret(Player owner, Vector2 facing)
    {
        Vector2 at = owner.Position + facing * Rules.TurretPlacementDistance;
        if (!Map.CanStand(at, 14) || !Map.LineOfSight(owner.Position, at))
        {
            return;
        }
        for (int i = 0; i < Turrets.Count; i++)
        {
            Turret turret = Turrets[i];
            if (turret.Owner != owner.Id && turret.Hp > 0 &&
                Vector2.DistanceSquared(turret.Position, at) < 28 * 28)
            {
                return;
            }
        }

        // 새로 설치할 때 이전 터렛을 제거한다. 증원을 반복해도 한 기만 유지된다.
        for (int i = Turrets.Count - 1; i >= 0; i--)
        {
            if (Turrets[i].Owner == owner.Id)
            {
                Turrets.RemoveAt(i);
            }
        }
        _nextId++;
        Turret created = new Turret(_nextId, at, owner.Id, Rules.TurretHealth, Now + Rules.TurretLifetime);
        created.Aim = owner.Aim;
        Turrets.Add(created);
        owner.NextTurret = Now + Rules.TurretCooldown;
        Effect(at, "deploy", 0.6);
    }

    private void UpdateTurrets()
    {
        for (int i = 0; i < Turrets.Count; i++)
        {
            Turret turret = Turrets[i];
            if (turret.Hp <= 0 || turret.ExpiresAt <= Now || Now < turret.NextShot)
            {
                continue;
            }

            // 분대에 보이고 사선이 열린 적 중 가장 가까운 적을 찾는다.
            Enemy? target = null;
            double closest = double.PositiveInfinity;
            double rangeSquared = Rules.TurretRange * Rules.TurretRange;
            for (int j = 0; j < Enemies.Count; j++)
            {
                Enemy enemy = Enemies[j];
                double distanceSquared = Vector2.DistanceSquared(enemy.Position, turret.Position);
                if (enemy.Hp <= 0 || distanceSquared > rangeSquared || distanceSquared >= closest)
                {
                    continue;
                }
                if (!Visible(enemy.Position) || !Map.LineOfSight(turret.Position, enemy.Position))
                {
                    continue;
                }
                target = enemy;
                closest = distanceSquared;
            }
            if (target == null)
            {
                continue;
            }

            Vector2 direction = target.Position - turret.Position;
            if (direction.LengthSquared() < 0.001f)
            {
                continue;
            }
            direction = Vector2.Normalize(direction);
            turret.Aim = Math.Atan2(direction.Y, direction.X);
            turret.NextShot = Now + Rules.TurretShotInterval;
            _nextId++;
            Bullet bullet = new Bullet(_nextId, turret.Position, direction * 900, turret.Owner, Rules.TurretDamage)
            {
                Kind = "turret",
                SourceTurret = turret.Id
            };
            bullet.Remaining = Rules.TurretRange / 900;
            Bullets.Add(bullet);
        }
    }

    private void SpawnEnemy()
    {
        double angle = NextRandom() * Math.Tau;
        Vector2 position = new Vector2(1080 + (float)Math.Cos(angle) * 680, 710 + (float)Math.Sin(angle) * 580);
        if (!Map.CanStand(position, 12))
        {
            return;
        }
        string kind = "melee";
        if (NextRandom() < 0.22)
        {
            kind = "ranged";
        }
        _nextId++;
        Enemy enemy = new Enemy(_nextId, position, kind);
        enemy.NextAttack = Now + 1.5;
        Enemies.Add(enemy);
    }

    private void BuildFlow()
    {
        Array.Fill(_flow, int.MaxValue);
        Queue<int> queue = new Queue<int>();
        for (int i = 0; i < Players.Count; i++)
        {
            if (Players[i].State == "alive")
            {
                AddFlowTarget(queue, Players[i].Position);
            }
        }
        for (int i = 0; i < Turrets.Count; i++)
        {
            if (Turrets[i].Hp > 0)
            {
                AddFlowTarget(queue, Turrets[i].Position);
            }
        }

        // 목표에 가까운 칸부터 차례로 거리를 채우는 너비 우선 탐색이다.
        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            int x = index % BattleMap.Columns;
            int y = index / BattleMap.Columns;
            for (int direction = 0; direction < 4; direction++)
            {
                int nx = x + NeighborX[direction];
                int ny = y + NeighborY[direction];
                if (Map.Solid(nx, ny))
                {
                    continue;
                }
                int next = ny * BattleMap.Columns + nx;
                if (_flow[next] <= _flow[index] + 1)
                {
                    continue;
                }
                _flow[next] = _flow[index] + 1;
                queue.Enqueue(next);
            }
        }
    }

    private void AddFlowTarget(Queue<int> queue, Vector2 position)
    {
        int index = (int)(position.Y / BattleMap.Cell) * BattleMap.Columns + (int)(position.X / BattleMap.Cell);
        if (index < 0 || index >= _flow.Length || _flow[index] == 0)
        {
            return;
        }
        _flow[index] = 0;
        queue.Enqueue(index);
    }

    private void UpdateEnemies(double dt)
    {
        List<Player> alive = new List<Player>();
        for (int i = 0; i < Players.Count; i++)
        {
            if (Players[i].State == "alive")
            {
                alive.Add(Players[i]);
            }
        }
        if (alive.Count == 0)
        {
            return;
        }

        for (int i = 0; i < Enemies.Count; i++)
        {
            Enemy enemy = Enemies[i];
            if (enemy.Hp <= 0)
            {
                continue;
            }

            Player target = alive[0];
            double closestPlayer = Vector2.DistanceSquared(target.Position, enemy.Position);
            for (int j = 1; j < alive.Count; j++)
            {
                double distanceSquared = Vector2.DistanceSquared(alive[j].Position, enemy.Position);
                if (distanceSquared < closestPlayer)
                {
                    target = alive[j];
                    closestPlayer = distanceSquared;
                }
            }

            Turret? targetTurret = null;
            double closestTurret = closestPlayer;
            for (int j = 0; j < Turrets.Count; j++)
            {
                Turret candidate = Turrets[j];
                double distanceSquared = Vector2.DistanceSquared(candidate.Position, enemy.Position);
                if (candidate.Hp > 0 && distanceSquared < closestTurret)
                {
                    targetTurret = candidate;
                    closestTurret = distanceSquared;
                }
            }

            Vector2 targetPosition = target.Position;
            if (targetTurret != null)
            {
                targetPosition = targetTurret.Position;
            }
            double distance = Vector2.Distance(targetPosition, enemy.Position);
            bool hasLineOfSight = Map.LineOfSight(enemy.Position, targetPosition);
            Vector2 heading = targetPosition - enemy.Position;
            if (!hasLineOfSight)
            {
                int x = (int)(enemy.Position.X / BattleMap.Cell);
                int y = (int)(enemy.Position.Y / BattleMap.Cell);
                int best = int.MaxValue;
                for (int direction = 0; direction < 4; direction++)
                {
                    int nx = x + NeighborX[direction];
                    int ny = y + NeighborY[direction];
                    if (Map.Solid(nx, ny))
                    {
                        continue;
                    }
                    int cost = _flow[ny * BattleMap.Columns + nx];
                    if (cost >= best)
                    {
                        continue;
                    }
                    best = cost;
                    heading = new Vector2((nx + 0.5f) * BattleMap.Cell, (ny + 0.5f) * BattleMap.Cell) - enemy.Position;
                }
            }
            if (heading.LengthSquared() > 1)
            {
                heading = Vector2.Normalize(heading);
            }

            double desiredDistance = 25;
            double moveSpeed = 86;
            if (enemy.Kind == "ranged")
            {
                desiredDistance = 220;
                moveSpeed = 65;
            }
            if (distance > desiredDistance || !hasLineOfSight)
            {
                Vector2 movement = heading * (float)(moveSpeed * dt) + enemy.Impulse * (float)dt;
                enemy.Position = Map.Move(enemy.Position, movement, 12);
            }
            else
            {
                enemy.Position = Map.Move(enemy.Position, enemy.Impulse * (float)dt, 12);
            }
            enemy.Impulse *= (float)Math.Exp(-8 * dt);

            if (!hasLineOfSight || Now < enemy.NextAttack)
            {
                continue;
            }
            if (enemy.Kind == "melee" && distance < 32)
            {
                if (targetTurret != null)
                {
                    DamageTurret(targetTurret, 11);
                }
                else
                {
                    Hurt(target, 11);
                }
                enemy.NextAttack = Now + 0.85;
            }
            if (enemy.Kind == "ranged" && distance > 0.01 && distance < 360)
            {
                Vector2 direction = Vector2.Normalize(targetPosition - enemy.Position);
                _nextId++;
                Bullet bullet = new Bullet(_nextId, enemy.Position, direction * 310, null, 16)
                {
                    Kind = "enemy"
                };
                bullet.Remaining = 2;
                Bullets.Add(bullet);
                enemy.NextAttack = Now + 1.8;
            }
        }
    }

    private void UpdateBullets(double dt)
    {
        for (int i = 0; i < Bullets.Count; i++)
        {
            Bullet bullet = Bullets[i];
            if (bullet.Remaining <= 0)
            {
                continue;
            }

            // 마지막 이동 구간도 남은 사거리를 넘지 않도록 제한한다.
            Vector2 to = bullet.Position + bullet.Velocity * (float)Math.Min(dt, bullet.Remaining);
            bullet.Remaining -= dt;
            WallHit wall = Map.RayWall(bullet.Position, to);
            double fraction = wall.Fraction;
            int tile = wall.Tile;
            Player? playerHit = null;
            Facility? facilityHit = null;
            Turret? turretHit = null;

            for (int j = 0; j < Players.Count; j++)
            {
                Player player = Players[j];
                if (player.State != "alive" || (player.Id == bullet.Owner && !bullet.SourceTurret.HasValue))
                {
                    continue;
                }
                double hit = BattleMap.RayCircle(bullet.Position, to, player.Position, 13);
                if (hit >= fraction)
                {
                    continue;
                }
                fraction = hit;
                playerHit = player;
                tile = -1;
            }
            for (int j = 0; j < Turrets.Count; j++)
            {
                Turret turret = Turrets[j];
                if (turret.Hp <= 0 || turret.Id == bullet.SourceTurret)
                {
                    continue;
                }
                double hit = BattleMap.RayCircle(bullet.Position, to, turret.Position, 14);
                if (hit >= fraction)
                {
                    continue;
                }
                fraction = hit;
                turretHit = turret;
                playerHit = null;
                tile = -1;
            }

            if (bullet.Owner != null)
            {
                for (int j = 0; j < Facilities.Count; j++)
                {
                    Facility facility = Facilities[j];
                    if (facility.Hp <= 0)
                    {
                        continue;
                    }
                    double hit = BattleMap.RayCircle(bullet.Position, to, facility.Position, 30);
                    if (hit >= fraction)
                    {
                        continue;
                    }
                    fraction = hit;
                    playerHit = null;
                    turretHit = null;
                    facilityHit = facility;
                    tile = -1;
                }

                // 가까운 적부터 한 명씩 판정한다. 이미 맞은 적은 다음 탐색에서 제외한다.
                bool stopped = false;
                while (true)
                {
                    Enemy? enemyHit = null;
                    double enemyFraction = double.PositiveInfinity;
                    for (int j = 0; j < Enemies.Count; j++)
                    {
                        Enemy enemy = Enemies[j];
                        if (enemy.Hp <= 0 || bullet.HitEnemies.Contains(enemy.Id))
                        {
                            continue;
                        }
                        double hit = BattleMap.RayCircle(bullet.Position, to, enemy.Position, 13);
                        if (hit > 1 || hit >= fraction || hit >= enemyFraction)
                        {
                            continue;
                        }
                        enemyHit = enemy;
                        enemyFraction = hit;
                    }
                    if (enemyHit == null)
                    {
                        break;
                    }

                    DamageEnemy(enemyHit, bullet.Damage, bullet.Owner);
                    bullet.HitEnemies.Add(enemyHit.Id);
                    Vector2 at = Vector2.Lerp(bullet.Position, to, (float)enemyFraction);
                    Effect(at, "impact", 0.18);
                    bullet.EnemyHitsLeft--;
                    if (bullet.EnemyHitsLeft > 0)
                    {
                        continue;
                    }
                    bullet.Position = at;
                    bullet.Remaining = 0;
                    stopped = true;
                    break;
                }
                if (stopped)
                {
                    continue;
                }
            }

            if (fraction <= 1)
            {
                bullet.Position = Vector2.Lerp(bullet.Position, to, (float)fraction);
                if (tile >= 0)
                {
                    Map.Damage(tile, bullet.Damage);
                }
                if (playerHit != null)
                {
                    Hurt(playerHit, bullet.Damage);
                }
                if (turretHit != null)
                {
                    DamageTurret(turretHit, bullet.Damage);
                }
                if (facilityHit != null)
                {
                    DamageFacility(facilityHit, bullet.Damage);
                }
                Effect(bullet.Position, "impact", 0.18);
                bullet.Remaining = 0;
            }
            else
            {
                bullet.Position = to;
            }
        }

        for (int i = Bullets.Count - 1; i >= 0; i--)
        {
            if (Bullets[i].Remaining <= 0)
            {
                Bullets.RemoveAt(i);
            }
        }
    }

    private void UpdateGrenades(double dt)
    {
        for (int i = 0; i < Grenades.Count; i++)
        {
            Grenade grenade = Grenades[i];
            Vector2 next = Map.Move(grenade.Position, grenade.Velocity * (float)dt, 5);
            if (Vector2.DistanceSquared(next, grenade.Position) < 1)
            {
                grenade.Velocity *= -0.35f;
            }
            grenade.Position = next;
            grenade.Velocity *= (float)Math.Exp(-2.1 * dt);
            grenade.Remaining -= dt;
            if (grenade.Remaining > 0)
            {
                continue;
            }
            Explode(grenade);
        }
        for (int i = Grenades.Count - 1; i >= 0; i--)
        {
            if (Grenades[i].Remaining <= 0)
            {
                Grenades.RemoveAt(i);
            }
        }
    }

    private void Explode(Grenade grenade)
    {
        // 벽을 파괴하기 전에 엄폐 판정을 끝내야 이번 폭발을 벽이 막아준다.
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State != "alive")
            {
                continue;
            }
            double distance = Vector2.Distance(player.Position, grenade.Position);
            if (distance > Rules.GrenadeRadius || !Map.LineOfSight(grenade.Position, player.Position))
            {
                continue;
            }
            Hurt(player, Rules.GrenadeDamage * (1 - distance / Rules.GrenadeRadius));
            player.Impulse += Push(grenade.Position, player.Position, distance);
        }
        for (int i = 0; i < Enemies.Count; i++)
        {
            Enemy enemy = Enemies[i];
            if (enemy.Hp <= 0)
            {
                continue;
            }
            double distance = Vector2.Distance(enemy.Position, grenade.Position);
            if (distance > Rules.GrenadeRadius || !Map.LineOfSight(grenade.Position, enemy.Position))
            {
                continue;
            }
            DamageEnemy(enemy, Rules.GrenadeDamage * (1 - distance / Rules.GrenadeRadius), grenade.Owner);
            enemy.Impulse += Push(grenade.Position, enemy.Position, distance);
        }
        for (int i = 0; i < Turrets.Count; i++)
        {
            Turret turret = Turrets[i];
            if (turret.Hp <= 0)
            {
                continue;
            }
            double distance = Vector2.Distance(turret.Position, grenade.Position);
            if (distance <= Rules.GrenadeRadius && Map.LineOfSight(grenade.Position, turret.Position))
            {
                DamageTurret(turret, Rules.GrenadeDamage * (1 - distance / Rules.GrenadeRadius));
            }
        }
        for (int i = 0; i < Facilities.Count; i++)
        {
            Facility facility = Facilities[i];
            if (facility.Hp <= 0)
            {
                continue;
            }
            if (Vector2.Distance(facility.Position, grenade.Position) < Rules.GrenadeRadius &&
                Map.LineOfSight(grenade.Position, facility.Position))
            {
                DamageFacility(facility, Rules.GrenadeDamage);
            }
        }
        for (int i = 0; i < Map.Tiles.Length; i++)
        {
            if (Map.Tiles[i] != 1)
            {
                continue;
            }
            Vector2 center = new Vector2((i % BattleMap.Columns + 0.5f) * BattleMap.Cell,
                (i / BattleMap.Columns + 0.5f) * BattleMap.Cell);
            if (Vector2.Distance(center, grenade.Position) < Rules.GrenadeRadius)
            {
                Map.Damage(i, Rules.GrenadeDamage);
            }
        }
        Effect(grenade.Position, "explosion", 0.55);
    }

    private Vector2 Push(Vector2 from, Vector2 to, double distance)
    {
        if (distance < 0.1)
        {
            return Vector2.Zero;
        }
        return Vector2.Normalize(to - from) * (float)(480 * (1 - distance / Rules.GrenadeRadius));
    }

    private void DamageTurret(Turret turret, double damage)
    {
        bool wasAlive = turret.Hp > 0;
        turret.Hp = Math.Max(0, turret.Hp - damage);
        if (wasAlive && turret.Hp == 0)
        {
            Effect(turret.Position, "kill", 0.35);
        }
    }

    private void DamageEnemy(Enemy enemy, double damage, string? owner)
    {
        bool wasAlive = enemy.Hp > 0;
        enemy.Hp -= damage;
        if (!wasAlive || enemy.Hp > 0)
        {
            return;
        }
        Player? player = FindPlayer(owner);
        if (player != null)
        {
            player.Kills++;
        }
        Effect(enemy.Position, "kill", 0.35);
    }

    private void DamageFacility(Facility facility, double damage)
    {
        bool wasAlive = facility.Hp > 0;
        facility.Hp = Math.Max(0, facility.Hp - damage);
        if (wasAlive && facility.Hp == 0)
        {
            Effect(facility.Position, "explosion", 0.8);
            int destroyed = 0;
            for (int i = 0; i < Facilities.Count; i++)
            {
                if (Facilities[i].Hp <= 0)
                {
                    destroyed++;
                }
            }
            Note($"시설 파괴 {destroyed}/3");
            if (ObjectivesComplete)
            {
                Note("주목표 완료 · 탈출 지점을 확보하세요");
            }
        }
    }

    public void Hurt(Player player, double damage)
    {
        if (player.State != "alive" || damage <= 0)
        {
            return;
        }
        player.Hp = Math.Max(0, player.Hp - damage);
        if (player.Hp > 0)
        {
            return;
        }
        player.State = "waiting";
        player.Deaths++;
        player.Firing = false;
        player.Move = Vector2.Zero;
        player.ReloadUntil = 0;
        Note($"{player.Name} 사망 · 증원 위치를 선택하세요");
    }

    public bool Visible(Vector2 point)
    {
        if (Vector2.DistanceSquared(BattleMap.Camp, point) <= BattleMap.CampVision * BattleMap.CampVision &&
            Map.LineOfSight(BattleMap.Camp, point))
        {
            return true;
        }
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State == "alive" &&
                Vector2.DistanceSquared(player.Position, point) <= Rules.Vision * Rules.Vision &&
                Map.LineOfSight(player.Position, point))
            {
                return true;
            }
        }
        return false;
    }

    private Player? FindPlayer(string? id)
    {
        for (int i = 0; i < Players.Count; i++)
        {
            if (Players[i].Id == id)
            {
                return Players[i];
            }
        }
        return null;
    }

    public void Note(string text)
    {
        Notices.Add(new Notice(Now, text));
        if (Notices.Count > 6)
        {
            Notices.RemoveAt(0);
        }
    }

    private void Effect(Vector2 at, string kind, double duration)
    {
        _nextId++;
        Effects.Add(new Effect(_nextId, at.X, at.Y, kind, Now + duration));
    }
}
