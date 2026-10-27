using System.Text.Json;
using Argus.Server.Game;
using Argus.Server.Networking;
using static TestTools;

// 분대가 본 칸(탐색)의 검사다. 맵은 처음에 캠프 둘레만 보이고, 캠프와 살아 있는 대원의 시야로 넓어진다.
// 안 본 칸의 벽과 그 칸의 시설·보급·탈출 지점은 스냅샷에 실리지 않고, 투입도 본 칸에만 받는다(LifecycleTests의 투입 검사).
internal static class ExplorationTests
{
    public static void Register(List<TestCase> tests)
    {
        tests.Add(new TestCase("The squad has seen only what the camp and landed players see, never open ground hidden behind walls", CampStart));
        tests.Add(new TestCase("Alive players' vision adds seen tiles, which stay seen after they leave; fallen players see nothing", VisionExplores));
        tests.Add(new TestCase("Snapshots send unseen tiles as unknown and leave out the facilities, supplies and extraction on them", SnapshotMasks));
        tests.Add(new TestCase("Newly seen tiles change the walls revision, so connections get the walls again", ExploredResend));
        tests.Add(new TestCase("The state summary tells apart rooms that differ only in the seen tiles or in the walls revision", HashCoversExploration));
    }

    // 칸 (x, y)의 가운데다.
    private static FixedVector Center(int x, int y)
    {
        return FixedVector.FromInt(x * BattleMap.Cell + BattleMap.Cell / 2, y * BattleMap.Cell + BattleMap.Cell / 2);
    }

    private static void CampStart()
    {
        Match match = new Match("EXPL01", new Rules { MaxEnemies = 0 }, 73);
        Player player = match.Join("p", "pilot")!;
        Fixed range = Fixed.FromInt(BattleMap.CampVision);
        int seen = 0;
        for (int y = 0; y < match.Map.Rows; y++)
        {
            for (int x = 0; x < match.Map.Columns; x++)
            {
                FixedVector center = Center(x, y);
                bool explored = match.Explored(center);
                if (!explored)
                {
                    continue;
                }
                seen++;
                // 본 칸은 모두 가운데가 캠프 시야 안에 있다.
                Check(FixedVector.DistanceSquared(center, match.Layout.Camp) <= range * range, $"캠프 시야 밖의 칸 ({x}, {y})을 보았습니다");
            }
        }
        // 빈 땅은 가운데가 캠프에서 보이는 칸만 본 칸이다.
        CheckOpenTiles(match);
        // 캠프 시야(반지름 260)의 넓이는 칸 약 92개다. 시야 안의 벽은 앞면이 보이면 본 칸이다(10, 18번 칸).
        Check(seen > 60 && seen <= 100, $"처음 본 칸이 {seen}개입니다");
        Check(match.Map.Solid(10, 18) && match.Explored(Center(10, 18)), "캠프에서 앞면이 보이는 벽을 보지 못했습니다");

        // 투입을 기다리는 대원은 시야가 없다. 20초를 기다려도 본 칸과 판 번호는 그대로다.
        int revision = match.WallsRevision;
        byte[] before = match.PublicTiles();
        Advance(match, 20);
        Equal(player.State, "waiting");
        Equal(match.WallsRevision, revision);
        Check(before.AsSpan().SequenceEqual(match.PublicTiles()), "기다리는 동안 본 칸이 바뀌었습니다");

        // 착지하면 대원의 시야(반지름 410)가 더해진다. 빈 땅은 여전히 가운데가 지금 보이는 칸만 본 칸이다.
        // 캠프 시야 안에는 벽에 가린 빈 땅이 없어, 대원의 시야로 벽에 가린 칸을 보지 못하는지 확인한다.
        Check(match.Deploy(player, 500, 730));
        Advance(match, match.Rules.DeploySeconds);
        Equal(player.State, "alive");
        int hidden = CheckOpenTiles(match);
        Check(hidden > 0, "시야 반경 안에 벽에 가린 빈 땅이 없어 가림을 확인하지 못했습니다");

        // 장애물이 흩어진 생성 맵에서도 같다.
        byte[] seed = SeededRandom.SeedFromNumber(3);
        Match generated = new Match("EXPL02", Rules.ForGeneratedMission(), seed, MissionGenerator.Generate(seed));
        CheckOpenTiles(generated);
        Player scout = generated.Join("p", "pilot")!;
        Check(generated.Deploy(scout, generated.Layout.Camp.X.ToDouble(), generated.Layout.Camp.Y.ToDouble()));
        Advance(generated, generated.Rules.DeploySeconds);
        Equal(scout.State, "alive");
        CheckOpenTiles(generated);
    }

    // 빈 땅은 가운데가 지금 보이는 칸만 본 칸인지 본다. 캠프와 살아 있는 대원이 처음 선 자리에서 움직이지 않았을 때만 맞다.
    // 시야 반경 안이지만 벽에 가려 보이지 않는 빈 땅의 수를 돌려준다. 가림을 실제로 확인했는지 보려고 센다.
    private static int CheckOpenTiles(Match match)
    {
        int hidden = 0;
        for (int y = 0; y < match.Map.Rows; y++)
        {
            for (int x = 0; x < match.Map.Columns; x++)
            {
                if (match.Map.Solid(x, y))
                {
                    continue;
                }
                FixedVector center = Center(x, y);
                bool visible = match.Visible(center);
                Check(match.Explored(center) == visible, $"빈 땅 ({x}, {y})이 보이는지({visible})와 본 칸인지가 다릅니다");
                if (!visible && InSightRange(match, center))
                {
                    hidden++;
                }
            }
        }
        return hidden;
    }

    // 캠프나 살아 있는 대원의 시야 반경 안인지다. 벽은 보지 않는다.
    private static bool InSightRange(Match match, FixedVector point)
    {
        Fixed camp = Fixed.FromInt(BattleMap.CampVision);
        if (FixedVector.DistanceSquared(match.Layout.Camp, point) <= camp * camp)
        {
            return true;
        }
        for (int i = 0; i < match.Players.Count; i++)
        {
            Player player = match.Players[i];
            if (player.State == "alive" && FixedVector.DistanceSquared(player.Position, point) <= match.VisionRange * match.VisionRange)
            {
                return true;
            }
        }
        return false;
    }

    private static void VisionExplores()
    {
        Fixture f = new Fixture();
        FixedVector far = FixedVector.FromInt(1000, 600);
        Check(!f.Match.Explored(far));
        // 대원이 그곳에 가면 본 칸이 되고, 떠나도 본 칸으로 남는다. 지금 보이는지(Visible)와는 따로다.
        f.Player.Position = far;
        f.Match.Step();
        Check(f.Match.Explored(far) && f.Match.Visible(far), "대원이 선 곳을 보지 못했습니다");
        f.Player.Position = f.Match.Layout.Camp;
        f.Match.Step();
        Check(f.Match.Explored(far), "떠난 곳을 잊었습니다");
        Check(!f.Match.Visible(far));

        // 쓰러져 투입을 기다리는 대원은 시야가 없어 선 곳을 보지 못한다. 동료가 살아 있어 작전은 이어진다.
        Ally(f, "ally");
        FixedVector other = FixedVector.FromInt(1400, 1250);
        Check(!f.Match.Explored(other));
        f.Match.Hurt(f.Player, f.Player.Hp);
        Equal(f.Player.State, "waiting");
        f.Player.Position = other;
        f.Match.Step();
        Equal(f.Match.Phase, "active");
        Check(!f.Match.Explored(other), "쓰러진 대원이 선 곳을 보았습니다");
    }

    private static void SnapshotMasks()
    {
        Fixture f = new Fixture();
        // 처음에는 캠프 둘레만 보았다. 시설과 탈출 지점은 모두 캠프에서 멀다.
        CheckSnapshot(f.Match, f.Player);
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(f.Match, f.Player, RoomHost.Json)))
        {
            JsonElement root = json.RootElement;
            Equal(root.GetProperty("facilities").GetArrayLength(), 0);
            Equal(root.GetProperty("extraction").ValueKind, JsonValueKind.Null);
            JsonElement supplies = root.GetProperty("supplies");
            Equal(supplies.GetArrayLength(), 1);
            Equal(supplies[0].GetProperty("kind").GetString(), "camp");
        }
        // 시설 하나를 둘러보면 그 시설이 실린다. 그곳에서 먼 시설(3번)은 여전히 빠진다.
        Scout(f.Match, f.Player, f.Match.Facilities[0].Position);
        Check(!f.Match.Explored(f.Match.Facilities[2].Position));
        CheckSnapshot(f.Match, f.Player);
        // 탈출 지점도 둘러본 뒤에야 실린다.
        Scout(f.Match, f.Player, f.Match.Layout.Extraction);
        CheckSnapshot(f.Match, f.Player);
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(f.Match, f.Player, RoomHost.Json)))
        {
            JsonElement extraction = json.RootElement.GetProperty("extraction");
            Equal(extraction.GetProperty("x").GetDouble(), f.Match.Layout.Extraction.X.ToDouble());
            Equal(extraction.GetProperty("y").GetDouble(), f.Match.Layout.Extraction.Y.ToDouble());
        }
    }

    // 스냅샷의 벽 배열은 본 칸이면 실제 값, 안 본 칸이면 모름(3)이다. 시설과 보급은 본 칸에 있는 것만 모두 실린다.
    private static void CheckSnapshot(Match match, Player recipient)
    {
        using (JsonDocument json = JsonDocument.Parse(Snapshot.Create(match, recipient, RoomHost.Json)))
        {
            JsonElement root = json.RootElement;
            byte[] walls = Convert.FromBase64String(root.GetProperty("walls").GetString()!);
            Equal(walls.Length, match.Map.Tiles.Length);
            int unknown = 0;
            for (int i = 0; i < walls.Length; i++)
            {
                if (match.Explored(Center(i % match.Map.Columns, i / match.Map.Columns)))
                {
                    Equal(walls[i], match.Map.Tiles[i]);
                }
                else
                {
                    Equal(walls[i], BattleMap.UnknownTile);
                    unknown++;
                }
            }
            Check(unknown > 0, "안 본 칸이 없습니다");
            Equal(root.GetProperty("mapRevision").GetInt32(), match.WallsRevision);
            JsonElement facilities = root.GetProperty("facilities");
            int explored = 0;
            for (int i = 0; i < match.Facilities.Count; i++)
            {
                Facility facility = match.Facilities[i];
                Equal(Contains(facilities, facility.Id), match.Explored(facility.Position));
                if (match.Explored(facility.Position))
                {
                    explored++;
                }
            }
            Equal(facilities.GetArrayLength(), explored);
            JsonElement supplies = root.GetProperty("supplies");
            explored = 0;
            for (int i = 0; i < match.Supplies.Count; i++)
            {
                Supply supply = match.Supplies[i];
                Equal(Contains(supplies, supply.Id), match.Explored(supply.Position));
                if (match.Explored(supply.Position))
                {
                    explored++;
                }
            }
            Equal(supplies.GetArrayLength(), explored);
        }
    }

    // 배열에 id가 같은 항목이 있는지다.
    private static bool Contains(JsonElement array, int id)
    {
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.GetProperty("id").GetInt32() == id)
            {
                return true;
            }
        }
        return false;
    }

    private static void ExploredResend()
    {
        using RoomHost host = NewHost();
        Guest guest = host.Create(new EntryRequest("pilot"))!;
        (Room room, Player player, Peer peer) = host.Attach(guest.Room, guest.Token)!.Value;
        Check(peer.Outgoing.Reader.TryRead(out OutgoingMessage? first), "접속 직후 스냅샷이 없습니다");
        peer.MarkSent(first!);
        // 캠프에 착지하면 대원의 시야(캠프 시야보다 넓다)로 본 칸이 늘어 벽을 다시 보낸다.
        lock (room.Gate)
        {
            Match match = room.Match;
            Check(match.Deploy(player, match.Layout.Camp.X.ToDouble(), match.Layout.Camp.Y.ToDouble()));
            Advance(match, match.Rules.DeploySeconds + 0.1);
            Equal(player.State, "alive");
            RoomHost.PublishSnapshots(room);
        }
        CheckWalls(room, peer, true);
        // 그 자리에 서 있으면 본 칸이 늘지 않아 벽을 다시 보내지 않는다.
        lock (room.Gate)
        {
            room.Match.Step();
            RoomHost.PublishSnapshots(room);
        }
        CheckWalls(room, peer, false);
        // 새 곳(탈출 지점)에 가면 본 칸이 늘어 벽을 다시 보낸다. 새로 본 칸은 실제 값으로 바뀌어 있다.
        lock (room.Gate)
        {
            Check(!room.Match.Explored(room.Match.Layout.Extraction));
            player.Position = room.Match.Layout.Extraction;
            room.Match.Step();
            Check(room.Match.Explored(room.Match.Layout.Extraction));
            RoomHost.PublishSnapshots(room);
        }
        CheckWalls(room, peer, true);
    }

    // 다음 메시지에 벽이 실렸는지 보고, 실렸으면 지금 보낼 벽 배열과 같은지 본다. 보낸 것으로 기록한다.
    private static void CheckWalls(Room room, Peer peer, bool expected)
    {
        Check(peer.Outgoing.Reader.TryRead(out OutgoingMessage? message), "보낼 메시지가 없습니다");
        using (JsonDocument json = JsonDocument.Parse(message!.Bytes))
        {
            bool carries = json.RootElement.TryGetProperty("walls", out JsonElement walls);
            Equal(carries, expected);
            if (carries)
            {
                byte[] tiles = Convert.FromBase64String(walls.GetString()!);
                Check(tiles.AsSpan().SequenceEqual(room.Match.PublicTiles()), "보낸 벽이 지금 본 칸과 다릅니다");
            }
        }
        peer.MarkSent(message);
    }

    private static void HashCoversExploration()
    {
        FixedVector first = FixedVector.FromInt(1000, 600);
        FixedVector second = FixedVector.FromInt(1400, 1250);
        // 본 칸만 다르다. 두 방 모두 한 번씩 둘러봐 판 번호는 같다.
        Fixture one = new Fixture();
        Fixture other = new Fixture();
        Scout(one.Match, one.Player, first);
        Scout(other.Match, other.Player, second);
        Equal(one.Match.WallsRevision, other.Match.WallsRevision);
        Check(!one.Match.PublicTiles().AsSpan().SequenceEqual(other.Match.PublicTiles()));
        Check(one.Match.StateHash() != other.Match.StateHash(), "본 칸만 다른데 상태 요약이 같습니다");

        // 본 칸은 같고 판 번호만 다르다. 한 방은 두 대원이 한 틱에 함께 둘러보고, 다른 방은 한 틱에 한 곳씩 둘러본다.
        Fixture together = new Fixture();
        Player togetherAlly = Ally(together, "ally");
        Fixture apart = new Fixture();
        Player apartAlly = Ally(apart, "ally");
        FixedVector home = together.Player.Position;
        together.Player.Position = first;
        togetherAlly.Position = second;
        together.Match.Step();
        together.Player.Position = home;
        togetherAlly.Position = home;
        together.Match.Step();
        Scout(apart.Match, apart.Player, first);
        Scout(apart.Match, apartAlly, second);
        Check(together.Match.PublicTiles().AsSpan().SequenceEqual(apart.Match.PublicTiles()), "같은 곳을 둘러봤는데 본 칸이 다릅니다");
        Equal(apart.Match.WallsRevision, together.Match.WallsRevision + 1);
        Check(together.Match.StateHash() != apart.Match.StateHash(), "판 번호만 다른데 상태 요약이 같습니다");
    }
}
