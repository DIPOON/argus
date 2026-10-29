using System.Text.Json;
using Argus.Server.Game;
using Argus.Server.Networking;
using static TestTools;

// 분대가 본 칸(탐색)의 검사다. 맵은 처음에 캠프 둘레만 보이고, 캠프와 살아 있는 대원의 시야로 넓어진다.
// 칸의 어느 부분이든 보이면 본 칸이다. 안 본 칸의 벽과 그 칸의 시설·보급·탈출 지점은 스냅샷에 실리지 않고,
// 투입도 본 칸에만 받는다(LifecycleTests의 투입 검사).
internal static class ExplorationTests
{
    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("The squad sees no tile without a visible part, and misses no open center or wall face it can see", CampStart));
        tests.Add(new TestCase("A long wall row seen at a shallow angle is seen tile by tile, from afar and along the wall", ShallowWallRow));
        tests.Add(new TestCase("Alive players' vision adds seen tiles, which stay seen after they leave; fallen players see nothing", VisionExplores));
        tests.Add(new TestCase("Players look again when they stop, move half a cell, keep moving a tenth of a second or a wall breaks, not every tick", ExploreThrottle));
        tests.Add(new TestCase("A player who stops after moving less than half a cell sees around the corner at once", StopPeek));
        tests.Add(new TestCase("Snapshots send unseen tiles as unknown and leave out the facilities, supplies and extraction on them", SnapshotMasks));
        tests.Add(new TestCase("Connections get the whole walls once and then only the tiles changed since what they received", ExploredResend));
        tests.Add(new TestCase("The state summary tells apart rooms that differ only in seen tiles, the walls revision or a look origin", HashCoversExploration));
    }

    // 칸 (x, y)의 가운데다.
    private static FixedVector Center(int x, int y)
    {
        return FixedVector.FromInt(x * BattleMap.Cell + BattleMap.Cell / 2, y * BattleMap.Cell + BattleMap.Cell / 2);
    }

    // 눈(캠프나 대원의 자리)과 그 시야 반경이다.
    private sealed class Eye
    {
        public FixedVector Position { get; }
        public Fixed Range { get; }

        public Eye(FixedVector position, Fixed range)
        {
            Position = position;
            Range = range;
        }
    }

    // point가 eye에서 보이는지다. 빈 땅은 그 점까지 벽에 막히지 않으면, 벽 칸은 그 점까지 가는 선분이 처음 닿는 벽이 그 칸이면 보인다.
    private static bool PointSeen(Match match, Eye eye, FixedVector point, int tile)
    {
        if (FixedVector.DistanceSquared(eye.Position, point) > eye.Range * eye.Range)
        {
            return false;
        }
        WallHit wall = match.Map.RayWall(eye.Position, point);
        if (match.Map.Tiles[tile] == 0)
        {
            return !wall.Hit;
        }
        return wall.Hit && wall.Tile == tile;
    }

    private static bool SeenByAny(Match match, List<Eye> eyes, FixedVector point, int tile)
    {
        for (int k = 0; k < eyes.Count; k++)
        {
            if (PointSeen(match, eyes[k], point, tile))
            {
                return true;
            }
        }
        return false;
    }

    // 칸 안에 고르게 찍은 점(9×9, 가장자리에서 1/16 안쪽) 가운데 하나라도 어느 눈에서 보이는지다.
    private static bool TileSeen(Match match, List<Eye> eyes, int x, int y)
    {
        int tile = y * match.Map.Columns + x;
        long left = (long)x * BattleMap.Cell * Fixed.Scale + Fixed.Scale / 16;
        long top = (long)y * BattleMap.Cell * Fixed.Scale + Fixed.Scale / 16;
        long span = BattleMap.Cell * Fixed.Scale - Fixed.Scale / 8;
        for (int i = 0; i <= 8; i++)
        {
            for (int j = 0; j <= 8; j++)
            {
                FixedVector point = new FixedVector(Fixed.FromRaw(left + span * i / 8), Fixed.FromRaw(top + span * j / 8));
                if (SeenByAny(match, eyes, point, tile))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // 벽 칸 (x, y)의 면 가운데 점(벽 안쪽으로 Raw 1)이 하나라도 어느 눈에서 보이는지다. 맞닿은 칸이 본 빈 땅인 면만 본다.
    private static bool FaceMiddleSeen(Match match, List<Eye> eyes, int x, int y)
    {
        int tile = y * match.Map.Columns + x;
        int[,] sides = new int[,] { { -1, 0 }, { 1, 0 }, { 0, -1 }, { 0, 1 } };
        for (int i = 0; i < 4; i++)
        {
            int nx = x + sides[i, 0];
            int ny = y + sides[i, 1];
            if (nx < 0 || ny < 0 || nx >= match.Map.Columns || ny >= match.Map.Rows || match.Map.Solid(nx, ny) || !match.Explored(Center(nx, ny)))
            {
                continue;
            }
            if (SeenByAny(match, eyes, FaceMiddle(x, y, sides[i, 0], sides[i, 1]), tile))
            {
                return true;
            }
        }
        return false;
    }

    // 벽 칸 (x, y)가 (stepX, stepY) 쪽 이웃과 맞닿은 면의 가운데에서 벽 안쪽으로 가장 작은 단위(Raw 1)만큼 들어간 점이다.
    // 안쪽으로 많이 들어가면 아주 비스듬한 선분이 옆 칸의 면에서 먼저 막혀, 보이는 면을 안 보이는 것으로 친다.
    private static FixedVector FaceMiddle(int x, int y, int stepX, int stepY)
    {
        long cell = BattleMap.Cell * Fixed.Scale;
        long px = x * cell + cell / 2;
        long py = y * cell + cell / 2;
        if (stepX < 0)
        {
            px = x * cell + 1;
        }
        if (stepX > 0)
        {
            px = (x + 1) * cell - 1;
        }
        if (stepY < 0)
        {
            py = y * cell + 1;
        }
        if (stepY > 0)
        {
            py = (y + 1) * cell - 1;
        }
        return new FixedVector(Fixed.FromRaw(px), Fixed.FromRaw(py));
    }

    // 칸이나 그 둘레 여덟 칸 가운데 하나라도 보이는 점이 있는지다. 광선이 칸 귀퉁이의 아주 작은 틈만 본 칸은 고르게 찍은 점이
    // 그 틈을 건너뛸 수 있다. 그런 칸도 광선이 지나온 바로 옆 칸은 보이므로, 보이는 곳에서 한 칸 넘게 떨어진 칸만 잘못 본 칸으로 친다.
    private static bool SeenNearby(Match match, List<Eye> eyes, int x, int y)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx;
                int ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < match.Map.Columns && ny < match.Map.Rows && TileSeen(match, eyes, nx, ny))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // 칸의 가장 가까운 점이 어느 눈의 시야 반경 안인지다. 벽은 보지 않는다.
    private static bool NearAnyEye(List<Eye> eyes, int x, int y)
    {
        for (int k = 0; k < eyes.Count; k++)
        {
            FixedVector eye = eyes[k].Position;
            FixedVector nearest = new FixedVector(
                Fixed.Clamp(eye.X, Fixed.FromInt(x * BattleMap.Cell), Fixed.FromInt((x + 1) * BattleMap.Cell)),
                Fixed.Clamp(eye.Y, Fixed.FromInt(y * BattleMap.Cell), Fixed.FromInt((y + 1) * BattleMap.Cell)));
            if (FixedVector.DistanceSquared(eye, nearest) <= eyes[k].Range * eyes[k].Range)
            {
                return true;
            }
        }
        return false;
    }

    // 본 칸이 eyes에서 실제로 보이는 칸과 맞는지 본다. 보이는 곳에서 떨어진 칸을 본 칸으로 치면 숨은 곳이 드러나고,
    // 가운데가 보이는 빈 땅이나 앞면 가운데가 보이는 벽을 빠뜨리면 지도에 구멍이 난다.
    // 벽에 가려 보이는 부분이 없는 빈 땅(시야 반경 안)의 수와, 가운데는 안 보이지만 일부가 보여 본 칸이 된 빈 땅의 수를 돌려준다.
    private static void CheckSeen(Match match, List<Eye> eyes, out int hidden, out int partial)
    {
        hidden = 0;
        partial = 0;
        for (int y = 0; y < match.Map.Rows; y++)
        {
            for (int x = 0; x < match.Map.Columns; x++)
            {
                bool explored = match.Explored(Center(x, y));
                bool near = NearAnyEye(eyes, x, y);
                Check(!explored || (near && (TileSeen(match, eyes, x, y) || SeenNearby(match, eyes, x, y))), $"보이는 곳에서 떨어진 칸 ({x}, {y})을 보았습니다");
                if (!near)
                {
                    continue;
                }
                if (match.Map.Solid(x, y))
                {
                    Check(explored || !FaceMiddleSeen(match, eyes, x, y), $"앞면 가운데가 보이는 벽 ({x}, {y})을 보지 못했습니다");
                    continue;
                }
                bool centerSeen = SeenByAny(match, eyes, Center(x, y), y * match.Map.Columns + x);
                Check(explored || !centerSeen, $"가운데가 보이는 빈 땅 ({x}, {y})을 보지 못했습니다");
                if (explored && !centerSeen)
                {
                    partial++;
                }
                if (!explored && !TileSeen(match, eyes, x, y))
                {
                    hidden++;
                }
            }
        }
    }

    private static int CountExplored(Match match)
    {
        int count = 0;
        for (int y = 0; y < match.Map.Rows; y++)
        {
            for (int x = 0; x < match.Map.Columns; x++)
            {
                if (match.Explored(Center(x, y)))
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static void CampStart()
    {
        Match match = new Match("EXPL01", new Rules { MaxEnemies = 0 }, 73);
        Player player = match.Join("p", "pilot")!;
        List<Eye> eyes = new List<Eye>();
        eyes.Add(new Eye(match.Layout.Camp, Fixed.FromInt(BattleMap.CampVision)));
        CheckSeen(match, eyes, out int hidden, out int partial);
        // 캠프 시야(반지름 260)의 넓이는 칸 약 92개다. 가장자리에 걸친 칸도 본 칸이라 그보다 많다.
        int seen = CountExplored(match);
        Check(seen > 92 && seen < 150, $"처음 본 칸이 {seen}개입니다");
        Check(partial > 0, "가운데는 안 보이고 일부만 보이는 칸이 없어 그런 칸을 확인하지 못했습니다");
        Check(match.Map.Solid(10, 18) && match.Explored(Center(10, 18)), "캠프에서 앞면이 보이는 벽을 보지 못했습니다");

        // 투입을 기다리는 대원은 시야가 없다. 20초를 기다려도 본 칸과 판 번호는 그대로다.
        int revision = match.WallsRevision;
        byte[] before = match.PublicTiles();
        Advance(match, 20);
        Equal(player.State, "waiting");
        Equal(match.WallsRevision, revision);
        Check(before.AsSpan().SequenceEqual(match.PublicTiles()), "기다리는 동안 본 칸이 바뀌었습니다");

        // 착지하면 대원의 시야(반지름 410)가 더해진다. 벽에 가린 빈 땅은 시야 반경 안이어도 보지 못한다.
        Check(match.Deploy(player, 500, 730));
        Advance(match, match.Rules.DeploySeconds);
        Equal(player.State, "alive");
        eyes.Add(new Eye(player.Position, match.VisionRange));
        CheckSeen(match, eyes, out hidden, out partial);
        Check(hidden > 0, "시야 반경 안에 벽에 가린 빈 땅이 없어 가림을 확인하지 못했습니다");

        // 장애물이 흩어진 생성 맵에서도 같다.
        byte[] seed = SeededRandom.SeedFromNumber(3);
        Match generated = new Match("EXPL02", Rules.ForGeneratedMission(), seed, MissionGenerator.Generate(seed));
        Player scout = generated.Join("p", "pilot")!;
        Check(generated.Deploy(scout, generated.Layout.Camp.X.ToDouble(), generated.Layout.Camp.Y.ToDouble()));
        Advance(generated, generated.Rules.DeploySeconds);
        Equal(scout.State, "alive");
        List<Eye> generatedEyes = new List<Eye>();
        generatedEyes.Add(new Eye(generated.Layout.Camp, Fixed.FromInt(BattleMap.CampVision)));
        generatedEyes.Add(new Eye(scout.Position, generated.VisionRange));
        CheckSeen(generated, generatedEyes, out hidden, out partial);
    }

    private static void ShallowWallRow()
    {
        // 가로로 긴 벽 줄(12번 행)을 비스듬히 본다. 칸 가운데로만 판정하면 벽 칸 가운데로 가는 선분이 같은 줄의 더 가까운 벽 윗면에
        // 먼저 닿아, 수선의 발 근처 몇 칸만 본 칸이 되고 나머지는 지도에서 빠졌다(PR #18 리뷰). 벽에서 떨어져 볼 때(120)와
        // 벽을 따라 걸을 때(14)를 모두 본다. 벽을 따라 걸을 때는 멀리 비스듬한 면이 광선 사이로 빠지기 쉽다.
        int columns = 60;
        int rows = 20;
        int row = 12;
        byte[] tiles = LayoutTests.BorderTiles(columns, rows);
        for (int x = 2; x < columns - 2; x++)
        {
            tiles[row * columns + x] = 1;
        }
        FixedVector[] facilities = new FixedVector[] { FixedVector.FromInt(1500, 750) };
        SupplySpot[] supplies = new SupplySpot[]
        {
            new SupplySpot(FixedVector.FromInt(300, 300), SupplyKind.Camp),
            new SupplySpot(FixedVector.FromInt(2500, 300), SupplyKind.Objective)
        };
        EnemySpot[] enemies = new EnemySpot[] { new EnemySpot(FixedVector.FromInt(2000, 800), EnemyDefinition.Grunt) };
        SpawnRing ring = new SpawnRing(FixedVector.FromInt(1440, 300), 1200, 150);
        MissionLayout layout = new MissionLayout(columns, rows, tiles, FixedVector.FromInt(150, 200), FixedVector.FromInt(2700, 200),
            facilities, supplies, enemies, ring);
        int face = row * BattleMap.Cell;
        int[] distances = new int[] { 120, 14 };
        for (int d = 0; d < distances.Length; d++)
        {
            Match match = new Match("ROW001", new Rules { MaxEnemies = 0 }, SeededRandom.SeedFromNumber(9), layout);
            Player player = match.Join("p", "pilot")!;
            Check(match.Deploy(player, 150, 200));
            Advance(match, match.Rules.DeploySeconds);
            Equal(player.State, "alive");
            Eye eye = new Eye(FixedVector.FromInt(1440, face - distances[d]), match.VisionRange);
            player.Position = eye.Position;
            match.Step();
            // 윗면 가운데가 보이는 벽 칸은 모두 본 칸이어야 한다.
            int expected = 0;
            for (int x = 2; x < columns - 2; x++)
            {
                int tile = row * columns + x;
                if (!PointSeen(match, eye, FaceMiddle(x, row, 0, -1), tile))
                {
                    continue;
                }
                expected++;
                Check(match.Explored(Center(x, row)), $"벽에서 {distances[d]} 떨어져 볼 때 윗면이 보이는 벽 ({x}, {row})을 보지 못했습니다");
            }
            // 시야 410 안에 든 윗면은 수선의 발 양쪽으로 여러 칸이다.
            Check(expected > 12, $"윗면이 보이는 벽이 {expected}칸뿐이라 비스듬한 면을 확인하지 못했습니다");
        }
    }

    private static void VisionExplores()
    {
        Fixture f = new Fixture();
        FixedVector far = FixedVector.FromInt(1000, 600);
        Check(!f.Match.Explored(far));
        // 대원이 그곳에 가면 본 칸이 되고, 떠나도 본 칸으로 남는다. 지금 보이는지(Visible)와는 따로다.
        f.Player.Position = far;
        f.Match.Step();
        Check(f.Match.Explored(far) && f.Match.Visible(far), "대원이 선 곳을 보지 못했습니다");
        f.Player.Position = f.Match.Layout.Camp;
        f.Match.Step();
        Check(f.Match.Explored(far), "떠난 곳을 잊었습니다");
        Check(!f.Match.Visible(far));

        // 쓰러져 투입을 기다리는 대원은 시야가 없어 선 곳을 보지 못한다. 동료가 살아 있어 작전은 이어진다.
        Ally(f, "ally");
        FixedVector other = FixedVector.FromInt(1400, 1250);
        Check(!f.Match.Explored(other));
        f.Match.Hurt(f.Player, f.Player.Hp);
        Equal(f.Player.State, "waiting");
        f.Player.Position = other;
        f.Match.Step();
        Equal(f.Match.Phase, "active");
        Check(!f.Match.Explored(other), "쓰러진 대원이 선 곳을 보았습니다");
    }

    private static void ExploreThrottle()
    {
        // 둘러보면 그 자리와 틱을 탐색 원점으로 적는다. 그 자리에 서 있는 동안은 다시 둘러보지 않는다.
        Fixture f = new Fixture();
        Check(f.Player.HasExploreOrigin && f.Player.ExploreOrigin == f.Player.Position, "착지한 자리에서 둘러보지 않았습니다");
        long landed = f.Player.ExploreTick;
        Advance(f.Match, 1);
        Equal(f.Player.ExploreTick, landed);

        // 움직이는 동안은 반 칸을 못 가도 0.1초(3틱)마다 둘러본다. 틱마다 둘러보지는 않는다. 8틱 동안 오른쪽으로 걸으면
        // 첫 틱(마지막으로 둘러본 지 오래됨)과 4번째, 7번째 틱에 둘러본다.
        long sequence = 1;
        int looks = 0;
        for (int i = 0; i < 8; i++)
        {
            long before = f.Player.ExploreTick;
            Check(f.Match.Input(f.Player, sequence, 1, 0, 0, 0));
            sequence++;
            f.Match.Step();
            if (f.Player.ExploreTick != before)
            {
                looks++;
                Equal(f.Player.ExploreOrigin, f.Player.Position);
            }
            Check(f.Match.Tick - f.Player.ExploreTick < Match.ExploreIntervalTicks, "움직이는 동안 0.1초 넘게 둘러보지 않았습니다");
        }
        Equal(looks, 3);

        // 멈추면(이동 입력 없음) 0.1초가 지나지 않았어도 그 자리에서 바로 둘러본다. 문 앞에 멈춰 안을 들여다볼 때다.
        // 마지막으로 둘러본 지 한 틱이라, 0.1초 조건으로는 아직 둘러보지 않는다.
        Equal(f.Match.Tick - f.Player.ExploreTick, 1L);
        Check(f.Player.Position != f.Player.ExploreOrigin);
        Check(f.Match.Input(f.Player, sequence, 0, 0, 0, 0));
        sequence++;
        f.Match.Step();
        Equal(f.Player.ExploreTick, f.Match.Tick);
        Equal(f.Player.ExploreOrigin, f.Player.Position);
        // 멈춘 뒤로는 다시 둘러보지 않는다.
        long stopped = f.Player.ExploreTick;
        Advance(f.Match, 1);
        Equal(f.Player.ExploreTick, stopped);

        // 0.1초가 지나지 않았어도 반 칸 넘게 움직였으면 둘러본다. 한 틱에 많이 움직일 때다. 먼저 걷기 시작해 한 번 둘러보게 한 뒤,
        // 다음 틱에 반 칸 넘게 옮긴다.
        Check(f.Match.Input(f.Player, sequence, 1, 0, 0, 0));
        sequence++;
        f.Match.Step();
        Equal(f.Player.ExploreTick, f.Match.Tick);
        f.Player.Position = f.Player.Position + FixedVector.FromInt(0, 25);
        Check(f.Match.Input(f.Player, sequence, 1, 0, 0, 0));
        sequence++;
        f.Match.Step();
        Equal(f.Player.ExploreTick, f.Match.Tick);

        // 제자리에 있어도 벽이 부서지면 다시 둘러본다. 그 뒤가 새로 보일 수 있다. 대원이 부술 수 있는 벽 (18, 21) 바로 위에 서면
        // 그 아래 칸 (18, 22)는 벽에 가려 보이지 않는다. 벽이 부서지면 대원이 움직이지 않아도 그 칸을 본다.
        Fixture g = new Fixture();
        int wall = 21 * g.Match.Map.Columns + 18;
        Equal(g.Match.Map.Tiles[wall], (byte)1);
        g.Player.Position = FixedVector.FromInt(18 * BattleMap.Cell + BattleMap.Cell / 2, 21 * BattleMap.Cell - 14);
        g.Match.Step();
        FixedVector stand = g.Player.ExploreOrigin;
        Check(g.Match.Explored(Center(18, 21)) && !g.Match.Explored(Center(18, 22)), "검사 조건(벽 18, 21이 가리는 칸)이 바뀌었습니다");
        int revision = g.Match.WallsRevision;
        Check(g.Match.Map.Damage(wall, 3), "벽이 부서지지 않았습니다");
        g.Match.Step();
        Equal(g.Player.ExploreOrigin, stand);
        Check(g.Match.Explored(Center(18, 22)), "벽이 부서진 뒤 제자리에서 다시 둘러보지 않았습니다");
        // 본 벽이 부서졌으므로 공개 벽 배열이 바뀌고 판 번호가 오른다.
        Equal(g.Match.PublicTiles()[wall], (byte)0);
        Check(g.Match.WallsRevision > revision, "본 벽이 부서졌는데 판 번호가 그대로입니다");
    }

    private static void StopPeek()
    {
        // 반 칸(24)보다 덜 움직이고 멈춰도 그 자리에서 바로 둘러본다. 모퉁이나 문 옆에서는 몇 걸음만 옮겨도 가려 있던 통로나 방이
        // 통째로 보인다. 생성 맵(시드 3)의 여러 자리에서 둘러본 뒤 네 방향으로 23만큼 옮겨 멈추고, 새 자리에서 가운데가 보이는
        // 빈 땅이 모두 본 칸인지 본다. 멈출 때 다시 둘러보지 않으면 네 곳 가운데 한 곳꼴로 칸이 빠졌다(PR #18 리뷰).
        byte[] seed = SeededRandom.SeedFromNumber(3);
        MissionLayout layout = MissionGenerator.Generate(seed);
        int[,] steps = new int[,] { { 23, 0 }, { -23, 0 }, { 0, 23 }, { 0, -23 } };
        int spots = 0;
        for (int py = 72; py < layout.Rows * BattleMap.Cell - 72; py += 240)
        {
            for (int px = 72; px < layout.Columns * BattleMap.Cell - 72; px += 240)
            {
                for (int d = 0; d < 4; d++)
                {
                    FixedVector first = FixedVector.FromInt(px, py);
                    FixedVector second = FixedVector.FromInt(px + steps[d, 0], py + steps[d, 1]);
                    Match match = new Match("PEEK01", Rules.ForGeneratedMission(), seed, layout);
                    if (!match.Map.CanStand(first, Rules.PlayerRadius) || !match.Map.CanStand(second, Rules.PlayerRadius) ||
                        !match.Map.LineOfSight(first, second))
                    {
                        continue;
                    }
                    // 투입을 거치지 않고 그 자리에 세운다. 작전이 시작되기 전이라 적은 움직이지 않고, 본 칸은 틱마다 맞춘다.
                    Player player = match.Join("p", "pilot")!;
                    player.State = "alive";
                    player.HasDeployed = true;
                    player.Position = first;
                    match.Step();
                    player.Position = second;
                    match.Step();
                    spots++;
                    CheckCentersSeen(match, second);
                }
            }
        }
        Check(spots > 100, $"확인한 자리가 {spots}곳뿐입니다");
    }

    // eye의 시야 반경 안에서 가운데가 eye에서 보이는 빈 땅은 모두 본 칸이어야 한다.
    private static void CheckCentersSeen(Match match, FixedVector eye)
    {
        Fixed range = match.VisionRange;
        for (int y = 0; y < match.Map.Rows; y++)
        {
            for (int x = 0; x < match.Map.Columns; x++)
            {
                FixedVector center = Center(x, y);
                if (match.Map.Solid(x, y) || FixedVector.DistanceSquared(eye, center) > range * range)
                {
                    continue;
                }
                if (match.Map.LineOfSight(eye, center))
                {
                    Check(match.Explored(center), $"{eye}에 멈춘 대원이 가운데가 보이는 빈 땅 ({x}, {y})을 보지 못했습니다");
                }
            }
        }
    }

    private static void SnapshotMasks()
    {
        Fixture f = new Fixture();
        // 처음에는 캠프 둘레만 보았다. 시설과 탈출 지점은 모두 캠프에서 멀다.
        CheckSnapshot(f.Match, f.Player);
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(f.Match, f.Player, RoomHost.Json)))
        {
            JsonElement root = json.RootElement;
            Equal(root.GetProperty("facilities").GetArrayLength(), 0);
            Equal(root.GetProperty("extraction").ValueKind, JsonValueKind.Null);
            JsonElement supplies = root.GetProperty("supplies");
            Equal(supplies.GetArrayLength(), 1);
            Equal(supplies[0].GetProperty("kind").GetString(), "camp");
        }
        // 시설 하나를 둘러보면 그 시설이 실린다. 그곳에서 먼 시설(3번)은 여전히 빠진다.
        Scout(f.Match, f.Player, f.Match.Facilities[0].Position);
        Check(!f.Match.Explored(f.Match.Facilities[2].Position));
        CheckSnapshot(f.Match, f.Player);
        // 탈출 지점도 둘러본 뒤에야 실린다.
        Scout(f.Match, f.Player, f.Match.Layout.Extraction);
        CheckSnapshot(f.Match, f.Player);
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(f.Match, f.Player, RoomHost.Json)))
        {
            JsonElement extraction = json.RootElement.GetProperty("extraction");
            Equal(extraction.GetProperty("x").GetDouble(), f.Match.Layout.Extraction.X.ToDouble());
            Equal(extraction.GetProperty("y").GetDouble(), f.Match.Layout.Extraction.Y.ToDouble());
        }
    }

    // 스냅샷의 벽 배열은 본 칸이면 실제 값, 안 본 칸이면 모름(3)이다. 시설과 보급은 본 칸에 있는 것만 모두 실린다.
    private static void CheckSnapshot(Match match, Player recipient)
    {
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(match, recipient, RoomHost.Json)))
        {
            JsonElement root = json.RootElement;
            byte[] walls = Convert.FromBase64String(root.GetProperty("walls").GetString()!);
            Equal(walls.Length, match.Map.Tiles.Length);
            int unknown = 0;
            for (int i = 0; i < walls.Length; i++)
            {
                if (match.Explored(Center(i % match.Map.Columns, i / match.Map.Columns)))
                {
                    Equal(walls[i], match.Map.Tiles[i]);
                }
                else
                {
                    Equal(walls[i], BattleMap.UnknownTile);
                    unknown++;
                }
            }
            Check(unknown > 0, "안 본 칸이 없습니다");
            Equal(root.GetProperty("mapRevision").GetInt32(), match.WallsRevision);
            JsonElement facilities = root.GetProperty("facilities");
            int explored = 0;
            for (int i = 0; i < match.Facilities.Count; i++)
            {
                Facility facility = match.Facilities[i];
                Equal(Contains(facilities, facility.Id), match.Explored(facility.Position));
                if (match.Explored(facility.Position))
                {
                    explored++;
                }
            }
            Equal(facilities.GetArrayLength(), explored);
            JsonElement supplies = root.GetProperty("supplies");
            explored = 0;
            for (int i = 0; i < match.Supplies.Count; i++)
            {
                Supply supply = match.Supplies[i];
                Equal(Contains(supplies, supply.Id), match.Explored(supply.Position));
                if (match.Explored(supply.Position))
                {
                    explored++;
                }
            }
            Equal(supplies.GetArrayLength(), explored);
        }
    }

    // 배열에 id가 같은 항목이 있는지다.
    private static bool Contains(JsonElement array, int id)
    {
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.GetProperty("id").GetInt32() == id)
            {
                return true;
            }
        }
        return false;
    }

    private static void ExploredResend()
    {
        using RoomHost host = NewHost();
        Guest guest = host.Create(new EntryRequest("pilot"))!;
        (Room room, Player player, Peer peer) = host.Attach(guest.Room, guest.Token)!.Value;
        // 클라이언트가 가진 벽 배열이다. 받은 대로 고쳐 가며 서버의 공개 벽 배열과 같은지 본다.
        // 접속 직후에는 벽 배열 전체가 온다.
        byte[]? client = ReceiveWalls(room, peer, null, "walls");
        // 캠프에 착지하면 대원의 시야(캠프 시야보다 넓다)로 본 칸이 늘어, 받은 판 뒤로 바뀐 칸만 보낸다.
        lock (room.Gate)
        {
            Match match = room.Match;
            Check(match.Deploy(player, match.Layout.Camp.X.ToDouble(), match.Layout.Camp.Y.ToDouble()));
            Advance(match, match.Rules.DeploySeconds + 0.1);
            Equal(player.State, "alive");
            RoomHost.PublishSnapshots(room);
        }
        client = ReceiveWalls(room, peer, client, "wallChanges");
        // 그 자리에 서 있으면 본 칸이 늘지 않아 벽을 다시 보내지 않는다.
        lock (room.Gate)
        {
            room.Match.Step();
            RoomHost.PublishSnapshots(room);
        }
        client = ReceiveWalls(room, peer, client, "none");
        // 새 곳(탈출 지점)에 가면 본 칸이 늘어 바뀐 칸을 보낸다.
        lock (room.Gate)
        {
            Check(!room.Match.Explored(room.Match.Layout.Extraction));
            player.Position = room.Match.Layout.Extraction;
            room.Match.Step();
            Check(room.Match.Explored(room.Match.Layout.Extraction));
            RoomHost.PublishSnapshots(room);
        }
        ReceiveWalls(room, peer, client, "wallChanges");

        // 받은 판보다 바뀐 칸이 너무 많으면(칸 수의 1/8 넘게) 바뀐 칸 대신 배열 전체를 보낸다. 받은 판이 하나 뒤면 그 한 칸만 보낸다.
        lock (room.Gate)
        {
            Match match = room.Match;
            Check(match.WallsRevision > match.Map.Tiles.Length / 8, "검사 조건(바뀐 칸 수)이 모자랍니다");
            PersonalSnapshot behind = Snapshot.CreatePersonal(match, player, 0, false);
            Check(behind.Walls != null && behind.WallChanges == null, "바뀐 칸이 많은데 배열 전체를 보내지 않았습니다");
            PersonalSnapshot oneBehind = Snapshot.CreatePersonal(match, player, match.WallsRevision - 1, false);
            Check(oneBehind.Walls == null && oneBehind.WallChanges != null && oneBehind.WallChanges.Length == 2, "바뀐 한 칸만 보내지 않았습니다");
        }
    }

    // 다음 메시지에 벽이 어떻게 실렸는지(walls·wallChanges·none) 보고, 클라이언트처럼 받은 배열을 고친 뒤 서버의 공개 벽 배열과 같은지 본다.
    // 벽 전체와 바뀐 칸이 함께 오면 안 된다. 보낸 것으로 기록하고 고친 배열을 돌려준다.
    private static byte[]? ReceiveWalls(Room room, Peer peer, byte[]? client, string expected)
    {
        Check(peer.Outgoing.Reader.TryRead(out OutgoingMessage? message), "보낼 메시지가 없습니다");
        using (JsonDocument json = JsonDocument.Parse(message!.Bytes))
        {
            JsonElement root = json.RootElement;
            bool hasWalls = root.TryGetProperty("walls", out JsonElement walls);
            bool hasChanges = root.TryGetProperty("wallChanges", out JsonElement changes);
            Check(!(hasWalls && hasChanges), "벽 전체와 바뀐 칸이 함께 왔습니다");
            string carried = "none";
            if (hasWalls)
            {
                carried = "walls";
                client = Convert.FromBase64String(walls.GetString()!);
            }
            else if (hasChanges)
            {
                carried = "wallChanges";
                Check(client != null, "벽 배열을 받기 전에 바뀐 칸이 왔습니다");
                Check(changes.GetArrayLength() % 2 == 0, "바뀐 칸이 [칸 번호, 값] 짝이 아닙니다");
                for (int i = 0; i < changes.GetArrayLength(); i += 2)
                {
                    client![changes[i].GetInt32()] = (byte)changes[i + 1].GetInt32();
                }
            }
            Equal(carried, expected);
            Equal(root.GetProperty("mapRevision").GetInt32(), room.Match.WallsRevision);
            Check(client != null && client.AsSpan().SequenceEqual(room.Match.PublicTiles()), "받은 벽 배열이 서버의 공개 벽 배열과 다릅니다");
        }
        peer.MarkSent(message);
        return client;
    }

    private static void HashCoversExploration()
    {
        // 본 칸만 다르다. 두 방 모두 한 번씩 둘러보고 제자리로 돌아온다. (650, 200)과 (750, 200)은 새로 본 칸 수가 같아 판 번호도 같다.
        Fixture one = new Fixture();
        Fixture other = new Fixture();
        Scout(one.Match, one.Player, FixedVector.FromInt(650, 200));
        Scout(other.Match, other.Player, FixedVector.FromInt(750, 200));
        one.Match.Step();
        other.Match.Step();
        Equal(one.Match.WallsRevision, other.Match.WallsRevision);
        Check(!one.Match.PublicTiles().AsSpan().SequenceEqual(other.Match.PublicTiles()));
        Check(one.Match.StateHash() != other.Match.StateHash(), "본 칸만 다른데 상태 요약이 같습니다");
        FixedVector first = FixedVector.FromInt(1000, 600);

        // 공개 벽 배열과 탐색 원점은 같고 판 번호만 다르다. 한 방은 본 벽 (24, 7)이 부서지고(바뀐 칸 둘), 다른 방은 부서진 뒤에 그 벽을 본다(하나).
        int wall = 7 * one.Match.Map.Columns + 24;
        Fixture seenFirst = new Fixture();
        Scout(seenFirst.Match, seenFirst.Player, first);
        Check(seenFirst.Match.Explored(Center(24, 7)) && seenFirst.Match.Map.Tiles[wall] == 1, "검사 조건(벽 24, 7)이 바뀌었습니다");
        Check(seenFirst.Match.Map.Damage(wall, 3));
        Scout(seenFirst.Match, seenFirst.Player, first);
        seenFirst.Match.Step();
        Fixture brokenFirst = new Fixture();
        Check(!brokenFirst.Match.Explored(Center(24, 7)));
        Check(brokenFirst.Match.Map.Damage(wall, 3));
        brokenFirst.Match.Step();
        Scout(brokenFirst.Match, brokenFirst.Player, first);
        brokenFirst.Match.Step();
        Check(seenFirst.Match.PublicTiles().AsSpan().SequenceEqual(brokenFirst.Match.PublicTiles()), "같은 곳을 같은 벽으로 봤는데 공개 벽 배열이 다릅니다");
        Equal(seenFirst.Match.WallsRevision, brokenFirst.Match.WallsRevision + 1);
        Equal(seenFirst.Player.ExploreOrigin, brokenFirst.Player.ExploreOrigin);
        Check(seenFirst.Match.StateHash() != brokenFirst.Match.StateHash(), "판 번호만 다른데 상태 요약이 같습니다");

        // 탐색 원점만 다르다. 원점은 다음에 언제 둘러볼지를 정하므로 진행 상태다.
        Fixture moved = new Fixture();
        Fixture still = new Fixture();
        Equal(moved.Match.StateHash(), still.Match.StateHash());
        moved.Player.ExploreOrigin = moved.Player.ExploreOrigin + FixedVector.FromInt(1, 0);
        Check(moved.Match.StateHash() != still.Match.StateHash(), "탐색 원점만 다른데 상태 요약이 같습니다");
        Fixture fresh = new Fixture();
        fresh.Player.HasExploreOrigin = false;
        Check(fresh.Match.StateHash() != still.Match.StateHash(), "둘러본 적이 있는지만 다른데 상태 요약이 같습니다");
        Fixture later = new Fixture();
        later.Player.ExploreTick = later.Player.ExploreTick + 1;
        Check(later.Match.StateHash() != still.Match.StateHash(), "마지막으로 둘러본 틱만 다른데 상태 요약이 같습니다");
    }
}
