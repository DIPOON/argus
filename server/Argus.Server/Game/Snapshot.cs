using System.Numerics;
using System.Text.Json;

namespace Argus.Server.Game;

public static class Snapshot
{
    // 연결 직후처럼 수신자 한 명에게만 보낼 JSON을 만든다. 방송과 같은 방식으로 공통 부분과
    // 수신자별 부분을 따로 직렬화해 이어 붙인다. 한 명에게 따로 보낼 때는 벽·규칙·맵을 모두 담는다.
    public static byte[] Create(Match match, Player recipient, JsonSerializerOptions options)
    {
        byte[] sharedJson = SerializeShared(CreateShared(match), options);
        PersonalSnapshot personal = CreatePersonal(match, recipient, true, true);
        return Combine(sharedJson, personal, options);
    }

    // 공통 부분을 JSON으로 직렬화한다. 방송마다 한 번만 부르고, 수신자마다 Combine으로 이어 붙인다.
    public static byte[] SerializeShared(SnapshotMessage shared, JsonSerializerOptions options)
    {
        return JsonSerializer.SerializeToUtf8Bytes(shared, options);
    }

    // 수신자별 부분과 공통 부분을 이어 스냅샷 JSON 하나를 만든다. 두 JSON 모두 {...} 형태이고 속성이 하나 이상이라,
    // 바깥 중괄호를 뺀 안쪽을 원본 배열에서 바로 옮겨 쉼표로 이으면 된다. 중괄호를 떼려고 따로 복사하지 않는다.
    public static byte[] Combine(byte[] sharedJson, PersonalSnapshot personal, JsonSerializerOptions options)
    {
        byte[] personalJson = JsonSerializer.SerializeToUtf8Bytes(personal, options);
        int personalLength = personalJson.Length - 2;
        int sharedLength = sharedJson.Length - 2;
        byte[] result = new byte[personalLength + sharedLength + 3];
        result[0] = (byte)'{';
        Array.Copy(personalJson, 1, result, 1, personalLength);
        result[1 + personalLength] = (byte)',';
        Array.Copy(sharedJson, 1, result, 2 + personalLength, sharedLength);
        result[result.Length - 1] = (byte)'}';
        return result;
    }

    // 팀 전체가 똑같이 받는 부분을 만든다. 시야 계산이 대부분의 비용이므로 방송할 때는 한 번만 부른다.
    // 수신자를 받지 않고 SnapshotMessage에 수신자별 속성도 없으므로 수신자별 정보가 여기에 섞일 수 없다.
    public static SnapshotMessage CreateShared(Match match)
    {
        SnapshotMessage message = new SnapshotMessage();
        message.Room = match.Code;
        message.Now = Seconds(match.Tick);
        message.Elapsed = Seconds(match.ElapsedTicks);
        message.Phase = match.Phase;
        message.Result = match.Result;
        message.Pulse = match.Pulse;
        long nextPulseTick = Rules.ToTicks((match.Pulse + 1) * match.Rules.PulseSeconds);
        message.NextPulse = Seconds(nextPulseTick - match.ElapsedTicks);
        message.Extraction.X = BattleMap.Extraction.X;
        message.Extraction.Y = BattleMap.Extraction.Y;
        message.Extraction.Progress = Round(match.ExtractionProgress);
        message.Extraction.Duration = match.Rules.ExtractionSeconds;
        message.Extraction.Unlocked = match.ObjectivesComplete;
        // 벽 자체는 수신자별 부분이 담는다. 판 번호는 매번 보내 클라이언트가 바뀐 것을 알게 한다.
        message.MapRevision = match.Map.Revision;
        for (int i = 0; i < match.Players.Count; i++)
        {
            message.Players.Add(CreatePlayer(match, match.Players[i]));
        }
        // 시야 밖의 적은 위치와 기술 예고 모두 직렬화 전에 제외한다.
        for (int i = 0; i < match.Enemies.Count; i++)
        {
            Enemy enemy = match.Enemies[i];
            if (enemy.Hp <= 0 || !match.Visible(enemy.Position))
            {
                continue;
            }
            EnemySnapshot entry = new EnemySnapshot();
            entry.Id = enemy.Id;
            entry.X = Round(enemy.Position.X);
            entry.Y = Round(enemy.Position.Y);
            entry.Kind = enemy.Definition.Id;
            entry.Name = enemy.Definition.Name;
            entry.Hp = enemy.Hp;
            entry.MaxHp = enemy.Definition.Health;
            entry.Radius = enemy.Definition.Radius;
            entry.Tiers = new int[enemy.Definition.Skills.Length];
            for (int j = 0; j < entry.Tiers.Length; j++)
            {
                entry.Tiers[j] = enemy.Definition.Skills[j].Tier;
            }
            if (enemy.Action != null)
            {
                EnemyAction action = enemy.Action;
                // 적 기술은 이름이 따로 없어 상성 종류를 이름으로도 보낸다.
                ActionSnapshot display = CreateAction(action, action.Skill.Kind, action.Skill.Kind, action.Aim,
                    action.Skill.Range, action.Skill.HalfWidth);
                display.Tier = action.Skill.Tier;
                display.Phase = "recovery";
                if (match.Tick < action.ContactTick)
                {
                    display.Phase = "telegraph";
                }
                else if (match.Tick < action.ActiveUntilTick)
                {
                    display.Phase = "active";
                }
                entry.Action = display;
            }
            message.Enemies.Add(entry);
        }
        for (int i = 0; i < match.Effects.Count; i++)
        {
            Effect effect = match.Effects[i];
            if (!match.Visible(new Vector2((float)effect.X, (float)effect.Y)))
            {
                continue;
            }
            EffectSnapshot entry = new EffectSnapshot();
            entry.Id = effect.Id;
            entry.X = effect.X;
            entry.Y = effect.Y;
            entry.Kind = effect.Kind;
            entry.Until = Seconds(effect.UntilTick);
            message.Effects.Add(entry);
        }
        message.Sight.Add(CreateSightPolygon(match, BattleMap.Camp, BattleMap.CampVision));
        for (int i = 0; i < match.Players.Count; i++)
        {
            if (match.Players[i].State == "alive")
            {
                message.Sight.Add(CreateSightPolygon(match, match.Players[i].Position, match.Rules.Vision));
            }
        }
        for (int i = 0; i < match.Facilities.Count; i++)
        {
            Facility facility = match.Facilities[i];
            FacilitySnapshot entry = new FacilitySnapshot();
            entry.Id = facility.Id;
            entry.X = facility.Position.X;
            entry.Y = facility.Position.Y;
            entry.Hp = facility.Hp;
            entry.MaxHp = facility.MaxHp;
            message.Facilities.Add(entry);
        }
        for (int i = 0; i < match.Notices.Count; i++)
        {
            Notice notice = match.Notices[i];
            NoticeSnapshot entry = new NoticeSnapshot();
            entry.Id = notice.Id;
            entry.At = Seconds(notice.AtTick);
            entry.Text = notice.Text;
            message.Notices.Add(entry);
        }
        return message;
    }

    private static RulesSnapshot CreateRules(Match match)
    {
        RulesSnapshot rules = new RulesSnapshot();
        rules.Deploy = match.Rules.DeploySeconds;
        rules.Speed = match.Rules.PlayerSpeed;
        rules.GuardHalfAngle = match.Rules.GuardHalfAngle;
        rules.ParryFollowupSeconds = match.Rules.ParryFollowupSeconds;
        rules.Skills.Add(match.Rules.Strike);
        rules.Skills.Add(match.Rules.Parry);
        rules.Skills.Add(match.Rules.Grab);
        return rules;
    }

    // 수신자마다 다른 값만 만든다. includeWalls는 이 수신자가 지금 판의 벽을 아직 받지 못했을 때,
    // includeStatic은 규칙과 맵을 아직 받지 못했을 때 켠다.
    public static PersonalSnapshot CreatePersonal(Match match, Player recipient, bool includeWalls, bool includeStatic)
    {
        PersonalSnapshot personal = new PersonalSnapshot();
        personal.You = recipient.Id;
        for (int i = 0; i < match.Supplies.Count; i++)
        {
            Supply supply = match.Supplies[i];
            SupplySnapshot entry = new SupplySnapshot();
            entry.Id = supply.Id;
            entry.X = supply.Position.X;
            entry.Y = supply.Position.Y;
            entry.Objective = supply.Objective;
            entry.Collected = supply.Collected;
            // 보급 대기 시간은 대원마다 다르다.
            entry.Cooldown = Seconds(Math.Max(0, supply.ReadyTick(recipient) - match.Tick));
            personal.Supplies.Add(entry);
        }
        if (includeWalls)
        {
            personal.Walls = match.Map.Tiles;
        }
        if (includeStatic)
        {
            personal.Map = new MapSnapshot();
            personal.Rules = CreateRules(match);
        }
        return personal;
    }

    private static PlayerSnapshot CreatePlayer(Match match, Player player)
    {
        PlayerSnapshot entry = new PlayerSnapshot();
        entry.Id = player.Id;
        entry.Name = player.Name;
        entry.Slots = (string[])player.Slots.Clone();
        entry.State = player.State;
        entry.Connected = player.Connected;
        entry.HasDeployed = player.HasDeployed;
        entry.X = Round(player.Position.X);
        entry.Y = Round(player.Position.Y);
        entry.Aim = player.Aim;
        entry.Hp = player.Hp;
        entry.MaxHp = player.MaxHp;
        entry.Mana = player.Mana;
        entry.MaxMana = player.MaxMana;
        entry.Busy = Seconds(Math.Max(0, player.BusyUntilTick - match.Tick));
        for (int i = 0; i < player.Actions.Count; i++)
        {
            PlayerAction action = player.Actions[i];
            // 대원의 기술 방향은 사용 중에도 조준을 따라 바뀐다.
            ActionSnapshot display = CreateAction(action, action.Skill.Id, action.Skill.Kind, player.Aim,
                action.Skill.Range, action.Skill.HalfWidth);
            display.Slot = action.Slot;
            display.Phase = "recovery";
            if (action.Cancelled)
            {
                display.Phase = "interrupted";
            }
            else if (action.IsActive(match.Tick))
            {
                display.Phase = "active";
            }
            else if (action.IsFollowup(match.Tick, match.Rules.ParryFollowupSeconds))
            {
                display.Phase = "followup";
                display.Kind = "none";
            }
            entry.Actions.Add(display);
        }
        if (player.State == "deploying")
        {
            entry.Deploy = Seconds(Math.Max(0, player.DeployTick - match.Tick));
        }
        entry.Kills = player.Kills;
        entry.Deaths = player.Deaths;
        entry.Ack = player.LastSequence;
        entry.LandingX = Round(player.Landing.X);
        entry.LandingY = Round(player.Landing.Y);
        return entry;
    }

    // 적과 대원의 행동에 공통인 값(id, 기술, 상성, 방향, 시각, 범위)을 담는다. 단계·티어·슬롯은 부르는 쪽이 채운다.
    private static ActionSnapshot CreateAction(ITimedAction action, string skill, string kind, double aim,
        double range, double halfWidth)
    {
        ActionSnapshot display = new ActionSnapshot();
        display.Id = action.Id;
        display.Skill = skill;
        display.Kind = kind;
        display.Aim = aim;
        // 시각은 틱으로 다루지만 통신 형식은 초다.
        display.StartedAt = Seconds(action.StartedTick);
        display.ContactAt = Seconds(action.ContactTick);
        display.ActiveUntil = Seconds(action.ActiveUntilTick);
        display.EndsAt = Seconds(action.EndsTick);
        display.Range = range;
        display.HalfWidth = halfWidth;
        return display;
    }

    private static int[] CreateSightPolygon(Match match, Vector2 origin, double radius)
    {
        int[] points = new int[96 * 2];
        for (int i = 0; i < 96; i++)
        {
            double angle = i * Math.Tau / 96;
            Vector2 target = origin + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * (float)radius;
            WallHit wall = match.Map.RayWall(origin, target);
            Vector2 end = target;
            if (!double.IsPositiveInfinity(wall.Fraction))
            {
                end = Vector2.Lerp(origin, target, (float)wall.Fraction);
            }
            // 시야 다각형은 그리기에만 쓰이고 1단위 미만은 화면에서 구분되지 않으므로 정수로 보낸다.
            points[i * 2] = (int)Math.Round(end.X);
            points[i * 2 + 1] = (int)Math.Round(end.Y);
        }
        return points;
    }

    private static double Round(double value)
    {
        return Math.Round(value, 2);
    }

    // 틱 시각과 틱 수를 보낼 초로 바꾼다. 모든 시간 필드가 이것을 거쳐 같은 방식(소수 둘째 자리)으로 반올림되므로,
    // 같은 틱이면 now와 다른 시각이 정확히 같은 값이 된다. 한 틱은 약 0.033초라 반올림해도 틱끼리 구분되고,
    // 받은 값에 30을 곱해 반올림하면 틱 번호로 돌아간다.
    private static double Seconds(long ticks)
    {
        return Round(Rules.ToSeconds(ticks));
    }
}
