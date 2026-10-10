using System.Numerics;
using System.Security.Cryptography;

namespace Argus.Server.Game;

// 방 하나의 상태와 진행 순서다. 전투 판정과 몸 충돌은 같은 partial class의 별도 파일에 있다.
public sealed partial class Match
{
    public string Code { get; }
    public Rules Rules { get; }
    public BattleMap Map { get; } = new BattleMap();
    public List<Player> Players { get; } = new List<Player>();
    public List<Enemy> Enemies { get; } = new List<Enemy>();
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
    private double _nextSpawn = 10;
    private int _nextId = 100;
    private readonly int[] _flow = new int[BattleMap.Columns * BattleMap.Rows];
    private double _nextFlow;
    private static readonly int[] NeighborX = new int[] { 1, -1, 0, 0 };
    private static readonly int[] NeighborY = new int[] { 0, 0, 1, -1 };

    public Match(string code, Rules? rules = null, int? seed = null)
    {
        Code = code;
        Rules = new Rules();
        if (rules != null)
        {
            Rules = rules;
        }
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
        Supplies.Add(new Supply(5, new Vector2(1080, 740), false));
        AddEnemy(new Vector2(610, 730), EnemyDefinition.Grunt);
        if (Rules.AllowEliteEnemies)
        {
            AddEnemy(new Vector2(970, 570), EnemyDefinition.Breaker);
            AddEnemy(new Vector2(1260, 770), EnemyDefinition.Warden);
        }
        else
        {
            AddEnemy(new Vector2(970, 570), EnemyDefinition.Grunt);
            AddEnemy(new Vector2(1260, 770), EnemyDefinition.Grunt);
        }
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
                if (Facilities[i].Hp > 0)
                {
                    return false;
                }
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
        player.MaxHp = Rules.PlayerHealth;
        player.Hp = player.MaxHp;
        player.MaxMana = Rules.MaxMana;
        Players.Add(player);
        Note($"{name} 합류");
        return player;
    }

    public bool SetLoadout(Player player, string[] slots)
    {
        if (Phase == "ended" || player.HasDeployed || player.State != "waiting" || !Rules.ValidSlots(slots))
        {
            return false;
        }
        // 호출자가 원본 배열을 나중에 바꾸더라도 서버의 장비는 바뀌지 않는다.
        player.Slots = (string[])slots.Clone();
        return true;
    }

    public bool Deploy(Player player, double x, double y)
    {
        if (Phase == "ended" || player.State != "waiting" || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return false;
        }
        Vector2 target = new Vector2((float)x, (float)y);
        // 위치 선택의 즉시 응답으로 시야 밖 적을 탐색할 수 없도록 공개된 벽만 검사한다.
        // 적과 겹치는지는 5초 뒤 실제 투입 직전에 검사한다.
        if (!Map.CanStand(target, Rules.PlayerRadius))
        {
            return false;
        }
        player.Landing = target;
        player.DeployAt = Now + Rules.DeploySeconds;
        player.State = "deploying";
        return true;
    }

    public bool Input(Player player, long sequence, double moveX, double moveY, double aim, int slot)
    {
        if (Phase == "ended" || player.State != "alive" || slot < 0 || slot > 4)
        {
            return false;
        }
        if (sequence <= player.LastSequence || sequence < 0 || sequence > 9_000_000_000_000)
        {
            return false;
        }
        if (!double.IsFinite(moveX) || !double.IsFinite(moveY) || !double.IsFinite(aim) ||
            Math.Abs(moveX) > 1.01 || Math.Abs(moveY) > 1.01)
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
        player.RequestedSlot = slot;
        return true;
    }

    public void Disconnect(Player player)
    {
        player.Connected = false;
        player.DisconnectedAt = Now;
        player.Move = Vector2.Zero;
        player.RequestedSlot = 0;
    }

    public void Step(double dt)
    {
        if (!double.IsFinite(dt) || dt <= 0)
        {
            return;
        }
        // 일시적으로 서버 틱이 늦어져도 짧은 방어 창이나 몸 충돌을 건너뛰지 않는다.
        while (dt > 0.0000001)
        {
            double step = Math.Min(dt, Rules.Step);
            StepOnce(step);
            dt -= step;
        }
    }

    private void StepOnce(double dt)
    {
        Now += dt;
        RemoveExpiredObjects();
        if (Phase == "ended" || CheckWipe())
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
            double damage = Rules.PulseDamage * Math.Pow(2, Pulse - 1);
            for (int i = 0; i < Players.Count; i++)
            {
                Hurt(Players[i], damage);
            }
            Note($"전역 충격 {Pulse}회 · {damage:0} 피해");
            if (CheckWipe())
            {
                return;
            }
        }
        for (int i = 0; i < Players.Count; i++)
        {
            if (Players[i].State == "alive")
            {
                UpdatePlayer(Players[i], dt);
            }
        }
        if (Elapsed >= _nextSpawn && Enemies.Count < Rules.MaxEnemies)
        {
            _nextSpawn = Elapsed + Math.Max(2.5, Rules.SpawnInterval - Elapsed / 240);
            SpawnEnemy();
        }
        if (Now >= _nextFlow)
        {
            _nextFlow = Now + 0.5;
            BuildFlow();
        }
        UpdateEnemies(dt);
        ResolveCombat();
        for (int i = Enemies.Count - 1; i >= 0; i--)
        {
            if (Enemies[i].Hp <= 0)
            {
                Enemies.RemoveAt(i);
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
        for (int i = Players.Count - 1; i >= 0; i--)
        {
            Player player = Players[i];
            if (player.DisconnectedAt.HasValue && Now - player.DisconnectedAt.Value > Rules.ReconnectSeconds)
            {
                Note($"{player.Name} 연결 복구 시간 만료");
                Players.RemoveAt(i);
                for (int j = 0; j < Supplies.Count; j++)
                {
                    Supplies[j].ReadyAt.Remove(player.Id);
                }
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
            if (!CanOccupy(player.Landing, Rules.PlayerRadius, null))
            {
                player.State = "waiting";
                Note($"{player.Name} 투입 지점이 막혔습니다. 다른 위치를 선택하세요.");
                continue;
            }
            player.Position = player.Landing;
            player.Hp = player.MaxHp;
            player.Mana = 0;
            player.Actions.Clear();
            player.BusyUntil = Now;
            player.RequestedSlot = 0;
            player.State = "alive";
            player.HasDeployed = true;
            player.Move = Vector2.Zero;
            player.LastInputAt = -100;
            Effect(player.Position, "deploy", 0.6);
            if (Phase == "staging")
            {
                Phase = "active";
                StartedAt = Now;
                Note("작전 시작 · 보급 상자에서 마나를 채우고 시설 3곳을 파괴하세요");
            }
        }
    }

    private void UpdatePlayer(Player player, double dt)
    {
        if (Now - player.LastInputAt > 0.35)
        {
            player.Move = Vector2.Zero;
            player.RequestedSlot = 0;
        }
        player.Position = MoveBody(player.Position, player.Move * (float)(Rules.PlayerSpeed * dt), Rules.PlayerRadius, null);
        for (int i = player.Actions.Count - 1; i >= 0; i--)
        {
            if (player.Actions[i].EndsAt <= Now)
            {
                player.Actions.RemoveAt(i);
            }
        }
        if (player.RequestedSlot > 0 && Now >= player.BusyUntil)
        {
            SkillDefinition skill = Rules.Skill(player.Slots[player.RequestedSlot - 1]);
            if (player.Mana >= skill.Mana)
            {
                player.Mana -= skill.Mana;
                _nextId++;
                PlayerAction action = new PlayerAction(_nextId, skill, player.RequestedSlot, Now);
                player.Actions.Add(action);
                player.BusyUntil = action.EndsAt;
            }
        }
        UpdateSupplies(player);
    }

    private void UpdateSupplies(Player player)
    {
        for (int i = 0; i < Supplies.Count; i++)
        {
            Supply supply = Supplies[i];
            if (supply.Collected || supply.AvailableAt(player) > Now ||
                Vector2.Distance(player.Position, supply.Position) > 35 || !Map.LineOfSight(player.Position, supply.Position))
            {
                continue;
            }
            if (supply.Objective)
            {
                supply.Collected = true;
                Note("부목표 보급품 회수");
            }
            else if (player.Hp >= player.MaxHp && player.Mana >= player.MaxMana)
            {
                continue;
            }
            player.Hp = player.MaxHp;
            player.Mana = player.MaxMana;
            // 난입자와 겹쳐 선 동료가 다른 사람의 보급 사용 때문에 기다리지 않게 한다.
            supply.ReadyAt[player.Id] = Now + Rules.SupplyCooldown;
            Effect(player.Position, "heal", 0.5);
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
            if (Players[i].State == "alive" && Vector2.Distance(Players[i].Position, BattleMap.Extraction) < 80)
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

    public void Hurt(Player player, double damage)
    {
        if (player.State != "alive" || damage <= 0)
        {
            return;
        }
        player.Hp = Math.Max(0, player.Hp - damage);
        Effect(player.Position, "hurt", 0.4);
        if (player.Hp > 0)
        {
            return;
        }
        player.State = "waiting";
        player.Deaths++;
        player.RequestedSlot = 0;
        player.Actions.Clear();
        player.Move = Vector2.Zero;
        Note($"{player.Name} 사망 · 증원 위치를 선택하세요");
    }

    private void DamageEnemy(Enemy enemy, double damage, Player owner, string effect = "hit")
    {
        if (enemy.Hp <= 0)
        {
            return;
        }
        enemy.Hp = Math.Max(0, enemy.Hp - damage);
        if (enemy.Hp == 0)
        {
            owner.Kills++;
        }
        Effect(enemy.Position, effect, 0.65);
    }

    private void DamageFacility(Facility facility)
    {
        facility.Hp = Math.Max(0, facility.Hp - 1);
        Effect(facility.Position, "hit", 0.4);
        if (facility.Hp == 0)
        {
            Note("적 시설 파괴");
            if (ObjectivesComplete)
            {
                Note("주목표 완료 · 탈출 지점을 확보하세요");
            }
        }
    }

    public bool Visible(Vector2 point)
    {
        if (Vector2.DistanceSquared(BattleMap.Camp, point) <= BattleMap.CampVision * BattleMap.CampVision && Map.LineOfSight(BattleMap.Camp, point))
        {
            return true;
        }
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State == "alive" && Vector2.DistanceSquared(player.Position, point) <= Rules.Vision * Rules.Vision &&
                Map.LineOfSight(player.Position, point))
            {
                return true;
            }
        }
        return false;
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
