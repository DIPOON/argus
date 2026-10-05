using System.Numerics;

namespace Argus.Server.Game;

public static class Snapshot
{
    public static object Create(Match match, Player recipient)
    {
        // Filter first, then serialize. There is no client-side toggle that can reveal hidden enemies.
        var enemies = match.Enemies.Where(e => e.Hp > 0 && match.Visible(e.Position)).Select(e => new { e.Id, x = Round(e.Position.X), y = Round(e.Position.Y), e.Kind, hp = Round(e.Hp) }).ToArray();
        var bullets = match.Bullets.Where(b => match.Visible(b.Position)).Select(b => new { b.Id, x = Round(b.Position.X), y = Round(b.Position.Y), vx = Round(b.Velocity.X), vy = Round(b.Velocity.Y), hostile = b.Owner is null }).ToArray();
        var grenades = match.Grenades.Where(g => match.Visible(g.Position)).Select(g => new { g.Id, x = Round(g.Position.X), y = Round(g.Position.Y), remaining = Round(g.Remaining) }).ToArray();
        var effects = match.Effects.Where(e => match.Visible(new((float)e.X, (float)e.Y))).Select(e => new { e.Id, e.X, e.Y, e.Kind, e.Until }).ToArray();
        var observers = new List<(Vector2 At, double Radius)> { (BattleMap.Camp, BattleMap.CampVision) };
        observers.AddRange(match.Players.Where(p => p.State == "alive").Select(p => (p.Position, match.Rules.Vision)));
        var sight = observers.Select(observer =>
        {
            var points = new List<double>();
            for (var i = 0; i < 96; i++)
            {
                var angle = i * Math.Tau / 96;
                var target = observer.At + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * (float)observer.Radius;
                var wall = match.Map.RayWall(observer.At, target);
                var end = double.IsPositiveInfinity(wall.Fraction) ? target : Vector2.Lerp(observer.At, target, (float)wall.Fraction);
                points.Add(Round(end.X)); points.Add(Round(end.Y));
            }
            return points;
        }).ToArray();
        return new
        {
            type = "snapshot", version = 1, room = match.Code, now = Round(match.Now), elapsed = Round(match.Elapsed), phase = match.Phase,
            result = match.Result, you = recipient.Id, pulse = match.Pulse, nextPulse = Round((match.Pulse + 1) * match.Rules.PulseSeconds - match.Elapsed),
            extraction = new { x = BattleMap.Extraction.X, y = BattleMap.Extraction.Y, progress = Round(match.ExtractionProgress), duration = match.Rules.ExtractionSeconds, unlocked = match.ObjectivesComplete },
            walls = match.Map.Tiles, mapRevision = match.Map.Revision,
            players = match.Players.Select(p => new
            {
                p.Id, p.Name, p.Passive, p.State, p.Connected, p.HasDeployed, x = Round(p.Position.X), y = Round(p.Position.Y), aim = Round(p.Aim),
                hp = Round(p.Hp), maxHp = p.MaxHp, p.Ammo, p.Grenades, reload = Round(Math.Max(0, p.ReloadUntil - match.Now)),
                deploy = Round(p.State == "deploying" ? Math.Max(0, p.DeployAt - match.Now) : 0), p.Kills, p.Deaths,
                ack = p.LastSequence, landingX = Round(p.Landing.X), landingY = Round(p.Landing.Y)
            }).ToArray(),
            enemies, bullets, grenades, effects, sight,
            facilities = match.Facilities.Select(f => new { f.Id, x = f.Position.X, y = f.Position.Y, hp = Round(f.Hp), maxHp = 340 }),
            supplies = match.Supplies.Select(s => new { s.Id, x = s.Position.X, y = s.Position.Y, s.Objective, s.Collected, cooldown = Round(Math.Max(0, s.AvailableAt - match.Now)) }),
            notices = match.Notices,
            map = new { width = BattleMap.Width, height = BattleMap.Height, cell = BattleMap.Cell, columns = BattleMap.Columns, rows = BattleMap.Rows, campX = BattleMap.Camp.X, campY = BattleMap.Camp.Y },
            rules = new { deploy = match.Rules.DeploySeconds, reload = match.Rules.ReloadSeconds, magazine = match.Rules.Magazine, grenades = match.Rules.Grenades, speed = match.Rules.PlayerSpeed }
        };
    }
    private static double Round(double value) => Math.Round(value, 2);
}
