using System.Numerics;
using System.Text.Json;
using Argus.Server.Game;
using Argus.Server.Networking;
using Microsoft.Extensions.Logging.Abstractions;
using static TestTools;

internal static class LifecycleTests
{
    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("Entry waits for selection and five seconds without revealing the landing", Deployment));
        tests.Add(new TestCase("Movement rejects invalid/replayed input and normalizes diagonals", InputAuthority));
        tests.Add(new TestCase("Stale input and disconnect stop movement and further casts", InputExpiry));
        tests.Add(new TestCase("Solo death and all-dead squads fail before pending deployment", Wipe));
        tests.Add(new TestCase("Respawn and late join wait equally, with full health and zero mana", Respawn));
        tests.Add(new TestCase("Mission pulses hit camp and late joins and eventually guarantee a wipe", Pulses));
        tests.Add(new TestCase("Supplies replenish HP/mana independently for each teammate without passive recovery", Supplies));
        tests.Add(new TestCase("Facilities must be destroyed before extraction can finish", Extraction));
        tests.Add(new TestCase("Guest tokens, replacement and reconnect preserve identity and resources", Reconnect));
        tests.Add(new TestCase("Disconnected slots expire and cannot keep a solo mission alive", Expiry));
        tests.Add(new TestCase("Snapshots omit hidden enemy actions and effects and share teammate sight", Visibility));
        tests.Add(new TestCase("Deployment never discloses hidden occupancy; a blocked arrival lands nearby without overlap", BlockedDeployment));
        tests.Add(new TestCase("A reused broadcast snapshot keeps no previous recipient data and sends integer sight", SharedSnapshot));
        tests.Add(new TestCase("A slot that never connects is released quickly while a dropped member keeps the long grace", FirstConnectGrace));
        tests.Add(new TestCase("A long session keeps its room for the full retention time after the last disconnect", RoomRetention));
    }

    private static void Deployment()
    {
        Match match = new Match("TEST01", new Rules { MaxEnemies = 0 });
        Player player = match.Join("p", "pilot")!;
        Advance(match, 20);
        Equal(player.State, "waiting");
        Equal(match.Phase, "staging");
        Check(!match.Deploy(player, double.NaN, 400));
        Check(!match.Deploy(player, 730, 350));
        Check(match.Deploy(player, 1200, 500));
        Check(!match.Visible(new Vector2(1200, 500)));
        Advance(match, 4.95);
        Equal(player.State, "deploying");
        Check(!match.Visible(new Vector2(1200, 500)));
        Advance(match, 0.1);
        Equal(player.State, "alive");
        Equal(player.Mana, 0);
        Check(match.Visible(new Vector2(1200, 500)));
    }

    private static void InputAuthority()
    {
        Fixture f = new Fixture();
        Vector2 from = f.Player.Position;
        Check(!f.Match.Input(f.Player, 0, 100, 0, 0, 0));
        Check(!f.Match.Input(f.Player, 0, double.NaN, 0, 0, 0));
        Check(!f.Match.Input(f.Player, 0, 0, 0, double.PositiveInfinity, 0));
        Check(!f.Match.Input(f.Player, 0, 0, 0, 0, 5));
        Check(!f.Match.Input(f.Player, 0, 0, 0, 0, -1));
        for (int i = 0; i < 30; i++)
        {
            Check(f.Match.Input(f.Player, i, 1, 1, 0, 0));
            f.Match.Step(Rules.Step);
        }
        Near(Vector2.Distance(from, f.Player.Position), 180, 0.1);
        Check(!f.Match.Input(f.Player, 29, -1, 0, 0, 0));
        from = f.Player.Position;
        for (int i = 30; i < 1030; i++)
        {
            f.Match.Input(f.Player, i, 1, 0, 0, 0);
        }
        Near(Vector2.Distance(from, f.Player.Position), 0);
    }

    private static void InputExpiry()
    {
        Fixture f = new Fixture();
        f.Player.Mana = 10;
        Vector2 from = f.Player.Position;
        f.Match.Input(f.Player, 0, 0, -1, 0, 1);
        Advance(f.Match, 1);
        Check(Vector2.Distance(from, f.Player.Position) < 65);
        Equal(f.Player.Mana, 9);
        f.Match.Disconnect(f.Player);
        from = f.Player.Position;
        Advance(f.Match, 2);
        Near(Vector2.Distance(from, f.Player.Position), 0);
        Equal(f.Player.Mana, 9);
    }

    private static void Wipe()
    {
        Fixture f = new Fixture();
        Player late = f.Match.Join("late", "late")!;
        Check(f.Match.Deploy(late, 300, 730));
        f.Match.Hurt(f.Player, 100);
        Advance(f.Match, 6);
        Equal(f.Match.Result, "failure");
        Equal(late.State, "deploying");
        double elapsed = f.Match.Elapsed;
        Advance(f.Match, 2);
        Near(f.Match.Elapsed, elapsed);
    }

    private static void Respawn()
    {
        Fixture f = new Fixture();
        Ally(f, "ally");
        f.Player.Mana = 9;
        f.Match.Hurt(f.Player, 100);
        Advance(f.Match, 10);
        Equal(f.Player.State, "waiting");
        Check(f.Match.Deploy(f.Player, 400, 730));
        Player late = f.Match.Join("late", "late")!;
        Check(f.Match.Deploy(late, 440, 730));
        Near(f.Player.DeployAt, late.DeployAt);
        Advance(f.Match, 4.95);
        Equal(f.Player.State, "deploying");
        Advance(f.Match, 0.1);
        Equal(f.Player.State, "alive");
        Equal(late.State, "alive");
        Equal(f.Player.Mana, 0);
        Equal(late.Mana, 0);
        Near(f.Player.Hp, f.Player.MaxHp);
    }

    private static void Pulses()
    {
        Fixture f = new Fixture(new Rules { MaxEnemies = 0, PulseSeconds = 10 });
        Equal(new Rules().PulseSeconds, 600d);
        Advance(f.Match, 7);
        Player late = Ally(f, "late");
        Advance(f.Match, 3.1);
        Near(f.Player.Hp, 4);
        Near(late.Hp, 4);
        f.Player.Hp = 6;
        late.Hp = 6;
        Advance(f.Match, 10);
        Near(f.Player.Hp, 2);
        f.Player.Hp = 6;
        late.Hp = 6;
        Advance(f.Match, 10);
        Equal(f.Match.Result, "failure");
    }

    private static void Supplies()
    {
        Fixture f = new Fixture();
        Player ally = Ally(f, "ally");
        f.Player.Hp = 2;
        f.Player.Position = f.Match.Supplies[0].Position;
        ally.Position = f.Player.Position;
        f.Match.Step(Rules.Step);
        Near(f.Player.Hp, f.Player.MaxHp);
        Equal(f.Player.Mana, f.Player.MaxMana);
        Equal(ally.Mana, ally.MaxMana);
        f.Player.Mana = 4;
        f.Player.Position = BattleMap.Camp;
        Advance(f.Match, 10);
        Equal(f.Player.Mana, 4);
        f.Player.Position = f.Match.Supplies[1].Position;
        f.Match.Step(Rules.Step);
        Check(f.Match.Supplies[1].Collected);
        Equal(f.Player.Mana, f.Player.MaxMana);
    }

    private static void Extraction()
    {
        Fixture f = new Fixture();
        f.Player.Position = BattleMap.Extraction;
        Advance(f.Match, 21);
        Equal(f.Match.Phase, "active");
        for (int i = 0; i < f.Match.Facilities.Count; i++)
        {
            f.Match.Facilities[i].Hp = 0;
        }
        Advance(f.Match, 20.1);
        Equal(f.Match.Result, "success");
    }

    private static void Reconnect()
    {
        using RoomHost host = new RoomHost(NullLogger<RoomHost>.Instance);
        Guest guest = host.Create(new EntryRequest("pilot"))!;
        (Room room, Player player, Peer peer) = host.Attach(guest.Room, guest.Token)!.Value;
        room.Match.Enemies.Clear();
        Check(host.Attach(guest.Room, "wrong") == null);
        room.Match.Deploy(player, 300, 730);
        Advance(room.Match, 2);
        host.Detach(room, player, peer);
        (Room Room, Player Player, Peer Peer) again = host.Attach(guest.Room, guest.Token)!.Value;
        Check(ReferenceEquals(player, again.Player));
        Equal(player.State, "deploying");
        Near(player.DeployAt - room.Match.Now, 3);
        Advance(room.Match, 3.1);
        player.Mana = 7;
        player.Hp = 3;
        player.Deaths = 2;
        host.Detach(room, player, again.Peer);
        (Room Room, Player Player, Peer Peer) third = host.Attach(guest.Room, guest.Token)!.Value;
        Equal(third.Player.Mana, 7);
        Near(third.Player.Hp, 3);
        Equal(third.Player.Deaths, 2);
        host.Detach(room, player, again.Peer);
        Check(player.Connected);
        host.Attach(guest.Room, guest.Token);
        Check(third.Peer.Replaced);
        for (int i = 0; i < 3; i++)
        {
            Check(host.Join(guest.Room, new EntryRequest("ally")) != null);
        }
        Check(host.Join(guest.Room, new EntryRequest("fifth")) == null);
    }

    private static void Expiry()
    {
        Fixture f = new Fixture(new Rules { MaxEnemies = 0, ReconnectSeconds = 1 });
        f.Match.Disconnect(f.Player);
        Advance(f.Match, 1.1);
        Equal(f.Match.Players.Count, 0);
        Equal(f.Match.Result, "failure");
    }

    private static void Visibility()
    {
        Fixture f = new Fixture();
        Enemy visible = Target(f);
        Enemy hidden = new Enemy(9001, new Vector2(1300, 1100), EnemyDefinition.Warden);
        hidden.Action = new EnemyAction(9002, hidden.Definition.Skills[1], 1, f.Match.Now);
        f.Match.Enemies.Add(hidden);
        f.Match.Effects.Add(new Effect(9003, 1300, 1100, "break", f.Match.Now + 1));
        string serialized = JsonSerializer.Serialize(Snapshot.Create(f.Match, f.Player), RoomHost.Json);
        using JsonDocument json = JsonDocument.Parse(serialized);
        Equal(json.RootElement.GetProperty("version").GetInt32(), 2);
        Equal(json.RootElement.GetProperty("enemies").GetArrayLength(), 1);
        Check(!serialized.Contains("9001") && !serialized.Contains("9002") && !serialized.Contains("9003"));
        Player ally = Ally(f, "scout");
        ally.Position = new Vector2(1260, 1120);
        Equal(Snapshot.Create(f.Match, f.Player).Enemies.Count, 2);
        ally.State = "waiting";
        Equal(Snapshot.Create(f.Match, f.Player).Enemies.Count, 1);
        Check(visible.Hp > 0);
    }

    private static void SharedSnapshot()
    {
        Fixture f = new Fixture();
        Player ally = Ally(f, "scout");
        // 한 대원만 보급 대기 중이게 해서 수신자마다 값이 달라지게 한다.
        f.Match.Supplies[0].ReadyAt[f.Player.Id] = f.Match.Now + 10;
        SnapshotMessage shared = Snapshot.CreateShared(f.Match);
        Snapshot.Personalize(shared, f.Match, f.Player);
        string forPilot = JsonSerializer.Serialize(shared, RoomHost.Json);
        Snapshot.Personalize(shared, f.Match, ally);
        string forScout = JsonSerializer.Serialize(shared, RoomHost.Json);
        // 같은 메시지를 다음 수신자에게 다시 써도 따로 만든 스냅샷과 같아야 한다.
        Equal(forPilot, JsonSerializer.Serialize(Snapshot.Create(f.Match, f.Player), RoomHost.Json));
        Equal(forScout, JsonSerializer.Serialize(Snapshot.Create(f.Match, ally), RoomHost.Json));
        Check(forPilot != forScout, "수신자별 값이 달라야 합니다");
        Near(shared.Supplies[0].Cooldown, 0);
        // 시야 다각형은 그리기 전용이라 정수로 보낸다. 실제 전송되는 JSON에서 확인한다.
        using JsonDocument json = JsonDocument.Parse(forScout);
        JsonElement sight = json.RootElement.GetProperty("sight");
        Check(sight.GetArrayLength() > 0);
        foreach (JsonElement polygon in sight.EnumerateArray())
        {
            foreach (JsonElement value in polygon.EnumerateArray())
            {
                Check(value.TryGetInt32(out int _), "시야 좌표가 정수가 아닙니다");
            }
        }
    }

    private static void FirstConnectGrace()
    {
        using RoomHost host = new RoomHost(NullLogger<RoomHost>.Instance);
        Guest owner = host.Create(new EntryRequest("owner"))!;
        (Room room, Player player, Peer peer) = host.Attach(owner.Room, owner.Token)!.Value;
        Guest squatter = host.Join(owner.Room, new EntryRequest("squatter"))!;
        Equal(room.Match.Players.Count, 2);
        // HTTP로 참가만 하고 접속하지 않은 자리는 짧은 유예 뒤에 풀린다.
        Advance(room.Match, room.Match.Rules.FirstConnectSeconds + 0.1);
        Equal(room.Match.Players.Count, 1);
        Check(host.Attach(owner.Room, squatter.Token) == null);
        // 접속했다가 끊긴 대원은 같은 시간이 지나도 긴 복구 유예를 유지한다.
        host.Detach(room, player, peer);
        Advance(room.Match, room.Match.Rules.FirstConnectSeconds + 0.1);
        Equal(room.Match.Players.Count, 1);
        Check(host.Attach(owner.Room, owner.Token) != null);
    }

    private static void RoomRetention()
    {
        using RoomHost host = new RoomHost(NullLogger<RoomHost>.Instance);
        Guest guest = host.Create(new EntryRequest("pilot"))!;
        (Room room, Player player, Peer peer) = host.Attach(guest.Room, guest.Token)!.Value;
        // 16분 동안 접속해 플레이한 상황이다. 마지막 참가·접속 시각이 16분 전이다.
        room.LastVisit = DateTime.UtcNow - TimeSpan.FromMinutes(16);
        Check(!room.Expired(DateTime.UtcNow), "접속 중인 방이 삭제 대상입니다");
        host.Detach(room, player, peer);
        // 연결이 끊긴 순간부터 15분을 센다. 끊기자마자 지우면 120초 재접속 유예가 의미가 없다.
        Check(!room.Expired(DateTime.UtcNow), "연결이 끊기자마자 방이 삭제 대상이 됩니다");
        Check(room.Expired(DateTime.UtcNow + TimeSpan.FromMinutes(16)));
    }

    private static void BlockedDeployment()
    {
        Fixture f = new Fixture();
        Player late = f.Match.Join("late", "late")!;
        Enemy enemy = Target(f);
        enemy.Position = new Vector2(1000, 600);
        Check(!f.Match.Visible(enemy.Position));
        Check(f.Match.Deploy(late, enemy.Position.X, enemy.Position.Y));
        Check(!f.Match.Visible(enemy.Position));
        Advance(f.Match, 5.1);
        // 대기로 되돌리지 않고 가까운 빈 자리로 투입한다. 투입 성공 여부로 안개 속을 엿볼 수 없다.
        Equal(late.State, "alive");
        Check(late.HasDeployed);
        double gap = Vector2.Distance(late.Position, enemy.Position);
        Check(gap >= Rules.PlayerRadius + enemy.Definition.Radius - 0.01, "적과 겹쳐 투입되었습니다");
        Check(gap < 80, "투입 지점에서 너무 멀리 옮겨졌습니다");
        Check(f.Match.Map.LineOfSight(late.Position, enemy.Position), "원래 투입 지점이 보이지 않는 곳으로 옮겨졌습니다");
        // 투입된 뒤에는 자기 시야로 적을 정상적으로 본다.
        Check(f.Match.Visible(enemy.Position));
    }
}
