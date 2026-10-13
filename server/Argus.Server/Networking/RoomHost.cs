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

    // 연결이 모두 끊긴 뒤 15분이 지나면 방을 지운다. 기준은 마지막 참가·접속·연결 해제 시각이다.
    public bool Expired(DateTime now)
    {
        if (Peers.Count > 0)
        {
            return false;
        }
        return now - LastVisit > TimeSpan.FromMinutes(15);
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
    public string[]? Slots { get; }

    public EntryRequest(string? name, string[]? slots = null)
    {
        Name = name;
        Slots = slots;
    }

    public bool Valid
    {
        get
        {
            return Slots == null || Rules.ValidSlots(Slots);
        }
    }
}

public sealed class RoomHost : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Room> _rooms = new ConcurrentDictionary<string, Room>();
    private readonly ILogger<RoomHost> _logger;
    private long _connection;
    // 방 수 확인과 추가가 따로 일어나면 동시 요청으로 상한을 넘을 수 있으므로 함께 잠근다.
    private readonly object _createGate = new object();

    public RoomHost(ILogger<RoomHost> logger)
    {
        _logger = logger;
    }

    public Guest? Create(EntryRequest request)
    {
        if (!request.Valid)
        {
            return null;
        }

        Room room;
        lock (_createGate)
        {
            if (_rooms.Count >= 32)
            {
                return null;
            }
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
        }

        return Join(room.Match.Code, request);
    }

    // 방 삭제는 그 방의 잠금 안에서만 일어난다. 그래서 잠금을 잡은 뒤 확인하면
    // 방을 찾은 뒤 잠금을 얻기 전에 지워졌는지 알 수 있다.
    private bool IsRegistered(Room room)
    {
        if (!_rooms.TryGetValue(room.Match.Code, out Room? current))
        {
            return false;
        }
        return current == room;
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
            // 지워진 방에 참가시키면 토큰은 발급되지만 이어지는 WebSocket 접속이 실패한다.
            if (!IsRegistered(room))
            {
                return null;
            }
            string name = CleanName(request.Name);
            string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            Player? player = room.Match.Join(id, name);
            if (player == null)
            {
                return null;
            }

            if (request.Slots != null)
            {
                room.Match.SetLoadout(player, request.Slots);
            }

            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            room.Tokens[token] = player;
            // HTTP 참가 후 WebSocket으로 접속하지 않으면 짧은 유예 뒤에 자리를 비운다.
            // 방 코드를 아는 사람이 참가만 반복해 자리를 막아 두지 못하게 한다.
            player.DisconnectedAt = room.Match.Now;
            player.AwaitingFirstConnection = true;
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
            // 지워진 방에 접속하면 더 이상 진행되지 않는 방에서 게임이 멈춘다.
            if (!IsRegistered(room))
            {
                return null;
            }
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
            player.AwaitingFirstConnection = false;
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
            // 방 보존 시간은 마지막 연결이 끊긴 순간부터 센다. 갱신하지 않으면 오래 플레이한 방이
            // 끊기자마자 지워져 120초 재접속 유예가 의미가 없어진다.
            room.LastVisit = DateTime.UtcNow;
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

            // 순회 중 방이 추가·삭제되어도 이번 틱의 대상이 바뀌지 않도록 한 시점의 복사본을 쓴다.
            // ToArray는 LINQ가 아니라 ConcurrentDictionary 자체의 메서드다.
            KeyValuePair<string, Room>[] active = _rooms.ToArray();
            // 방마다 상태와 잠금이 독립이므로 여러 방을 동시에 진행할 수 있다.
            if (active.Length > 1)
            {
                ParallelOptions options = new ParallelOptions();
                options.MaxDegreeOfParallelism = Environment.ProcessorCount;
                Parallel.For(0, active.Length, options, index => StepRoom(active[index].Key, active[index].Value, delta, publish));
            }
            else if (active.Length == 1)
            {
                // 방이 하나뿐이면 작업 분배 비용이 아깝다.
                StepRoom(active[0].Key, active[0].Value, delta, publish);
            }
        }
    }

    private void StepRoom(string code, Room room, double delta, bool publish)
    {
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
                if (room.Expired(DateTime.UtcNow))
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
        if (room.Peers.Count == 0)
        {
            return;
        }
        // 수신자마다 다른 것은 you와 보급 대기 시간뿐이다. 시야 계산을 포함한 공통 부분은 한 번만 만든다.
        SnapshotMessage message = Snapshot.CreateShared(room.Match);
        for (int i = 0; i < room.Match.Players.Count; i++)
        {
            Player player = room.Match.Players[i];
            if (!room.Peers.TryGetValue(player.Id, out Peer? peer))
            {
                continue;
            }
            // 같은 메시지에 수신자별 값만 바꿔 넣고 바로 직렬화한다.
            Snapshot.Personalize(message, room.Match, player);
            byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(message, Json);
            peer.Outgoing.Writer.TryWrite(snapshot);
        }
    }
}
