namespace Argus.Server.Game;

// 플레이 시험을 위한 초기 수치다. 확정한 판정 규칙은 docs/combat.md에 기록한다.
public sealed class Rules
{
    public const double Step = 1.0 / 30;
    public const int MaxPlayers = 4;
    public const float PlayerRadius = 13;
    public double DeploySeconds { get; init; } = 5;
    public double PulseSeconds { get; init; } = 600;
    public double PulseDamage { get; init; } = 2;
    public double ExtractionSeconds { get; init; } = 20;
    public double ReconnectSeconds { get; init; } = 120;
    public double SpawnInterval { get; init; } = 7;
    public int MaxEnemies { get; init; } = 60;
    // 첫 작전에서는 기본 상성을 익히도록 일반 적만 내보내고 준비 시간을 늘린다.
    public bool AllowEliteEnemies { get; init; } = false;
    public double EnemyWindupMultiplier { get; init; } = 2;
    public double PlayerSpeed { get; init; } = 180;
    public double PlayerHealth { get; init; } = 6;
    public int MaxMana { get; init; } = 24;
    public double Vision { get; init; } = 410;
    public double SupplyCooldown { get; init; } = 18;
    public double GuardHalfAngle { get; init; } = Math.PI / 3;

    public SkillDefinition Strike { get; } =
        new SkillDefinition("strike", "strike", 1, 0.18, 0.42, 0.66, 54, 10);
    public SkillDefinition Parry { get; } =
        new SkillDefinition("parry", "block", 1, 0, 0.55, 0.98, 54, 10);
    public SkillDefinition Grab { get; } =
        new SkillDefinition("grab", "channel", 1, 0.12, 0.65, 0.85, 48, 8);
    public double ParryFollowupSeconds { get; init; } = 0.18;

    public SkillDefinition Skill(string id)
    {
        if (id == "strike")
        {
            return Strike;
        }
        if (id == "parry")
        {
            return Parry;
        }
        if (id == "grab")
        {
            return Grab;
        }
        throw new ArgumentOutOfRangeException(nameof(id));
    }

    public static bool ValidSlots(string[]? slots)
    {
        if (slots == null || slots.Length != 4)
        {
            return false;
        }
        if (slots[0] != "strike" || slots[1] != "parry" || slots[2] != "grab")
        {
            return false;
        }
        return slots[3] == "strike" || slots[3] == "parry" || slots[3] == "grab";
    }
}
