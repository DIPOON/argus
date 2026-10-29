namespace Argus.Server.Game;

// 방 하나의 상태와 진행 순서다. 전투 판정과 몸 충돌은 같은 partial class의 별도 파일에 있다.
public sealed partial class Match
{
    public string Code { get; }
    public Rules Rules { get; }
    public BattleMap Map { get; }
    // 이 방의 맵 크기와 임무 배치다. 진행 중에는 바뀌지 않는다.
    public MissionLayout Layout { get; }
    public List<Player> Players { get; } = new List<Player>();
    public List<Enemy> Enemies { get; } = new List<Enemy>();
    public List<Facility> Facilities { get; } = new List<Facility>();
    public List<Supply> Supplies { get; } = new List<Supply>();
    public List<Effect> Effects { get; } = new List<Effect>();
    public List<Notice> Notices { get; } = new List<Notice>();
    // 분대가 본 칸이다(칸 번호마다 참·거짓). 처음에는 캠프 둘레만 보이고, 캠프와 살아 있는 대원의 시야로 넓어진다.
    // 칸의 어느 부분이든 보이면 본 칸이다. 한 번 본 칸은 계속 안다. 스냅샷은 본 칸의 벽과 본 칸에 있는 시설·보급·탈출 지점만
    // 보내고, 투입도 본 칸에만 받는다.
    private readonly bool[] _explored;
    // 스냅샷으로 보내는 벽 배열이다. 본 칸은 실제 값, 아직 보지 못한 칸은 모름(BattleMap.UnknownTile)이다.
    private readonly byte[] _publicTiles;
    // 공개 벽 배열이 바뀐 기록이다. [칸 번호, 값, 칸 번호, 값, …] 순서로 쌓는다. 바뀐 수가 벽 배열의 판 번호(WallsRevision)이고,
    // 받은 판이 있는 연결에는 그 뒤로 바뀐 칸만 보낸다. 칸마다 본 순간과 본 벽이 부서질 때만 쌓이므로 칸 수의 두어 배를 넘지 않는다.
    private readonly List<int> _publicChanges = new List<int>();
    // 본 칸을 마지막으로 맞춘 때의 벽 판 번호(BattleMap.Revision)다. 벽이 부서지면 그 뒤가 새로 보이므로 다시 둘러본다.
    private int _exploredMapRevision = -1;
    // 본 칸을 넓힐 때 새로 본 칸을 잠깐 모으는 곳이다. 상태가 아니라 계산 중에만 쓴다.
    private readonly List<int> _revealed = new List<int>();
    // 지금까지 진행한 틱 수다. 시각은 모두 이 틱 번호로 저장하고 비교한다. 초는 스냅샷으로 보낼 때만 쓴다.
    public long Tick { get; private set; }
    // 첫 투입이 끝난 틱이다. 작전이 시작되기 전에는 -1이다.
    public long StartedTick { get; private set; } = -1;
    public long? EndedTick { get; private set; }
    public string Phase { get; private set; } = "staging";
    public string? Result { get; private set; }
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
    private readonly int[] _flow;
    private long _nextFlowTick;
    // 탈출 진행도다. 탈출 지점을 비우면 절반 속도로 줄어들므로 반 틱 단위로 센다.
    private long _extractionHalfTicks;
    // 방어와 측면 판정의 전방 반각이다. 반각은 방향이 아니라 크기이므로 한 바퀴로 감지 않는다(Angle.SpanUnits).
    // 음수 반각은 어느 방향도 막지 않고, 반 바퀴 이상이면 모든 방향을 막는다. 그 사이는 사잇각 대신 내적으로 판정하므로 반각의 코사인을 한 번 구해 둔다.
    private readonly bool _guardsAnyDirection;
    private readonly bool _guardsEveryDirection;
    private readonly Fixed _guardCosine;
    // 대원이 한 틱에 움직이는 거리다. 1/30을 곱하지 않고 30으로 나눈다. 1/30을 고정소수점으로 반올림하면 0.3% 어긋난다.
    private readonly Fixed _playerStep;
    // 시야 반경의 제곱이다. 거리 비교는 제곱근을 구하지 않고 제곱끼리 한다.
    private readonly Fixed _visionSquared;
    // 캠프 시야 반경의 제곱이다. 캠프 시야는 규칙이 아니라 맵에 정해져 있어 한 번만 구한다.
    private static readonly Fixed CampVisionSquared = Fixed.FromInt(BattleMap.CampVision * BattleMap.CampVision);
    // 입력이 이만큼(0.35초, 11틱) 없으면 대원을 멈춘다. 끊긴 연결이 마지막 입력으로 계속 움직이지 않게 한다.
    // 틱을 올린 뒤 비교하므로, 입력 하나는 받은 다음 틱부터 최대 10틱(InputTimeoutTicks - 1) 쓰인다. 클라이언트 예측도 같은 한도를 쓴다.
    public static readonly long InputTimeoutTicks = Rules.ToTicks(0.35);
    // 투입 위치로 받을 수 있는 가장 큰 좌표 크기다. 맵(2000 미만)보다 훨씬 큰 값은 고정소수점으로 바꾸기 전에 거른다.
    private const double MaxCoordinate = 1_000_000;
    // 아래 세 기준은 뜻이 다르다. 앞의 둘은 값이 같지만(Raw 4, 약 0.001) 하나는 거리의 제곱에 두는 여유이고,
    // 하나는 길이의 제곱으로 본 "방향 없음"의 기준이다. 셋째는 길이(제곱이 아님)의 기준이다.
    // 적이 행동을 고르는 거리다. 쫓는 대원이 이 거리 안에 있고 사선이 열려 있으면 행동한다.
    public const int EngageDistance = 75;
    // 몸끼리 딱 붙은 자리를 겹친 것으로 보지 않게 하는, 거리의 제곱에 두는 여유다. 거리의 제곱은 성분마다 반올림하므로 조금 작게 나올 수 있다.
    private static readonly Fixed TouchTolerance = Fixed.FromRaw(4);
    // 길이의 제곱이 이보다 작은 벡터는 방향이 없는 것으로 본다. 거의 0인 벡터로 적의 이동 방향을 정하지 않게 한다.
    // 이 기준을 넘는 벡터는 길이가 Raw 111(약 0.027)보다 길어, 정규화한 결과의 길이가 1에서 0.5% 넘게 벗어나지 않는다.
    private static readonly Fixed TinyLengthSquared = Fixed.FromRaw(4);
    // 적끼리 밀어내는 방향을 구하는 가장 짧은 거리다. 길이는 Raw 단위로 반올림하므로, 아주 짧은 벡터를 그 길이로 나누면
    // 길이가 1에서 크게 벗어난다(Raw (2, 4)는 길이 4로 나뉘어 길이 1.118이 된다). 1 이상이면 벗어나는 정도가 0.03% 아래다.
    // 살아 있는 적끼리는 몸 충돌(CanOccupy) 때문에 반지름의 합보다 가까워지지 않으므로, 정상 진행에서는 이 기준에 걸리지 않는다.
    private static readonly Fixed SeparationMinimum = Fixed.One;
    private static readonly int[] NeighborX = new int[] { 1, -1, 0, 0 };
    private static readonly int[] NeighborY = new int[] { 0, 0, 1, -1 };

    // 코드·규칙·시드가 같은 두 방에 같은 틱마다 같은 입력을 넣으면, 언제 어느 기계에서 돌려도 같은 상태가 된다.
    // 운영 방은 RoomHost가 만든 비밀 시드를 넘긴다. 시드를 알면 적의 행동과 생성 위치를 미리 알 수 있으므로 클라이언트에 보내지 않는다.
    public Match(string code, Rules rules, byte[] seed, MissionLayout layout)
    {
        Code = code;
        Rules = rules;
        Layout = layout;
        Map = new BattleMap(layout);
        _explored = new bool[Map.Columns * Map.Rows];
        _publicTiles = new byte[Map.Columns * Map.Rows];
        for (int i = 0; i < _publicTiles.Length; i++)
        {
            _publicTiles[i] = BattleMap.UnknownTile;
        }
        _flow = new int[Map.Columns * Map.Rows];
        _random = new SeededRandom(seed);
        _rulesJson = StateHasher.ToJson(rules);
        int guardHalfAngle = Angle.SpanUnits(rules.GuardHalfAngle);
        _guardsAnyDirection = guardHalfAngle >= 0;
        _guardsEveryDirection = guardHalfAngle >= Angle.HalfTurn;
        if (_guardsAnyDirection)
        {
            _guardCosine = Angle.FromUnits(guardHalfAngle).Cos();
        }
        _playerStep = Fixed.FromDouble(rules.PlayerSpeed) / Rules.TicksPerSecond;
        VisionRange = Fixed.FromDouble(rules.Vision);
        _visionSquared = VisionRange * VisionRange;
        // 시설과 보급은 배치의 순서대로 1번부터 번호를 붙인다.
        for (int i = 0; i < layout.Facilities.Count; i++)
        {
            Facilities.Add(new Facility(i + 1, layout.Facilities[i]));
        }
        // 상자마다 정해진 횟수만 쓴다. 캠프 상자와 들판 상자는 분대가 함께 쓰고, 부목표 상자는 한 번 회수하면 사라진다.
        for (int i = 0; i < layout.Supplies.Count; i++)
        {
            SupplySpot spot = layout.Supplies[i];
            int uses = 1;
            if (spot.Kind == SupplyKind.Camp)
            {
                uses = rules.CampSupplyUses;
            }
            else if (spot.Kind == SupplyKind.Field)
            {
                uses = rules.FieldSupplyUses;
            }
            Supplies.Add(new Supply(i + 1, spot.Position, spot.Kind, uses));
        }
        // 처음 배치하는 적이다. 자리와 종류는 배치(Layout.InitialEnemySpots)가 정한다.
        // 엘리트를 끈 작전에서는 엘리트 자리에도 병사를 둔다.
        for (int i = 0; i < layout.InitialEnemySpots.Count; i++)
        {
            EnemySpot spot = layout.InitialEnemySpots[i];
            EnemyDefinition kind = spot.Kind;
            if (!Rules.AllowEliteEnemies && kind.IsElite)
            {
                kind = EnemyDefinition.Grunt;
            }
            PlaceInitialEnemy(spot.Position, kind);
        }
        // 처음에는 캠프의 시야만큼 보인다. 대원이 투입하기 전에는 여기에만 투입할 수 있다.
        Explore();
    }

    // 손으로 만든 고정 맵의 방이다. 검사와 재현에 쓴다.
    public Match(string code, Rules rules, byte[] seed) : this(code, rules, seed, MissionLayout.CreateHandmade())
    {
    }

    // 검사와 재현용이다. 정수에서 32바이트 시드를 만든다.
    public Match(string code, Rules rules, int seed) : this(code, rules, SeededRandom.SeedFromNumber(seed))
    {
    }

    // 살아 있는 대원의 시야 반경이다. 스냅샷의 시야 다각형도 이 값으로 그린다.
    public Fixed VisionRange { get; }

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
        player.Position = Layout.Camp;
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
        // 맵에서 아주 먼 값은 고정소수점으로 바꿀 수 없으므로 먼저 거른다. 어차피 맵 밖이라 아래 벽 검사에서도 거절된다.
        if (Math.Abs(x) > MaxCoordinate || Math.Abs(y) > MaxCoordinate)
        {
            return false;
        }
        FixedVector target = FixedVector.FromDouble(x, y);
        // 분대가 본 칸에만 투입할 수 있고, 몸이 들어가는지도 본 칸의 벽만으로 본다. 안 본 칸의 벽이나 시야 밖 적에 따라
        // 답이 달라지면 투입 요청으로 숨은 곳을 엿볼 수 있기 때문이다. 적이나 안 본 벽과 겹치는지는 5초 뒤 실제 투입 직전에
        // 검사하고, 겹치면 가까운 빈 자리로 옮긴다(FindLanding).
        if (!Explored(target) || !Map.CanStand(target, Rules.PlayerRadius, _explored))
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
        // 이동 방향은 길이 1까지만 쓴다. 대각선 입력(1, 1)이 한 방향 입력보다 빠르지 않게 한다.
        player.Move = FixedVector.FromDouble(moveX, moveY);
        if (player.Move.LengthSquared() > Fixed.One)
        {
            player.Move = player.Move.Normalize();
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
        player.Move = FixedVector.Zero;
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
        // 그 틱의 마지막 상태(착지한 대원, 움직인 자리, 부서진 벽, 끝난 작전)로 본 칸을 맞춘다. 틱 진행 순서 안의 어디에서
        // 무엇이 바뀌어도, 스냅샷의 시야와 본 칸이 같은 순간의 것이 된다.
        Explore();
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
        for (int i = 0; i < Players.Count; i++)
        {
            if (Players[i].State == "alive")
            {
                UpdatePlayer(Players[i]);
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
        UpdateEnemies();
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
            FixedVector? landing = FindLanding(player.Landing);
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
            player.Move = FixedVector.Zero;
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
    private FixedVector? FindLanding(FixedVector landing)
    {
        if (CanOccupy(landing, Rules.PlayerRadius, null))
        {
            return landing;
        }
        for (int ring = 1; ring <= 8; ring++)
        {
            int radius = ring * 16;
            for (int i = 0; i < 16; i++)
            {
                Angle angle = Angle.FromFraction(i, 16);
                FixedVector candidate = landing + angle.Direction() * radius;
                if (CanOccupy(candidate, Rules.PlayerRadius, null) && Map.LineOfSight(candidate, landing))
                {
                    return candidate;
                }
            }
        }
        if (CanOccupy(Layout.Camp, Rules.PlayerRadius, null))
        {
            return Layout.Camp;
        }
        // 근처와 캠프가 모두 막힌 드문 경우다. 투입 중 상태에 묶어 두지 않도록 원래 지점에서 점점 넓혀 빈 자리를 찾는다.
        // 여기서는 원래 지점이 보이는지 따지지 않는다. 이만큼 막힌 곳이라면 옮겨진 사실로 새로 알게 되는 것이 거의 없다.
        // 반지름이 커질수록 고리 둘레에 맞춰 후보를 늘려, 후보 사이 간격이 16을 넘지 않게 한다.
        for (int ring = 9; ring <= 40; ring++)
        {
            int radius = ring * 16;
            // 둘레(2π × 반지름)를 16씩 나눈 수다. 반지름이 16 × ring이므로 2π × ring을 내린 값과 같다.
            int samples = Math.Max(16, (int)(Math.Tau * ring));
            for (int i = 0; i < samples; i++)
            {
                Angle angle = Angle.FromFraction(i, samples);
                FixedVector candidate = landing + angle.Direction() * radius;
                if (CanOccupy(candidate, Rules.PlayerRadius, null))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    private void UpdatePlayer(Player player)
    {
        // 입력이 0.35초(11틱) 동안 없으면 멈춘다(InputTimeoutTicks).
        if (Tick - player.LastInputTick >= InputTimeoutTicks)
        {
            player.Move = FixedVector.Zero;
            player.RequestedSlot = 0;
        }
        player.Position = MoveBody(player.Position, player.Move * _playerStep, Rules.PlayerRadius, null);
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
            // 거리는 제곱끼리 비교한다(35² = 1225). 다 쓴 상자(남은 횟수 0)는 더 채우지 않는다.
            if (supply.Collected || supply.UsesLeft <= 0 || supply.ReadyTick(player) > Tick ||
                FixedVector.DistanceSquared(player.Position, supply.Position) > Fixed.FromInt(35 * 35) ||
                !Map.LineOfSight(player.Position, supply.Position))
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
            // 한 번 썼다. 쓴 사람만 채우므로 곁에 선 동료는 따로 한 번을 쓴다.
            supply.UsesLeft--;
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
            if (Players[i].State == "alive" &&
                FixedVector.DistanceSquared(Players[i].Position, Layout.Extraction) < Fixed.FromInt(80 * 80))
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
        player.Move = FixedVector.Zero;
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
            // 부순 시설 자리에 보급 상자가 생긴다. 시설 파괴는 공개 정보이므로 보급 번호는 이어 붙인다.
            Supplies.Add(new Supply(Supplies.Count + 1, facility.Position, SupplyKind.Field, Rules.FacilitySupplyUses));
            Note("적 시설 파괴 · 그 자리에 보급 상자");
            if (ObjectivesComplete)
            {
                Note("주목표 완료 · 탈출 지점을 확보하세요");
            }
        }
    }

    // 본 칸을 넓힐 때 쏘는 광선의 방향 수다. 시야 다각형(96방향)의 4배라 다각형의 광선이 모두 이 안에 들고, 대원 시야 끝(410)에서도
    // 광선 사이가 약 6.7이라 칸(48)이 광선 사이로 빠지지 않는다.
    private const int ExploreDirectionCount = 384;
    private static readonly FixedVector[] ExploreDirections = CreateExploreDirections();
    // 대원이 마지막으로 둘러본 자리에서 반 칸(24) 넘게 움직이면 다시 둘러본다.
    private static readonly Fixed ExploreStepSquared = Fixed.FromInt((BattleMap.Cell / 2) * (BattleMap.Cell / 2));
    // 움직이는 동안은 마지막으로 둘러본 지 이만큼(3틱, 0.1초, 방송 한 번) 지나면 반 칸을 못 갔어도 다시 둘러본다.
    // 반 칸보다 덜 움직여도 모퉁이나 문 옆에서는 가려 있던 방이 통째로 보일 수 있다(PR #18 리뷰). 반 칸 안에서 오가며 싸워도
    // 본 칸이 0.1초 넘게 뒤처지지 않는다.
    public const long ExploreIntervalTicks = 3;
    // 벽 면 위에서 확인하는 자리(칸 왼쪽·위에서 잰 거리)다. 양 끝 가까이와 가운데, 그 사이를 본다.
    // 점은 면에서 벽 안쪽으로 가장 작은 단위(Raw 1)만큼 들어간 곳이다. 그 점까지 가는 선분이 처음 닿는 벽이 그 벽이면 면이 보인다.
    // 안쪽으로 많이 들어가면 아주 비스듬한 선분이 옆 칸의 면에서 먼저 막혀, 보이는 면을 못 본 것으로 친다.
    private static readonly int[] FaceOffsets = new int[] { 1, 12, BattleMap.Cell / 2, 36, BattleMap.Cell - 1 };

    private static FixedVector[] CreateExploreDirections()
    {
        FixedVector[] directions = new FixedVector[ExploreDirectionCount];
        for (int i = 0; i < directions.Length; i++)
        {
            directions[i] = Angle.FromFraction(i, directions.Length).Direction();
        }
        return directions;
    }

    // 분대가 본 칸을 넓힌다. 만들 때와 틱마다 진행이 끝난 뒤에 한 번 부른다.
    // 광선은 필요할 때만 쏜다. 캠프는 벽이 바뀌었을 때, 살아 있는 대원은 LooksAgain이 정한 때다.
    // 제자리에 선 대원 둘레의 볼 수 없는 칸에 틱마다 광선을 다시 쏘지 않는다.
    private void Explore()
    {
        bool wallsChanged = Map.Revision != _exploredMapRevision;
        _exploredMapRevision = Map.Revision;
        if (wallsChanged)
        {
            // 본 칸에서 부서진 벽을 공개 벽 배열에 옮기고, 캠프에서도 새로 보이는 곳이 있는지 본다.
            for (int i = 0; i < _publicTiles.Length; i++)
            {
                if (_explored[i])
                {
                    PublishTile(i);
                }
            }
            ExploreFrom(Layout.Camp, Fixed.FromInt(BattleMap.CampVision));
        }
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State != "alive")
            {
                continue;
            }
            if (!LooksAgain(player, wallsChanged))
            {
                continue;
            }
            player.ExploreOrigin = player.Position;
            player.HasExploreOrigin = true;
            player.ExploreTick = Tick;
            ExploreFrom(player.Position, VisionRange);
        }
    }

    // 살아 있는 대원이 이번 틱에 다시 둘러봐야 하는지다. 처음이거나 벽이 바뀌었으면 둘러본다. 마지막으로 둘러본 자리에 그대로
    // 서 있으면 둘러보지 않는다. 그 밖에는 반 칸 넘게 움직였거나, 멈췄거나(이동 입력이 없음), 마지막으로 둘러본 지 0.1초가
    // 지났으면 둘러본다. 멈춘 자리는 바로 둘러보므로, 문 앞에 서서 안을 들여다보면 그 틱에 방 안이 본 칸이 된다.
    private bool LooksAgain(Player player, bool wallsChanged)
    {
        if (!player.HasExploreOrigin || wallsChanged)
        {
            return true;
        }
        if (player.Position == player.ExploreOrigin)
        {
            return false;
        }
        if (FixedVector.DistanceSquared(player.Position, player.ExploreOrigin) >= ExploreStepSquared)
        {
            return true;
        }
        if (player.Move == FixedVector.Zero)
        {
            return true;
        }
        return Tick - player.ExploreTick >= ExploreIntervalTicks;
    }

    // eye에서 range까지 여러 방향으로 광선을 쏴, 광선이 지나는 칸과 처음 닿는 벽 칸을 본 칸으로 적는다. 칸의 어느 부분이든 보이면
    // 본 칸이 되므로 시야 끝에 걸친 칸도 본 칸이다. 시야 다각형도 같은 방식(광선)으로 그린다.
    // 아주 비스듬히 보이는 벽 면은 광선 사이가 벌어져 건너뛸 수 있어 RevealFaces로 따로 확인한다.
    private void ExploreFrom(FixedVector eye, Fixed range)
    {
        _revealed.Clear();
        for (int i = 0; i < ExploreDirections.Length; i++)
        {
            Map.Reveal(eye, eye + ExploreDirections[i] * range, _explored, _revealed);
        }
        RevealFaces(eye, range);
        for (int i = 0; i < _revealed.Count; i++)
        {
            PublishTile(_revealed[i]);
        }
    }

    // 본 칸과 맞닿은 아직 못 본 벽 칸마다, 맞닿은 면 위의 점(양 끝 가까이와 가운데, 벽 안쪽으로 1)이 eye에서 보이는지 본다.
    // 그 점까지 가는 선분이 처음 닿는 벽이 바로 그 칸이고 점이 range 안이면 그 면이 보이므로 본 칸으로 적는다.
    // 벽을 따라 걸을 때 멀리 비스듬히 보이는 벽 줄이 광선 사이로 빠지는 것을 메운다.
    private void RevealFaces(FixedVector eye, Fixed range)
    {
        int minX = Math.Max(0, (eye.X - range).FloorDivide(BattleMap.Cell));
        int maxX = Math.Min(Map.Columns - 1, (eye.X + range).FloorDivide(BattleMap.Cell));
        int minY = Math.Max(0, (eye.Y - range).FloorDivide(BattleMap.Cell));
        int maxY = Math.Min(Map.Rows - 1, (eye.Y + range).FloorDivide(BattleMap.Cell));
        Fixed rangeSquared = range * range;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int index = y * Map.Columns + x;
                if (_explored[index] || !Map.Solid(x, y))
                {
                    continue;
                }
                if (FaceSeen(eye, rangeSquared, x, y, -1, 0) || FaceSeen(eye, rangeSquared, x, y, 1, 0) ||
                    FaceSeen(eye, rangeSquared, x, y, 0, -1) || FaceSeen(eye, rangeSquared, x, y, 0, 1))
                {
                    _explored[index] = true;
                    _revealed.Add(index);
                }
            }
        }
    }

    // 벽 칸 (x, y)가 (stepX, stepY) 쪽 이웃과 맞닿은 면이 eye에서 보이는지다. 이웃이 이미 본 빈 땅일 때만 본다.
    private bool FaceSeen(FixedVector eye, Fixed rangeSquared, int x, int y, int stepX, int stepY)
    {
        int nx = x + stepX;
        int ny = y + stepY;
        if (nx < 0 || ny < 0 || nx >= Map.Columns || ny >= Map.Rows || Map.Solid(nx, ny) || !_explored[ny * Map.Columns + nx])
        {
            return false;
        }
        int index = y * Map.Columns + x;
        long cell = BattleMap.Cell * Fixed.Scale;
        for (int i = 0; i < FaceOffsets.Length; i++)
        {
            // 면이 세로(이웃이 왼쪽·오른쪽)면 x를 면 바로 안쪽에, y를 면을 따라 둔다. 가로면 그 반대다. 좌표는 Raw다.
            long px;
            long py;
            if (stepX != 0)
            {
                px = x * cell + 1;
                if (stepX > 0)
                {
                    px = (x + 1) * cell - 1;
                }
                py = y * cell + FaceOffsets[i] * Fixed.Scale;
            }
            else
            {
                py = y * cell + 1;
                if (stepY > 0)
                {
                    py = (y + 1) * cell - 1;
                }
                px = x * cell + FaceOffsets[i] * Fixed.Scale;
            }
            FixedVector point = new FixedVector(Fixed.FromRaw(px), Fixed.FromRaw(py));
            if (FixedVector.DistanceSquared(eye, point) > rangeSquared)
            {
                continue;
            }
            WallHit wall = Map.RayWall(eye, point);
            if (wall.Hit && wall.Tile == index)
            {
                return true;
            }
        }
        return false;
    }

    // 칸 index의 실제 값을 공개 벽 배열에 옮긴다. 값이 바뀌었으면 바뀐 기록에 더한다. 본 칸이 된 순간과 본 벽이 부서졌을 때 부른다.
    private void PublishTile(int index)
    {
        byte value = Map.Tiles[index];
        if (_publicTiles[index] == value)
        {
            return;
        }
        _publicTiles[index] = value;
        _publicChanges.Add(index);
        _publicChanges.Add(value);
    }

    // 그 자리의 칸을 분대가 보았는지다. 맵 밖이면 거짓이다.
    public bool Explored(FixedVector point)
    {
        int x = point.X.FloorDivide(BattleMap.Cell);
        int y = point.Y.FloorDivide(BattleMap.Cell);
        if (x < 0 || y < 0 || x >= Map.Columns || y >= Map.Rows)
        {
            return false;
        }
        return _explored[y * Map.Columns + x];
    }

    // 스냅샷으로 보내는 벽 배열의 사본이다. 분대가 보지 못한 칸은 모름(BattleMap.UnknownTile)이다.
    public byte[] PublicTiles()
    {
        return (byte[])_publicTiles.Clone();
    }

    // 스냅샷 벽 배열의 판 번호다. 공개 벽 배열이 바뀐 수라, 본 칸이 늘거나 본 벽이 부서질 때만 는다.
    public int WallsRevision
    {
        get
        {
            return _publicChanges.Count / 2;
        }
    }

    // 판 번호 revision 뒤로 공개 벽 배열이 바뀐 칸을 [칸 번호, 값, …]으로 돌려준다. 앞에서부터 차례로 적용하면 지금 배열이 된다.
    // 같은 칸이 두 번 나올 수 있다(본 벽이 나중에 부서짐).
    public int[] PublicChangesSince(int revision)
    {
        if (revision < 0 || revision > WallsRevision)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "없는 판 번호입니다");
        }
        int start = revision * 2;
        int[] changes = new int[_publicChanges.Count - start];
        for (int i = 0; i < changes.Length; i++)
        {
            changes[i] = _publicChanges[start + i];
        }
        return changes;
    }

    public bool Visible(FixedVector point)
    {
        if (FixedVector.DistanceSquared(Layout.Camp, point) <= CampVisionSquared && Map.LineOfSight(Layout.Camp, point))
        {
            return true;
        }
        for (int i = 0; i < Players.Count; i++)
        {
            Player player = Players[i];
            if (player.State == "alive" && FixedVector.DistanceSquared(player.Position, point) <= _visionSquared &&
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

    private void Effect(FixedVector at, string kind, double duration)
    {
        _nextId++;
        Effects.Add(new Effect(_nextId, at, kind, Tick + Rules.ToTicks(duration)));
    }
}
