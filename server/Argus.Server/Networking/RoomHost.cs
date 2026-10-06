using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Argus.Server.Game;

namespace Argus.Server.Networking;

public sealed class Peer(long connection)
{
    public long Connection { get; } = connection;
    public bool Replaced { get; set; }
    public Channel<byte[]> Outgoing { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
}

public sealed class Room(string code)
{
    public object Gate { get; } = new();
    public Match Match { get; } = new(code);
    public Dictionary<string, Player> Tokens { get; } = [];
    public Dictionary<string, Peer> Peers { get; } = [];
    public DateTime LastVisit { get; set; } = DateTime.UtcNow;
}

public sealed record Guest(string Room, string Id, string Token);
public sealed record EntryRequest(string? Name, string? Passive, string? Weapon = null, string? Secondary = null)
{
    public bool Valid => (Passive is null or "vitality" or "mobility") &&
        Rules.ValidWeapon(Weapon ?? "rifle") && Rules.ValidSecondary(Secondary ?? "grenade");
}

public sealed class RoomHost(ILogger<RoomHost> logger) : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private long _connection;

    public Guest? Create(EntryRequest request)
    {
        if (!request.Valid || _rooms.Count >= 32) return null;
        Room room;
        do
        {
            var bytes = RandomNumberGenerator.GetBytes(6);
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var code = new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
            room = new(code);
        } while (!_rooms.TryAdd(room.Match.Code, room));
        return Join(room.Match.Code, request);
    }

    public Guest? Join(string code, EntryRequest request)
    {
        if (!request.Valid || !_rooms.TryGetValue(code.ToUpperInvariant(), out var room)) return null;
        lock (room.Gate)
        {
            var name = new string((request.Name ?? "대원").Trim().Where(c => !char.IsControl(c)).Take(16).ToArray());
            if (name.Length == 0) name = "대원";
            var player = room.Match.Join(Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(), name);
            if (player is null) return null;
            room.Match.SetLoadout(player, request.Passive ?? "vitality", request.Weapon ?? "rifle", request.Secondary ?? "grenade");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            room.Tokens[token] = player;
            // Abandoned HTTP joins reserve a slot only for the reconnect grace period.
            player.DisconnectedAt = room.Match.Now;
            room.LastVisit = DateTime.UtcNow;
            return new(room.Match.Code, player.Id, token);
        }
    }

    public (Room Room, Player Player, Peer Peer)? Attach(string code, string token)
    {
        if (!_rooms.TryGetValue(code.ToUpperInvariant(), out var room)) return null;
        lock (room.Gate)
        {
            if (!room.Tokens.TryGetValue(token, out var player) || !room.Match.Players.Contains(player)) return null;
            if (room.Peers.TryGetValue(player.Id, out var old))
            {
                old.Replaced = true;
                old.Outgoing.Writer.TryComplete();
            }
            var peer = new Peer(Interlocked.Increment(ref _connection));
            room.Peers[player.Id] = peer;
            player.Connection = peer.Connection;
            player.Connected = true;
            player.DisconnectedAt = null;
            room.LastVisit = DateTime.UtcNow;
            peer.Outgoing.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(Snapshot.Create(room.Match, player), Json));
            return (room, player, peer);
        }
    }

    public void Detach(Room room, Player player, Peer peer)
    {
        lock (room.Gate)
        {
            if (player.Connection != peer.Connection) return;
            room.Peers.Remove(player.Id);
            room.Match.Disconnect(player);
            peer.Outgoing.Writer.TryComplete();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Rules.Step));
        var watch = Stopwatch.StartNew();
        var previous = watch.Elapsed.TotalSeconds;
        var publishAt = 0d;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var current = watch.Elapsed.TotalSeconds;
            var delta = current - previous;
            previous = current;
            var publish = current >= publishAt;
            if (publish) publishAt = current + .1;
            foreach (var (code, room) in _rooms)
            {
                try
                {
                    lock (room.Gate)
                    {
                        room.Match.Step(delta);
                        foreach (var token in room.Tokens.Where(x => !room.Match.Players.Contains(x.Value)).Select(x => x.Key).ToArray()) room.Tokens.Remove(token);
                        if (publish)
                        foreach (var p in room.Match.Players)
                        {
                            if (!room.Peers.TryGetValue(p.Id, out var peer)) continue;
                            peer.Outgoing.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(Snapshot.Create(room.Match, p), Json));
                        }
                        if (room.Peers.Count == 0 && DateTime.UtcNow - room.LastVisit > TimeSpan.FromMinutes(15)) _rooms.TryRemove(code, out _);
                    }
                }
                catch (Exception error) { logger.LogError(error, "Room simulation failed: {Room}", code); }
            }
        }
    }
}
