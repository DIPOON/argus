using Argus.Server.Game;
using Argus.Server.Networking;
using static TestTools;

internal static class DeterminismTests
{
    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("The seeded generator keeps its known first value, repeats for a seed, differs for another and stays in range", SeededSequence));
        tests.Add(new TestCase("Two rooms made with the same seed start with the same enemy ids, and the summary tells apart other rules and codes", SameSeedEnemies));
        tests.Add(new TestCase("Two rooms with the same seed and the same inputs on the same ticks stay identical every tick", SameSeedSameRun));
        tests.Add(new TestCase("Running ticks one at a time or several per wake gives the same state", TickBatching));
        tests.Add(new TestCase("The tick clock turns irregular wakes into whole ticks, carries the remainder and drops a long stall", TickClockWakes));
        tests.Add(new TestCase("A duration takes the same whole number of ticks whatever tick it starts on", DeadlineTicks));
        tests.Add(new TestCase("Game code calls no operating-system math functions, clocks or unseeded random generators", PortableGameCode));
    }

    private static void PortableGameCode()
    {
        // 컴파일된 게임 코드(Argus.Server.Game 이름공간의 모든 형식, 람다와 지역 함수 포함)가 부르는 메서드를 IL에서 모두 찾는다.
        // 실수 수학 형식은 결과가 정확히 정해진 허용 목록만 쓰고, 실수 벡터(Vector2 등)와 시드 없는 난수, 시계는 하나도 쓰지 않아야 한다.
        // 실제로 Windows의 Math.Atan2가 리눅스와 마지막 자리를 다르게 계산해, 같은 기록이 510틱부터 갈라진 적이 있다(docs/verification.md).
        List<string> game = PortabilityCheck.Violations(typeof(Match).Assembly, "Argus.Server.Game", out int inspected);
        Check(game.Count == 0, "게임 코드가 기계마다 다를 수 있는 함수를 씁니다: " + string.Join(", ", game));
        // 게임 코드는 메서드가 수백 개다. 너무 적으면 이름공간을 잘못 적어 아무것도 보지 않은 것이다.
        Check(inspected > 200, $"게임 코드 메서드를 {inspected}개만 살펴봤습니다");

        // 검사가 실제로 잡는지도 본다. 소스를 글자로 찾던 방식이 놓치던 경우를 모은 예제가 하나도 빠짐없이 걸려야 한다.
        List<string> probe = PortabilityCheck.Violations(typeof(DeterminismTests).Assembly, "PortabilityProbes", out int probed);
        Check(probed >= 15, $"예제 메서드를 {probed}개만 살펴봤습니다");
        string[] expected = new string[]
        {
            "System.Math.Sin", "System.Double.Atan2", "System.Math.SinCos", "System.Math.ReciprocalEstimate",
            "System.Math.Atan2", "System.Math.Cos", "System.MathF.FusedMultiplyAdd", "System.Numerics.Vector2.Lerp",
            "System.Numerics.Vector2.Length",
            "System.Random..ctor", "System.Random.Next", "System.DateTime.get_UtcNow", "System.Diagnostics.Stopwatch.GetTimestamp",
            "System.Numerics.ITrigonometricFunctions`1.Sin", "System.Numerics.INumberBase`1.MultiplyAddEstimate"
        };
        for (int i = 0; i < expected.Length; i++)
        {
            Check(EndsWithAny(probe, " → " + expected[i]), $"이식성 검사가 {expected[i]}를 잡지 못했습니다: " + string.Join(", ", probe));
        }
        // 허용한 함수만 쓰는 예제는 걸리지 않아야 한다.
        for (int i = 0; i < probe.Count; i++)
        {
            bool allowed = probe[i].StartsWith("PortabilityProbes.Probe.Allowed ", StringComparison.Ordinal) ||
                probe[i].StartsWith("PortabilityProbes.Probe.GenericAllowed ", StringComparison.Ordinal);
            Check(!allowed, $"허용한 함수를 막았습니다: {probe[i]}");
        }
    }

    private static bool EndsWithAny(List<string> lines, string suffix)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static void SeededSequence()
    {
        SeededRandom first = new SeededRandom(SeededRandom.SeedFromNumber(73));
        SeededRandom again = new SeededRandom(SeededRandom.SeedFromNumber(73));
        SeededRandom other = new SeededRandom(SeededRandom.SeedFromNumber(74));
        // 뽑는 방식이 바뀌면 같은 시드로 기록한 진행을 다시 돌릴 수 없다. 앞의 값을 고정해 두어 바뀌면 알 수 있게 한다.
        // 값은 HMAC-SHA256(키: SHA-256(73의 4바이트 리틀 엔디언), 내용: 순번의 8바이트 리틀 엔디언)을
        // 8바이트씩 리틀 엔디언으로 읽은 것이다. 서버 코드와 따로 파이썬 hmac 모듈로 계산했다.
        Equal(first.NextUInt64(), 0xcfffbf307298db7dUL);
        Equal(first.NextUInt64(), 0x3e7572c12c53074cUL);
        Equal(first.NextUInt64(), 0xa0884ace64d02784UL);
        Equal(first.NextUInt64(), 0xbacfd71ed5c8bd5eUL);
        // 다섯 번째 값은 다음 순번의 블록에서 나온다.
        Equal(first.NextUInt64(), 0x8ec2b4eb95be553eUL);
        Equal(first.Position, 40L);
        for (int i = 0; i < 5; i++)
        {
            again.NextUInt64();
        }

        // 같은 시드는 같은 순서로 같은 값을 낸다. 다른 시드는 다른 값을 낸다.
        bool differs = false;
        bool[] seen = new bool[3];
        for (int i = 0; i < 1000; i++)
        {
            int value = first.NextInt(5, 8);
            Equal(again.NextInt(5, 8), value);
            Check(value >= 5 && value < 8, $"범위 밖의 값 {value}가 나왔습니다");
            seen[value - 5] = true;
            if (other.NextInt(5, 8) != value)
            {
                differs = true;
            }

            double fraction = first.NextDouble();
            Equal(again.NextDouble(), fraction);
            Check(fraction >= 0 && fraction < 1, $"범위 밖의 실수 {fraction}가 나왔습니다");
            other.NextDouble();

            int id = first.NextInt(1, int.MaxValue);
            Equal(again.NextInt(1, int.MaxValue), id);
            Check(id >= 1, $"범위 밖의 id {id}가 나왔습니다");
            other.NextInt(1, int.MaxValue);
        }
        Check(differs, "다른 시드가 같은 값을 냈습니다");
        for (int i = 0; i < seen.Length; i++)
        {
            Check(seen[i], $"{i + 5}가 한 번도 나오지 않았습니다");
        }
        Equal(again.Position, first.Position);
    }

    private static void SameSeedEnemies()
    {
        // 적 id는 스냅샷으로 공개되고, 잡기 대상이 같은 거리일 때 순서를 정한다. 시드가 같으면 같아야 재현할 수 있다.
        Rules rules = new Rules { AllowEliteEnemies = true };
        Match first = new Match("SEED01", rules, 73);
        Match again = new Match("SEED01", rules, 73);
        Match other = new Match("SEED01", rules, 74);
        Check(first.Enemies.Count > 0, "처음 배치된 적이 없습니다");
        Equal(again.Enemies.Count, first.Enemies.Count);
        for (int i = 0; i < first.Enemies.Count; i++)
        {
            Equal(again.Enemies[i].Id, first.Enemies[i].Id);
        }
        Check(other.Enemies[0].Id != first.Enemies[0].Id, "다른 시드인데 같은 적 id가 나왔습니다");
        Equal(again.StateHash(), first.StateHash());
        Check(other.StateHash() != first.StateHash(), "다른 시드인데 상태 요약이 같습니다");
        // 규칙이나 방 코드만 다른 방은 처음 적이 같아도 요약이 달라야 한다. 재생할 때 규칙을 잘못 넣은 실수를 바로 잡는다.
        Match slower = new Match("SEED01", new Rules { AllowEliteEnemies = true, SpawnInterval = 6 }, 73);
        Equal(slower.Enemies[0].Id, first.Enemies[0].Id);
        Check(slower.StateHash() != first.StateHash(), "규칙만 다른데 상태 요약이 같습니다");
        Match renamed = new Match("SEED02", rules, 73);
        Check(renamed.StateHash() != first.StateHash(), "방 코드만 다른데 상태 요약이 같습니다");
    }

    // 각본대로 진행하며 모은 결과다.
    private sealed class Run
    {
        public List<string> Hashes { get; } = new List<string>();
        public HashSet<int> EnemyIds { get; } = new HashSet<int>();
        public HashSet<int> EnemyActions { get; } = new HashSet<int>();
        public HashSet<int> PlayerActions { get; } = new HashSet<int>();
        public bool SawElite;
        public int Kills;
        public int Deaths;
    }

    // 대원 넷이 캠프 둘레를 돌며 기술 네 개를 번갈아 쓴다. 넣는 입력은 틱 번호와 대원 상태로만 정해지므로
    // 두 진행의 상태가 같으면 입력도 같다. changedTick 틱에만 첫 대원의 조준을 조금 바꾼다. -1이면 바꾸지 않는다.
    // 기본 체력의 네 명이면 시드 70~79 모두에서 90초 동안 작전이 이어지며 사망과 재투입, 처치를 거쳤다.
    private static Run Play(int seed, int ticks, int changedTick)
    {
        // 엘리트와 잦은 증원으로 난수를 쓰는 경로를 모두 지나게 한다.
        Rules rules = new Rules { AllowEliteEnemies = true, SpawnInterval = 3 };
        Match match = new Match("SEED01", rules, seed);
        Player[] squad = new Player[]
        {
            match.Join("a", "alpha")!, match.Join("b", "bravo")!, match.Join("c", "charlie")!, match.Join("d", "delta")!
        };
        Run run = new Run();
        long sequence = 0;
        for (int tick = 0; tick < ticks; tick++)
        {
            for (int i = 0; i < squad.Length; i++)
            {
                Player player = squad[i];
                if (player.State == "waiting")
                {
                    match.Deploy(player, match.Layout.Camp.X.ToDouble() + i * 30, match.Layout.Camp.Y.ToDouble());
                    continue;
                }
                double heading = tick * 0.05;
                double aim = tick * 0.1 + i;
                if (tick == changedTick && i == 0)
                {
                    aim += 0.001;
                }
                int slot = 0;
                if (tick % 8 == 0)
                {
                    slot = 1 + (tick / 8 + i) % 4;
                }
                sequence++;
                match.Input(player, sequence, Math.Cos(heading), Math.Sin(heading), aim, slot);
            }
            match.Step();
            run.Hashes.Add(match.StateHash());
            for (int i = 0; i < match.Enemies.Count; i++)
            {
                Enemy enemy = match.Enemies[i];
                run.EnemyIds.Add(enemy.Id);
                if (enemy.Definition.Id != "grunt")
                {
                    run.SawElite = true;
                }
                if (enemy.Action != null)
                {
                    run.EnemyActions.Add(enemy.Action.Id);
                }
            }
            for (int i = 0; i < squad.Length; i++)
            {
                for (int j = 0; j < squad[i].Actions.Count; j++)
                {
                    run.PlayerActions.Add(squad[i].Actions[j].Id);
                }
            }
        }
        for (int i = 0; i < squad.Length; i++)
        {
            run.Kills += squad[i].Kills;
            run.Deaths += squad[i].Deaths;
        }
        return run;
    }

    private static void SameSeedSameRun()
    {
        const int ticks = 90 * Rules.TicksPerSecond;
        Run first = Play(73, ticks, -1);
        Run again = Play(73, ticks, -1);
        // 각본이 실제로 전투를 거쳤는지 본다. 아무 일도 없으면 같은 결과가 나오는 것이 당연하다.
        Check(first.EnemyIds.Count > 10, $"증원이 {first.EnemyIds.Count}마리뿐입니다");
        Check(first.SawElite, "엘리트가 나오지 않았습니다");
        Check(first.EnemyActions.Count > 30, $"적 행동이 {first.EnemyActions.Count}번뿐입니다");
        Check(first.PlayerActions.Count > 30, $"대원 기술이 {first.PlayerActions.Count}번뿐입니다");
        Check(first.Kills > 0, "처치가 없습니다");
        Check(first.Deaths > 0, "사망이 없습니다");
        for (int tick = 0; tick < ticks; tick++)
        {
            if (again.Hashes[tick] != first.Hashes[tick])
            {
                throw new Exception($"{tick + 1}번째 틱에서 상태가 갈라졌습니다");
            }
        }

        // 요약이 실제로 차이를 잡아내는지 본다. 한 틱의 조준만 0.001 바꿔도 그 틱부터 다르다.
        const int changedTick = 30 * Rules.TicksPerSecond;
        Run changed = Play(73, ticks, changedTick);
        Equal(changed.Hashes[changedTick - 1], first.Hashes[changedTick - 1]);
        Check(changed.Hashes[changedTick] != first.Hashes[changedTick], "입력이 달라졌는데 상태 요약이 같습니다");
        // 시드가 다르면 처음부터 다르다.
        Run other = Play(74, Rules.TicksPerSecond, -1);
        Check(other.Hashes[0] != first.Hashes[0], "다른 시드인데 상태 요약이 같습니다");
    }

    private static Room StartRoom()
    {
        Match match = new Match("BATCH1", new Rules(), 73);
        Player player = match.Join("p", "pilot")!;
        player.Connected = true;
        Check(match.Deploy(player, match.Layout.Camp.X.ToDouble(), match.Layout.Camp.Y.ToDouble()));
        Advance(match, match.Rules.DeploySeconds + 0.01);
        Equal(player.State, "alive");
        player.Mana = player.MaxMana;
        return new Room(match, null);
    }

    private static void TickBatching()
    {
        using RoomHost host = NewHost();
        Room single = StartRoom();
        Room batched = StartRoom();
        // 한 번에 1·2·3·6틱을 진행하면 합이 12틱이다. 입력은 두 방 모두 12틱마다 같은 틱에 넣는다.
        int[] batches = new int[] { 1, 2, 3, 6 };
        long sequence = 0;
        for (int round = 0; round < 60; round++)
        {
            sequence++;
            double heading = round * 0.7;
            int slot = 1 + round % 4;
            single.Match.Input(single.Match.Players[0], sequence, Math.Cos(heading), Math.Sin(heading), heading, slot);
            batched.Match.Input(batched.Match.Players[0], sequence, Math.Cos(heading), Math.Sin(heading), heading, slot);
            for (int i = 0; i < 12; i++)
            {
                host.StepRoom(single.Match.Code, single, 1, false);
            }
            for (int i = 0; i < batches.Length; i++)
            {
                host.StepRoom(batched.Match.Code, batched, batches[i], false);
            }
            Equal(batched.Match.Tick, single.Match.Tick);
            Equal(batched.Match.StateHash(), single.Match.StateHash());
        }
    }

    private static void DeadlineTicks()
    {
        // 초로 정한 기간은 틱으로 올려 센다. 정수 틱이 되어야 할 기간이 실수 오차로 한 틱 늘지 않는다.
        Equal(Rules.ToTicks(5), 150L);
        Equal(Rules.ToTicks(1.7), 51L);
        Equal(Rules.ToTicks(0.1), 3L);
        Equal(Rules.ToTicks(0.18), 6L);
        Equal(Rules.ToTicks(0), 0L);

        // 시작 틱이 달라도 5초 투입은 언제나 150틱 걸린다. 마감을 초로 저장하던 때는 시작 틱 600개 중 45개에서 151틱이었다.
        for (int start = 0; start < 300; start++)
        {
            Match match = new Match("LATE01", new Rules { MaxEnemies = 0 }, 1);
            Player player = match.Join("p", "pilot")!;
            for (int i = 0; i < start; i++)
            {
                match.Step();
            }
            Check(match.Deploy(player, match.Layout.Camp.X.ToDouble(), match.Layout.Camp.Y.ToDouble()));
            int ticks = 0;
            while (player.State != "alive" && ticks < 200)
            {
                match.Step();
                ticks++;
            }
            if (ticks != 150)
            {
                throw new Exception($"{start}틱에 시작한 투입이 {ticks}틱 걸렸습니다");
            }
        }

        // 적 행동의 각 단계도 시작 틱과 관계없이 같은 틱 수 뒤에 온다. 일반 적의 타격 예고는 배율 2에서 1.7초다.
        for (long start = 0; start < 300; start++)
        {
            EnemyAction action = new EnemyAction(1, EnemyDefinition.Grunt.Skills[0], Angle.Zero, start, 2);
            Equal(action.ContactTick - action.StartedTick, 51L);
            // 공격 창 0.25초를 더한 1.95초는 58.5틱이라 59틱째에 끝나고, 회복 0.65초를 더한 2.6초는 78틱이다.
            Equal(action.ActiveUntilTick - action.StartedTick, 59L);
            Equal(action.EndsTick - action.StartedTick, 78L);
        }
    }

    private static void TickClockWakes()
    {
        // 서버 타이머와 같은 주기(1/30초를 밀리초 단위로 내린 33ms)로 10초 동안 깨어난다. 예정보다 0~2ms 늦을 수 있다.
        double period = TickClock.TimerPeriod.TotalSeconds;
        int wakes = (int)(10 / period);
        TickClock clock = new TickClock();
        Random random = new Random(7);
        long total = 0;
        int idle = 0;
        double current = 0;
        for (int wake = 1; wake <= wakes; wake++)
        {
            current = wake * period + random.NextDouble() * 0.002;
            int ticks = clock.Due(current);
            Check(ticks >= 0 && ticks <= 2, $"한 번 깨어나 {ticks}틱을 진행했습니다");
            if (ticks == 0)
            {
                idle++;
            }
            total += ticks;
        }
        // 한 틱에 못 미치는 시간은 다음으로 넘기므로, 지난 시간만큼의 틱을 빠짐없이 진행한다.
        Equal(total, (long)Math.Floor(current * Rules.TicksPerSecond));
        // 타이머 주기가 틱보다 조금 짧아서 가끔은 진행할 틱 없이 깨어난다.
        Check(idle > 0, "진행할 틱 없이 깨어난 적이 없습니다");

        // 서버가 5초 멈췄다 깨어나면 상한만큼만 진행하고 나머지는 버린다. 그다음부터는 평소처럼 진행한다.
        double resume = current + 5;
        Equal(clock.Due(resume), TickClock.MaxTicksPerWake);
        int next = clock.Due(resume + period);
        Check(next <= 1, $"멈춘 뒤 다음 깨어남에서 {next}틱을 진행했습니다");
    }
}
