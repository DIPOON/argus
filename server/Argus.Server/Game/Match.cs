using System.Numerics;

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
    // 지금까지 진행한 틱 수다. 시각은 모두 이 틱 번호로 저장하고 비교한다. 초는 스냅샷으로 보낼 때만 쓴다.
    public long Tick { get; private set; }
    // 첫 투입이 끝난 틱이다. 작전이 시작되기 전에는 -1이다.
    public long StartedTick { get; private set; } = -1;
    public long? EndedTick { get; private set; }
    public string Phase { get; private set; } = "staging";
    public string? Result { get; private set; }
    public int Pulse { get; private set; }
    // 대원이 방에서 빠질 때마다 늘어난다. 방 관리 쪽이 토큰 정리가 필요한지 알 때 쓴다.
    public int Departures { get; private set; }
    // 진행을 다시 돌릴 수 있도록 바깥 요청을 적는 곳이다. 없으면 적지 않는다.
    // 바깥에서 상태를 바꾸는 공개 메서드는 모두 스스로 적는다. 그런 메서드를 새로 만들면 기록도 함께 넣는다.
    public IMatchRecorder? Recorder { get; set; }
    // 게임 진행에 쓰는 난수는 모두 이 생성기 하나에서 뽑는다. 시드가 같으면 같은 순서로 같은 수가 나온다.
    private readonly SeededRandom _random;
    // 규칙을 JSON으로 바꾼 값이다. 규칙은 만든 뒤 바뀌지 않으므로 한 번만 만들어 상태 요약에 쓴다.
    private readonly byte[] _rulesJson;
    // 다음 증원을 보낼 임무 경과 틱이다. 첫 증원은 10초 뒤다.
    private long _nextSpawnTick = Rules.ToTicks(10);
    private int _nextId = 100;
    // 안내 순번이다. 효과·행동의 순번(_nextId)과 따로 센다.
    private int _nextNoticeId;
    private readonly int[] _flow = new int[BattleMap.Columns * BattleMap.Rows];
    private long _nextFlowTick;
    // 탈출 진행도다. 탈출 지점을 비우면 절반 속도로 줄어들므로 반 틱 단위로 센다.
    private long _extractionHalfTicks;
    // 방어와 측면 판정의 전방 반각(칸 수)이다. 규칙의 라디안을 한 번만 바꿔 둔다.
    private readonly int _guardHalfAngle;
    private static readonly int[] NeighborX = new int[] { 1, -1, 0, 0 };
    private static readonly int[] NeighborY = new int[] { 0, 0, 1, -1 };

    // 코드·규칙·시드가 같은 두 방에 같은 틱마다 같은 입력을 넣으면, 언제 어느 기계에서 돌려도 같은 상태가 된다.
    // 운영 방은 RoomHost가 만든 비밀 시드를 넘긴다. 시드를 알면 적의 행동과 생성 위치를 미리 알 수 있으므로 클라이언트에 보내지 않는다.
    public Match(string code, Rules rules, byte[] seed)
    {
        Code = code;
        Rules = rules;
        _random = new SeededRandom(seed);
        _rulesJson = StateHasher.ToJson(rules);
        _guardHalfAngle = Angle.FromRadians(rules.GuardHalfAngle).Units;
        Facilities.Add(new Facility(1, new Vector2(960, 485)));
        Facilities.Add(new Facility(2, new Vector2(1210, 695)));
        Facilities.Add(new Facility(3, new Vector2(990, 920)));
        Supplies.Add(new Supply(1, BattleMap.Camp + new Vector2(70, 40), false));
        Supplies.Add(new Supply(2, new Vector2(845, 640), true));
        Supplies.Add(new Supply(3, new Vector2(1150, 835), true));
        Supplies.Add(new Supply(4, new Vector2(815, 985), false));
        Supplies.Add(new Supply(5, new Vector2(1080, 740), false));
        // 처음 배치하는 적의 종류다. 첫 작전은 모두 병사이고, 엘리트를 켠 작전은 뒤의 둘이 분쇄자와 철위병이다.
        // 자리는 InitialEnemySpots 한 곳에서 정하고 여기서는 종류만 고른다.
        EnemyDefinition[] kinds = new EnemyDefinition[] { EnemyDefinition.Grunt, EnemyDefinition.Grunt, EnemyDefinition.Grunt };
        if (Rules.AllowEliteEnemies)
        {
            kinds[1] = EnemyDefinition.Breaker;
            kinds[2] = EnemyDefinition.Warden;
        }
        for (int i = 0; i < InitialEnemySpots.Count; i++)
        {
            PlaceInitialEnemy(InitialEnemySpots[i], kinds[i]);
        }
    }

    // 검사와 재현용이다. 정수에서 32바이트 시드를 만든다.
    public Match(string code, Rules rules, int seed) : this(code, rules, SeededRandom.SeedFromNumber(seed))
    {
    }

    // 지금 시각(초)이다. 틱 번호에서 바로 구한다.
    public double Now
    {
        get
        {
            return Rules.ToSeconds(Tick);
        }
    }

    // 첫 투입 이후 임무가 진행된 틱 수다. 임무가 끝나면 더 늘지 않는다.
    public long ElapsedTicks
    {
        get
        {
            if (StartedTick < 0)
            {
                return 0;
            }
            long end = Tick;
            if (EndedTick.HasValue)
            {
                end = EndedTick.Value;
            }
            return end - StartedTick;
        }
    }

    public double Elapsed
    {
        get
        {
            return Rules.ToSeconds(ElapsedTicks);
        }
    }

    // 탈출 진행도(초)다. 스냅샷으로 보낼 때 쓴다.
    public double ExtractionProgress
    {
        get
        {
            return _extractionHalfTicks / (2.0 * Rules.TicksPerSecond);
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

    // 대원 한 명에 대한 값 없는 요청을 지금 틱으로 기록한다. 값이 있는 요청은 각 메서드가 줄을 채워 기록한다.
    // 거절되는 요청도 적는다. 다시 돌릴 때 똑같이 거절되므로 결과가 같다.
    private void Record(string type, Player player)
    {
        if (Recorder == null)
        {
            return;
        }
        MatchRecord record = new MatchRecord(Tick, type);
        record.Player = player.Id;
        Recorder.Write(record);
    }

    public Player? Join(string id, string name)
    {
        if (Recorder != null)
        {
            MatchRecord record = new MatchRecord(Tick, "join");
            record.Player = id;
            record.Name = name;
            Recorder.Write(record);
        }
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
        if (Recorder != null)
        {
            MatchRecord record = new MatchRecord(Tick, "loadout");
            record.Player = player.Id;
            record.Slots = (string[])slots.Clone();
            Recorder.Write(record);
        }
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
        if (Recorder != null)
        {
            MatchRecord record = new MatchRecord(Tick, "deploy");
            record.Player = player.Id;
            record.X = x;
            record.Y = y;
            Recorder.Write(record);
        }
        // 이미 투입 중이면 처음 고른 자리와 도착 시각대로 진행한다. 투입 버튼을 두 번 눌러도 거절 안내가 뜨지 않도록
        // 받아들인 것으로 답한다. 답은 공개된 상태(투입 중인지)에만 달려 있어 숨은 정보를 드러내지 않는다.
        if (Phase != "ended" && player.State == "deploying")
        {
            return true;
        }
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
        player.DeployTick = Tick + Rules.ToTicks(Rules.DeploySeconds);
        player.State = "deploying";
        return true;
    }

    public bool Input(Player player, long sequence, double moveX, double moveY, double aim, int slot)
    {
        if (Recorder != null)
        {
            MatchRecord record = new MatchRecord(Tick, "input");
            record.Player = player.Id;
            record.Sequence = sequence;
            record.MoveX = moveX;
            record.MoveY = moveY;
            record.Aim = aim;
            record.Slot = slot;
            Recorder.Write(record);
        }
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
        player.LastInputTick = Tick;
        player.Move = new Vector2((float)moveX, (float)moveY);
        if (player.Move.LengthSquared() > 1)
        {
            player.Move = Vector2.Normalize(player.Move);
        }
        player.Aim = Angle.FromRadians(aim);
        player.RequestedSlot = slot;
        return true;
    }

    public void Disconnect(Player player)
    {
        Record("disconnect", player);
        player.Connected = false;
        player.DisconnectedTick = Tick;
        player.Move = Vector2.Zero;
        player.RequestedSlot = 0;
    }

    // HTTP로 참가만 하고 아직 WebSocket으로 접속하지 않은 자리로 둔다. 짧은 유예(FirstConnectSeconds) 안에
    // 접속하지 않으면 자리가 풀린다. 방 관리 쪽이 참가 직후에 부른다.
    public void AwaitConnection(Player player)
    {
        Record("await", player);
        player.DisconnectedTick = Tick;
        player.AwaitingFirstConnection = true;
    }

    // WebSocket 접속이 붙었다. 끊겼던 대원이 다시 접속해도 같다. 연결 번호(Connection)는 접속 처리가 정한다.
    public void Connect(Player player)
    {
        Record("connect", player);
        player.Connected = true;
        player.DisconnectedTick = null;
        player.AwaitingFirstConnection = false;
    }

    // 정확히 한 틱(Rules.Step)을 진행한다. 진행할 시간을 받지 않으므로, 같은 입력을 같은 틱에 넣으면
    // 서버가 언제 깨어났는지와 관계없이 같은 결과가 나온다. 서버가 늦어지면 부르는 쪽이 여러 번 부른다.
    // 한 번에 긴 시간을 진행하지 않으므로 짧은 방어 창이나 몸 충돌을 건너뛰지 않는다.
    public void Step()
    {
        Tick++;
        RunTick();
        // 기록할 때는 1초(30틱)마다 상태 요약값을 함께 남긴다. 다시 돌릴 때 같은 길을 가는지 이 값으로 확인한다.
        // 진행 중 예외가 나면 여기까지 오지 않으므로 그 틱의 요약값은 없다. 다시 돌릴 때도 같은 곳에서 예외가 난다.
        if (Recorder != null && Tick % Rules.TicksPerSecond == 0)
        {
            MatchRecord record = new MatchRecord(Tick, "hash");
            record.Hash = StateHash();
            Recorder.Write(record);
        }
    }

    private void RunTick()
    {
        double dt = Rules.Step;
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
        while (ElapsedTicks >= Rules.ToTicks((Pulse + 1) * Rules.PulseSeconds))
        {
            Pulse++;
            // 회차마다 두 배가 된다(2, 4, 8, …). 거듭제곱 함수 대신 곱해서 구해 어느 기계에서나 같다.
            double damage = Rules.PulseDamage;
            for (int doubling = 1; doubling < Pulse; doubling++)
            {
                damage *= 2;
            }
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
        if (ElapsedTicks >= _nextSpawnTick && Enemies.Count < Rules.MaxEnemies)
        {
            // 증원 간격은 시간이 지날수록 줄어들고 2.5초 아래로는 줄지 않는다.
            double interval = Math.Max(2.5, Rules.SpawnInterval - Elapsed / 240);
            _nextSpawnTick = ElapsedTicks + Rules.ToTicks(interval);
            SpawnEnemy();
        }
        if (Tick >= _nextFlowTick)
        {
            _nextFlowTick = Tick + Rules.ToTicks(0.5);
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
        // 탈출 지점에 있으면 한 틱(반 틱 2개)씩 차고, 비우면 반 틱씩 줄어든다.
        if (ObjectivesComplete && HasPlayerAtExtraction())
        {
            _extractionHalfTicks += 2;
        }
        else
        {
            _extractionHalfTicks = Math.Max(0, _extractionHalfTicks - 1);
        }
        if (_extractionHalfTicks >= 2 * Rules.ToTicks(Rules.ExtractionSeconds))
        {
            Finish("success");
        }
    }

    private void RemoveExpiredObjects()
    {
        // 효과는 정한 시간만큼의 틱 동안 보이고, 마감 틱이 되면 지운다.
        for (int i = Effects.Count - 1; i >= 0; i--)
        {
            if (Effects[i].UntilTick <= Tick)
            {
                Effects.RemoveAt(i);
            }
        }
        for (int i = Players.Count - 1; i >= 0; i--)
        {
            Player player = Players[i];
            if (!player.DisconnectedTick.HasValue)
            {
                continue;
            }
            // 한 번도 접속하지 않은 자리는 짧게, 접속했다가 끊긴 대원은 복구할 수 있도록 길게 기다린다.
            double grace = Rules.ReconnectSeconds;
            string notice = $"{player.Name} 연결 복구 시간 만료";
            if (player.AwaitingFirstConnection)
            {
                grace = Rules.FirstConnectSeconds;
                notice = $"{player.Name} 접속하지 않아 자리 해제";
            }
            // 유예 시간을 다 채운 다음 틱에 뺀다.
            if (Tick - player.DisconnectedTick.Value <= Rules.ToTicks(grace))
            {
                continue;
            }
            Note(notice);
            Players.RemoveAt(i);
            Departures++;
            for (int j = 0; j < Supplies.Count; j++)
            {
                Supplies[j].ReadyTicks.Remove(player.Id);
            }
        }
    }

    private void CompleteDeployments()
    {
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State != "deploying" || player.DeployTick > Tick)
            {
                continue;
            }
            // 숨은 적이 투입 지점을 막고 있어도 실패시키지 않고 가장 가까운 빈 자리로 투입한다.
            // 대기로 되돌리면 대기 중인 대원이 투입 결과만으로 안개 속 적의 위치를 알아낼 수 있다.
            Vector2? landing = FindLanding(player.Landing);
            if (!landing.HasValue)
            {
                // 반지름 640 안이 모두 막힌, 맵 대부분이 막힌 경우다. 대기로 되돌리지 않고 다음 틱에 다시 찾는다.
                continue;
            }
            player.Position = landing.Value;
            player.Hp = player.MaxHp;
            player.Mana = 0;
            player.Actions.Clear();
            player.BusyUntilTick = Tick;
            player.RequestedSlot = 0;
            player.State = "alive";
            player.HasDeployed = true;
            player.Move = Vector2.Zero;
            player.LastInputTick = Player.NoInputTick;
            Effect(player.Position, "deploy", 0.6);
            if (Phase == "staging")
            {
                Phase = "active";
                StartedTick = Tick;
                Note("작전 시작 · 보급 상자에서 마나를 채우고 시설 3곳을 파괴하세요");
            }
        }
    }

    // 투입 지점이 비어 있으면 그대로 쓴다. 막혀 있으면 16단위 간격의 고리를 넓혀 가며 가장 가까운 빈 자리를 찾는다.
    // 후보는 원래 투입 지점이 보이는 곳으로 제한한다. 벽 너머로 옮겨지면 옮겨졌다는 사실만으로
    // 보이지 않는 곳에 무언가 있다는 것을 알게 되기 때문이다. 128단위 안에 없으면 캠프, 캠프도 막혔으면 반지름 640까지 넓혀 찾고, 그래도 없으면 null이다.
    private Vector2? FindLanding(Vector2 landing)
    {
        if (CanOccupy(landing, Rules.PlayerRadius, null))
        {
            return landing;
        }
        for (int ring = 1; ring <= 8; ring++)
        {
            float radius = ring * 16;
            for (int i = 0; i < 16; i++)
            {
                Angle angle = Angle.FromUnits(i * Angle.UnitsPerTurn / 16);
                Vector2 candidate = landing + Direction(angle) * radius;
                if (CanOccupy(candidate, Rules.PlayerRadius, null) && Map.LineOfSight(candidate, landing))
                {
                    return candidate;
                }
            }
        }
        if (CanOccupy(BattleMap.Camp, Rules.PlayerRadius, null))
        {
            return BattleMap.Camp;
        }
        // 근처와 캠프가 모두 막힌 드문 경우다. 투입 중 상태에 묶어 두지 않도록 원래 지점에서 점점 넓혀 빈 자리를 찾는다.
        // 여기서는 원래 지점이 보이는지 따지지 않는다. 이만큼 막힌 곳이라면 옮겨진 사실로 새로 알게 되는 것이 거의 없다.
        // 반지름이 커질수록 고리 둘레에 맞춰 후보를 늘려, 후보 사이 간격이 16을 넘지 않게 한다.
        for (int ring = 9; ring <= 40; ring++)
        {
            float radius = ring * 16;
            int samples = Math.Max(16, (int)(Math.Tau * radius / 16));
            for (int i = 0; i < samples; i++)
            {
                Angle angle = Angle.FromUnits(Fixed.DivideRounded((long)i * Angle.UnitsPerTurn, samples));
                Vector2 candidate = landing + Direction(angle) * radius;
                if (CanOccupy(candidate, Rules.PlayerRadius, null))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    private void UpdatePlayer(Player player, double dt)
    {
        // 입력이 0.35초(11틱) 동안 없으면 멈춘다. 끊긴 연결이 마지막 입력으로 계속 움직이지 않게 한다.
        if (Tick - player.LastInputTick >= Rules.ToTicks(0.35))
        {
            player.Move = Vector2.Zero;
            player.RequestedSlot = 0;
        }
        player.Position = MoveBody(player.Position, player.Move * (float)(Rules.PlayerSpeed * dt), Rules.PlayerRadius, null);
        for (int i = player.Actions.Count - 1; i >= 0; i--)
        {
            if (player.Actions[i].EndsTick <= Tick)
            {
                player.Actions.RemoveAt(i);
            }
        }
        if (player.RequestedSlot > 0 && Tick >= player.BusyUntilTick)
        {
            SkillDefinition skill = Rules.Skill(player.Slots[player.RequestedSlot - 1]);
            if (player.Mana >= skill.Mana)
            {
                player.Mana -= skill.Mana;
                _nextId++;
                PlayerAction action = new PlayerAction(_nextId, skill, player.RequestedSlot, Tick);
                player.Actions.Add(action);
                player.BusyUntilTick = action.EndsTick;
            }
        }
        UpdateSupplies(player);
    }

    private void UpdateSupplies(Player player)
    {
        for (int i = 0; i < Supplies.Count; i++)
        {
            Supply supply = Supplies[i];
            if (supply.Collected || supply.ReadyTick(player) > Tick ||
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
            supply.ReadyTicks[player.Id] = Tick + Rules.ToTicks(Rules.SupplyCooldown);
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
        EndedTick = Tick;
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

    // 안내는 모든 대원에게 그대로 가므로 공개된 사건에만 낸다.
    public void Note(string text)
    {
        _nextNoticeId++;
        Notices.Add(new Notice(_nextNoticeId, Tick, text));
        if (Notices.Count > 6)
        {
            Notices.RemoveAt(0);
        }
    }

    private void Effect(Vector2 at, string kind, double duration)
    {
        _nextId++;
        Effects.Add(new Effect(_nextId, at.X, at.Y, kind, Tick + Rules.ToTicks(duration)));
    }
}
