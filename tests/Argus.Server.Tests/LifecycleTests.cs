using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Argus.Server.Game;
using Argus.Server.Networking;
using static TestTools;

internal static class LifecycleTests
{
    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("Entry waits for selection and five seconds without revealing the landing", Deployment));
        tests.Add(new TestCase("A repeated deploy request while deploying is accepted without moving the landing or the timer", RepeatedDeploy));
        tests.Add(new TestCase("Movement rejects invalid/replayed input and normalizes diagonals", InputAuthority));
        tests.Add(new TestCase("Steps of a split move add up to exactly the move even when it does not divide evenly", StepsAddUp));
        tests.Add(new TestCase("Stale input and disconnect stop movement and further casts", InputExpiry));
        tests.Add(new TestCase("Solo death and all-dead squads fail before pending deployment", Wipe));
        tests.Add(new TestCase("Respawn and late join wait equally, with full health and zero mana", Respawn));
        tests.Add(new TestCase("Time alone never hurts anyone: there is no global shock", NoGlobalShock));
        tests.Add(new TestCase("Field boxes run out after their uses, the camp box never does, and a destroyed facility leaves a box", FiniteSupplies));
        tests.Add(new TestCase("Supplies replenish HP/mana independently for each teammate without passive recovery", Supplies));
        tests.Add(new TestCase("Facilities must be destroyed before extraction can finish", Extraction));
        tests.Add(new TestCase("Guest tokens, replacement and reconnect preserve identity and resources", Reconnect));
        tests.Add(new TestCase("Disconnected slots expire and cannot keep a solo mission alive", Expiry));
        tests.Add(new TestCase("Snapshots omit hidden enemy actions and effects and share teammate sight", Visibility));
        tests.Add(new TestCase("Deployment never discloses hidden occupancy; a blocked arrival lands nearby without overlap", BlockedDeployment));
        tests.Add(new TestCase("A broadcast serializes the shared part once and matches a snapshot built for one recipient", SharedSnapshot));
        tests.Add(new TestCase("Effects, notices and actions keep their wire shape and send tick times as seconds rounded like now", TimedWireShape));
        tests.Add(new TestCase("A parry followup ends on the tick its followupUntil names, not after the followup time rounded on its own", FollowupWireTime));
        tests.Add(new TestCase("Extraction progress times sixty rounds back to the exact half-tick count, odd counts included", ExtractionProgressWire));
        tests.Add(new TestCase("Coordinates go out rounded to hundredths for drawing and as exact raw values for prediction", CoordinateWireRounding));
        tests.Add(new TestCase("Two identical notices on the same tick keep distinct ids that keep growing", NoticeIds));
        tests.Add(new TestCase("A slot that never connects is released quickly while a dropped member keeps the long grace", FirstConnectGrace));
        tests.Add(new TestCase("A long session keeps its room for the full retention time after the last disconnect", RoomRetention));
        tests.Add(new TestCase("A room with nobody left is released soon instead of holding a slot for fifteen minutes", EmptyRoomExpiry));
        tests.Add(new TestCase("One address cannot hold more than a few rooms at once", RoomsPerAddress));
        tests.Add(new TestCase("A full server refuses new rooms before generating their maps", ServerFull));
        tests.Add(new TestCase("Rooms whose maps are still being generated count toward the room limits", PendingRooms));
        tests.Add(new TestCase("A room that fails while generating its map gives its reserved slot back", FailedCreation));
        tests.Add(new TestCase("Room creation and joining report why they failed so the HTTP layer can answer accurately", EntryFailures));
        tests.Add(new TestCase("Rooms are generated missions with elites, and the handmade map only when the server allows it", RoomLayouts));
        tests.Add(new TestCase("Limits count an IPv6 /64 as one address and leave local and private networks out of the room cap", AddressGrouping));
        tests.Add(new TestCase("Hidden spawns do not advance the id sequence that player actions reveal", HiddenIdGaps));
        tests.Add(new TestCase("Walls, rules and map are resent until delivered and walls again after they change", StaticParts));
        tests.Add(new TestCase("Tokens of removed players are cleaned when a room steps", TokenCleanup));
        tests.Add(new TestCase("A room whose simulation keeps throwing is closed instead of holding its slot", FailingRoom));
        tests.Add(new TestCase("A deployment whose surroundings and camp are blocked still lands somewhere", CrowdedLanding));
        tests.Add(new TestCase("Error and pong messages use their own channel and are never dropped behind snapshots", ControlChannel));
        tests.Add(new TestCase("Sending puts error and pong ahead of snapshots, wakes for them and ends when snapshots close", SendOrder));
        tests.Add(new TestCase("Sending ends on cancellation instead of spinning on a cancelled wait", SendCancel));
        tests.Add(new TestCase("Sending still delivers an error that arrives as the snapshot channel closes", SendDrainsControl));
        tests.Add(new TestCase("Sending snapshots alone does not pile up unfinished waits on the control channel", SendWaits));
        tests.Add(new TestCase("Snapshots go out ten times a second on the real timer period and do not burst after a stall", PublishRate));
    }

    private static void Deployment()
    {
        Match match = new Match("TEST01", new Rules { MaxEnemies = 0 }, 73);
        Player player = match.Join("p", "pilot")!;
        Advance(match, 20);
        Equal(player.State, "waiting");
        Equal(match.Phase, "staging");
        Check(!match.Deploy(player, double.NaN, 400));
        Check(!match.Deploy(player, 730, 350));
        Check(match.Deploy(player, 1200, 500));
        Check(!match.Visible(FixedVector.FromInt(1200, 500)));
        Advance(match, 4.95);
        Equal(player.State, "deploying");
        Check(!match.Visible(FixedVector.FromInt(1200, 500)));
        Advance(match, 0.1);
        Equal(player.State, "alive");
        Equal(player.Mana, 0);
        Check(match.Visible(FixedVector.FromInt(1200, 500)));
    }

    private static void RepeatedDeploy()
    {
        Match match = new Match("TEST01", new Rules { MaxEnemies = 0 }, 73);
        Player player = match.Join("p", "pilot")!;
        Check(match.Deploy(player, 1200, 500));
        FixedVector landing = player.Landing;
        long arrives = player.DeployTick;
        Advance(match, 1);
        // 투입 버튼을 두 번 눌러도 거절로 답하지 않는다. 자리와 도착 시각은 처음 요청대로다.
        Check(match.Deploy(player, 1100, 520), "투입 중인 대원의 재요청을 거절했습니다");
        Equal(player.State, "deploying");
        Equal(player.Landing, landing);
        Equal(player.DeployTick, arrives);
        Advance(match, match.Rules.DeploySeconds);
        Equal(player.State, "alive");
        // 살아 있는 대원의 투입 요청은 여전히 거절한다.
        Check(!match.Deploy(player, 1100, 520));
    }

    private static void InputAuthority()
    {
        Fixture f = new Fixture();
        FixedVector from = f.Player.Position;
        Check(!f.Match.Input(f.Player, 0, 100, 0, 0, 0));
        Check(!f.Match.Input(f.Player, 0, double.NaN, 0, 0, 0));
        Check(!f.Match.Input(f.Player, 0, 0, 0, double.PositiveInfinity, 0));
        Check(!f.Match.Input(f.Player, 0, 0, 0, 0, 5));
        Check(!f.Match.Input(f.Player, 0, 0, 0, 0, -1));
        for (int i = 0; i < 30; i++)
        {
            Check(f.Match.Input(f.Player, i, 1, 1, 0, 0));
            f.Match.Step();
        }
        Near(Distance(from, f.Player.Position), 180, 0.1);
        // 한 방향으로는 한 틱에 정확히 6(초당 180 / 30)씩 움직인다. 1/30을 곱하면 고정소수점 반올림 때문에 0.3% 어긋난다.
        Fixture straight = new Fixture();
        FixedVector start = straight.Player.Position;
        for (int i = 0; i < 10; i++)
        {
            Check(straight.Match.Input(straight.Player, i, 1, 0, 0, 0));
            straight.Match.Step();
        }
        Equal(straight.Player.Position, start + FixedVector.FromInt(60, 0));
        Check(!f.Match.Input(f.Player, 29, -1, 0, 0, 0));
        from = f.Player.Position;
        for (int i = 30; i < 1030; i++)
        {
            f.Match.Input(f.Player, i, 1, 0, 0, 0);
        }
        Near(Distance(from, f.Player.Position), 0);
    }

    private static void StepsAddUp()
    {
        // 한 틱의 이동은 4단위보다 길면 여러 걸음으로 나눈다. 초당 200이면 한 틱에 약 6.667(Raw 27307)이라 두 걸음이고,
        // 둘로 나누어떨어지지 않는다. 걸음마다 delta / 2(Raw 13654)를 쓰면 한 틱에 Raw 하나씩 더 간다.
        // 걸음을 누적 이동량(delta × i / 걸음 수)의 차로 정하므로, 걸음을 다 더하면 정확히 한 틱의 이동 거리다.
        Rules rules = new Rules { MaxEnemies = 0, PlayerSpeed = 200 };
        Fixture f = new Fixture(rules);
        Fixed step = Fixed.FromDouble(rules.PlayerSpeed) / Rules.TicksPerSecond;
        Equal(step.Raw, 27307L);
        FixedVector start = f.Player.Position;
        for (int i = 0; i < 3; i++)
        {
            Check(f.Match.Input(f.Player, i, 1, 0, 0, 0));
            f.Match.Step();
        }
        FixedVector moved = new FixedVector(step * 3, Fixed.Zero);
        Equal(f.Player.Position, start + moved);
    }

    private static void InputExpiry()
    {
        Fixture f = new Fixture();
        f.Player.Mana = 10;
        FixedVector from = f.Player.Position;
        f.Match.Input(f.Player, 0, 0, -1, 0, 1);
        Advance(f.Match, 1);
        Check(Distance(from, f.Player.Position) < 65);
        Equal(f.Player.Mana, 9);
        f.Match.Disconnect(f.Player);
        from = f.Player.Position;
        Advance(f.Match, 2);
        Near(Distance(from, f.Player.Position), 0);
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
        Equal(f.Player.DeployTick, late.DeployTick);
        Advance(f.Match, 4.95);
        Equal(f.Player.State, "deploying");
        Advance(f.Match, 0.1);
        Equal(f.Player.State, "alive");
        Equal(late.State, "alive");
        Equal(f.Player.Mana, 0);
        Equal(late.Mana, 0);
        Near(f.Player.Hp, f.Player.MaxHp);
    }

    private static void NoGlobalShock()
    {
        // 예전에는 600초마다 전역 충격이 2 → 4 → 8… 피해를 줬다. 이제 작전 시간은 보급이 떨어지는 것으로만 제한한다.
        // 예전 충격 두 번(600초, 1200초)이 지나도록 적 없이 기다려도 아무도 다치지 않고 작전이 이어진다.
        Fixture f = new Fixture(new Rules { MaxEnemies = 0 });
        Player late = Ally(f, "late");
        Advance(f.Match, 21 * 60);
        Near(f.Player.Hp, f.Player.MaxHp);
        Near(late.Hp, late.MaxHp);
        Equal(f.Match.Phase, "active");
    }

    private static void FiniteSupplies()
    {
        Fixture f = new Fixture();
        Player ally = Ally(f, "ally");
        Player third = Ally(f, "third");
        Player[] squad = new Player[] { f.Player, ally, third };
        // 손으로 만든 맵의 보급은 캠프(1번), 부목표(2·3번), 들판(4·5번) 순서다.
        Supply camp = f.Match.Supplies[0];
        Supply field = f.Match.Supplies[3];
        Equal(camp.Kind, SupplyKind.Camp);
        Equal(field.Kind, SupplyKind.Field);
        Check(camp.UsesLeft == null, "캠프 상자에 횟수 제한이 있습니다");
        Equal(field.UsesLeft, (int?)f.Match.Rules.FieldSupplyUses);

        // 들판 상자는 쓴 사람만 채우고 한 번에 하나씩 줄어든다. 세 대원이 함께 서면 횟수(2)만큼만 채운다.
        for (int i = 0; i < squad.Length; i++)
        {
            squad[i].Mana = 0;
            squad[i].Position = field.Position;
        }
        f.Match.Step();
        Equal(f.Player.Mana, f.Player.MaxMana);
        Equal(ally.Mana, ally.MaxMana);
        Equal(third.Mana, 0);
        Equal(field.UsesLeft, (int?)0);
        // 다 쓴 상자는 대기 시간이 지나도 다시 채우지 않는다.
        Advance(f.Match, f.Match.Rules.SupplyCooldown + 0.1);
        Equal(third.Mana, 0);

        // 캠프 상자는 횟수 제한이 없다. 대기 시간이 지날 때마다 몇 번이고 모두를 채운다.
        for (int round = 0; round < 4; round++)
        {
            for (int i = 0; i < squad.Length; i++)
            {
                squad[i].Mana = 0;
                squad[i].Position = camp.Position;
            }
            f.Match.Step();
            for (int i = 0; i < squad.Length; i++)
            {
                Equal(squad[i].Mana, squad[i].MaxMana);
            }
            Advance(f.Match, f.Match.Rules.SupplyCooldown + 0.1);
        }
        Check(camp.UsesLeft == null, "캠프 상자의 횟수가 줄었습니다");

        // 시설을 부수면 그 자리에 횟수가 있는 보급 상자가 생긴다. 시설 파괴는 공개 정보라 보급 번호를 이어 붙인다.
        Facility facility = f.Match.Facilities[0];
        int before = f.Match.Supplies.Count;
        f.Player.Position = facility.Position - FixedVector.FromInt(55, 0);
        for (int i = 0; i < 100; i++)
        {
            f.Match.Input(f.Player, i, 0, 0, 0, 1);
            f.Match.Step();
        }
        Near(facility.Hp, 0);
        Equal(f.Match.Supplies.Count, before + 1);
        Supply left = f.Match.Supplies[before];
        Equal(left.Id, before + 1);
        Equal(left.Position, facility.Position);
        Equal(left.Kind, SupplyKind.Field);
        Equal(left.UsesLeft, (int?)f.Match.Rules.FacilitySupplyUses);
        // 그 상자도 쓴 사람만 채운다. 곁에 있어도 상자 근처(35)에 들지 않은 동료는 채우지 않는다.
        ally.Mana = 0;
        third.Mana = 0;
        ally.Position = left.Position;
        third.Position = left.Position + FixedVector.FromInt(60, 0);
        f.Match.Step();
        Equal(ally.Mana, ally.MaxMana);
        Equal(third.Mana, 0);
        Equal(left.UsesLeft, (int?)(f.Match.Rules.FacilitySupplyUses - 1));

        // 스냅샷은 보급마다 남은 횟수를 보낸다. 캠프 상자는 횟수 제한이 없어 null이다. 전역 충격 필드는 없다.
        byte[] snapshot = Snapshot.Create(f.Match, f.Player, RoomHost.Json);
        using (JsonDocument json = JsonDocument.Parse(snapshot))
        {
            JsonElement root = json.RootElement;
            Equal(root.GetProperty("version").GetInt32(), 4);
            Check(!root.TryGetProperty("pulse", out JsonElement _) && !root.TryGetProperty("nextPulse", out JsonElement _), "전역 충격 필드가 남아 있습니다");
            JsonElement supplies = root.GetProperty("supplies");
            Equal(supplies[0].GetProperty("uses").ValueKind, JsonValueKind.Null);
            Equal(supplies[3].GetProperty("uses").GetInt32(), 0);
            Equal(supplies[before].GetProperty("uses").GetInt32(), f.Match.Rules.FacilitySupplyUses - 1);
        }
    }

    private static void Supplies()
    {
        Fixture f = new Fixture();
        Player ally = Ally(f, "ally");
        f.Player.Hp = 2;
        f.Player.Position = f.Match.Supplies[0].Position;
        ally.Position = f.Player.Position;
        f.Match.Step();
        Near(f.Player.Hp, f.Player.MaxHp);
        Equal(f.Player.Mana, f.Player.MaxMana);
        Equal(ally.Mana, ally.MaxMana);
        f.Player.Mana = 4;
        f.Player.Position = f.Match.Layout.Camp;
        Advance(f.Match, 10);
        Equal(f.Player.Mana, 4);
        f.Player.Position = f.Match.Supplies[1].Position;
        f.Match.Step();
        Check(f.Match.Supplies[1].Collected);
        Equal(f.Player.Mana, f.Player.MaxMana);
    }

    private static void Extraction()
    {
        Fixture f = new Fixture();
        f.Player.Position = f.Match.Layout.Extraction;
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
        using RoomHost host = NewHost();
        Guest guest = host.Create(new EntryRequest("pilot"))!;
        (Room room, Player player, Peer peer) = host.Attach(guest.Room, guest.Token)!.Value;
        room.Match.Enemies.Clear();
        Check(host.Attach(guest.Room, "wrong") == null);
        // 방마다 맵이 다르므로 그 방의 캠프에 투입한다.
        Check(room.Match.Deploy(player, room.Match.Layout.Camp.X.ToDouble(), room.Match.Layout.Camp.Y.ToDouble()));
        Advance(room.Match, 2);
        host.Detach(room, player, peer);
        (Room Room, Player Player, Peer Peer) again = host.Attach(guest.Room, guest.Token)!.Value;
        Check(ReferenceEquals(player, again.Player));
        Equal(player.State, "deploying");
        Equal(player.DeployTick - room.Match.Tick, Rules.ToTicks(3));
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
        Enemy hidden = new Enemy(9001, FixedVector.FromInt(1300, 1100), EnemyDefinition.Warden);
        hidden.Action = new EnemyAction(9002, hidden.Definition.Skills[1], Angle.FromRadians(1), f.Match.Tick);
        f.Match.Enemies.Add(hidden);
        f.Match.Effects.Add(new Effect(9003, FixedVector.FromInt(1300, 1100), "break", f.Match.Tick + Rules.ToTicks(1)));
        string serialized = Encoding.UTF8.GetString(Snapshot.Create(f.Match, f.Player, RoomHost.Json));
        using JsonDocument json = JsonDocument.Parse(serialized);
        Equal(json.RootElement.GetProperty("version").GetInt32(), 4);
        Equal(json.RootElement.GetProperty("enemies").GetArrayLength(), 1);
        Check(!serialized.Contains("9001") && !serialized.Contains("9002") && !serialized.Contains("9003"));
        Player ally = Ally(f, "scout");
        ally.Position = FixedVector.FromInt(1260, 1120);
        Equal(Snapshot.CreateShared(f.Match).Enemies.Count, 2);
        ally.State = "waiting";
        Equal(Snapshot.CreateShared(f.Match).Enemies.Count, 1);
        Check(visible.Hp > 0);
    }

    private static void TimedWireShape()
    {
        // 효과·안내·기술 시각은 서버에서 틱으로 다루지만 통신 형식은 초다. 전송용 자료구조를 따로 두므로 모양도 그대로인지 본다.
        // 투입 시간 같은 시험 설정은 바뀔 수 있으므로 기대값은 규칙에서 구하고, 항목은 순서가 아니라 내용으로 찾는다.
        Match match = new Match("WIRE01", new Rules { MaxEnemies = 0 }, 1);
        Player player = match.Join("p", "pilot")!;
        Check(match.Deploy(player, match.Layout.Camp.X.ToDouble(), match.Layout.Camp.Y.ToDouble()));
        long landing = Rules.ToTicks(match.Rules.DeploySeconds);
        Advance(match, match.Rules.DeploySeconds);
        Equal(match.Tick, landing);

        // 착지한 틱에 작전 시작 안내와 투입 효과가 나온다.
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(match, player, RoomHost.Json)))
        {
            JsonElement root = json.RootElement;
            double now = root.GetProperty("now").GetDouble();
            CheckTickTime(now);
            // 받은 초에 30을 곱해 반올림하면 틱 번호가 된다.
            Equal((long)Math.Round(now * Rules.TicksPerSecond), landing);
            JsonElement start = Find(root.GetProperty("notices"), "text", "작전 시작");
            Equal(PropertyNames(start), "id,at,text");
            // 같은 틱의 시각은 같은 방식으로 반올림해 보내므로 now와 정확히 같다.
            Equal(start.GetProperty("at").GetDouble(), now);
            JsonElement effect = Find(root.GetProperty("effects"), "kind", "deploy");
            Equal(PropertyNames(effect), "id,x,y,kind,until");
            double until = effect.GetProperty("until").GetDouble();
            CheckTickTime(until);
            // 초라면 투입 효과는 지금부터 1초 안에 사라진다. 틱 번호를 보내면 수백이 된다.
            Check(until > now && until < now + 1, $"투입 효과의 until {until}이 초가 아닙니다");
        }

        // 다음 틱에 타격을 시작해 기술 시각도 본다.
        player.Mana = player.MaxMana;
        Check(match.Input(player, 1, 0, 0, 0, 1));
        match.Step();
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(match, player, RoomHost.Json)))
        {
            JsonElement root = json.RootElement;
            double now = root.GetProperty("now").GetDouble();
            JsonElement action = root.GetProperty("players")[0].GetProperty("actions")[0];
            // 방금 시작한 기술의 startedAt은 같은 틱의 now와 정확히 같다.
            Equal(action.GetProperty("startedAt").GetDouble(), now);
            string[] times = new string[] { "contactAt", "activeUntil", "followupUntil", "endsAt" };
            for (int i = 0; i < times.Length; i++)
            {
                double value = action.GetProperty(times[i]).GetDouble();
                CheckTickTime(value);
                Check(value > now && value < now + 1, $"기술의 {times[i]} {value}가 초가 아닙니다");
            }
            // 타격에는 반격이 없으므로 반격 구간이 비어 있다.
            Equal(action.GetProperty("followupUntil").GetDouble(), action.GetProperty("activeUntil").GetDouble());
        }
    }

    private static void FollowupWireTime()
    {
        // 받아치기의 반격은 시작부터 잰 초(유효 시간 + 반격 시간)를 한 번에 올림한 틱에 끝난다.
        // 반격 시간만 따로 올림해 유효 시간 끝에 더하면 한 틱 늦을 수 있다(docs/combat.md의 예).
        // 그래서 서버가 반격이 끝나는 시각(followupUntil)을 보내고, 단계(phase)도 바로 그 틱에 바뀌어야 한다.
        Match match = new Match("WIRE02", new Rules { MaxEnemies = 0 }, 1);
        Player player = match.Join("p", "pilot")!;
        Check(match.Deploy(player, match.Layout.Camp.X.ToDouble(), match.Layout.Camp.Y.ToDouble()));
        Advance(match, match.Rules.DeploySeconds);
        player.Mana = player.MaxMana;
        // 2번 슬롯은 받아치기다. 주변에 적이 없어도 빈 방어 뒤에 반격한다.
        Check(match.Input(player, 1, 0, 0, 0, 2));
        match.Step();
        PlayerAction parry = player.Actions[0];
        Equal(parry.Skill.Id, "parry");
        long followupUntil = parry.StartedTick + Rules.ToTicks(match.Rules.Parry.ActiveSeconds + match.Rules.ParryFollowupSeconds);
        Check(followupUntil > parry.ActiveUntilTick, "반격 구간이 비어 있습니다");

        // 유효 시간이 끝나기 직전까지는 방어이고, 끝나는 틱부터 반격이다.
        while (match.Tick < parry.ActiveUntilTick - 1)
        {
            match.Step();
        }
        Equal(SnapshotPhase(match, player), "active");
        match.Step();
        Equal(SnapshotPhase(match, player), "followup");

        // 반격의 마지막 틱까지 반격이고, 보낸 followupUntil의 틱에서 회복으로 바뀐다.
        while (match.Tick < followupUntil - 1)
        {
            match.Step();
        }
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(match, player, RoomHost.Json)))
        {
            JsonElement action = json.RootElement.GetProperty("players")[0].GetProperty("actions")[0];
            Equal(action.GetProperty("phase").GetString(), "followup");
            double until = action.GetProperty("followupUntil").GetDouble();
            CheckTickTime(until);
            Equal((long)Math.Round(until * Rules.TicksPerSecond), followupUntil);
        }
        match.Step();
        Equal(SnapshotPhase(match, player), "recovery");
    }

    private static void ExtractionProgressWire()
    {
        // 탈출 진행도는 반 틱 단위로 센 값을 초로 바꿔 소수 둘째 자리로 반올림해 보낸다.
        // 반 틱 간격(1/60초)이 반올림 오차(최대 0.005초)의 두 배보다 크므로, 받은 값에 60을 곱해 반올림하면 반 틱 수가 그대로 돌아온다.
        Fixture f = new Fixture();
        for (int i = 0; i < f.Match.Facilities.Count; i++)
        {
            f.Match.Facilities[i].Hp = 0;
        }
        // 구역 안에서는 한 틱에 반 틱 2개씩 찬다. 탈출이 끝나기 직전까지 채워 보낼 수 있는 값을 거의 다 본다.
        f.Player.Position = f.Match.Layout.Extraction;
        long halfTicks = 0;
        long inside = Rules.ToTicks(f.Match.Rules.ExtractionSeconds) - 10;
        for (long i = 0; i < inside; i++)
        {
            f.Match.Step();
            halfTicks += 2;
            Equal(SentHalfTicks(f.Match, f.Player), halfTicks);
        }
        // 구역을 비우면 한 틱에 반 틱 1개씩 줄어 홀수도 나온다. 홀수 반 틱은 30을 곱해도 정수가 되지 않는다.
        f.Player.Position = f.Match.Layout.Camp;
        for (int i = 0; i < 25; i++)
        {
            f.Match.Step();
            halfTicks -= 1;
            Equal(SentHalfTicks(f.Match, f.Player), halfTicks);
        }
        // 탈출이 끝나기 전에 멈췄으므로 작전은 아직 진행 중이다.
        Equal(f.Match.Phase, "active");
    }

    // 스냅샷의 탈출 진행도(초)에 60을 곱해 반올림한 반 틱 수다.
    private static long SentHalfTicks(Match match, Player player)
    {
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(match, player, RoomHost.Json)))
        {
            double progress = json.RootElement.GetProperty("extraction").GetProperty("progress").GetDouble();
            return (long)Math.Round(progress * 2 * Rules.TicksPerSecond);
        }
    }

    private static void CoordinateWireRounding()
    {
        // 좌표는 4096분의 1 단위라 0.125(Raw 512)나 0.625(Raw 2560)처럼 소수 셋째 자리가 딱 5인 값이 있다.
        // 실수의 Math.Round는 이런 값을 짝수 쪽(0.12, 0.62)으로 보내지만, 보낼 때는 Fixed처럼 0에서 먼 쪽(0.13, 0.63)으로 반올림한다.
        Fixture f = new Fixture();
        Fixed x = Fixed.FromRaw(300 * Fixed.Scale + 512);
        Fixed y = Fixed.FromRaw(730 * Fixed.Scale + 2560);
        f.Player.Position = new FixedVector(x, y);
        Enemy enemy = Target(f);
        Enemy breaker = Target(f, EnemyDefinition.Breaker);
        Enemy warden = Target(f, EnemyDefinition.Warden);
        byte[] bytes = Snapshot.Create(f.Match, f.Player, RoomHost.Json);
        using (JsonDocument json = JsonDocument.Parse(bytes))
        {
            JsonElement root = json.RootElement;
            JsonElement player = root.GetProperty("players")[0];
            Equal(player.GetProperty("x").GetDouble(), 300.13);
            Equal(player.GetProperty("y").GetDouble(), 730.63);
            // 클라이언트 예측은 반올림하지 않은 Raw에서 출발해야 서버와 같은 계산이 된다.
            Equal(player.GetProperty("rawX").GetInt64(), x.Raw);
            Equal(player.GetProperty("rawY").GetInt64(), y.Raw);

            // 시설·보급·탈출 지점·캠프의 좌표와 몸 반지름도 같은 도우미를 거친다. 지금은 모두 정수라 값이 그대로다.
            JsonElement facility = root.GetProperty("facilities")[0];
            Equal(facility.GetProperty("x").GetDouble(), f.Match.Facilities[0].Position.X.ToDouble());
            Equal(facility.GetProperty("y").GetDouble(), f.Match.Facilities[0].Position.Y.ToDouble());
            JsonElement supply = root.GetProperty("supplies")[0];
            Equal(supply.GetProperty("x").GetDouble(), f.Match.Supplies[0].Position.X.ToDouble());
            Equal(supply.GetProperty("y").GetDouble(), f.Match.Supplies[0].Position.Y.ToDouble());
            JsonElement extraction = root.GetProperty("extraction");
            Equal(extraction.GetProperty("x").GetDouble(), f.Match.Layout.Extraction.X.ToDouble());
            Equal(extraction.GetProperty("y").GetDouble(), f.Match.Layout.Extraction.Y.ToDouble());
            JsonElement map = root.GetProperty("map");
            Equal(map.GetProperty("campX").GetDouble(), f.Match.Layout.Camp.X.ToDouble());
            Equal(map.GetProperty("campY").GetDouble(), f.Match.Layout.Camp.Y.ToDouble());
            JsonElement rules = root.GetProperty("rules");
            Equal(rules.GetProperty("playerRadius").GetDouble(), Rules.PlayerRadius.ToDouble());
            JsonElement seen = root.GetProperty("enemies")[0];
            Equal(seen.GetProperty("radius").GetDouble(), enemy.Definition.Radius.ToDouble());
            Equal(seen.GetProperty("rawX").GetInt64(), enemy.Position.X.Raw);
            Equal(seen.GetProperty("rawY").GetInt64(), enemy.Position.Y.Raw);

            // 클라이언트는 받은 반지름을 FromDouble로 다시 Raw로 바꿔 예측에 쓴다. 지금 반지름은 모두 정수라 소수 둘째 자리로 보내도
            // 그대로 돌아온다. 그 자리에서 그대로 돌아오지 않는 반지름을 쓰게 되면 이 검사가 실패하므로, 그때는 반지름의 Raw도 함께 보내야 한다.
            Equal(Fixed.FromDouble(rules.GetProperty("playerRadius").GetDouble()), Rules.PlayerRadius);
            Enemy[] kinds = new Enemy[] { enemy, breaker, warden };
            for (int i = 0; i < kinds.Length; i++)
            {
                JsonElement sent = Find(root.GetProperty("enemies"), "kind", kinds[i].Definition.Id);
                Equal(Fixed.FromDouble(sent.GetProperty("radius").GetDouble()), kinds[i].Definition.Radius);
            }
        }
    }

    // 대원의 첫 기술이 스냅샷에서 어느 단계로 나가는지 본다.
    private static string? SnapshotPhase(Match match, Player player)
    {
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(match, player, RoomHost.Json)))
        {
            JsonElement action = json.RootElement.GetProperty("players")[0].GetProperty("actions")[0];
            return action.GetProperty("phase").GetString();
        }
    }

    // 보내는 시각은 틱 시각을 초로 바꿔 소수 둘째 자리로 반올림한 값이어야 한다.
    private static void CheckTickTime(double value)
    {
        Check(Math.Abs(value * 100 - Math.Round(value * 100)) < 0.000001, $"{value}가 소수 둘째 자리로 반올림되지 않았습니다");
        // 반올림 오차는 0.005초(0.15틱) 이하라 틱 번호에서 크게 벗어나지 않는다.
        double ticks = value * Rules.TicksPerSecond;
        Check(Math.Abs(ticks - Math.Round(ticks)) < 0.2, $"{value}가 틱 시각이 아닙니다");
    }

    // 배열에서 name 속성이 prefix로 시작하는 첫 항목을 찾는다. 생성 순서에 기대지 않으려고 내용으로 찾는다.
    private static JsonElement Find(JsonElement array, string name, string prefix)
    {
        foreach (JsonElement item in array.EnumerateArray())
        {
            string? value = item.GetProperty(name).GetString();
            if (value != null && value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return item;
            }
        }
        throw new Exception($"{name}이 {prefix}로 시작하는 항목이 없습니다");
    }

    // JSON 객체의 속성 이름을 순서대로 쉼표로 이어 돌려준다.
    private static string PropertyNames(JsonElement element)
    {
        List<string> names = new List<string>();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            names.Add(property.Name);
        }
        return string.Join(",", names);
    }

    private static void NoticeIds()
    {
        // 같은 이름의 두 대원이 같은 틱에 참가하면 문구와 시각이 같은 안내가 두 번 나온다. 순번으로 둘을 구분한다.
        Match match = new Match("NOTE01", new Rules { MaxEnemies = 0 }, 1);
        Player first = match.Join("a", "pilot")!;
        Check(match.Join("b", "pilot") != null, "같은 이름의 두 번째 대원이 참가하지 못했습니다");
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(match, first, RoomHost.Json)))
        {
            JsonElement notices = json.RootElement.GetProperty("notices");
            Equal(notices.GetArrayLength(), 2);
            JsonElement one = notices[0];
            JsonElement two = notices[1];
            Equal(two.GetProperty("text").GetString(), one.GetProperty("text").GetString());
            Equal(two.GetProperty("at").GetDouble(), one.GetProperty("at").GetDouble());
            Check(two.GetProperty("id").GetInt32() > one.GetProperty("id").GetInt32(), "같은 안내 둘의 순번이 늘지 않았습니다");
        }
        // 오래된 안내가 목록에서 빠져도 순번은 계속 늘어난다. 목록에는 최근 여섯 개만 남는다.
        for (int i = 0; i < 10; i++)
        {
            match.Note("시험 안내 " + i);
        }
        Equal(match.Notices.Count, 6);
        Equal(match.Notices[0].Id, 7);
        Equal(match.Notices[5].Id, 12);
    }

    private static void SharedSnapshot()
    {
        Fixture f = new Fixture();
        Player ally = Ally(f, "scout");
        // 한 대원만 보급 대기 중이게 해서 수신자마다 값이 달라지게 한다.
        f.Match.Supplies[0].ReadyTicks[f.Player.Id] = f.Match.Tick + Rules.ToTicks(10);
        byte[] body = Snapshot.SerializeShared(Snapshot.CreateShared(f.Match), RoomHost.Json);
        JsonObject shared = JsonNode.Parse(body)!.AsObject();
        // 공통 몸통에는 수신자별 값이 들어가지 않는다. SnapshotMessage에 그런 속성이 없다.
        string[] personalKeys = new string[] { "you", "supplies", "walls", "map", "rules" };
        for (int i = 0; i < personalKeys.Length; i++)
        {
            Check(!shared.ContainsKey(personalKeys[i]), $"공통 부분에 수신자별 값 {personalKeys[i]}이 섞였습니다");
        }

        // 수신자마다 공통 몸통에 자기 값만 이어 붙인다. 조종사는 벽·규칙·맵을 아직 받지 못했고 정찰병은 받았다.
        byte[] pilotBytes = Snapshot.Combine(body, Snapshot.CreatePersonal(f.Match, f.Player, true, true), RoomHost.Json);
        byte[] scoutBytes = Snapshot.Combine(body, Snapshot.CreatePersonal(f.Match, ally, false, false), RoomHost.Json);
        JsonObject pilot = JsonNode.Parse(pilotBytes)!.AsObject();
        JsonObject scout = JsonNode.Parse(scoutBytes)!.AsObject();
        Equal((string)pilot["you"]!, f.Player.Id);
        Equal((string)scout["you"]!, ally.Id);
        double pilotCooldown = (double)pilot["supplies"]![0]!["cooldown"]!;
        double scoutCooldown = (double)scout["supplies"]![0]!["cooldown"]!;
        Check(pilotCooldown > 9, "조종사의 보급 대기 시간이 빠졌습니다");
        Near(scoutCooldown, 0);
        Check(pilot.ContainsKey("walls") && pilot.ContainsKey("map") && pilot.ContainsKey("rules"), "조종사에게 벽·규칙·맵이 빠졌습니다");
        Check(!scout.ContainsKey("walls") && !scout.ContainsKey("map") && !scout.ContainsKey("rules"), "정찰병에게 이미 받은 값을 다시 보냈습니다");
        // 수신자별 값을 빼면 두 스냅샷 모두 공통 몸통과 같아야 한다.
        for (int i = 0; i < personalKeys.Length; i++)
        {
            pilot.Remove(personalKeys[i]);
            scout.Remove(personalKeys[i]);
        }
        Check(JsonNode.DeepEquals(pilot, shared), "조종사의 스냅샷에서 공통 부분이 달라졌습니다");
        Check(JsonNode.DeepEquals(scout, shared), "정찰병의 스냅샷에서 공통 부분이 달라졌습니다");

        // 연결 직후 한 명에게 보내는 스냅샷도 같은 방식으로 만들고 벽·규칙·맵을 모두 담는다.
        JsonObject alone = JsonNode.Parse(Snapshot.Create(f.Match, ally, RoomHost.Json))!.AsObject();
        Equal((string)alone["you"]!, ally.Id);
        Check(alone.ContainsKey("walls") && alone.ContainsKey("map") && alone.ContainsKey("rules"), "연결 직후 스냅샷에 벽·규칙·맵이 빠졌습니다");

        // 시야 다각형은 그리기 전용이라 정수로 보낸다. 실제 전송되는 JSON에서 확인한다.
        using JsonDocument json = JsonDocument.Parse(scoutBytes);
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
        using RoomHost host = NewHost();
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
        using RoomHost host = NewHost();
        Guest guest = host.Create(new EntryRequest("pilot"))!;
        (Room room, Player player, Peer peer) = host.Attach(guest.Room, guest.Token)!.Value;
        // 16분 동안 접속해 플레이한 상황이다. 마지막 참가·접속 시각이 16분 전이다.
        room.LastVisit = DateTime.UtcNow - TimeSpan.FromMinutes(16);
        Check(!room.Expired(DateTime.UtcNow), "접속 중인 방이 삭제 대상입니다");
        host.Detach(room, player, peer);
        // 대원이 남아 있으면 연결이 끊겨도 바로 지우지 않는다. 끊기자마자 지우면 120초 재접속 유예가 의미가 없다.
        // 실제로는 유예가 끝나 대원이 빠지면 2초 규칙으로 지워지고, 15분은 대원이 빠지지 않을 때의 안전장치다.
        Check(!room.Expired(DateTime.UtcNow), "연결이 끊기자마자 방이 삭제 대상이 됩니다");
        Check(room.Expired(DateTime.UtcNow + TimeSpan.FromMinutes(16)));
    }

    private static void EmptyRoomExpiry()
    {
        // HTTP로 방을 만들고 참가만 한 뒤 한 번도 접속하지 않은 방이다.
        Room squatted = new Room(new Match("SQUAT1", new Rules(), 1), "local");
        Player squatter = squatted.Match.Join("q", "squatter")!;
        squatter.DisconnectedTick = squatted.Match.Tick;
        squatter.AwaitingFirstConnection = true;
        Check(!squatted.Expired(DateTime.UtcNow), "막 만든 방이 바로 삭제 대상입니다");
        Advance(squatted.Match, squatted.Match.Rules.FirstConnectSeconds + 0.1);
        Equal(squatted.Match.Players.Count, 0);
        // 접속 없이 참가만 한 자리가 풀린 방은 바로 지운다. 만든 지 15초 남짓 지난 때다.
        // 오래 남기면 분당 30번 만들 수 있는 방으로 32개 상한을 채울 수 있다.
        Check(squatted.Expired(DateTime.UtcNow + TimeSpan.FromSeconds(16)), "접속 없이 자리가 풀린 방이 남아 방 생성을 막습니다");
        // 방을 만들고 첫 대원을 넣기 전의 짧은 틈에는 지우지 않는다.
        Room fresh = new Room(new Match("FRESH1", new Rules(), 2), "local");
        Check(!fresh.Expired(DateTime.UtcNow), "막 만든 빈 방이 첫 대원을 넣기 전에 지워집니다");

        // 연결이 끊겼지만 복구 유예 중인 대원이 있는 방은 그대로 남는다.
        using RoomHost host = NewHost();
        Guest guest = host.Create(new EntryRequest("pilot"))!;
        (Room room, Player player, Peer peer) = host.Attach(guest.Room, guest.Token)!.Value;
        host.Detach(room, player, peer);
        Check(!room.Expired(DateTime.UtcNow + TimeSpan.FromSeconds(31)), "복구를 기다리는 대원이 있는 방이 지워집니다");
    }

    private static void RoomsPerAddress()
    {
        TestSeeds seeds = new TestSeeds();
        using RoomHost host = NewHost(null, false, seeds.Next);
        // 한 주소에서 만든 방은 동시에 상한만큼만 둔다. 방을 만들고 접속을 붙잡아 두는 방법으로
        // 몇 개 주소만으로 방 32개 상한을 채우지 못하게 한다.
        for (int i = 0; i < RoomHost.MaxRoomsPerAddress; i++)
        {
            EntryFailure failure;
            Guest? guest = host.Create(new EntryRequest("a"), "203.0.113.1", out failure);
            Check(guest != null && failure == EntryFailure.None, "상한보다 적은 방을 만들지 못했습니다");
        }
        EntryFailure capped;
        Guest? extra = host.Create(new EntryRequest("a"), "203.0.113.1", out capped);
        Check(extra == null, "한 주소에서 상한보다 많은 방을 만들었습니다");
        Equal(capped, EntryFailure.AddressFull);
        // 상한에 걸린 요청은 맵을 만들기 전에 거절한다. 맵을 만들 때 쓰는 시드를 받지 않았다.
        Equal(seeds.Issued, RoomHost.MaxRoomsPerAddress);
        // 다른 주소는 따로 센다.
        EntryFailure otherFailure;
        Guest? other = host.Create(new EntryRequest("b"), "203.0.113.2", out otherFailure);
        Check(other != null && otherFailure == EntryFailure.None, "다른 주소의 방 생성이 막혔습니다");
        // 상한에서 뺀 주소(같은 기계)는 상한보다 많이 만들 수 있다.
        for (int i = 0; i <= RoomHost.MaxRoomsPerAddress; i++)
        {
            EntryFailure local;
            Guest? guest = host.Create(new EntryRequest("c"), null, out local);
            Check(guest != null && local == EntryFailure.None, "상한에서 뺀 주소의 방 생성이 막혔습니다");
        }
    }

    private static void ServerFull()
    {
        TestSeeds seeds = new TestSeeds();
        using RoomHost host = NewHost(null, false, seeds.Next);
        // 주소별 상한에서 뺀 접속(주소 null)으로 서버의 방을 상한까지 채운다.
        for (int i = 0; i < RoomHost.MaxRooms; i++)
        {
            Check(host.Create(new EntryRequest("pilot")) != null, $"{i + 1}번째 방을 만들지 못했습니다");
        }
        EntryFailure failure;
        Check(host.Create(new EntryRequest("late"), null, out failure) == null, "서버의 방 상한보다 많은 방을 만들었습니다");
        Equal(failure, EntryFailure.ServerFull);
        // 거절한 요청은 맵을 만들기 전에 걸렀다. 맵을 만들 때 쓰는 시드를 받지 않았다.
        Equal(seeds.Issued, RoomHost.MaxRooms);
    }

    // 시드를 내주면서 같은 RoomHost로 방을 한 번 더 만든다. RoomHost는 자리를 예약한 뒤 맵을 만들기 직전에 시드를 받으므로,
    // 그 안에서 부른 방 만들기는 맵을 만들고 있는 요청과 동시에 들어온 요청과 같다.
    private sealed class NestedCreate
    {
        private readonly TestSeeds _seeds = new TestSeeds();
        // 이 값이 있으면 다음에 시드를 내줄 때 한 번 끼어들어 방을 만든다.
        public RoomHost? Host { get; set; }
        public string? Address { get; set; }
        public Guest? Nested { get; private set; }
        public EntryFailure NestedFailure { get; private set; }

        public int Issued
        {
            get
            {
                return _seeds.Issued;
            }
        }

        public byte[] Next()
        {
            RoomHost? host = Host;
            if (host != null)
            {
                // 끼어든 요청이 시드를 받을 때 다시 끼어들지 않도록 먼저 비운다.
                Host = null;
                EntryFailure failure;
                Nested = host.Create(new EntryRequest("nested"), Address, out failure);
                NestedFailure = failure;
            }
            return _seeds.Next();
        }
    }

    private static void PendingRooms()
    {
        NestedCreate seeds = new NestedCreate();
        using RoomHost host = NewHost(null, false, seeds.Next);
        string address = "203.0.113.7";
        // 한 주소가 상한보다 방을 하나 덜 가진 채 마지막 방의 맵을 만드는 동안, 같은 주소에서 요청이 하나 더 들어온다.
        for (int i = 0; i < RoomHost.MaxRoomsPerAddress - 1; i++)
        {
            EntryFailure made;
            Check(host.Create(new EntryRequest("a"), address, out made) != null, "상한보다 적은 방을 만들지 못했습니다");
        }
        seeds.Host = host;
        seeds.Address = address;
        EntryFailure last;
        Check(host.Create(new EntryRequest("a"), address, out last) != null, "주소의 마지막 자리에 방을 만들지 못했습니다");
        // 맵을 만들고 있는 방이 예약한 자리까지 세므로, 끼어든 요청은 맵을 만들기 전에 거절된다. 방은 상한만큼만 생긴다.
        Check(seeds.Nested == null, "맵을 만들고 있는 방을 세지 않아 주소의 방이 상한을 넘었습니다");
        Equal(seeds.NestedFailure, EntryFailure.AddressFull);
        Equal(seeds.Issued, RoomHost.MaxRoomsPerAddress);

        // 서버 전체의 상한도 같다. 주소별 상한에서 뺀 접속으로 방을 하나 덜 채운 뒤, 마지막 방을 만드는 동안 하나 더 들어온다.
        for (int i = RoomHost.MaxRoomsPerAddress; i < RoomHost.MaxRooms - 1; i++)
        {
            Check(host.Create(new EntryRequest("b")) != null, $"{i + 1}번째 방을 만들지 못했습니다");
        }
        seeds.Host = host;
        seeds.Address = null;
        Check(host.Create(new EntryRequest("b")) != null, "서버의 마지막 자리에 방을 만들지 못했습니다");
        Check(seeds.Nested == null, "맵을 만들고 있는 방을 세지 않아 서버의 방이 상한을 넘었습니다");
        Equal(seeds.NestedFailure, EntryFailure.ServerFull);
        Equal(seeds.Issued, RoomHost.MaxRooms);
    }

    // 시드를 내줄 때 한 번 예외를 낸다. 맵이나 Match를 만들다 실패한 경우와 같다.
    private sealed class FailingSeeds
    {
        private readonly TestSeeds _seeds = new TestSeeds();
        public bool FailNext { get; set; }

        public byte[] Next()
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("검사가 일부러 낸 생성 실패");
            }
            return _seeds.Next();
        }
    }

    private static void FailedCreation()
    {
        FailingSeeds seeds = new FailingSeeds();
        using RoomHost host = NewHost(null, false, seeds.Next);
        string address = "203.0.113.9";
        // 맵을 만들다 예외가 난 요청은 방을 등록하지 못한다. 예약한 자리를 돌려놓지 않으면 상한이 영영 하나씩 줄어든다.
        seeds.FailNext = true;
        bool failed = false;
        try
        {
            EntryFailure ignored;
            host.Create(new EntryRequest("a"), address, out ignored);
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }
        Check(failed, "일부러 낸 생성 실패가 나오지 않았습니다");
        // 주소의 예약을 돌려놓았으므로 이 주소는 상한만큼 방을 만들 수 있다.
        for (int i = 0; i < RoomHost.MaxRoomsPerAddress; i++)
        {
            EntryFailure made;
            Check(host.Create(new EntryRequest("a"), address, out made) != null, $"실패한 요청의 예약이 남아 이 주소의 {i + 1}번째 방을 만들지 못했습니다");
        }
        // 서버 전체의 예약도 돌려놓았으므로 남은 자리를 모두 채울 수 있고, 그다음에야 가득 찬다.
        for (int i = RoomHost.MaxRoomsPerAddress; i < RoomHost.MaxRooms; i++)
        {
            Check(host.Create(new EntryRequest("b")) != null, $"실패한 요청의 예약이 남아 {i + 1}번째 방을 만들지 못했습니다");
        }
        EntryFailure full;
        Check(host.Create(new EntryRequest("b"), null, out full) == null, "서버의 방 상한보다 많은 방을 만들었습니다");
        Equal(full, EntryFailure.ServerFull);
    }

    private static void RoomLayouts()
    {
        // 운영 방은 방의 시드로 만든 생성 작전이고, 엘리트가 나오고 적의 준비 동작은 기본 길이다.
        using RoomHost host = NewHost();
        Guest guest = host.Create(new EntryRequest("pilot"))!;
        (Room room, Player player, Peer peer) = host.Attach(guest.Room, guest.Token)!.Value;
        Check(room.Match.Layout.Columns >= MissionGenerator.MinimumColumns && room.Match.Layout.Columns <= MissionGenerator.MaximumColumns,
            $"생성 맵이 아닙니다({room.Match.Layout.Columns}칸)");
        Check(room.Match.Rules.AllowEliteEnemies, "생성 작전에 엘리트가 나오지 않습니다");
        Equal(room.Match.Rules.EnemyWindupMultiplier, 1.0);
        // 맵은 방의 시드로 만든다. 검사의 RoomHost는 첫 방에 정수 1로 만든 시드를 넘긴다(TestTools.NewHost).
        MissionLayout expected = MissionGenerator.Generate(SeededRandom.SeedFromNumber(1));
        string made = JsonSerializer.Serialize(LayoutRecord.From(room.Match.Layout), RoomHost.Json);
        Check(made == JsonSerializer.Serialize(LayoutRecord.From(expected), RoomHost.Json), "방의 맵이 넘긴 시드로 만든 맵이 아닙니다");
        host.Detach(room, player, peer);

        // 손으로 만든 맵은 서버 설정이 허락하지 않으면 고를 수 없다. 모르는 맵 이름도 받지 않는다.
        EntryFailure failure;
        Check(host.Create(new EntryRequest("pilot", null, EntryRequest.HandmadeLayout), null, out failure) == null);
        Equal(failure, EntryFailure.LayoutUnavailable);
        using RoomHost allowing = NewHost(null, true);
        Check(allowing.Create(new EntryRequest("pilot", null, "dungeon"), null, out failure) == null);
        Equal(failure, EntryFailure.LayoutUnavailable);

        // 허락하면 손으로 만든 맵에 그 맵의 검사가 맞춰 둔 기본 규칙(병사만, 준비 동작 두 배)이다.
        Guest handmade = allowing.Create(new EntryRequest("pilot", null, EntryRequest.HandmadeLayout))!;
        (Room fixedRoom, Player fixedPlayer, Peer fixedPeer) = allowing.Attach(handmade.Room, handmade.Token)!.Value;
        MissionLayout reference = MissionLayout.CreateHandmade();
        Equal(fixedRoom.Match.Layout.Columns, reference.Columns);
        Equal(fixedRoom.Match.Layout.Camp, reference.Camp);
        Check(!fixedRoom.Match.Rules.AllowEliteEnemies, "손으로 만든 맵의 방에 엘리트가 나옵니다");
        Equal(fixedRoom.Match.Rules.EnemyWindupMultiplier, 2.0);
        allowing.Detach(fixedRoom, fixedPlayer, fixedPeer);
    }

    private static void EntryFailures()
    {
        // 장비 검증과 실패 이유는 RoomHost가 정한다. HTTP 처리는 이 값으로 상태 코드만 고른다.
        using RoomHost host = NewHost();
        string[] invalid = new string[] { "strike", "parry", "grab", "turret" };
        EntryFailure badCreate;
        Check(host.Create(new EntryRequest("bad", invalid), null, out badCreate) == null);
        Equal(badCreate, EntryFailure.InvalidRequest);
        Guest guest = host.Create(new EntryRequest("owner"))!;
        EntryFailure badJoin;
        Check(host.Join(guest.Room, new EntryRequest("bad", invalid), out badJoin) == null);
        Equal(badJoin, EntryFailure.InvalidRequest);
        EntryFailure missing;
        Check(host.Join("NOPE23", new EntryRequest("late"), out missing) == null);
        Equal(missing, EntryFailure.Unavailable);
        EntryFailure joined;
        Check(host.Join(guest.Room, new EntryRequest("friend"), out joined) != null);
        Equal(joined, EntryFailure.None);
    }

    private static void AddressGrouping()
    {
        // IPv6 사용자는 보통 /64 대역을 통째로 받아 요청마다 주소를 바꿀 수 있다. 두 제한 모두 /64를 한 주소로 센다.
        IPAddress first = IPAddress.Parse("2001:db8:1:2::1");
        IPAddress sameBlock = IPAddress.Parse("2001:db8:1:2:ffff:abcd:1:5");
        IPAddress otherBlock = IPAddress.Parse("2001:db8:1:3::1");
        Equal(AddressKeys.ForRoomLimit(first), AddressKeys.ForRoomLimit(sameBlock));
        Check(AddressKeys.ForRoomLimit(first) != AddressKeys.ForRoomLimit(otherBlock), "다른 /64 대역을 한 주소로 셌습니다");
        Equal(AddressKeys.ForRateLimit(first), AddressKeys.ForRateLimit(sameBlock));
        Check(AddressKeys.ForRateLimit(first) != AddressKeys.ForRateLimit(otherBlock), "참가 제한이 다른 /64 대역을 한 주소로 셌습니다");

        // IPv4는 주소 하나가 단위다. IPv6에 담긴 IPv4 주소도 같은 주소로 센다.
        Equal(AddressKeys.ForRoomLimit(IPAddress.Parse("203.0.113.5")), "203.0.113.5");
        Equal(AddressKeys.ForRoomLimit(IPAddress.Parse("::ffff:203.0.113.5")), "203.0.113.5");
        Equal(AddressKeys.ForRateLimit(IPAddress.Parse("::ffff:203.0.113.5")), "203.0.113.5");

        // 같은 기계와 사설망에서 온 접속은 방 수 상한에서 뺀다. Docker로 띄우면 모든 요청이 브리지 주소(172.17.0.1 등)로 온다.
        string[] local = new string[] { "127.0.0.1", "::1", "172.17.0.1", "172.31.255.1", "10.0.0.3", "192.168.1.10", "fd00::1", "fe80::1", "169.254.1.1", "::ffff:192.168.0.2" };
        for (int i = 0; i < local.Length; i++)
        {
            Check(AddressKeys.ForRoomLimit(IPAddress.Parse(local[i])) == null, $"{local[i]}을 방 수 상한에 넣었습니다");
        }
        // 사설 대역 경계 밖은 공개 주소로 센다.
        Check(AddressKeys.ForRoomLimit(IPAddress.Parse("172.32.0.1")) != null, "172.32.0.1을 사설망으로 보았습니다");
        Check(AddressKeys.ForRoomLimit(IPAddress.Parse("172.15.255.1")) != null, "172.15.255.1을 사설망으로 보았습니다");
        // 참가 제한은 모든 주소에 건다. 주소를 모르면 하나로 묶는다.
        Equal(AddressKeys.ForRateLimit(IPAddress.Parse("172.17.0.1")), "172.17.0.1");
        Equal(AddressKeys.ForRateLimit(null), "local");
    }

    private static void HiddenIdGaps()
    {
        Rules rules = new Rules();
        Match match = new Match("IDGAP1", rules, 73);
        // 처음 배치된 적이 다가와 행동하면 그 id가 끼어들므로 치우고 추가 생성만 본다.
        match.Enemies.Clear();
        Player player = match.Join("p", "pilot")!;
        player.Connected = true;
        Check(match.Deploy(player, match.Layout.Camp.X.ToDouble(), match.Layout.Camp.Y.ToDouble()));
        Advance(match, rules.DeploySeconds + 0.01);
        player.Mana = player.MaxMana;
        long sequence = 1;
        match.Input(player, sequence, 0, 0, 0, 1);
        Advance(match, Rules.Step);
        int first = player.Actions[player.Actions.Count - 1].Id;
        sequence++;
        match.Input(player, sequence, 0, 0, 0, 0);
        // 안개 속에서 적이 생성될 때까지 진행한다. 생성된 적은 1초 동안 행동하지 않는다.
        double limit = match.Now + 30;
        while (match.Enemies.Count == 0 && match.Now < limit)
        {
            Advance(match, Rules.Step);
        }
        Check(match.Enemies.Count > 0, "적이 생성되지 않았습니다");
        sequence++;
        match.Input(player, sequence, 0, 0, 0, 1);
        Advance(match, Rules.Step);
        int second = player.Actions[player.Actions.Count - 1].Id;
        // 그 사이 보이는 곳에서는 아무 일도 없었다. 숨은 생성이 순번을 쓰면 빈 번호로 생성 수가 드러난다.
        Equal(second, first + 1);
    }

    private static OutgoingMessage Next(Peer peer)
    {
        Check(peer.Outgoing.Reader.TryRead(out OutgoingMessage? message), "보낼 메시지가 없습니다");
        return message!;
    }

    private static bool Carries(OutgoingMessage message, string field)
    {
        using JsonDocument json = JsonDocument.Parse(message.Bytes);
        return json.RootElement.TryGetProperty(field, out JsonElement _);
    }

    private static void StaticParts()
    {
        using RoomHost host = NewHost();
        Guest guest = host.Create(new EntryRequest("pilot"))!;
        (Room room, Player player, Peer peer) = host.Attach(guest.Room, guest.Token)!.Value;
        // 접속 직후 스냅샷에는 벽·규칙·맵이 모두 있다.
        OutgoingMessage first = Next(peer);
        Check(Carries(first, "walls") && Carries(first, "rules") && Carries(first, "map"));
        // 아직 보내지 않았다. 느린 연결에서 버려질 수 있으므로 다음 방송에도 다시 담는다.
        lock (room.Gate)
        {
            RoomHost.PublishSnapshots(room);
        }
        OutgoingMessage resent = Next(peer);
        Check(Carries(resent, "walls") && Carries(resent, "rules") && Carries(resent, "map"), "보내기 전에 버려질 수 있는 정보를 다시 담지 않았습니다");
        // 실제로 보낸 뒤에는 바뀌지 않은 정보를 뺀다. 판 번호는 매번 보낸다.
        peer.MarkSent(resent);
        lock (room.Gate)
        {
            RoomHost.PublishSnapshots(room);
        }
        OutgoingMessage lean = Next(peer);
        Check(!Carries(lean, "walls") && !Carries(lean, "rules") && !Carries(lean, "map"), "보낸 정보를 또 보냅니다");
        Check(Carries(lean, "mapRevision"));
        peer.MarkSent(lean);
        // 벽이 부서져 판 번호가 바뀌면 벽만 다시 보낸다. 방마다 맵이 다르므로 부술 수 있는 벽(체력 3)을 하나 찾아 부순다.
        int breakable = Array.IndexOf(room.Match.Map.Tiles, (byte)1);
        Check(breakable >= 0, "부술 수 있는 벽이 없습니다");
        Check(room.Match.Map.Damage(breakable, 3));
        lock (room.Gate)
        {
            RoomHost.PublishSnapshots(room);
        }
        OutgoingMessage changed = Next(peer);
        Check(Carries(changed, "walls") && !Carries(changed, "rules"), "바뀐 벽을 다시 보내지 않았습니다");
    }

    private static void TokenCleanup()
    {
        using RoomHost host = NewHost();
        Guest owner = host.Create(new EntryRequest("owner"))!;
        (Room room, Player player, Peer peer) = host.Attach(owner.Room, owner.Token)!.Value;
        host.Join(owner.Room, new EntryRequest("squatter"));
        Equal(room.Tokens.Count, 2);
        // 접속하지 않은 자리가 풀린 뒤 방을 한 틱 진행하면 그 자리의 토큰도 정리된다.
        Advance(room.Match, room.Match.Rules.FirstConnectSeconds + 0.1);
        host.StepRoom(room.Match.Code, room, 1, false);
        Equal(room.Tokens.Count, room.Match.Players.Count);
        Equal(room.Tokens.Count, 1);
    }

    private static void FailingRoom()
    {
        using RoomHost host = NewHost();
        Guest guest = host.Create(new EntryRequest("pilot"))!;
        (Room room, Player player, Peer peer) = host.Attach(guest.Room, guest.Token)!.Value;
        Check(room.Match.Deploy(player, room.Match.Layout.Camp.X.ToDouble(), room.Match.Layout.Camp.Y.ToDouble()));
        Advance(room.Match, room.Match.Rules.DeploySeconds + 0.1);
        room.Match.Enemies.Clear();
        // 행동 목록이 빈 적이 대원 곁에 있다. 행동을 고를 때마다 예외가 나므로 매 틱 진행이 실패한다.
        EnemyDefinition broken = new EnemyDefinition("broken", "고장", Fixed.FromInt(13), 1, 0, new EnemySkill[0]);
        room.Match.Enemies.Add(new Enemy(30040, player.Position + FixedVector.FromInt(40, 0), broken));
        for (int tick = 0; tick < 60; tick++)
        {
            host.StepRoom(room.Match.Code, room, 1, false);
        }
        // 고칠 수 없는 방은 닫는다. 남겨 두면 32개 자리 중 하나를 차지한 채 오류만 쌓인다.
        Check(host.Join(guest.Room, new EntryRequest("late")) == null, "계속 실패하는 방이 남아 있습니다");
        // 연결도 닫아 클라이언트가 멈춘 방에 붙어 있지 않게 한다.
        Check(!peer.Outgoing.Writer.TryWrite(new OutgoingMessage(new byte[0], -1, false)), "닫힌 방의 연결이 열려 있습니다");
    }

    private static void CrowdedLanding()
    {
        Fixture f = new Fixture();
        Player late = f.Match.Join("late", "late")!;
        // 움직이지도 행동하지도 않는 적으로 투입 지점 주변 반경 150을 간격 30으로 채운다. 어느 점이든 적과 26보다 가깝다.
        EnemyDefinition still = new EnemyDefinition("still", "정지", Fixed.FromInt(13), 1, 0, EnemyDefinition.Grunt.Skills);
        FixedVector landing = FixedVector.FromInt(1400, 1250);
        int id = 31000;
        for (int x = -5; x <= 5; x++)
        {
            for (int y = -5; y <= 5; y++)
            {
                FixedVector at = landing + FixedVector.FromInt(x * 30, y * 30);
                if (Distance(at, landing) > 150 || !f.Match.Map.CanStand(at, Fixed.FromInt(13)))
                {
                    continue;
                }
                id++;
                Enemy blocker = new Enemy(id, at, still);
                blocker.NextActionTick = long.MaxValue;
                f.Match.Enemies.Add(blocker);
            }
        }
        // 캠프도 막는다.
        Enemy guard = new Enemy(id + 1, f.Match.Layout.Camp, still);
        guard.NextActionTick = long.MaxValue;
        f.Match.Enemies.Add(guard);
        Check(f.Match.Deploy(late, landing.X.ToDouble(), landing.Y.ToDouble()));
        Advance(f.Match, f.Match.Rules.DeploySeconds + 0.1);
        // 근처와 캠프가 모두 막혀도 더 넓게 찾아 투입한다. deploying에 묶여 "0.0초 후 투입"이 계속 표시되면 안 된다.
        Equal(late.State, "alive");
        Check(Distance(late.Position, landing) > 128, "막힌 범위 안에 투입되었습니다");
        for (int i = 0; i < f.Match.Enemies.Count; i++)
        {
            Check(Distance(late.Position, f.Match.Enemies[i].Position) >= 26 - 0.01, "적과 겹쳐 투입되었습니다");
        }
    }

    private static void ControlChannel()
    {
        Peer peer = new Peer(1);
        byte[] error = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"error\"}");
        Check(peer.Control.Writer.TryWrite(new OutgoingMessage(error, -1, false)));
        // 스냅샷 통로는 용량 2에서 오래된 것부터 버린다. 스냅샷이 몰려도 오류 안내는 따로 남아 있어야 한다.
        for (int i = 0; i < 3; i++)
        {
            peer.Outgoing.Writer.TryWrite(new OutgoingMessage(new byte[] { (byte)i }, -1, false));
        }
        Check(peer.Control.Reader.TryRead(out OutgoingMessage? kept), "오류 안내가 버려졌습니다");
        Equal(kept!.Bytes.Length, error.Length);
    }

    private static void PublishRate()
    {
        // 서버 타이머와 같은 주기로 깨어난다. PeriodicTimer는 주기를 밀리초 단위로 내리므로 1/30초가 33ms가 되어
        // 세 번이 0.1초에 조금 못 미친다. 깨어나는 시각은 예정보다 0~2ms 늦을 수 있다. 10초 동안 방송은 100번 안팎이어야 한다.
        double period = TickClock.TimerPeriod.TotalSeconds;
        int wakes = (int)(10 / period);
        Random random = new Random(7);
        PublishSchedule schedule = new PublishSchedule();
        int published = 0;
        double current = 0;
        for (int tick = 1; tick <= wakes; tick++)
        {
            current = tick * period + random.NextDouble() * 0.002;
            if (schedule.Due(current))
            {
                published++;
            }
        }
        Check(published >= 100 && published <= 101, $"10초 동안 방송이 {published}번이었습니다");

        // 서버가 5초 멈췄다 깨어나도 밀린 방송을 몰아서 보내지 않는다.
        double resume = current + 5;
        int burst = 0;
        for (int tick = 0; tick < 3; tick++)
        {
            if (schedule.Due(resume + tick * period))
            {
                burst++;
            }
        }
        Equal(burst, 1);
    }

    private static OutgoingMessage Text(string text)
    {
        return new OutgoingMessage(Encoding.UTF8.GetBytes(text), -1, false);
    }

    // 보내기 작업이 끝났는지 본다. 취소나 오류로 끝나도 끝난 것이다.
    private static bool Finishes(Task task)
    {
        try
        {
            return task.Wait(2000);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    private static void SendOrder()
    {
        // 스냅샷이 먼저 들어와 있어도 오류 안내를 먼저 보낸다.
        Peer peer = new Peer(1);
        RecordingSocket socket = new RecordingSocket();
        peer.Outgoing.Writer.TryWrite(Text("snapshot"));
        peer.Control.Writer.TryWrite(Text("error"));
        using CancellationTokenSource stop = new CancellationTokenSource();
        Task send = Task.Run(() => peer.Send(socket, stop.Token));
        socket.WaitSent(2);
        Equal(socket.Message(0), "error");
        Equal(socket.Message(1), "snapshot");
        // 보낼 것이 없어 기다리는 중에 들어온 pong도 곧바로 보낸다.
        peer.Control.Writer.TryWrite(Text("pong"));
        socket.WaitSent(3);
        Equal(socket.Message(2), "pong");
        // 스냅샷 통로가 닫히면(연결 교체·해제·방 닫힘) 보내기를 끝낸다.
        peer.Outgoing.Writer.TryComplete();
        Check(Finishes(send), "스냅샷 통로가 닫혔는데 보내기가 끝나지 않았습니다");
        Check(send.IsCompletedSuccessfully, "스냅샷 통로가 닫혔을 때 보내기가 오류로 끝났습니다");
    }

    private static void SendCancel()
    {
        // 이미 취소된 채로 기다림에 들어가도 곧바로 끝나야 한다. 취소된 기다림은 바로 끝나므로 다시 기다리면 헛돈다.
        Peer idle = new Peer(1);
        using CancellationTokenSource cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Task first = Task.Run(() => idle.Send(new RecordingSocket(), cancelled.Token));
        Check(Finishes(first), "취소된 보내기가 끝나지 않고 헛돌았습니다");
        Check(first.IsCanceled, "취소된 보내기가 취소로 끝나지 않았습니다");

        // 스냅샷을 보낸 뒤 기다리는 중에 취소해도 끝나야 한다. 연결을 끊을 때 이 순서로 취소한다.
        Peer busy = new Peer(2);
        RecordingSocket socket = new RecordingSocket();
        using CancellationTokenSource stop = new CancellationTokenSource();
        Task second = Task.Run(() => busy.Send(socket, stop.Token));
        busy.Outgoing.Writer.TryWrite(Text("snapshot"));
        socket.WaitSent(1);
        stop.Cancel();
        Check(Finishes(second), "기다리는 중에 취소된 보내기가 끝나지 않았습니다");
        Check(second.IsCanceled, "기다리는 중에 취소된 보내기가 취소로 끝나지 않았습니다");
    }

    private static void SendDrainsControl()
    {
        // 스냅샷 통로가 닫히는 순간 들어온 오류 안내도 보내고 끝나야 한다. 두 기다림이 함께 깨면 어느 쪽이 먼저인지
        // 정해져 있지 않으므로, 이어서 할 일을 쌓아 두는 문맥으로 순서를 정한다.
        // 스냅샷 통로 쪽 기다림이 먼저 깬 것을 확인한 뒤 오류 안내를 넣고, 그다음에 보내기 작업을 이어서 실행한다.
        Peer peer = new Peer(1);
        RecordingSocket socket = new RecordingSocket();
        ManualContext context = new ManualContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        Task send;
        try
        {
            // 보낼 것이 없으므로 두 통로를 기다리는 데까지 가서 멈춘다.
            send = peer.Send(socket, CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        peer.Outgoing.Writer.TryComplete();
        Check(context.WaitForWork(2000), "스냅샷 통로가 닫혔는데 보내기 작업이 깨지 않았습니다");
        peer.Control.Writer.TryWrite(Text("error"));
        context.RunPending();
        Check(send.IsCompleted, "스냅샷 통로가 닫혔는데 보내기가 끝나지 않았습니다");
        Equal(socket.Sent, 1);
        Equal(socket.Message(0), "error");
    }

    private static void SendWaits()
    {
        Peer peer = new Peer(1);
        RecordingSocket socket = new RecordingSocket();
        using CancellationTokenSource stop = new CancellationTokenSource();
        Task send = Task.Run(() => peer.Send(socket, stop.Token));
        // 처음 몇백 번은 지연 컴파일이나 작업 스레드 추가로 메모리가 한 번 늘 수 있으므로 측정에서 뺀다.
        PumpSnapshots(peer, socket, 300);
        long before = GC.GetTotalMemory(true);
        PumpSnapshots(peer, socket, 2000);
        long after = GC.GetTotalMemory(true);
        stop.Cancel();
        Finishes(send);
        long grown = after - before;
        // 스냅샷마다 제어 통로에 끝나지 않은 기다림이 하나씩 남으면 하나에 약 340 B, 2000번에 600 KB 넘게 늘어난다.
        Check(grown < 200000, $"제어 메시지 없이 스냅샷 2000개를 보내는 동안 메모리가 {grown} B 늘었습니다");
    }

    // 스냅샷을 하나씩 넣고, 보내기 작업이 보낸 뒤 다시 기다림에 들어갈 때까지 0.1 ms 쉰다.
    // 기다림에 들어가기 전에 다음 스냅샷을 넣으면 기다리지 않고 바로 읽으므로 검사할 상황이 생기지 않는다.
    private static void PumpSnapshots(Peer peer, RecordingSocket socket, int count)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("snapshot");
        for (int i = 0; i < count; i++)
        {
            int sent = socket.Sent;
            peer.Outgoing.Writer.TryWrite(new OutgoingMessage(bytes, -1, false));
            socket.WaitSent(sent + 1);
            long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10000;
            while (Stopwatch.GetTimestamp() < until)
            {
                Thread.SpinWait(20);
            }
        }
    }

    private static void BlockedDeployment()
    {
        Fixture f = new Fixture();
        Player late = f.Match.Join("late", "late")!;
        Enemy enemy = Target(f);
        enemy.Position = FixedVector.FromInt(1000, 600);
        Check(!f.Match.Visible(enemy.Position));
        Check(f.Match.Deploy(late, enemy.Position.X.ToDouble(), enemy.Position.Y.ToDouble()));
        Check(!f.Match.Visible(enemy.Position));
        Advance(f.Match, 5.1);
        // 대기로 되돌리지 않고 가까운 빈 자리로 투입한다. 투입 성공 여부로 안개 속을 엿볼 수 없다.
        Equal(late.State, "alive");
        Check(late.HasDeployed);
        double gap = Distance(late.Position, enemy.Position);
        Check(gap >= (Rules.PlayerRadius + enemy.Definition.Radius).ToDouble() - 0.01, "적과 겹쳐 투입되었습니다");
        Check(gap < 80, "투입 지점에서 너무 멀리 옮겨졌습니다");
        Check(f.Match.Map.LineOfSight(late.Position, enemy.Position), "원래 투입 지점이 보이지 않는 곳으로 옮겨졌습니다");
        // 투입된 뒤에는 자기 시야로 적을 정상적으로 본다.
        Check(f.Match.Visible(enemy.Position));
    }
}

// 이어서 할 일(await 뒤의 코드)을 바로 실행하지 않고 쌓아 두는 동기화 문맥이다.
// 검사가 원하는 때에 실행해, 스레드 순서에 달린 경합을 정해진 순서로 재현한다.
internal sealed class ManualContext : SynchronizationContext
{
    private readonly Queue<KeyValuePair<SendOrPostCallback, object?>> _work = new Queue<KeyValuePair<SendOrPostCallback, object?>>();
    private readonly object _gate = new object();

    public override void Post(SendOrPostCallback callback, object? state)
    {
        lock (_gate)
        {
            _work.Enqueue(new KeyValuePair<SendOrPostCallback, object?>(callback, state));
            Monitor.PulseAll(_gate);
        }
    }

    // 쌓인 일이 생길 때까지 기다린다.
    public bool WaitForWork(int milliseconds)
    {
        lock (_gate)
        {
            long deadline = Environment.TickCount64 + milliseconds;
            while (_work.Count == 0)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    return false;
                }
                Monitor.Wait(_gate, (int)left);
            }
            return true;
        }
    }

    // 쌓인 일을 이 스레드에서 차례로 실행한다. 실행하는 동안 이 문맥을 현재 문맥으로 둔다.
    public void RunPending()
    {
        SynchronizationContext? previous = Current;
        SetSynchronizationContext(this);
        try
        {
            while (true)
            {
                KeyValuePair<SendOrPostCallback, object?> item;
                lock (_gate)
                {
                    if (_work.Count == 0)
                    {
                        return;
                    }
                    item = _work.Dequeue();
                }
                item.Key(item.Value);
            }
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}

// 보낸 메시지를 세는 가짜 소켓이다. 보내기 작업을 실제 네트워크 없이 검사한다.
internal sealed class RecordingSocket : WebSocket
{
    private readonly List<string> _messages = new List<string>();
    private int _sent;

    public int Sent
    {
        get
        {
            return Volatile.Read(ref _sent);
        }
    }

    public override WebSocketCloseStatus? CloseStatus
    {
        get
        {
            return null;
        }
    }

    public override string? CloseStatusDescription
    {
        get
        {
            return null;
        }
    }

    public override WebSocketState State
    {
        get
        {
            return WebSocketState.Open;
        }
    }

    public override string? SubProtocol
    {
        get
        {
            return null;
        }
    }

    public string Message(int index)
    {
        lock (_messages)
        {
            return _messages[index];
        }
    }

    // 보낸 메시지가 count개가 될 때까지 기다린다. 처음에는 돌면서 기다리고 길어지면 양보한다.
    public void WaitSent(int count)
    {
        long limit = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
        SpinWait spinner = new SpinWait();
        while (Sent < count)
        {
            if (Stopwatch.GetTimestamp() > limit)
            {
                throw new Exception($"메시지 {count}개를 기다렸지만 {Sent}개만 보냈습니다");
            }
            spinner.SpinOnce();
        }
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_messages)
        {
            // 메모리 검사에서 기록이 늘지 않도록 앞의 몇 개만 남긴다.
            if (_messages.Count < 16)
            {
                _messages.Add(Encoding.UTF8.GetString(buffer));
            }
        }
        Interlocked.Increment(ref _sent);
        return Task.CompletedTask;
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override void Abort()
    {
    }

    public override void Dispose()
    {
    }
}
