using Argus.Server.Game;

namespace Argus.Server.Recording;

// 기록 하나를 다시 돌려 본 판정이다.
public enum CheckOutcome
{
    // 끝 줄까지 같은 상태로 재현됐고 진행 중 예외도 없다.
    Reproduced,
    // 요약값은 모두 같았지만 끝 줄이 없거나, 파일 끝이 잘렸거나, 진행 중 예외가 났다.
    // 서버를 정상 종료한 뒤 확인할 때(CI)는 이것도 실패다. 쓰는 중인 기록이나 서버가 갑자기 꺼진 기록이면 그럴 수 있다.
    Incomplete,
    // 요약값이 기록과 달라졌거나, 이해할 수 없는 줄이 있다.
    Diverged,
    // 파일을 읽지 못했다.
    Unreadable,
    // 다른 코드 버전으로 만든 기록이라 확인하지 않았다. 코드가 다르면 같은 결과가 나오지 않을 수 있다.
    Skipped
}

public sealed class CheckResult
{
    public CheckOutcome Outcome { get; }
    // 판정의 까닭이다. 재현됐으면 비어 있다.
    public string Reason { get; }
    // 다시 돌린 결과다. 읽지 못했거나 건너뛰었으면 null이다.
    public ReplayResult? Replay { get; }

    public CheckResult(CheckOutcome outcome, string reason, ReplayResult? replay)
    {
        Outcome = outcome;
        Reason = reason;
        Replay = replay;
    }
}

public static class RecordingCheck
{
    public static CheckResult Check(string path)
    {
        RecordingContent content;
        try
        {
            content = RecordingReader.Read(path);
        }
        catch (Exception error)
        {
            return new CheckResult(CheckOutcome.Unreadable, error.Message, null);
        }
        if (content.Header.Version != RecordingFormat.CodeVersion)
        {
            return new CheckResult(CheckOutcome.Skipped, $"다른 코드 버전({content.Header.Version})으로 만든 기록", null);
        }
        ReplayResult replay = MatchReplay.Run(content.Header, content.Records);
        if (replay.DivergedAt.HasValue)
        {
            return new CheckResult(CheckOutcome.Diverged, $"{replay.DivergedAt.Value}틱에서 요약값이 다름", replay);
        }
        if (replay.Problems.Count > 0)
        {
            return new CheckResult(CheckOutcome.Diverged, $"이해할 수 없는 줄 {replay.Problems.Count}개: {replay.Problems[0]}", replay);
        }
        List<string> missing = new List<string>();
        if (!replay.Ended)
        {
            missing.Add("끝 줄 없음");
        }
        if (content.Truncated)
        {
            missing.Add("파일 끝이 잘림");
        }
        if (replay.Failures.Count > 0)
        {
            missing.Add($"진행 중 예외 {replay.Failures.Count}번({replay.Failures[0]})");
        }
        if (missing.Count > 0)
        {
            return new CheckResult(CheckOutcome.Incomplete, string.Join(", ", missing), replay);
        }
        return new CheckResult(CheckOutcome.Reproduced, "", replay);
    }
}
