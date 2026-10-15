using System.Text.Json.Serialization;

namespace Argus.Server.Game;

// 모든 위치·피해·기술 시간은 서버가 계산한 값이다. JSON 속성명은 camelCase다.
// 팀 전체가 똑같이 받는 부분만 담는다. 수신자별 값(you, 보급, 벽·규칙·맵)은 PersonalSnapshot에 있고
// 직렬화한 뒤 이어 붙이므로, 이 클래스에는 수신자별 속성을 두지 않는다.
public sealed class SnapshotMessage
{
    public string Type { get; set; } = "snapshot";
    public int Version { get; set; } = 3;
    public string Room { get; set; } = "";
    public double Now { get; set; }
    public double Elapsed { get; set; }
    public string Phase { get; set; } = "";
    public string? Result { get; set; }
    public int Pulse { get; set; }
    public double NextPulse { get; set; }
    public ExtractionSnapshot Extraction { get; set; } = new ExtractionSnapshot();
    public int MapRevision { get; set; }
    public List<PlayerSnapshot> Players { get; set; } = new List<PlayerSnapshot>();
    public List<EnemySnapshot> Enemies { get; set; } = new List<EnemySnapshot>();
    public List<Effect> Effects { get; set; } = new List<Effect>();
    // 정수 좌표라 double보다 직렬화가 빠르다. 전송되는 JSON은 같다.
    public List<int[]> Sight { get; set; } = new List<int[]>();
    public List<FacilitySnapshot> Facilities { get; set; } = new List<FacilitySnapshot>();
    public List<Notice> Notices { get; set; } = new List<Notice>();
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
    public double X { get; set; }
    public double Y { get; set; }
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
    public double EndsAt { get; set; }
    public double Range { get; set; }
    public double HalfWidth { get; set; }
}

public sealed class EnemySnapshot
{
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public float Radius { get; set; }
    public double Hp { get; set; }
    public int MaxHp { get; set; }
    public int[] Tiers { get; set; } = Array.Empty<int>();
    public ActionSnapshot? Action { get; set; }
}

public sealed class FacilitySnapshot
{
    public int Id { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public double Hp { get; set; }
    public double MaxHp { get; set; }
}

public sealed class SupplySnapshot
{
    public int Id { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public bool Objective { get; set; }
    public bool Collected { get; set; }
    public double Cooldown { get; set; }
}

public sealed class ExtractionSnapshot
{
    public float X { get; set; }
    public float Y { get; set; }
    public double Progress { get; set; }
    public double Duration { get; set; }
    public bool Unlocked { get; set; }
}

public sealed class MapSnapshot
{
    public int Width { get; set; } = BattleMap.Width;
    public int Height { get; set; } = BattleMap.Height;
    public int Cell { get; set; } = BattleMap.Cell;
    public int Columns { get; set; } = BattleMap.Columns;
    public int Rows { get; set; } = BattleMap.Rows;
    public float CampX { get; set; } = BattleMap.Camp.X;
    public float CampY { get; set; } = BattleMap.Camp.Y;
}

public sealed class RulesSnapshot
{
    public double Deploy { get; set; }
    public double Speed { get; set; }
    public float PlayerRadius { get; set; } = Rules.PlayerRadius;
    public double GuardHalfAngle { get; set; }
    public double ParryFollowupSeconds { get; set; }
    public List<SkillDefinition> Skills { get; set; } = new List<SkillDefinition>();
}
