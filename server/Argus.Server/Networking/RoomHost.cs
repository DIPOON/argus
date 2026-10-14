using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Argus.Server.Game;

namespace Argus.Server.Networking;

// 연결로 보낼 메시지와, 그 안에 바뀌지 않는 정보가 담겼는지를 함께 둔다.
public sealed class OutgoingMessage
{
    public byte[] Bytes { get; }
    // 담긴 벽 정보의 판 번호다. 벽이 없으면 -1이다.
    public int WallRevision { get; }
    // 규칙과 맵 정보를 담았는지 나타낸다.
    public bool HasStatic { get; }

    public OutgoingMessage(byte[] bytes, int wallRevision, bool hasStatic)
    {
        Bytes = bytes;
        WallRevision = wallRevision;
        HasStatic = hasStatic;
    }
}

public sealed class Peer
{
    public long Connection { get; }
    public bool Replaced { get; set; }
    public Channel<OutgoingMessage> Outgoing { get; }
    // 오류 안내와 pong을 보내는 통로다. 스냅샷은 느린 연결에서 오래된 것부터 버려도 되지만 이 메시지들은
    // 버려지면 안 되므로 따로 둔다. 읽지 않는 연결이 ping만 쏟아 서버 메모리를 늘리지 못하도록 용량은 둔다.
    // 이 통로가 차면 새 메시지를 버리며, 그 피해는 스스로 넘치게 만든 연결만 입는다.
    public Channel<OutgoingMessage> Control { get; }
    // 실제로 보낸 벽 판 번호와 규칙·맵 전송 여부다. 느린 수신자에게 쌓인 메시지는 버려질 수 있으므로
    // 만들 때가 아니라 보낸 뒤에 기록한다. 보내는 작업과 방송이 다른 스레드에서 쓰고 읽으므로 volatile로 둔다.
    private volatile int _sentWallRevision = -1;
    private volatile bool _sentStatic;

    public int SentWallRevision
    {
        get
        {
            return _sentWallRevision;
        }
    }

    public bool SentStatic
    {
        get
        {
            return _sentStatic;
        }
    }

    public Peer(long connection)
    {
        Connection = connection;
        BoundedChannelOptions options = new BoundedChannelOptions(2);
        options.FullMode = BoundedChannelFullMode.DropOldest;
        options.SingleReader = true;
        options.SingleWriter = false;
        Outgoing = Channel.CreateBounded<OutgoingMessage>(options);
        BoundedChannelOptions controlOptions = new BoundedChannelOptions(32);
        controlOptions.FullMode = BoundedChannelFullMode.DropWrite;
        controlOptions.SingleReader = true;
        controlOptions.SingleWriter = false;
        Control = Channel.CreateBounded<OutgoingMessage>(controlOptions);
    }

    // 메시지를 실제로 보낸 뒤에 부른다.
    public void MarkSent(OutgoingMessage message)
    {
        if (message.WallRevision >= 0)
        {
            _sentWallRevision = message.WallRevision;
        }
        if (message.HasStatic)
        {
            _sentStatic = true;
        }
    }

    // 두 통로의 메시지를 소켓으로 보낸다. 오류 안내와 pong을 스냅샷보다 먼저 보낸다.
    // 스냅샷 통로가 닫히면(연결 교체·해제·방 닫힘) 끝나고, 취소되면 OperationCanceledException으로 끝난다.
    public async Task Send(WebSocket socket, CancellationToken token)
    {
        // 끝나지 않은 기다림은 다음 차례에도 그대로 쓴다. 차례마다 새로 기다리면, 메시지가 드문 제어 통로에
        // 끝나지 않은 기다림이 스냅샷마다 하나씩 쌓인다. ping을 보내지 않는 연결에서는 끝없이 쌓인다.
        Task<bool>? controlReady = null;
        Task<bool>? snapshotReady = null;
        while (true)
        {
            // 오류 안내와 pong을 먼저 모두 보낸다. 이 통로의 메시지는 스냅샷과 달리 버려지지 않는다.
            while (Control.Reader.TryRead(out OutgoingMessage? control))
            {
                await socket.SendAsync(control.Bytes.AsMemory(), WebSocketMessageType.Text, true, token);
            }
            if (Outgoing.Reader.TryRead(out OutgoingMessage? message))
            {
                await socket.SendAsync(message.Bytes.AsMemory(), WebSocketMessageType.Text, true, token);
                // 실제로 보낸 뒤에야 이 연결이 바뀌지 않는 정보를 받았다고 본다.
                MarkSent(message);
                continue;
            }
            // 둘 다 비었으면 어느 쪽이든 새 메시지가 올 때까지 기다린다.
            if (controlReady == null || controlReady.IsCompleted)
            {
                controlReady = Control.Reader.WaitToReadAsync(token).AsTask();
            }
            if (snapshotReady == null || snapshotReady.IsCompleted)
            {
                snapshotReady = Outgoing.Reader.WaitToReadAsync(token).AsTask();
            }
            Task<bool> first = await Task.WhenAny(controlReady, snapshotReady);
            // 취소되었으면 여기서 예외로 끝난다. 취소된 기다림은 곧바로 끝나므로, 넘어가면 같은 자리를 헛돈다.
            bool open = await first;
            if (!open)
            {
                return;
            }
        }
    }
}

// 스냅샷을 방송할 시각을 정한다. 방송 간격은 0.1초다.
public sealed class PublishSchedule
{
    public const double Interval = 0.1;
    private double _next;

    // 이번 틱에 방송할지 정하고, 방송하면 다음 방송 시각을 정한다.
    public bool Due(double current)
    {
        if (current < _next)
        {
            return false;
        }
        // 다음 방송 시각은 실제로 깨어난 시각이 아니라 이번 방송의 예정 시각에서 잡는다.
        // 깨어난 시각에서 잡으면, 타이머 주기(1/30초를 100ns 단위로 바꾼 값) 세 번이 0.1초에 조금 못 미치거나
        // 조금만 늦게 깨어나도 방송이 한 틱씩 밀려 실제로는 8Hz 안팎이 된다.
        _next += Interval;
        // 서버가 오래 멈췄다 깨어나 방송을 한 번 이상 건너뛰었으면, 밀린 방송을 몰아서 보내지 않고 지금부터 다시 센다.
        if (_next <= current)
        {
            _next = current + Interval;
        }
        return true;
    }
}

public sealed class Room
{
    public object Gate { get; } = new object();
    public Match Match { get; }
    public Dictionary<string, Player> Tokens { get; } = new Dictionary<string, Player>();
    public Dictionary<string, Peer> Peers { get; } = new Dictionary<string, Peer>();
    public DateTime LastVisit { get; set; } = DateTime.UtcNow;
    // 방을 만든 접속 주소다. 한 주소가 동시에 가질 수 있는 방 수를 셀 때 쓴다. 상한에서 뺀 주소면 null이다.
    public string? Address { get; }
    // 토큰을 마지막으로 정리했을 때의 Match.Departures 값이다.
    public int SeenDepartures { get; set; }
    // 진행에 연속해서 실패한 횟수다.
    public int Failures { get; set; }

    public Room(string code, string? address)
    {
        Match = new Match(code);
        Address = address;
    }

    // 대원이 모두 떠난 방에는 복구할 자리가 없으므로 곧바로 지운다. 오래 남기면 빈 방으로 방 생성을 막을 수 있다.
    // 방을 만들고 첫 대원을 넣기 전의 짧은 틈에 지워지지 않도록 2초만 기다린다.
    // 접속 없이 참가만 한 자리는 15초 뒤에 풀리므로 그런 방은 만든 지 15초 남짓이면 사라진다.
    private static readonly TimeSpan EmptyRoomGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleRoomGrace = TimeSpan.FromMinutes(15);

    // 연결이 모두 끊긴 방을 지울지 정한다. 기준은 마지막 참가·접속·연결 해제 시각이다.
    // 복구를 기다리는 대원이 있으면 15분, 대원이 아무도 없으면 30초 뒤에 지운다.
    public bool Expired(DateTime now)
    {
        if (Peers.Count > 0)
        {
            return false;
        }
        if (Match.Players.Count == 0)
        {
            return now - LastVisit > EmptyRoomGrace;
        }
        return now - LastVisit > IdleRoomGrace;
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
    // 한 틱에 진행하는 시간의 상한이다. 서버가 오래 멈췄다 깨어나도 이보다 많이 따라잡지 않는다.
    private const double MaxTickSeconds = 0.25;
    // 연속해서 이만큼 진행에 실패한 방은 닫는다. 30번이면 약 1초다.
    private const int MaxRoomFailures = 30;
    // 한 주소가 동시에 가질 수 있는 방 수다. 방을 만들고 접속을 붙잡아 두는 방법으로 몇 개 주소만으로
    // 방 32개 상한을 채우지 못하게 한다. 끝난 방도 마지막 대원이 나간 뒤 약 2분(재접속 유예) 동안 남으므로
    // 혼자 여러 판을 연달아 해도 걸리지 않을 만큼은 둔다.
    public const int MaxRoomsPerAddress = 4;
    // 방 수 확인과 추가가 따로 일어나면 동시 요청으로 상한을 넘을 수 있으므로 함께 잠근다.
    private readonly object _createGate = new object();

    public RoomHost(ILogger<RoomHost> logger)
    {
        _logger = logger;
    }

    // 주소별 상한을 적용하지 않고 만든다. 검사처럼 접속 주소가 없을 때 쓴다.
    public Guest? Create(EntryRequest request)
    {
        bool addressFull;
        return Create(request, null, out addressFull);
    }

    // address가 null이면 주소별 상한을 적용하지 않는다.
    // addressFull은 이 주소가 만든 방이 이미 상한만큼 있어서 거절했을 때 켠다.
    public Guest? Create(EntryRequest request, string? address, out bool addressFull)
    {
        addressFull = false;
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
            if (address != null && CountRooms(address) >= MaxRoomsPerAddress)
            {
                addressFull = true;
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
                room = new Room(code, address);
            } while (!_rooms.TryAdd(room.Match.Code, room));
        }

        return Join(room.Match.Code, request);
    }

    // 이 주소가 만든 방 가운데 아직 남아 있는 방의 수다.
    private int CountRooms(string address)
    {
        int count = 0;
        foreach (KeyValuePair<string, Room> entry in _rooms)
        {
            if (entry.Value.Address == address)
            {
                count++;
            }
        }
        return count;
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
            byte[] snapshot = Snapshot.Create(room.Match, player, Json);
            peer.Outgoing.Writer.TryWrite(new OutgoingMessage(snapshot, room.Match.Map.Revision, true));
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
        PublishSchedule schedule = new PublishSchedule();
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            double current = watch.Elapsed.TotalSeconds;
            double delta = current - previous;
            previous = current;
            // 절전이나 디버거로 서버가 멈췄다 깨어나도 밀린 시간을 한꺼번에 따라잡지 않는다.
            // 따라잡으면 잠금을 쥔 채 수천 단계를 돌리고, 아무도 보지 못하는 사이에 대원이 죽거나 작전이 끝난다.
            // 넘친 시간은 게임 시간에서 버린다. 처리가 밀릴수록 다음 단계가 커지는 악순환도 막는다.
            if (delta > MaxTickSeconds)
            {
                delta = MaxTickSeconds;
            }
            bool publish = schedule.Due(current);

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

    // 방 하나를 한 틱 진행한다. 타이머 반복문이 부르며, 검사에서도 직접 부른다.
    public void StepRoom(string code, Room room, double delta, bool publish)
    {
        lock (room.Gate)
        {
            try
            {
                room.Match.Step(delta);
                // 대원이 실제로 빠졌을 때만 토큰을 정리한다. 매 틱 모든 방을 훑을 필요가 없다.
                if (room.Match.Departures != room.SeenDepartures)
                {
                    RemoveExpiredTokens(room);
                    room.SeenDepartures = room.Match.Departures;
                }
                if (publish)
                {
                    PublishSnapshots(room);
                }
                room.Failures = 0;
            }
            catch (Exception error)
            {
                room.Failures++;
                // 같은 오류가 틱마다 반복되므로 연속 실패의 첫 번째만 기록한다.
                if (room.Failures == 1)
                {
                    _logger.LogError(error, "Room simulation failed: {Room}", code);
                }
            }
            // 진행에 계속 실패하는 방은 고칠 수 없으므로 닫는다. 남겨 두면 자리를 차지한 채 오류만 쌓인다.
            if (room.Failures >= MaxRoomFailures)
            {
                _logger.LogError("Closing room after repeated simulation failures: {Room}", code);
                CloseRoom(code, room);
                return;
            }
            // 진행에 실패한 틱에도 만료 검사는 한다.
            if (room.Expired(DateTime.UtcNow))
            {
                _rooms.TryRemove(code, out _);
            }
        }
    }

    // 방을 목록에서 지우고 연결을 닫는다. 보낼 통로를 닫으면 Send가 끝나고 연결 처리가 소켓을 닫는다.
    // 방 잠금 안에서 불러야 한다.
    private void CloseRoom(string code, Room room)
    {
        _rooms.TryRemove(code, out _);
        foreach (KeyValuePair<string, Peer> entry in room.Peers)
        {
            entry.Value.Outgoing.Writer.TryComplete();
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

    // 방에 연결된 대원들에게 스냅샷을 보낸다. 방 잠금 안에서 불러야 한다.
    public static void PublishSnapshots(Room room)
    {
        if (room.Peers.Count == 0)
        {
            return;
        }
        // 공통 부분(시야 계산 포함)은 방송마다 한 번만 만들고 한 번만 직렬화한다. 수신자마다 다른 값
        // (you, 보급 대기 시간, 아직 받지 못한 벽·규칙·맵)만 따로 직렬화해 앞에 이어 붙인다.
        SnapshotMessage shared = Snapshot.CreateShared(room.Match);
        byte[] sharedJson = Snapshot.SerializeShared(shared, Json);
        int revision = room.Match.Map.Revision;
        for (int i = 0; i < room.Match.Players.Count; i++)
        {
            Player player = room.Match.Players[i];
            if (!room.Peers.TryGetValue(player.Id, out Peer? peer))
            {
                continue;
            }
            // 벽은 이 연결이 아직 받지 못한 판일 때만, 규칙과 맵은 아직 받지 못했을 때만 담는다.
            bool includeWalls = peer.SentWallRevision != revision;
            bool includeStatic = !peer.SentStatic;
            PersonalSnapshot personal = Snapshot.CreatePersonal(room.Match, player, includeWalls, includeStatic);
            byte[] snapshot = Snapshot.Combine(sharedJson, personal, Json);
            int wallRevision = -1;
            if (includeWalls)
            {
                wallRevision = revision;
            }
            peer.Outgoing.Writer.TryWrite(new OutgoingMessage(snapshot, wallRevision, includeStatic));
        }
    }
}
