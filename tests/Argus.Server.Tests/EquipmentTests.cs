using Argus.Server.Game;
using Argus.Server.Networking;
using static TestTools;

internal static class EquipmentTests
{
    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("Four-slot loadouts validate categories and reject changes after first deployment", Slots));
        tests.Add(new TestCase("Zero mana prevents abilities; whiffs consume mana once with no regeneration", Mana));
        tests.Add(new TestCase("Input spam cannot stack basic abilities or shorten their recovery", Spam));
        tests.Add(new TestCase("The freely chosen fourth slot casts its selected basic ability", FreeSlot));
        tests.Add(new TestCase("Each kind slot takes any skill of its kind and the free slot takes any skill", KindSlots));
    }

    private static void Slots()
    {
        using RoomHost host = NewHost();
        string[] invalid = new string[] { "grab", "parry", "grab", "strike" };
        Check(host.Create(new EntryRequest("bad", invalid)) == null);
        Check(!Rules.ValidSlots(new string[] { "strike", "parry", "grab", "turret" }));
        Check(!Rules.ValidSlots(new string[] { "strike", "parry", "grab" }));
        string[] valid = new string[] { "strike", "parry", "grab", "parry" };
        Guest guest = host.Create(new EntryRequest("pilot", valid))!;
        (Room room, Player player, Peer peer) = host.Attach(guest.Room, guest.Token)!.Value;
        Equal(player.Slots[3], "parry");
        valid[3] = "grab";
        Equal(player.Slots[3], "parry");
        Check(!room.Match.SetLoadout(player, invalid));
        Equal(player.Slots[0], "strike");
        room.Match.Enemies.Clear();
        // 방마다 맵이 다르므로 그 방의 캠프에 투입한다.
        Check(room.Match.Deploy(player, room.Match.Layout.Camp.X.ToDouble(), room.Match.Layout.Camp.Y.ToDouble()));
        Check(!room.Match.SetLoadout(player, valid));
        Advance(room.Match, 5.1);
        Check(!room.Match.SetLoadout(player, valid));
        host.Detach(room, player, peer);
        Equal(host.Attach(guest.Room, guest.Token)!.Value.Player.Slots[3], "parry");
    }

    private static void Mana()
    {
        Fixture f = new Fixture();
        f.Match.Input(f.Player, 0, 0, 0, 0, 1);
        Advance(f.Match, 0.2);
        Equal(f.Player.Actions.Count, 0);
        f.Player.Mana = 3;
        f.Match.Input(f.Player, 1, 0, 0, 0, 1);
        f.Match.Step();
        Equal(f.Player.Mana, 2);
        f.Match.Input(f.Player, 2, 0, 0, 0, 0);
        Advance(f.Match, 20);
        Equal(f.Player.Mana, 2);
    }

    private static void Spam()
    {
        Fixture f = new Fixture();
        f.Player.Mana = 24;
        for (int i = 0; i < 1000; i++)
        {
            Check(f.Match.Input(f.Player, i, 0, 0, 0, 1));
        }
        f.Match.Step();
        Equal(f.Player.Mana, 23);
        Equal(f.Player.Actions.Count, 1);
        f.Match.Input(f.Player, 1000, 0, 0, 0, 2);
        f.Match.Step();
        Equal(f.Player.Actions.Count, 1);
        Equal(f.Player.Actions[0].Skill.Id, "strike");
        Equal(f.Player.Mana, 23);
    }

    private static void KindSlots()
    {
        // 앞의 세 칸은 차례로 타격·방어·잡기 종류의 기술 가운데 하나다. 자유 칸은 아무 기술이나 된다.
        Check(Rules.ValidSlots(new string[] { "spin", "ward", "snatch", "strike" }));
        Check(Rules.ValidSlots(new string[] { "strike", "ward", "grab", "snatch" }));
        Check(Rules.ValidSlots(new string[] { "spin", "parry", "snatch", "spin" }));
        // 종류가 다른 기술은 그 칸에 들 수 없다. 모르는 id와 빈 값도 받지 않는다.
        Check(!Rules.ValidSlots(new string[] { "ward", "parry", "grab", "strike" }));
        Check(!Rules.ValidSlots(new string[] { "strike", "spin", "grab", "strike" }));
        Check(!Rules.ValidSlots(new string[] { "strike", "parry", "ward", "strike" }));
        Check(!Rules.ValidSlots(new string[] { "strike", "parry", "grab", "whirl" }));
        Check(!Rules.ValidSlots(new string[] { "strike", "parry", "grab", null! }));

        // 칸마다 고른 기술이 나간다.
        Match match = new Match("TEST01", new Rules { MaxEnemies = 0 }, 73);
        Player player = match.Join("p", "pilot")!;
        string[] slots = new string[] { "spin", "ward", "snatch", "parry" };
        Check(match.SetLoadout(player, slots));
        match.Deploy(player, match.Layout.Camp.X.ToDouble(), match.Layout.Camp.Y.ToDouble());
        Advance(match, 5.1);
        for (int slot = 1; slot <= 4; slot++)
        {
            player.Actions.Clear();
            player.BusyUntilTick = 0;
            player.Mana = 1;
            match.Input(player, slot, 0, 0, 0, slot);
            match.Step();
            Equal(player.Actions[0].Skill.Id, slots[slot - 1]);
            Equal(player.Actions[0].Slot, slot);
            Equal(player.Mana, 0);
        }
    }

    private static void FreeSlot()
    {
        Match match = new Match("TEST01", new Rules { MaxEnemies = 0 }, 73);
        Player player = match.Join("p", "pilot")!;
        Check(match.SetLoadout(player, new string[] { "strike", "parry", "grab", "grab" }));
        match.Deploy(player, 300, 730);
        Advance(match, 5.1);
        player.Mana = 1;
        match.Input(player, 0, 0, 0, 0, 4);
        match.Step();
        Equal(player.Actions[0].Skill.Id, "grab");
        Equal(player.Actions[0].Slot, 4);
        Equal(player.Mana, 0);
    }
}
