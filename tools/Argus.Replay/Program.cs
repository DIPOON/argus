using System.Globalization;
using System.Text;
using Argus.Server.Game;
using Argus.Server.Networking;
using Argus.Server.Recording;

// 서버가 남긴 기록 파일을 다시 돌려, 같은 상황이 그대로 재현되는지 확인하고 원하는 순간의 상태를 보여 준다.
// 같은 코드 버전이어야 같은 결과가 나온다. 기록에는 방의 비밀 시드가 들어 있으므로 기록 파일을 서버 밖에 공개하지 않는다.
internal static class Program
{
    private const string Usage = """
        사용법:
          dotnet run --project tools/Argus.Replay -- <기록 파일>
              끝까지 다시 돌려 재현되는지 확인하고, 버그 제보 목록을 보여 준다.
          dotnet run --project tools/Argus.Replay -- <기록 파일> --until <틱> [--snapshot <대원 id>]
              그 틱을 진행하고 그 틱의 요청까지 반영한 순간에 멈춰 방 상태를 보여 준다.
              --snapshot을 주면 그 대원이 그 순간 받은 스냅샷 JSON도 출력한다.
          dotnet run --project tools/Argus.Replay -- <폴더>
              폴더의 기록을 모두 확인한다. 하나라도 갈라지거나 읽지 못하면 실패로 끝난다.
        종료 코드: 모두 재현되면 0, 갈라졌거나 읽지 못했으면 1, 사용법이 틀리면 2
        """;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        string path = args[0];
        long untilTick = long.MaxValue;
        string? snapshotPlayer = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--until" && i + 1 < args.Length && long.TryParse(args[i + 1], out long tick) && tick >= 0)
            {
                untilTick = tick;
                i++;
            }
            else if (args[i] == "--snapshot" && i + 1 < args.Length)
            {
                snapshotPlayer = args[i + 1];
                i++;
            }
            else
            {
                Console.Error.WriteLine($"알 수 없는 인자: {args[i]}");
                Console.Error.WriteLine(Usage);
                return 2;
            }
        }
        if (Directory.Exists(path))
        {
            return CheckFolder(path);
        }
        if (File.Exists(path))
        {
            return ReplayFile(path, untilTick, snapshotPlayer);
        }
        Console.Error.WriteLine($"기록 파일이나 폴더가 없습니다: {path}");
        return 2;
    }

    // 폴더의 기록을 모두 다시 돌린다. 다른 코드 버전으로 만든 기록은 같은 결과가 나오지 않을 수 있어 건너뛴다.
    private static int CheckFolder(string folder)
    {
        string[] files = Directory.GetFiles(folder, "*" + RecordingFormat.Extension);
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
        {
            Console.WriteLine($"확인할 기록이 없습니다: {folder}");
            return 1;
        }
        int failed = 0;
        int skipped = 0;
        for (int i = 0; i < files.Length; i++)
        {
            string name = Path.GetFileName(files[i]);
            RecordingContent content;
            try
            {
                content = RecordingReader.Read(files[i]);
            }
            catch (Exception error)
            {
                failed++;
                Console.WriteLine($"읽지 못함  {name}: {error.Message}");
                continue;
            }
            if (content.Header.Version != RecordingFormat.CodeVersion)
            {
                skipped++;
                Console.WriteLine($"건너뜀    {name}: 다른 코드 버전({content.Header.Version})으로 만든 기록");
                continue;
            }
            ReplayResult result = MatchReplay.Run(content.Header, content.Records);
            if (result.DivergedAt.HasValue || result.Problems.Count > 0)
            {
                failed++;
                Console.WriteLine($"갈라짐    {name}: {Describe(result, false)}");
                continue;
            }
            Console.WriteLine($"재현됨    {name}: {Describe(result, false)}");
        }
        Console.WriteLine($"기록 {files.Length}개 · 재현 {files.Length - failed - skipped}개 · 실패 {failed}개 · 건너뜀 {skipped}개");
        if (failed > 0)
        {
            return 1;
        }
        return 0;
    }

    private static int ReplayFile(string path, long untilTick, string? snapshotPlayer)
    {
        RecordingContent content = RecordingReader.Read(path);
        RecordingHeader header = content.Header;
        Console.WriteLine($"기록: {path}");
        Console.WriteLine($"방 {header.Room} · 만든 시각 {header.Created.ToString("u", CultureInfo.InvariantCulture)} · 코드 {header.Version}");
        if (header.Version != RecordingFormat.CodeVersion)
        {
            Console.WriteLine($"주의: 지금 도구는 {RecordingFormat.CodeVersion}입니다. 코드가 다르면 같은 결과가 나오지 않을 수 있습니다.");
        }
        if (content.Truncated)
        {
            Console.WriteLine("주의: 파일 끝이 잘려 있어 읽을 수 있는 데까지만 다시 돌립니다.");
        }
        PrintPlayers(content.Records);

        ReplayResult result = MatchReplay.Run(header, content.Records, untilTick);
        Console.WriteLine($"결과: {Describe(result, untilTick != long.MaxValue)}");
        if (result.DivergedAt.HasValue)
        {
            Console.WriteLine($"  기록한 요약값 {result.ExpectedHash}");
            Console.WriteLine($"  재생한 요약값 {result.ActualHash}");
            Console.WriteLine($"  {result.DivergedAt.Value - Rules.TicksPerSecond}틱까지는 같았습니다. 그 사이에서 원인을 찾으세요.");
        }
        for (int i = 0; i < result.Failures.Count; i++)
        {
            Console.WriteLine($"진행 중 예외: {result.Failures[i]}");
        }
        for (int i = 0; i < result.Problems.Count; i++)
        {
            Console.WriteLine($"읽지 못한 줄: {result.Problems[i]}");
        }
        PrintReports(content.Records);
        if (untilTick != long.MaxValue)
        {
            PrintState(result.Match);
            if (snapshotPlayer != null)
            {
                PrintSnapshot(result.Match, snapshotPlayer);
            }
        }
        if (result.DivergedAt.HasValue || result.Problems.Count > 0)
        {
            return 1;
        }
        return 0;
    }

    // stopped는 --until로 멈추게 했는지다. 그때는 끝 줄이 없는 것이 당연하다.
    private static string Describe(ReplayResult result, bool stopped)
    {
        StringBuilder text = new StringBuilder();
        if (result.DivergedAt.HasValue)
        {
            text.Append($"{result.DivergedAt.Value}틱에서 요약값이 기록과 다릅니다");
        }
        else
        {
            text.Append("재현됨");
        }
        text.Append($" · {result.Match.Tick}틱({Clock(result.Match.Tick)}) · 요약값 {result.Checkpoints}개 일치");
        if (result.Ended)
        {
            text.Append($" · 끝: {result.EndReason}");
        }
        else if (stopped && !result.DivergedAt.HasValue)
        {
            text.Append($" · --until {result.Match.Tick}에서 멈춤");
        }
        else
        {
            text.Append(" · 끝 줄 없음(쓰는 중이었거나 서버가 갑자기 꺼짐)");
        }
        if (result.Failures.Count > 0)
        {
            text.Append($" · 진행 중 예외 {result.Failures.Count}번");
        }
        if (result.Reports.Count > 0)
        {
            text.Append($" · 제보 {result.Reports.Count}건");
        }
        return text.ToString();
    }

    // 틱을 mm:ss.f 모양의 시각으로 바꾼다.
    private static string Clock(long tick)
    {
        double seconds = Rules.ToSeconds(tick);
        int minutes = (int)(seconds / 60);
        return $"{minutes:00}:{seconds - minutes * 60:00.0}";
    }

    private static void PrintPlayers(List<MatchRecord> records)
    {
        StringBuilder text = new StringBuilder("대원:");
        int count = 0;
        for (int i = 0; i < records.Count; i++)
        {
            if (records[i].Type == "join")
            {
                count++;
                text.Append($" {records[i].Player}({records[i].Name}, {records[i].Tick}틱)");
            }
        }
        if (count == 0)
        {
            text.Append(" 없음");
        }
        Console.WriteLine(text.ToString());
    }

    // 제보 목록과, 그 순간을 다시 보는 명령을 보여 준다.
    private static void PrintReports(List<MatchRecord> records)
    {
        for (int i = 0; i < records.Count; i++)
        {
            MatchRecord record = records[i];
            if (record.Type != "report")
            {
                continue;
            }
            Console.WriteLine($"제보: {record.Tick}틱({Clock(record.Tick)}) 대원 {record.Player}: \"{record.Text}\" → 다시 보기 --until {record.Tick} --snapshot {record.Player}");
        }
    }

    private static void PrintState(Match match)
    {
        Console.WriteLine($"상태: {match.Tick}틱({Clock(match.Tick)}) · 단계 {match.Phase} · 임무 경과 {Clock(match.ElapsedTicks)} · 전역 충격 {match.Pulse}회 · 탈출 {match.ExtractionProgress:0.0}초");
        for (int i = 0; i < match.Players.Count; i++)
        {
            Player player = match.Players[i];
            StringBuilder line = new StringBuilder();
            line.Append($"  대원 {player.Id} {player.Name} · {player.State} · 체력 {player.Hp:0.#}/{player.MaxHp:0.#} · 마나 {player.Mana}/{player.MaxMana}");
            line.Append($" · 위치 ({player.Position.X:0.0}, {player.Position.Y:0.0}) · 조준 {player.Aim:0.00} · 연결 {player.Connected}");
            for (int j = 0; j < player.Actions.Count; j++)
            {
                PlayerAction action = player.Actions[j];
                line.Append($" · 기술 {action.Skill.Id}(시작 {action.StartedTick}, 접촉 {action.ContactTick}, 유효 {action.ActiveUntilTick}까지)");
            }
            Console.WriteLine(line.ToString());
        }
        Console.WriteLine($"  적 {match.Enemies.Count}마리");
        for (int i = 0; i < match.Enemies.Count; i++)
        {
            Enemy enemy = match.Enemies[i];
            StringBuilder line = new StringBuilder();
            line.Append($"    {enemy.Id} {enemy.Definition.Id} · 체력 {enemy.Hp:0.#} · 위치 ({enemy.Position.X:0.0}, {enemy.Position.Y:0.0})");
            if (enemy.Action != null)
            {
                line.Append($" · 행동 {enemy.Action.Skill.Kind}(시작 {enemy.Action.StartedTick}, 접촉 {enemy.Action.ContactTick}, 끝 {enemy.Action.EndsTick})");
            }
            line.Append($" · 보임 {match.Visible(enemy.Position)}");
            Console.WriteLine(line.ToString());
        }
        StringBuilder facilities = new StringBuilder("  시설");
        for (int i = 0; i < match.Facilities.Count; i++)
        {
            facilities.Append($" {match.Facilities[i].Id}:{match.Facilities[i].Hp:0}/{match.Facilities[i].MaxHp:0}");
        }
        Console.WriteLine(facilities.ToString());
    }

    // 그 대원이 그 순간 받았을 스냅샷이다. 시야 밖 적은 서버가 보낼 때처럼 빠져 있다.
    private static void PrintSnapshot(Match match, string playerId)
    {
        for (int i = 0; i < match.Players.Count; i++)
        {
            if (match.Players[i].Id == playerId)
            {
                byte[] json = Snapshot.Create(match, match.Players[i], RoomHost.Json);
                Console.WriteLine("스냅샷:");
                Console.WriteLine(Encoding.UTF8.GetString(json));
                return;
            }
        }
        Console.WriteLine($"그 순간 방에 대원 {playerId}가 없습니다.");
    }
}
