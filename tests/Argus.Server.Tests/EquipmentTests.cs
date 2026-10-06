using System.Numerics;
using System.Text.Json;
using Argus.Server.Game;
using Argus.Server.Networking;
using Microsoft.Extensions.Logging.Abstractions;

internal static class EquipmentTests
{
    public static readonly (string Name, Action Run)[] All =
    [
        ("Equipment selection is validated atomically and reconnect keeps the chosen kit", () =>
        {
            using var host = new RoomHost(NullLogger<RoomHost>.Instance);
            Check(host.Create(new("invalid", null, "invented", "turret")) is null);
            var guest = host.Create(new("engineer", "mobility", "shotgun", "turret"))!;
            var (room, p, peer) = host.Attach(guest.Room, guest.Token)!.Value;
            Check(p.Weapon == "shotgun" && p.Secondary == "turret" && p.Ammo == 6 && p.Grenades == 0);
            Check(!room.Match.SetLoadout(p, "vitality", "piercer", "invented"));
            Check(p.Passive == "mobility" && p.Weapon == "shotgun" && p.Secondary == "turret");
            room.Match.Deploy(p, 300, 730); Advance(room.Match, 5.05);
            Check(!room.Match.SetLoadout(p, "vitality", "piercer", "grenade"));
            p.Ammo = 2; p.NextTurret = room.Match.Now + 17;
            host.Detach(room, p, peer);
            var restored = host.Attach(guest.Room, guest.Token)!.Value.Player;
            Check(ReferenceEquals(p, restored));
            Check(restored.Weapon == "shotgun" && restored.Secondary == "turret" && restored.Ammo == 2);
            Near(restored.NextTurret - room.Match.Now, 17);
            var late = host.Join(guest.Room, new("late", "vitality", "piercer", "grenade"))!;
            var newcomer = host.Attach(late.Room, late.Token)!.Value.Player;
            Check(newcomer.Weapon == "piercer" && newcomer.Ammo == 8 && newcomer.Grenades == 3);
        }),
        ("Shotgun consumes one shell for a server-defined fan, respects cadence and stops at short range", () =>
        {
            var (m, p) = Active("shotgun");
            m.Input(p, 0, 0, 0, 0, true, false, false); m.Step(.001);
            Check(p.Ammo == 5 && m.Bullets.Count == 7);
            Near(Math.Atan2(m.Bullets[0].Velocity.Y, m.Bullets[0].Velocity.X), -.21);
            Near(Math.Atan2(m.Bullets[^1].Velocity.Y, m.Bullets[^1].Velocity.X), .21);
            for (var i = 1; i <= 40; i++) m.Input(p, i, 0, 0, 0, true, false, false);
            m.Step(.001); Check(p.Ammo == 5 && m.Bullets.Count == 7);
            p.Firing = false;
            var distant = Target(900, new(700, 730)); m.Enemies.Add(distant);
            m.Step(1); Check(m.Bullets.Count == 0); Near(distant.Hp, 1000);

            var (close, shooter) = Active("shotgun");
            var enemy = Target(901, shooter.Position + new Vector2(35, 0)); close.Enemies.Add(enemy);
            close.Input(shooter, 0, 0, 0, 0, true, false, false); close.Step(.1);
            Near(enemy.Hp, 1000 - 7 * 12);
        }),
        ("Every primary reloads its own magazine in under five seconds and survives respawn", () =>
        {
            foreach (var weapon in new[] { "rifle", "shotgun", "piercer" })
            {
                var (m, p) = Active(weapon);
                var stats = m.Rules.Weapon(weapon);
                p.Ammo = 1;
                m.Input(p, 0, 0, 0, 0, false, true, false); m.Step(Rules.Step);
                Near(p.ReloadUntil - m.Now, stats.ReloadSeconds);
                Check(stats.ReloadSeconds < m.Rules.DeploySeconds);
                Advance(m, stats.ReloadSeconds - .01); Check(p.Ammo == 1);
                Advance(m, .02); Check(p.Ammo == stats.Magazine);
                var ally = m.Join("ally", "ally")!; ally.State = "alive"; ally.Position = new(300, 800);
                p.Ammo = 0; m.Hurt(p, 10000);
                Check(!m.SetLoadout(p, "vitality", "shotgun", "turret"));
                Check(m.Deploy(p, 300, 730)); Advance(m, 5.05);
                Check(p.State == "alive" && p.Weapon == weapon && p.Ammo == stats.Magazine);
            }
        }),
        ("Piercing rounds hit the first three enemies in travel order and do not hit the same body twice", () =>
        {
            var (m, p) = Active("piercer");
            var targets = new[] { 70, 110, 150, 190 }.Select((x, i) => Target(900 + i, p.Position + new Vector2(x, 0))).ToArray();
            m.Enemies.AddRange(targets.Reverse());
            m.Input(p, 0, 0, 0, 0, true, false, false); m.Step(.15);
            Check(p.Ammo == 7);
            foreach (var enemy in targets.Take(3)) Near(enemy.Hp, 940);
            Near(targets[3].Hp, 1000); Check(m.Bullets.Count == 0);

            var (slow, owner) = Active("piercer");
            var body = Target(999, owner.Position + new Vector2(30, 0)); slow.Enemies.Add(body);
            slow.Bullets.Add(new(800, owner.Position, new(100, 0), owner.Id, 60) { EnemyHitsLeft = 3, Kind = "piercer" });
            slow.Step(.2); Near(body.Hp, 940);
            slow.Step(.05); Near(body.Hp, 940);
            Advance(slow, .5); Near(body.Hp, 940);
        }),
        ("Piercing stops at walls and friendly bodies; expired bullets cannot extend their range", () =>
        {
            var (m, p) = Active("piercer");
            p.Position = new(680, 500);
            var behindWall = Target(900, new(800, 500)); m.Enemies.Add(behindWall);
            m.Input(p, 0, 0, 0, 0, true, false, false); m.Step(.15);
            Near(behindWall.Hp, 1000); Check(m.Bullets.Count == 0 && m.Map.Solid(15, 10));

            var (friendly, shooter) = Active("piercer");
            var ally = friendly.Join("ally", "ally")!; ally.State = "alive"; ally.Position = new(350, 730);
            var behindAlly = Target(901, new(420, 730)); friendly.Enemies.Add(behindAlly);
            friendly.Input(shooter, 0, 0, 0, 0, true, false, false); friendly.Step(.15);
            Near(ally.Hp, ally.MaxHp - 60); Near(behindAlly.Hp, 1000);

            friendly.Bullets.Add(new(802, new(300, 730), new(1000, 0), shooter.Id, 60) { Remaining = .02, EnemyHitsLeft = 3 });
            shooter.Firing = false; friendly.Step(.3);
            Near(ally.Hp, ally.MaxHp - 60); Check(friendly.Bullets.Count == 0);
        }),
        ("Turret placement validates cover, cooldown, one-per-owner replacement and lifetime", () =>
        {
            var (m, p) = Active(secondary: "turret");
            m.Input(p, 0, 0, 0, 0, false, false, true); m.Step(Rules.Step);
            Check(m.Turrets.Count == 1 && m.Grenades.Count == 0 && p.Grenades == 0);
            var first = m.Turrets.Single(); Near(first.Position.X, 344);
            m.Input(p, 1, 0, 0, 1, false, false, true); m.Step(Rules.Step);
            Check(m.Turrets.Single().Id == first.Id);
            Advance(m, 20.05);
            p.Position = new(690, 500);
            m.Input(p, 2, 0, 0, 0, false, false, true); m.Step(Rules.Step);
            Check(m.Turrets.Single().Id == first.Id && p.NextTurret <= m.Now);
            m.Map.Damage(10 * BattleMap.Columns + 15, 100);
            m.Input(p, 3, 0, 0, 0, false, false, true); m.Step(Rules.Step);
            Check(m.Turrets.Count == 1 && m.Turrets[0].Id != first.Id);
            Near(m.Turrets[0].Position.X, 734);
            Advance(m, 45.05); Check(m.Turrets.Count == 0);
        }),
        ("Turrets neither reveal nor target hidden enemies, and shared sight enables fire", () =>
        {
            var (m, p) = Active(secondary: "turret");
            var turret = new Turret(800, new(1250, 1100), p.Id, 100, m.Now + 45) { Aim = 1 };
            m.Turrets.Add(turret);
            var enemy = Target(900, new(1300, 1100)); m.Enemies.Add(enemy);
            m.Step(.01); Check(!m.Visible(enemy.Position) && m.Bullets.Count == 0); Near(turret.Aim, 1);
            using (var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(Snapshot.Create(m, p), RoomHost.Json)))
            {
                Check(snapshot.RootElement.GetProperty("enemies").GetArrayLength() == 0);
                Check(snapshot.RootElement.GetProperty("turrets").GetArrayLength() == 1);
                Check(snapshot.RootElement.GetProperty("sight").GetArrayLength() == 2);
            }
            var ally = m.Join("ally", "ally")!; ally.State = "alive"; ally.Position = new(1260, 1140);
            m.Step(.01); Check(m.Bullets.Count == 1 && m.Bullets[0].Kind == "turret");
            Advance(m, .1); Near(enemy.Hp, 984);
        }),
        ("Turrets can hit their owner, attract melee attacks and be destroyed by projectiles and blasts", () =>
        {
            var (m, p) = Active(secondary: "turret");
            p.Position = new(380, 730);
            var turret = new Turret(800, new(340, 730), p.Id, 100, m.Now + 45); m.Turrets.Add(turret);
            m.Enemies.Add(Target(900, new(450, 730)));
            m.Step(.1); Near(p.Hp, p.MaxHp - 16); Near(turret.Hp, 100);
            m.Bullets.Add(new(801, new(260, 730), new(1000, 0), null, 35));
            m.Step(.1); Near(turret.Hp, 65);
            m.Enemies.Clear();
            m.Enemies.Add(new(901, new(330, 730), "melee") { Hp = 1000 });
            m.Step(.01); Near(turret.Hp, 54);
            m.Grenades.Add(new(802, turret.Position, Vector2.Zero, p.Id) { Remaining = 0 });
            m.Step(.01); Check(m.Turrets.Count == 0);
        }),
        ("Respawning a turret user restores the selected kit and replaces rather than accumulates turrets", () =>
        {
            var (m, p) = Active("shotgun", "turret");
            var ally = m.Join("ally", "ally")!; ally.State = "alive"; ally.Position = new(300, 850);
            m.Input(p, 0, 0, 0, 0, false, false, true); m.Step(Rules.Step);
            var previous = m.Turrets.Single().Id;
            p.Ammo = 1; m.Hurt(p, 10000); Check(m.Deploy(p, 350, 800)); Advance(m, 5.05);
            Check(p.Weapon == "shotgun" && p.Secondary == "turret" && p.Ammo == 6 && p.Grenades == 0 && p.NextTurret <= m.Now);
            m.Input(p, 1, 0, 0, 0, false, false, true); m.Step(Rules.Step);
            Check(m.Turrets.Count == 1 && m.Turrets[0].Id != previous);
            p.DisconnectedAt = m.Now - 121; m.Step(Rules.Step);
            Check(m.Turrets.Count == 0);
        })
    ];

    private static (Match, Player) Active(string weapon = "rifle", string secondary = "grenade")
    {
        var match = new Match("GEAR01", new Rules { MaxEnemies = 0 }, seed: 73);
        var player = match.Join("pilot", "pilot")!;
        Check(match.SetLoadout(player, "vitality", weapon, secondary));
        Check(match.Deploy(player, 300, 730)); Advance(match, 5.05);
        return (match, player);
    }

    private static Enemy Target(int id, Vector2 at) => new(id, at, "ranged") { Hp = 1000, NextAttack = double.PositiveInfinity };
    private static void Advance(Match match, double seconds)
    {
        var until = match.Now + seconds;
        while (until - match.Now > .00000001) match.Step(Math.Min(Rules.Step, until - match.Now));
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Expected condition to be true"); }
    private static void Near(double actual, double expected)
    {
        if (Math.Abs(actual - expected) > .001) throw new Exception($"Expected {expected}, got {actual}");
    }
}
