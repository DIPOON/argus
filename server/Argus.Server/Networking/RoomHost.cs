using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Argus.Server.Game;

namespace Argus.Server.Networking;

public sealed class Peer
{
    public long Connection { get; }
    public bool Replaced { get; set; }
    public Channel<byte[]> Outgoing { get; }

    public Peer(long connection)
    {
        Connection = connection;
        BoundedChannelOptions options = new BoundedChannelOptions(2);
        options.FullMode = BoundedChannelFullMode.DropOldest;
        options.SingleReader = true;
        options.SingleWriter = false;
        Outgoing = Channel.CreateBounded<byte[]>(options);
    }
}

public sealed class Room
{
    public object Gate { get; } = new object();
    public Match Match { get; }
    public Dictionary<string, Player> Tokens { get; } = new Dictionary<string, Player>();
    public Dictionary<string, Peer> Peers { get; } = new Dictionary<string, Peer>();
    public DateTime LastVisit { get; set; } = DateTime.UtcNow;

    public Room(string code)
    {
        Match = new Match(code);
    }
}

public sealed class Guest
{
    public string Room { get; }
    public string Id { get; }
    public string Token { get; }

    public Guest(string room, string id, string token)
    {
        Room = room;
        Id = id;
        Token = token;
    }
}

public sealed class EntryRequest
{
    public string? Name { get; }
    public string? Passive { get; }
    public string? Weapon { get; }
    public string? Secondary { get; }

    public EntryRequest(string? name, string? passive, string? weapon = null, string? secondary = null)
    {
        Name = name;
        Passive = passive;
        Weapon = weapon;
        Secondary = secondary;
    }

    public bool Valid
    {
        get
        {
            if (Passive != null && Passive != "vitality" && Passive != "mobility")
            {
                return false;
            }
            if (Weapon != null && !Rules.ValidWeapon(Weapon))
            {
                return false;
            }
            if (Secondary != null && !Rules.ValidSecondary(Secondary))
            {
                return false;
            }
            return true;
        }
    }
}

public sealed class RoomHost : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Room> _rooms = new ConcurrentDictionary<string, Room>();
    private readonly ILogger<RoomHost> _logger;
    private long _connection;

    public RoomHost(ILogger<RoomHost> logger)
    {
        _logger = logger;
    }

    public Guest? Create(EntryRequest request)
    {
        if (!request.Valid || _rooms.Count >= 32)
        {
            return null;
        }

        Room room;
        do
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(6);
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            char[] letters = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                letters[i] = alphabet[bytes[i] % alphabet.Length];
            }
            string code = new string(letters);
            room = new Room(code);
        } while (!_rooms.TryAdd(room.Match.Code, room));

        return Join(room.Match.Code, request);
    }

    public Guest? Join(string code, EntryRequest request)
    {
        if (!request.Valid || !_rooms.TryGetValue(code.ToUpperInvariant(), out Room? room))
        {
            return null;
        }

        // 같은 방의 접속 처리와 게임 진행이 동시에 상태를 바꾸지 않도록 잠근다.
        lock (room.Gate)
        {
            string name = CleanName(request.Name);
            string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            Player? player = room.Match.Join(id, name);
            if (player == null)
            {
                return null;
            }

            string passive = "vitality";
            if (request.Passive != null)
            {
                passive = request.Passive;
            }
            string weapon = "rifle";
            if (request.Weapon != null)
            {
                weapon = request.Weapon;
            }
            string secondary = "grenade";
            if (request.Secondary != null)
            {
                secondary = request.Secondary;
            }
            room.Match.SetLoadout(player, passive, weapon, secondary);

            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            room.Tokens[token] = player;
            // HTTP 참가 후 WebSocket으로 접속하지 않아도 복구 유예 시간이 지나면 자리를 비운다.
            player.DisconnectedAt = room.Match.Now;
            room.LastVisit = DateTime.UtcNow;
            return new Guest(room.Match.Code, player.Id, token);
        }
    }

    private static string CleanName(string? requestedName)
    {
        if (requestedName == null)
        {
            return "대원";
        }

        string name = requestedName.Trim();
        char[] letters = new char[16];
        int length = 0;
        for (int i = 0; i < name.Length && length < letters.Length; i++)
        {
            if (!char.IsControl(name[i]))
            {
                letters[length] = name[i];
                length++;
            }
        }
        if (length == 0)
        {
            return "대원";
        }
        return new string(letters, 0, length);
    }

    // 성공하면 방, 대원, 연결 정보를 묶어 반환한다. 실패하면 null을 반환한다.
    public (Room Room, Player Player, Peer Peer)? Attach(string code, string token)
    {
        if (!_rooms.TryGetValue(code.ToUpperInvariant(), out Room? room))
        {
            return null;
        }
        lock (room.Gate)
        {
            if (!room.Tokens.TryGetValue(token, out Player? player) || !room.Match.Players.Contains(player))
            {
                return null;
            }
            if (room.Peers.TryGetValue(player.Id, out Peer? old))
            {
                old.Replaced = true;
                old.Outgoing.Writer.TryComplete();
            }

            Peer peer = new Peer(Interlocked.Increment(ref _connection));
            room.Peers[player.Id] = peer;
            player.Connection = peer.Connection;
            player.Connected = true;
            player.DisconnectedAt = null;
            room.LastVisit = DateTime.UtcNow;
            byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(Snapshot.Create(room.Match, player), Json);
            peer.Outgoing.Writer.TryWrite(snapshot);
            return (room, player, peer);
        }
    }

    public void Detach(Room room, Player player, Peer peer)
    {
        lock (room.Gate)
        {
            if (player.Connection != peer.Connection)
            {
                return;
            }
            room.Peers.Remove(player.Id);
            room.Match.Disconnect(player);
            peer.Outgoing.Writer.TryComplete();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // await로 다음 틱을 기다리는 동안 스레드는 다른 작업에 사용될 수 있다.
        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(Rules.Step));
        Stopwatch watch = Stopwatch.StartNew();
        double previous = watch.Elapsed.TotalSeconds;
        double publishAt = 0;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            double current = watch.Elapsed.TotalSeconds;
            double delta = current - previous;
            previous = current;
            bool publish = current >= publishAt;
            if (publish)
            {
                publishAt = current + 0.1;
            }

            foreach (KeyValuePair<string, Room> entry in _rooms)
            {
                string code = entry.Key;
                Room room = entry.Value;
                try
                {
                    lock (room.Gate)
                    {
                        room.Match.Step(delta);
                        RemoveExpiredTokens(room);
                        if (publish)
                        {
                            PublishSnapshots(room);
                        }
                        if (room.Peers.Count == 0 && DateTime.UtcNow - room.LastVisit > TimeSpan.FromMinutes(15))
                        {
                            _rooms.TryRemove(code, out _);
                        }
                    }
                }
                catch (Exception error)
                {
                    _logger.LogError(error, "Room simulation failed: {Room}", code);
                }
            }
        }
    }

    private static void RemoveExpiredTokens(Room room)
    {
        // Dictionary를 순회하는 중에 지우지 않도록 삭제할 키를 먼저 모은다.
        List<string> expired = new List<string>();
        foreach (KeyValuePair<string, Player> entry in room.Tokens)
        {
            if (!room.Match.Players.Contains(entry.Value))
            {
                expired.Add(entry.Key);
            }
        }
        for (int i = 0; i < expired.Count; i++)
        {
            room.Tokens.Remove(expired[i]);
        }
    }

    private static void PublishSnapshots(Room room)
    {
        for (int i = 0; i < room.Match.Players.Count; i++)
        {
            Player player = room.Match.Players[i];
            if (!room.Peers.TryGetValue(player.Id, out Peer? peer))
            {
                continue;
            }
            byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(Snapshot.Create(room.Match, player), Json);
            peer.Outgoing.Writer.TryWrite(snapshot);
        }
    }
}
