using System.Text.Json;

namespace Argus.Server.Game;

public static class Snapshot
{
    // 연결 직후처럼 수신자 한 명에게만 보낼 JSON을 만든다. 방송과 같은 방식으로 공통 부분과
    // 수신자별 부분을 따로 직렬화해 이어 붙인다. 한 명에게 따로 보낼 때는 벽·규칙·맵을 모두 담는다.
    public static byte[] Create(Match match, Player recipient, JsonSerializerOptions options)
    {
        byte[] sharedJson = SerializeShared(CreateShared(match), options);
        PersonalSnapshot personal = CreatePersonal(match, recipient, NoWallsReceived, true);
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
        // 탈출 지점은 분대가 그 칸을 보기 전까지 보내지 않는다. 시설·보급과 마찬가지로 안 본 곳은 숨은 정보다.
        if (match.Explored(match.Layout.Extraction))
        {
            ExtractionSnapshot extraction = new ExtractionSnapshot();
            extraction.X = Round(match.Layout.Extraction.X);
            extraction.Y = Round(match.Layout.Extraction.Y);
            extraction.Progress = Round(match.ExtractionProgress);
            extraction.Duration = match.Rules.ExtractionSeconds;
            extraction.Unlocked = match.ObjectivesComplete;
            message.Extraction = extraction;
        }
        // 벽 자체는 수신자별 부분이 담는다. 판 번호는 매번 보내 클라이언트가 바뀐 것을 알게 한다.
        // 벽이 부서지거나 분대가 본 칸이 늘면 바뀐다.
        message.MapRevision = match.WallsRevision;
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
            entry.RawX = enemy.Position.X.Raw;
            entry.RawY = enemy.Position.Y.Raw;
            entry.Kind = enemy.Definition.Id;
            entry.Name = enemy.Definition.Name;
            entry.Hp = enemy.Hp;
            entry.MaxHp = enemy.Definition.Health;
            entry.Radius = Round(enemy.Definition.Radius);
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
            if (!match.Visible(effect.Position))
            {
                continue;
            }
            EffectSnapshot entry = new EffectSnapshot();
            entry.Id = effect.Id;
            entry.X = Round(effect.Position.X);
            entry.Y = Round(effect.Position.Y);
            entry.Kind = effect.Kind;
            entry.Until = Seconds(effect.UntilTick);
            message.Effects.Add(entry);
        }
        message.Sight.Add(CreateSightPolygon(match, match.Layout.Camp, Fixed.FromInt(BattleMap.CampVision)));
        for (int i = 0; i < match.Players.Count; i++)
        {
            if (match.Players[i].State == "alive")
            {
                message.Sight.Add(CreateSightPolygon(match, match.Players[i].Position, match.VisionRange));
            }
        }
        // 시설은 분대가 그 칸을 본 것만 보낸다.
        for (int i = 0; i < match.Facilities.Count; i++)
        {
            Facility facility = match.Facilities[i];
            if (!match.Explored(facility.Position))
            {
                continue;
            }
            FacilitySnapshot entry = new FacilitySnapshot();
            entry.Id = facility.Id;
            entry.X = Round(facility.Position.X);
            entry.Y = Round(facility.Position.Y);
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
        rules.PlayerRadius = Round(Rules.PlayerRadius);
        rules.GuardHalfAngle = match.Rules.GuardHalfAngle;
        rules.ParryFollowupSeconds = match.Rules.ParryFollowupSeconds;
        for (int i = 0; i < Rules.AllSkills.Count; i++)
        {
            rules.Skills.Add(Rules.AllSkills[i]);
        }
        return rules;
    }

    // 벽 배열을 아직 받지 못한 수신자의 판 번호다.
    public const int NoWallsReceived = -1;
    // 바뀐 칸이 칸 수의 1/8보다 많으면 바뀐 칸 대신 벽 배열 전체를 보낸다. 바뀐 칸 하나가 JSON으로 수 두 개(칸 번호와 값)라,
    // 이쯤에서 base64로 적은 배열 전체와 길이가 비슷해진다.
    private const int ChangesPerFullWalls = 8;

    // 수신자마다 다른 값만 만든다. knownWalls는 이 수신자가 이미 받은 벽 배열의 판 번호(받은 적이 없으면 NoWallsReceived)다.
    // 지금 판과 같으면 벽을 담지 않고, 받은 판이 있으면 그 뒤로 바뀐 칸만, 처음이거나 바뀐 칸이 많으면 배열 전체를 담는다.
    // includeStatic은 규칙과 맵을 아직 받지 못했을 때 켠다.
    public static PersonalSnapshot CreatePersonal(Match match, Player recipient, int knownWalls, bool includeStatic)
    {
        PersonalSnapshot personal = new PersonalSnapshot();
        personal.You = recipient.Id;
        // 보급은 분대가 그 칸을 본 것만 보낸다.
        for (int i = 0; i < match.Supplies.Count; i++)
        {
            Supply supply = match.Supplies[i];
            if (!match.Explored(supply.Position))
            {
                continue;
            }
            SupplySnapshot entry = new SupplySnapshot();
            entry.Id = supply.Id;
            entry.X = Round(supply.Position.X);
            entry.Y = Round(supply.Position.Y);
            entry.Kind = SupplyRecord.KindName(supply.Kind);
            entry.Objective = supply.Objective;
            entry.Collected = supply.Collected;
            entry.Uses = supply.UsesLeft;
            // 보급 대기 시간은 대원마다 다르다.
            entry.Cooldown = Seconds(Math.Max(0, supply.ReadyTick(recipient) - match.Tick));
            personal.Supplies.Add(entry);
        }
        // 분대가 보지 못한 칸은 모름(3)으로 가린 배열이다.
        int revision = match.WallsRevision;
        if (knownWalls != revision)
        {
            bool fewChanges = revision - knownWalls <= match.Map.Tiles.Length / ChangesPerFullWalls;
            if (knownWalls >= 0 && knownWalls < revision && fewChanges)
            {
                personal.WallChanges = match.PublicChangesSince(knownWalls);
            }
            else
            {
                personal.Walls = match.PublicTiles();
            }
        }
        if (includeStatic)
        {
            personal.Map = CreateMap(match);
            personal.Rules = CreateRules(match);
        }
        return personal;
    }

    // 맵의 크기와 칸 수는 방마다 다르므로 그 방의 맵에서 담고, 고정소수점인 캠프 위치는 다른 좌표처럼 바꿔 담는다.
    private static MapSnapshot CreateMap(Match match)
    {
        MapSnapshot map = new MapSnapshot();
        map.Width = match.Map.Width;
        map.Height = match.Map.Height;
        map.Cell = BattleMap.Cell;
        map.Columns = match.Map.Columns;
        map.Rows = match.Map.Rows;
        map.CampX = Round(match.Layout.Camp.X);
        map.CampY = Round(match.Layout.Camp.Y);
        return map;
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
        entry.RawX = player.Position.X.Raw;
        entry.RawY = player.Position.Y.Raw;
        entry.Aim = player.Aim.ToRadians();
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
            // 반격 끝은 규칙 값으로 다시 계산하면 올림 방식에 따라 한 틱 어긋날 수 있으므로 서버가 정한 틱을 보낸다.
            display.FollowupUntil = Seconds(action.FollowupUntilTick(match.Rules.ParryFollowupSeconds));
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
    private static ActionSnapshot CreateAction(ITimedAction action, string skill, string kind, Angle aim,
        double range, double halfWidth)
    {
        ActionSnapshot display = new ActionSnapshot();
        display.Id = action.Id;
        display.Skill = skill;
        display.Kind = kind;
        display.Aim = aim.ToRadians();
        // 시각은 틱으로 다루지만 통신 형식은 초다.
        display.StartedAt = Seconds(action.StartedTick);
        display.ContactAt = Seconds(action.ContactTick);
        display.ActiveUntil = Seconds(action.ActiveUntilTick);
        // 적의 행동에는 반격이 없어 유효 시간 끝과 같다. 대원의 기술은 부르는 쪽이 반격 끝으로 바꾼다.
        display.FollowupUntil = display.ActiveUntil;
        display.EndsAt = Seconds(action.EndsTick);
        display.Range = range;
        display.HalfWidth = halfWidth;
        return display;
    }

    // 시야 다각형의 96방향이다. 시야는 그리기에만 쓰지만, 같은 상태면 어느 기계에서나 같은 다각형을 보내도록 정수 각도로 구한다.
    // 방향은 늘 같으므로 방송마다 다시 구하지 않고 처음 한 번만 만들어 둔다.
    private static readonly FixedVector[] SightDirections = CreateSightDirections();

    private static FixedVector[] CreateSightDirections()
    {
        FixedVector[] directions = new FixedVector[96];
        for (int i = 0; i < directions.Length; i++)
        {
            directions[i] = Angle.FromFraction(i, directions.Length).Direction();
        }
        return directions;
    }

    private static int[] CreateSightPolygon(Match match, FixedVector origin, Fixed radius)
    {
        int[] points = new int[SightDirections.Length * 2];
        for (int i = 0; i < SightDirections.Length; i++)
        {
            FixedVector target = origin + SightDirections[i] * radius;
            WallHit wall = match.Map.RayWall(origin, target);
            FixedVector end = target;
            if (wall.Hit)
            {
                // 벽에 닿은 비율만큼만 나아간 점에서 시야가 끝난다.
                end = FixedVector.Lerp(origin, target, wall.Fraction);
            }
            // 시야 다각형은 그리기에만 쓰이고 1단위 미만은 화면에서 구분되지 않으므로 정수로 보낸다.
            // 다른 좌표처럼 딱 절반이면 0에서 먼 쪽으로 반올림한다.
            points[i * 2] = end.X.RoundToInt();
            points[i * 2 + 1] = end.Y.RoundToInt();
        }
        return points;
    }

    // 시간(틱 ÷ 30)과 탈출 진행도(반 틱 ÷ 60)를 소수 둘째 자리로 반올림한다. 둘 다 100을 곱하면 소수 부분이 0, 1/3, 2/3 중
    // 하나라 딱 절반이 나오지 않으므로, 딱 절반을 짝수 쪽으로 보내는 실수의 기본 반올림을 그대로 써도 결과가 같다.
    private static double Round(double value)
    {
        return Math.Round(value, 2);
    }

    // 고정소수점 값(좌표·반지름)을 보낼 실수로 바꿔 소수 둘째 자리로 반올림한다. 고정소수점 값에는 0.125(Raw 512)처럼
    // 소수 셋째 자리가 딱 5인 값이 실제로 있는데, 실수의 Math.Round는 이것을 짝수 쪽(0.12)으로 보낸다.
    // 그래서 Raw에서 100분의 1 단위 정수로 바로 반올림해, Fixed의 다른 연산처럼 딱 절반이면 0에서 먼 쪽(0.13)으로 보낸다.
    private static double Round(Fixed value)
    {
        long hundredths = Fixed.DivideRounded(checked(value.Raw * 100), Fixed.Scale);
        return hundredths / 100.0;
    }

    // 틱 시각과 틱 수를 보낼 초로 바꾼다. 틱으로 된 시간 필드는 모두 이것을 거쳐 같은 방식(소수 둘째 자리)으로 반올림되므로,
    // 같은 틱이면 now와 다른 시각이 정확히 같은 값이 된다. 한 틱은 약 0.033초라 반올림해도 틱끼리 구분되고,
    // 받은 값에 30을 곱해 반올림하면 틱 번호로 돌아간다. 규칙 값(CreateRules, Extraction.Duration)은 설정한 초를 그대로 보내고,
    // 탈출 진행도(Extraction.Progress)는 반 틱 단위라 이 함수를 거치지 않는다.
    private static double Seconds(long ticks)
    {
        return Round(Rules.ToSeconds(ticks));
    }
}
