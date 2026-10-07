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

        // 팀원 정보는 모두 공유한다.
        for (int i = 0; i < match.Players.Count; i++)
        {
            Player player = match.Players[i];
            message.Players.Add(CreatePlayer(match, player));
        }

        // 적과 투사체는 시야에 들어온 것만 전송한다.
        // 숨겨진 위치는 JSON에 포함되지 않으므로 클라이언트를 바꿔도 알아낼 수 없다.
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
            entry.Kind = enemy.Kind;
            entry.Hp = Round(enemy.Hp);
            message.Enemies.Add(entry);
        }

        for (int i = 0; i < match.Bullets.Count; i++)
        {
            Bullet bullet = match.Bullets[i];
            if (!match.Visible(bullet.Position))
            {
                continue;
            }

            BulletSnapshot entry = new BulletSnapshot();
            entry.Id = bullet.Id;
            entry.X = Round(bullet.Position.X);
            entry.Y = Round(bullet.Position.Y);
            entry.Vx = Round(bullet.Velocity.X);
            entry.Vy = Round(bullet.Velocity.Y);
            entry.Hostile = bullet.Owner == null;
            entry.Kind = bullet.Kind;
            message.Bullets.Add(entry);
        }

        for (int i = 0; i < match.Grenades.Count; i++)
        {
            Grenade grenade = match.Grenades[i];
            if (!match.Visible(grenade.Position))
            {
                continue;
            }

            GrenadeSnapshot entry = new GrenadeSnapshot();
            entry.Id = grenade.Id;
            entry.X = Round(grenade.Position.X);
            entry.Y = Round(grenade.Position.Y);
            entry.Remaining = Round(grenade.Remaining);
            message.Grenades.Add(entry);
        }

        for (int i = 0; i < match.Effects.Count; i++)
        {
            Effect effect = match.Effects[i];
            Vector2 position = new Vector2((float)effect.X, (float)effect.Y);
            if (match.Visible(position))
            {
                message.Effects.Add(effect);
            }
        }

        // 캠프와 살아 있는 팀원의 시야를 합쳐 클라이언트가 그릴 수 있게 보낸다.
        message.Sight.Add(CreateSightPolygon(match, BattleMap.Camp, BattleMap.CampVision));
        for (int i = 0; i < match.Players.Count; i++)
        {
            Player player = match.Players[i];
            if (player.State == "alive")
            {
                message.Sight.Add(CreateSightPolygon(match, player.Position, match.Rules.Vision));
            }
        }

        // 터렛 위치는 팀에 공개하지만, 터렛 자체가 시야를 넓혀주지는 않는다.
        for (int i = 0; i < match.Turrets.Count; i++)
        {
            Turret turret = match.Turrets[i];
            if (turret.Hp <= 0 || turret.ExpiresAt <= match.Now)
            {
                continue;
            }

            TurretSnapshot entry = new TurretSnapshot();
            entry.Id = turret.Id;
            entry.Owner = turret.Owner;
            entry.X = Round(turret.Position.X);
            entry.Y = Round(turret.Position.Y);
            entry.Aim = Round(turret.Aim);
            entry.Hp = Round(turret.Hp);
            entry.MaxHp = turret.MaxHp;
            entry.Remaining = Round(turret.ExpiresAt - match.Now);
            message.Turrets.Add(entry);
        }

        for (int i = 0; i < match.Facilities.Count; i++)
        {
            Facility facility = match.Facilities[i];
            FacilitySnapshot entry = new FacilitySnapshot();
            entry.Id = facility.Id;
            entry.X = facility.Position.X;
            entry.Y = facility.Position.Y;
            entry.Hp = Round(facility.Hp);
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
            entry.Cooldown = Round(Math.Max(0, supply.AvailableAt - match.Now));
            message.Supplies.Add(entry);
        }

        message.Notices = match.Notices;
        message.Rules.Deploy = match.Rules.DeploySeconds;
        message.Rules.Reload = match.Rules.ReloadSeconds;
        message.Rules.Magazine = match.Rules.Magazine;
        message.Rules.Grenades = match.Rules.Grenades;
        message.Rules.Speed = match.Rules.PlayerSpeed;
        message.Rules.TurretPlacement = Rules.TurretPlacementDistance;
        message.Rules.TurretCooldown = match.Rules.TurretCooldown;
        return message;
    }

    private static PlayerSnapshot CreatePlayer(Match match, Player player)
    {
        WeaponDefinition weapon = match.Rules.Weapon(player.Weapon);
        PlayerSnapshot entry = new PlayerSnapshot();
        entry.Id = player.Id;
        entry.Name = player.Name;
        entry.Passive = player.Passive;
        entry.Weapon = player.Weapon;
        entry.Secondary = player.Secondary;
        entry.State = player.State;
        entry.Connected = player.Connected;
        entry.HasDeployed = player.HasDeployed;
        entry.X = Round(player.Position.X);
        entry.Y = Round(player.Position.Y);
        entry.Aim = Round(player.Aim);
        entry.Hp = Round(player.Hp);
        entry.MaxHp = player.MaxHp;
        entry.Ammo = player.Ammo;
        entry.Grenades = player.Grenades;
        entry.Reload = Round(Math.Max(0, player.ReloadUntil - match.Now));
        entry.Magazine = weapon.Magazine;
        entry.ReloadSeconds = weapon.ReloadSeconds;
        entry.TurretCooldown = Round(Math.Max(0, player.NextTurret - match.Now));
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
            Vector2 direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            Vector2 target = origin + direction * (float)radius;
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
