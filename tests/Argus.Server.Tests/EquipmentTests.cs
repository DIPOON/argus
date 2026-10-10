using Argus.Server.Game;
using Argus.Server.Networking;
using Microsoft.Extensions.Logging.Abstractions;
using static TestTools;

internal static class EquipmentTests
{
    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("Four-slot loadouts validate categories and reject changes after first deployment", Slots));
        tests.Add(new TestCase("Zero mana prevents abilities; whiffs consume mana once with no regeneration", Mana));
        tests.Add(new TestCase("Input spam cannot stack basic abilities or shorten their recovery", Spam));
        tests.Add(new TestCase("The freely chosen fourth slot casts its selected basic ability", FreeSlot));
    }

    private static void Slots()
    {
        using RoomHost host = new RoomHost(NullLogger<RoomHost>.Instance);
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
        room.Match.Deploy(player, 300, 730);
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
        f.Match.Step(Rules.Step);
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
        f.Match.Step(Rules.Step);
        Equal(f.Player.Mana, 23);
        Equal(f.Player.Actions.Count, 1);
        f.Match.Input(f.Player, 1000, 0, 0, 0, 2);
        f.Match.Step(Rules.Step);
        Equal(f.Player.Actions.Count, 1);
        Equal(f.Player.Actions[0].Skill.Id, "strike");
        Equal(f.Player.Mana, 23);
    }

    private static void FreeSlot()
    {
        Match match = new Match("TEST01", new Rules { MaxEnemies = 0 });
        Player player = match.Join("p", "pilot")!;
        Check(match.SetLoadout(player, new string[] { "strike", "parry", "grab", "grab" }));
        match.Deploy(player, 300, 730);
        Advance(match, 5.1);
        player.Mana = 1;
        match.Input(player, 0, 0, 0, 0, 4);
        match.Step(Rules.Step);
        Equal(player.Actions[0].Skill.Id, "grab");
        Equal(player.Actions[0].Slot, 4);
        Equal(player.Mana, 0);
    }
}
