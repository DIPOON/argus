namespace Argus.Server.Game;

// 방의 진행을 다시 돌릴 수 있게 남기는 기록 한 줄이다. 종류(Type)는 다음 가운데 하나다.
// 바깥 요청: join, loadout, deploy, input, disconnect, await, connect
// 확인용 상태 요약값: hash / 버그 제보: report(그 순간), reportNote(나중에 덧붙인 내용) / 기록의 끝: end
// 파일에는 JSON 한 줄로 저장하며, 종류에 따라 쓰지 않는 값은 비워 둔다(null).
public sealed class MatchRecord
{
    // 이 틱을 진행한 뒤, 다음 틱을 진행하기 전에 반영했다. 요약값은 이 틱을 진행한 직후의 값이다.
    public long Tick { get; set; }
    public string Type { get; set; } = "";
    public string? Player { get; set; }
    public string? Name { get; set; }
    public string[]? Slots { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public long? Sequence { get; set; }
    public double? MoveX { get; set; }
    public double? MoveY { get; set; }
    public double? Aim { get; set; }
    public int? Slot { get; set; }
    public string? Hash { get; set; }
    // 제보 내용, 또는 기록이 끝난 이유다.
    public string? Text { get; set; }
    // reportNote가 내용을 덧붙이는 제보의 틱이다.
    public long? About { get; set; }

    public MatchRecord(long tick, string type)
    {
        Tick = tick;
        Type = type;
    }
}

// 기록 파일의 첫 줄이다. 같은 방을 처음부터 다시 만들 때 필요한 값을 담는다.
// 시드가 들어 있으므로 이 파일은 서버 밖으로 내보내지 않는다. 시드를 알면 진행 중인 방의 숨은 적을 미리 알 수 있다.
public sealed class RecordingHeader
{
    // 기록 형식의 판 번호다. 줄의 모양을 바꾸면 올린다.
    public int Format { get; set; } = 1;
    // 기록한 서버의 코드 버전(어셈블리 버전과 커밋)이다. 다른 코드로 재생하면 같은 결과가 나오지 않을 수 있다.
    public string Version { get; set; } = "";
    public string Room { get; set; } = "";
    // 32바이트 시드를 16진수로 적는다.
    public string Seed { get; set; } = "";
    public Rules Rules { get; set; } = new Rules();
    // 사람이 기록을 찾을 때 보는 시각이다. 재생에는 쓰지 않는다.
    public DateTime Created { get; set; }
}

// 기록을 받아 어딘가에 쓰는 쪽이다. Match는 방 잠금 안에서 부르므로 오래 붙잡지 말고 바로 돌아와야 한다.
public interface IMatchRecorder
{
    void Write(MatchRecord record);
}
