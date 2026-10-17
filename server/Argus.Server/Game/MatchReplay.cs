namespace Argus.Server.Game;

// 기록에 남은 버그 제보다. 틱은 제보 버튼을 누른 순간이고, 내용은 그 뒤에 덧붙인 것이다.
public sealed class ReplayReport
{
    public long Tick { get; }
    public string? Player { get; }
    // 제보와 함께 온 내용이다. 공식 클라이언트는 버튼을 누르는 순간 내용 없이 보낸다.
    public string? Text { get; }
    // 나중에 덧붙인 내용과 그 틱이다. 덧붙이지 않았으면 null이다.
    public string? Note { get; set; }
    public long? NoteTick { get; set; }

    public ReplayReport(long tick, string? player, string? text)
    {
        Tick = tick;
        Player = player;
        Text = text;
    }
}

// 기록을 다시 돌린 결과다. Match에는 멈춘 시점의 방 상태가 그대로 남아 있어 그 순간을 살펴볼 수 있다.
public sealed class ReplayResult
{
    public Match Match { get; }
    // 기록과 같았던 상태 요약값의 수다.
    public int Checkpoints { get; set; }
    // 처음으로 상태 요약값이 기록과 달라진 틱이다. 끝까지 같으면 null이다.
    public long? DivergedAt { get; set; }
    public string? ExpectedHash { get; set; }
    public string? ActualHash { get; set; }
    // 기록의 끝 줄까지 갔는지와 끝난 이유다. 서버가 갑자기 꺼졌거나 기록이 아직 쓰이는 중이면 끝 줄이 없다.
    public bool Ended { get; set; }
    public string? EndReason { get; set; }
    // 진행 중 난 예외다. 서버처럼 다음 틱을 계속 진행하므로 몇 틱에서 났는지와 메시지를 모아 둔다.
    public List<string> Failures { get; } = new List<string>();
    // 이해할 수 없는 줄이다. 형식이 깨졌거나 이 코드가 모르는 종류다.
    public List<string> Problems { get; } = new List<string>();

    public ReplayResult(Match match)
    {
        Match = match;
    }
}

// 기록한 방을 처음부터 다시 돌린다. 같은 코드라면 틱마다 기록할 때와 같은 상태가 된다(결정론).
public static class MatchReplay
{
    // 기록 전체에서 버그 제보를 모은다. 나중에 덧붙인 내용은 같은 대원의 같은 틱 제보에 이어 붙인다.
    // 제보는 진행 상태를 바꾸지 않으므로 재생과 따로 모은다. 그래서 --until로 일찍 멈춰도 모든 제보를 볼 수 있다.
    public static List<ReplayReport> Reports(IEnumerable<MatchRecord> records)
    {
        List<ReplayReport> reports = new List<ReplayReport>();
        foreach (MatchRecord record in records)
        {
            if (record.Type == "report")
            {
                reports.Add(new ReplayReport(record.Tick, record.Player, record.Text));
            }
            else if (record.Type == "reportNote")
            {
                for (int i = 0; i < reports.Count; i++)
                {
                    if (reports[i].Player == record.Player && reports[i].Tick == record.About)
                    {
                        reports[i].Note = record.Text;
                        reports[i].NoteTick = record.Tick;
                    }
                }
            }
        }
        return reports;
    }

    // untilTick을 주면 그 틱을 진행하고 그 틱에 들어온 요청까지 반영한 뒤 멈춘다. 그 순간의 상태를 살펴볼 때 쓴다.
    // 요약값이 달라지면 그 틱에서 멈춘다. 그 뒤로는 기록과 다른 길이라 더 돌려도 뜻이 없다.
    public static ReplayResult Run(RecordingHeader header, IEnumerable<MatchRecord> records, long untilTick = long.MaxValue)
    {
        byte[] seed = Convert.FromHexString(header.Seed);
        Match match = new Match(header.Room, header.Rules, seed);
        ReplayResult result = new ReplayResult(match);
        foreach (MatchRecord record in records)
        {
            if (record.Tick > untilTick)
            {
                AdvanceTo(match, untilTick, result);
                return result;
            }
            AdvanceTo(match, record.Tick, result);
            Apply(match, record, result);
            if (result.DivergedAt.HasValue || result.Ended)
            {
                return result;
            }
        }
        return result;
    }

    // 서버의 StepRoom처럼 진행 중 예외가 나도 기록해 두고 다음 틱을 계속 진행한다.
    private static void AdvanceTo(Match match, long tick, ReplayResult result)
    {
        while (match.Tick < tick)
        {
            try
            {
                match.Step();
            }
            catch (Exception error)
            {
                result.Failures.Add($"{match.Tick}틱: {error.GetType().Name}: {error.Message}");
            }
        }
    }

    private static void Apply(Match match, MatchRecord record, ReplayResult result)
    {
        if (record.Type == "hash")
        {
            string actual = match.StateHash();
            if (actual == record.Hash)
            {
                result.Checkpoints++;
                return;
            }
            result.DivergedAt = record.Tick;
            result.ExpectedHash = record.Hash;
            result.ActualHash = actual;
            return;
        }
        // 제보는 진행 상태를 바꾸지 않는다. Reports가 따로 모은다.
        if (record.Type == "report" || record.Type == "reportNote")
        {
            return;
        }
        if (record.Type == "end")
        {
            result.Ended = true;
            result.EndReason = record.Text;
            return;
        }
        if (record.Type == "join")
        {
            if (record.Player == null || record.Name == null)
            {
                result.Problems.Add($"{record.Tick}틱: 참가 줄에 대원 id나 이름이 없습니다");
                return;
            }
            match.Join(record.Player, record.Name);
            return;
        }

        // 나머지는 이미 참가한 대원에 대한 요청이다. 방에서 빠진 대원에 대한 요청은 서버에서도 방 상태를 바꾸지 않았으므로 건너뛴다.
        Player? player = FindPlayer(match, record.Player);
        if (player == null)
        {
            return;
        }
        if (record.Type == "loadout")
        {
            if (record.Slots == null)
            {
                result.Problems.Add($"{record.Tick}틱: 장비 줄에 슬롯이 없습니다");
                return;
            }
            match.SetLoadout(player, record.Slots);
        }
        else if (record.Type == "deploy")
        {
            match.Deploy(player, Value(record.X), Value(record.Y));
        }
        else if (record.Type == "input")
        {
            long sequence = -1;
            if (record.Sequence.HasValue)
            {
                sequence = record.Sequence.Value;
            }
            int slot = -1;
            if (record.Slot.HasValue)
            {
                slot = record.Slot.Value;
            }
            match.Input(player, sequence, Value(record.MoveX), Value(record.MoveY), Value(record.Aim), slot);
        }
        else if (record.Type == "disconnect")
        {
            match.Disconnect(player);
        }
        else if (record.Type == "await")
        {
            match.AwaitConnection(player);
        }
        else if (record.Type == "connect")
        {
            match.Connect(player);
        }
        else
        {
            result.Problems.Add($"{record.Tick}틱: 모르는 종류 {record.Type}");
        }
    }

    // 값이 빠진 줄은 서버에서도 거절될 값(NaN)으로 넘긴다.
    private static double Value(double? value)
    {
        if (value.HasValue)
        {
            return value.Value;
        }
        return double.NaN;
    }

    private static Player? FindPlayer(Match match, string? id)
    {
        if (id == null)
        {
            return null;
        }
        for (int i = 0; i < match.Players.Count; i++)
        {
            if (match.Players[i].Id == id)
            {
                return match.Players[i];
            }
        }
        return null;
    }
}
