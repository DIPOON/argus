using System.Numerics;
using System.Text.Json;
using Argus.Server.Game;
using Argus.Server.Networking;
using Microsoft.Extensions.Logging.Abstractions;

var tests = new (string Name, Action Run)[]
{
    ("Entry waits indefinitely for selection, then five seconds without revealing the landing", () =>
    {
        var m = New(); var p = m.Join("p", "pilot")!;
        Advance(m, 20); Equal(p.State, "waiting"); Equal(m.Phase, "staging");
        Check(!m.Deploy(p, double.NaN, 400)); Check(!m.Deploy(p, 730, 350)); Check(!m.Deploy(p, -2, 500));
        Check(m.Deploy(p, 1200, 500)); Check(!m.Visible(new(1200, 500)));
        Advance(m, 4.95); Equal(p.State, "deploying"); Check(!m.Visible(new(1200, 500)));
        Advance(m, .1); Equal(p.State, "alive"); Near(p.Position.X, 1200); Check(m.Visible(new(1200, 500)));
    }),
    ("Movement rejects invalid/replayed input and normalizes diagonals", () =>
    {
        var (m, p) = Active(); var from = p.Position;
        Check(!m.Input(p, 0, 100, 0, 0, false, false, false));
        Check(!m.Input(p, 0, double.NaN, 0, 0, false, false, false));
        Check(!m.Input(p, 0, 0, 0, double.PositiveInfinity, false, false, false));
        for (var i = 0; i < 30; i++) { Check(m.Input(p, i, 1, 1, 0, false, false, false)); m.Step(Rules.Step); }
        Near(Vector2.Distance(from, p.Position), 180, .1);
        Check(!m.Input(p, 29, -1, 0, 0, false, false, false));
        var before = p.Position;
        for (var i = 30; i < 1030; i++) m.Input(p, i, 1, 0, 0, false, false, false);
        Near(Vector2.Distance(before, p.Position), 0);
    }),
    ("Stale input and disconnect stop motion", () =>
    {
        var (m, p) = Active(); var from = p.Position;
        m.Input(p, 0, 1, 0, 0, true, false, false); Advance(m, 1);
        Check(Vector2.Distance(from, p.Position) < 65);
        m.Disconnect(p); from = p.Position; Advance(m, 1); Near(Vector2.Distance(from, p.Position), 0);
    }),
    ("Rifle cadence/ammo are server-owned and reload completes in two seconds, below deployment", () =>
    {
        var (m, p) = Active();
        for (var i = 0; i < 30; i++) { m.Input(p, i, 0, 0, -Math.PI / 2, true, false, false); m.Step(Rules.Step); }
        Check(p.Ammo is >= 22 and < 30);
        var ammo = p.Ammo;
        m.Input(p, 30, 0, 0, 0, true, true, false); m.Step(Rules.Step);
        Check(p.ReloadUntil > m.Now); Near(p.ReloadUntil - m.Now, 2); Check(m.Rules.ReloadSeconds < m.Rules.DeploySeconds);
        Advance(m, 1.9); Equal(p.Ammo, ammo);
        Advance(m, .14); Equal(p.Ammo, 30); Near(p.ReloadUntil, 0);
        p.Ammo = 0; m.Step(Rules.Step); Check(p.ReloadUntil > m.Now);
        Advance(m, 2.05); Equal(p.Ammo, 30);
    }),
    ("Bullets hit friendly players and cannot pass through an intact wall", () =>
    {
        var (m, p) = Active(); var q = m.Join("q", "ally")!;
        q.State = "alive"; q.Position = p.Position + new Vector2(70, 0);
        m.Input(p, 0, 0, 0, 0, true, false, false); Advance(m, .1);
        Near(q.Hp, q.MaxHp - 24);
        p.Firing = false; m.Bullets.Clear(); q.Position = new(790, 500);
        var oldHp = q.Hp;
        m.Bullets.Add(new(800, new(675, 500), new(1050, 0), p.Id, 24)); Advance(m, .2);
        Near(q.Hp, oldHp); Check(m.Map.Solid(15, 10));
    }),
    ("Movement/knockback cannot tunnel through walls; destruction opens the same wall", () =>
    {
        var map = new BattleMap();
        Check(map.CanStand(BattleMap.Extraction));
        var blocked = map.Move(new(680, 500), new(300, 0)); Check(blocked.X < 720);
        Check(map.Damage(10 * BattleMap.Columns + 15, 100));
        var free = map.Move(new(680, 500), new(140, 0)); Near(free.X, 820, .1);
        Check(!map.Damage(0, 10000));
    }),
    ("Explosion cover is evaluated before destruction and grenade stock is enforced", () =>
    {
        var (m, p) = Active(); var q = m.Join("q", "covered")!;
        q.State = "alive"; q.Position = new(790, 500);
        var e = new Enemy(910, new(670, 540), "melee") { Hp = 200, NextAttack = 10000 }; m.Enemies.Add(e);
        m.Grenades.Add(new(900, new(700, 500), Vector2.Zero, p.Id) { Remaining = 0 });
        m.Step(Rules.Step); Near(q.Hp, q.MaxHp); Check(e.Hp < 200); Check(e.Impulse.Length() > 0); Check(!m.Map.Solid(15, 10));
        m.Enemies.Clear();
        p.Grenades = 0; m.Input(p, 0, 0, 0, 0, false, false, true); m.Step(Rules.Step); Equal(m.Grenades.Count, 0);
    }),
    ("Serialization omits hidden enemy/projectile/effect positions and uses shared vision", () =>
    {
        var (m, p) = Active();
        m.Enemies.Add(new(1001, new(330, 730), "melee"));
        m.Enemies.Add(new(1002, new(1300, 1100), "ranged"));
        m.Bullets.Add(new(1003, new(1300, 1100), new(2, 3), null, 16));
        m.Effects.Add(new(1004, 1300, 1100, "kill", m.Now + 1));
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(Snapshot.Create(m, p), RoomHost.Json)))
        {
            var root = json.RootElement;
            Equal(root.GetProperty("enemies").GetArrayLength(), 1);
            Equal(root.GetProperty("enemies")[0].GetProperty("id").GetInt32(), 1001);
            Equal(root.GetProperty("bullets").GetArrayLength(), 0);
            Check(!root.GetProperty("effects").EnumerateArray().Any(e => e.GetProperty("id").GetInt32() == 1004));
        }
        var ally = m.Join("q", "scout")!; ally.State = "alive"; ally.Position = new(1260, 1120);
        Check(m.Visible(new(1300, 1100)));
        using var shared = JsonDocument.Parse(JsonSerializer.Serialize(Snapshot.Create(m, p), RoomHost.Json));
        Equal(shared.RootElement.GetProperty("enemies").GetArrayLength(), 2);
        ally.Position = new(690, 500); Check(!m.Visible(new(800, 500)));
    }),
    ("Solo death fails and pending newcomers cannot save a wiped squad", () =>
    {
        var (m, p) = Active(); var q = m.Join("q", "late")!; m.Deploy(q, 300, 730);
        m.Hurt(p, 10000); Advance(m, 6); Equal(m.Result, "failure"); Equal(q.State, "deploying");
        var elapsed = m.Elapsed; Advance(m, 5); Near(m.Elapsed, elapsed);
        var (solo, only) = Active(); solo.Hurt(only, 10000); solo.Step(Rules.Step); Equal(solo.Result, "failure");
    }),
    ("Respawn and late join wait equally and respawn restores the original loadout", () =>
    {
        var (m, p) = Active(); var q = m.Join("q", "ally")!; m.Deploy(q, 330, 730); Advance(m, 5.05);
        p.Ammo = 2; p.Grenades = 0; m.Hurt(p, 1000); m.Step(Rules.Step); Equal(m.Phase, "active");
        Advance(m, 10); Equal(p.State, "waiting");
        Check(m.Deploy(p, 400, 730)); var late = m.Join("r", "late")!; Check(m.Deploy(late, 460, 730));
        Near(p.DeployAt, late.DeployAt); Advance(m, 4.95); Equal(p.State, "deploying");
        Advance(m, .1); Equal(p.State, "alive"); Equal(late.State, "alive"); Near(p.Hp, p.MaxHp); Equal(p.Ammo, 30); Equal(p.Grenades, 3);
        Check(!m.SetLoadout(p, "mobility"));
    }),
    ("Mission-wide pulses hit camp and late joins, escalate to inevitable wipe", () =>
    {
        Equal(new Rules().PulseSeconds, 600d);
        var (m, p) = Active(new Rules { MaxEnemies = 0, PulseSeconds = 10 });
        Advance(m, 7); var q = m.Join("q", "late")!; q.State = "alive"; q.Position = new(1100, 600);
        Advance(m, 3.1); Equal(m.Pulse, 1); Near(p.Hp, 70); Near(q.Hp, 70);
        p.Hp = p.MaxHp; q.Hp = q.MaxHp; Advance(m, 10); Equal(m.Pulse, 2); Near(p.Hp, 10); Near(q.Hp, 10);
        p.Hp = p.MaxHp; q.Hp = q.MaxHp; Advance(m, 10); Equal(m.Result, "failure");
    }),
    ("Supplies restore HP/grenades; optional caches count independently from extraction", () =>
    {
        var (m, p) = Active(); p.Hp = 10; p.Grenades = 0; p.Ammo = 5; p.Position = m.Supplies[0].Position;
        m.Step(Rules.Step); Near(p.Hp, p.MaxHp); Equal(p.Grenades, 3); Equal(p.Ammo, 5);
        p.Position = m.Supplies[1].Position; m.Step(Rules.Step); Check(m.Supplies[1].Collected);
        p.Position = BattleMap.Extraction; Advance(m, 22); Check(m.Phase != "ended");
        foreach (var f in m.Facilities) f.Hp = 0;
        Advance(m, 20.1); Equal(m.Result, "success");
    }),
    ("Room capacity and guest token isolate identity; reconnect preserves resources and timers", () =>
    {
        using var host = new RoomHost(NullLogger<RoomHost>.Instance);
        var guest = host.Create(new("pilot", "mobility"))!;
        var attached = host.Attach(guest.Room, guest.Token)!.Value;
        var (room, p, peer) = attached;
        Equal(p.Passive, "mobility"); Check(host.Attach(guest.Room, "wrong") is null);
        room.Match.Deploy(p, 300, 730); Advance(room.Match, 2);
        host.Detach(room, p, peer); var again = host.Attach(guest.Room, guest.Token)!.Value;
        Check(ReferenceEquals(p, again.Player)); Equal(p.State, "deploying"); Near(p.DeployAt - room.Match.Now, 3, .01);
        Advance(room.Match, 3.1); p.Ammo = 7; p.Grenades = 1; p.Hp = 40; p.Deaths = 2;
        host.Detach(room, p, again.Peer); var third = host.Attach(guest.Room, guest.Token)!.Value;
        Equal(third.Player.Ammo, 7); Equal(third.Player.Grenades, 1); Near(third.Player.Hp, 40); Equal(third.Player.Deaths, 2);
        host.Detach(room, p, again.Peer); Check(p.Connected); // obsolete socket cannot disconnect a replacement
        var replacement = host.Attach(guest.Room, guest.Token)!.Value;
        Check(third.Peer.Replaced); Check(replacement.Player.Connected);
        for (var i = 0; i < 3; i++) Check(host.Join(guest.Room, new("ally", null)) is not null);
        Check(host.Join(guest.Room, new("fifth", null)) is null);
    }),
    ("Expired disconnected slots are released and cannot keep a solo match alive", () =>
    {
        var (m, p) = Active(new Rules { MaxEnemies = 0, ReconnectSeconds = 1 });
        m.Disconnect(p); Advance(m, 1.1); Equal(m.Players.Count, 0); Equal(m.Result, "failure");
    }),
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}\n{error}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} checks passed");
return failed == 0 ? 0 : 1;

static Match New(Rules? rules = null) => new("TEST01", rules ?? new Rules { MaxEnemies = 0 }, seed: 73);
static (Match Match, Player Player) Active(Rules? rules = null)
{
    var m = New(rules); var p = m.Join("p", "pilot")!; p.Connected = true;
    Check(m.Deploy(p, BattleMap.Camp.X, BattleMap.Camp.Y)); Advance(m, 5.05);
    Equal(p.State, "alive"); return (m, p);
}
static void Advance(Match match, double seconds)
{
    var until = match.Now + seconds;
    while (until - match.Now > .00000001) match.Step(Math.Min(Rules.Step, until - match.Now));
}
static void Check(bool condition) { if (!condition) throw new Exception("Expected condition to be true"); }
static void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"Expected {expected}, got {actual}"); }
static void Near(double actual, double expected, double tolerance = .001) { if (Math.Abs(actual - expected) > tolerance) throw new Exception($"Expected {expected} ±{tolerance}, got {actual}"); }
