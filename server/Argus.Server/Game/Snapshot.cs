using System.Numerics;

namespace Argus.Server.Game;

public static class Snapshot
{
    public static SnapshotMessage Create(Match match, Player recipient)
    {
        SnapshotMessage message = new SnapshotMessage();
        message.Room = match.Code;
        message.Now = Round(match.Now);
        message.Elapsed = Round(match.Elapsed);
        message.Phase = match.Phase;
        message.Result = match.Result;
        message.You = recipient.Id;
        message.Pulse = match.Pulse;
        message.NextPulse = Round((match.Pulse + 1) * match.Rules.PulseSeconds - match.Elapsed);
        message.Extraction.X = BattleMap.Extraction.X;
        message.Extraction.Y = BattleMap.Extraction.Y;
        message.Extraction.Progress = Round(match.ExtractionProgress);
        message.Extraction.Duration = match.Rules.ExtractionSeconds;
        message.Extraction.Unlocked = match.ObjectivesComplete;
        message.Walls = match.Map.Tiles;
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
                ActionSnapshot display = new ActionSnapshot();
                display.Id = action.Id;
                display.Skill = action.Skill.Kind;
                display.Kind = action.Skill.Kind;
                display.Tier = action.Skill.Tier;
                display.Aim = action.Aim;
                display.StartedAt = action.StartedAt;
                display.ContactAt = action.ContactAt;
                display.ActiveUntil = action.ActiveUntil;
                display.EndsAt = action.EndsAt;
                display.Range = action.Skill.Range;
                display.HalfWidth = action.Skill.HalfWidth;
                display.Phase = "recovery";
                if (match.Now < action.ContactAt)
                {
                    display.Phase = "telegraph";
                }
                else if (match.Now < action.ActiveUntil)
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
            if (match.Visible(new Vector2((float)effect.X, (float)effect.Y)))
            {
                message.Effects.Add(effect);
            }
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
        for (int i = 0; i < match.Supplies.Count; i++)
        {
            Supply supply = match.Supplies[i];
            SupplySnapshot entry = new SupplySnapshot();
            entry.Id = supply.Id;
            entry.X = supply.Position.X;
            entry.Y = supply.Position.Y;
            entry.Objective = supply.Objective;
            entry.Collected = supply.Collected;
            entry.Cooldown = Round(Math.Max(0, supply.AvailableAt(recipient) - match.Now));
            message.Supplies.Add(entry);
        }
        message.Notices = match.Notices;
        message.Rules.Deploy = match.Rules.DeploySeconds;
        message.Rules.Speed = match.Rules.PlayerSpeed;
        message.Rules.GuardHalfAngle = match.Rules.GuardHalfAngle;
        message.Rules.ParryFollowupSeconds = match.Rules.ParryFollowupSeconds;
        message.Rules.Skills.Add(match.Rules.Strike);
        message.Rules.Skills.Add(match.Rules.Parry);
        message.Rules.Skills.Add(match.Rules.Grab);
        return message;
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
        entry.Busy = Round(Math.Max(0, player.BusyUntil - match.Now));
        for (int i = 0; i < player.Actions.Count; i++)
        {
            PlayerAction action = player.Actions[i];
            ActionSnapshot display = new ActionSnapshot();
            display.Id = action.Id;
            display.Skill = action.Skill.Id;
            display.Kind = action.Skill.Kind;
            display.Slot = action.Slot;
            display.Aim = player.Aim;
            display.StartedAt = action.StartedAt;
            display.ContactAt = action.ContactAt;
            display.ActiveUntil = action.ActiveUntil;
            display.EndsAt = action.EndsAt;
            display.Range = action.Skill.Range;
            display.HalfWidth = action.Skill.HalfWidth;
            display.Phase = "recovery";
            if (action.Cancelled)
            {
                display.Phase = "interrupted";
            }
            else if (action.IsActive(match.Now))
            {
                display.Phase = "active";
            }
            else if (action.IsFollowup(match.Now, match.Rules.ParryFollowupSeconds))
            {
                display.Phase = "followup";
                display.Kind = "none";
            }
            entry.Actions.Add(display);
        }
        if (player.State == "deploying")
        {
            entry.Deploy = Round(Math.Max(0, player.DeployAt - match.Now));
        }
        entry.Kills = player.Kills;
        entry.Deaths = player.Deaths;
        entry.Ack = player.LastSequence;
        entry.LandingX = Round(player.Landing.X);
        entry.LandingY = Round(player.Landing.Y);
        return entry;
    }

    private static List<double> CreateSightPolygon(Match match, Vector2 origin, double radius)
    {
        List<double> points = new List<double>();
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
            points.Add(Round(end.X));
            points.Add(Round(end.Y));
        }
        return points;
    }

    private static double Round(double value)
    {
        return Math.Round(value, 2);
    }
}
