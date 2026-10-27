using System.Text.Json.Serialization;

namespace Argus.Server.Game;

// 모든 위치·피해·기술 시간은 서버가 계산한 값이다. JSON 속성명은 camelCase다.
// 팀 전체가 똑같이 받는 부분만 담는다. 수신자별 값(you, 보급, 벽·규칙·맵)은 PersonalSnapshot에 있고
// 직렬화한 뒤 이어 붙이므로, 이 클래스에는 수신자별 속성을 두지 않는다.
public sealed class SnapshotMessage
{
    public string Type { get; set; } = "snapshot";
    public int Version { get; set; } = 5;
    public string Room { get; set; } = "";
    public double Now { get; set; }
    public double Elapsed { get; set; }
    public string Phase { get; set; } = "";
    public string? Result { get; set; }
    // 분대가 탈출 지점의 칸을 보기 전에는 null이다.
    public ExtractionSnapshot? Extraction { get; set; }
    public int MapRevision { get; set; }
    public List<PlayerSnapshot> Players { get; set; } = new List<PlayerSnapshot>();
    public List<EnemySnapshot> Enemies { get; set; } = new List<EnemySnapshot>();
    public List<EffectSnapshot> Effects { get; set; } = new List<EffectSnapshot>();
    // 정수 좌표라 double보다 직렬화가 빠르다. 전송되는 JSON은 같다.
    public List<int[]> Sight { get; set; } = new List<int[]>();
    public List<FacilitySnapshot> Facilities { get; set; } = new List<FacilitySnapshot>();
    public List<NoticeSnapshot> Notices { get; set; } = new List<NoticeSnapshot>();
}

// 수신자마다 다른 값만 담는다. 공통 부분과 따로 직렬화해 앞에 이어 붙인다.
// 수신자별 값을 새로 추가하면 여기에 둔다. 공통 부분(SnapshotMessage)에 두면 모든 수신자에게 같은 값이 간다.
public sealed class PersonalSnapshot
{
    public string You { get; set; } = "";
    // 보급 대기 시간이 대원마다 달라 보급 목록 전체를 수신자별 값으로 다룬다.
    public List<SupplySnapshot> Supplies { get; set; } = new List<SupplySnapshot>();
    // 벽은 수신자가 아직 받지 못한 판일 때만, 맵과 규칙은 아직 받지 못했을 때만 담는다.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Walls { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapSnapshot? Map { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RulesSnapshot? Rules { get; set; }
}

public sealed class PlayerSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Slots { get; set; } = Array.Empty<string>();
    public string State { get; set; } = "";
    public bool Connected { get; set; }
    public bool HasDeployed { get; set; }
    // 그리기용 좌표(소수 둘째 자리로 반올림)다.
    public double X { get; set; }
    public double Y { get; set; }
    // 예측용 좌표다. 고정소수점의 Raw(4096분의 1 단위 정수)를 반올림 없이 보내, 클라이언트가 서버와 같은 자리에서 계산을 시작하게 한다.
    public long RawX { get; set; }
    public long RawY { get; set; }
    public double Aim { get; set; }
    public double Hp { get; set; }
    public double MaxHp { get; set; }
    public int Mana { get; set; }
    public int MaxMana { get; set; }
    public double Busy { get; set; }
    public List<ActionSnapshot> Actions { get; set; } = new List<ActionSnapshot>();
    public double Deploy { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public long Ack { get; set; }
    public double LandingX { get; set; }
    public double LandingY { get; set; }
}

public sealed class ActionSnapshot
{
    public int Id { get; set; }
    public string Skill { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Slot { get; set; }
    public int Tier { get; set; } = 1;
    public string Phase { get; set; } = "";
    public double Aim { get; set; }
    public double StartedAt { get; set; }
    public double ContactAt { get; set; }
    public double ActiveUntil { get; set; }
    // 반격이 끝나는 시각이다. 반격은 ActiveUntil부터 이 시각 전까지이고, 반격이 없는 기술과 적의 행동은 ActiveUntil과 같다.
    public double FollowupUntil { get; set; }
    public double EndsAt { get; set; }
    public double Range { get; set; }
    public double HalfWidth { get; set; }
}

public sealed class EnemySnapshot
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    // 예측용 좌표(Raw)다. 클라이언트 예측이 대원을 막는 몸을 서버와 같은 자리로 본다.
    public long RawX { get; set; }
    public long RawY { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public double Radius { get; set; }
    public double Hp { get; set; }
    public int MaxHp { get; set; }
    public int[] Tiers { get; set; } = Array.Empty<int>();
    public ActionSnapshot? Action { get; set; }
}

// 효과는 서버에서 사라지는 틱으로 다루지만 보낼 때는 초(until)로 바꾼다.
public sealed class EffectSnapshot
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string Kind { get; set; } = "";
    public double Until { get; set; }
}

// 안내도 서버에서는 나온 틱으로 다루고 보낼 때 초(at)로 바꾼다. 클라이언트는 순번(id)으로 새 안내를 가린다.
public sealed class NoticeSnapshot
{
    public int Id { get; set; }
    public double At { get; set; }
    public string Text { get; set; } = "";
}

public sealed class FacilitySnapshot
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Hp { get; set; }
    public double MaxHp { get; set; }
}

public sealed class SupplySnapshot
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    // 보급 종류(camp·field·objective)다. 시설 자리 상자도 field다.
    public string Kind { get; set; } = "";
    public bool Objective { get; set; }
    public bool Collected { get; set; }
    // 남은 횟수다. 0이면 다 쓴 상자다. 부목표 상자는 1에서 회수하면 0이 된다.
    public int Uses { get; set; }
    public double Cooldown { get; set; }
}

public sealed class ExtractionSnapshot
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Progress { get; set; }
    public double Duration { get; set; }
    public bool Unlocked { get; set; }
}

public sealed class MapSnapshot
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Cell { get; set; }
    public int Columns { get; set; }
    public int Rows { get; set; }
    public double CampX { get; set; }
    public double CampY { get; set; }
}

public sealed class RulesSnapshot
{
    public double Deploy { get; set; }
    public double Speed { get; set; }
    public double PlayerRadius { get; set; }
    public double GuardHalfAngle { get; set; }
    public double ParryFollowupSeconds { get; set; }
    public List<SkillDefinition> Skills { get; set; } = new List<SkillDefinition>();
}
