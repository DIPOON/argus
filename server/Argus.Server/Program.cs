using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.RateLimiting;
using Argus.Server.Networking;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RoomHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RoomHost>());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("entry", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
    { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
var app = builder.Build();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ok", protocol = 1 }));
app.MapPost("/api/rooms", (EntryRequest request, RoomHost host) =>
    !request.Valid ? Results.Json(new { error = "선택할 수 없는 장비입니다." }, statusCode: 400) :
    host.Create(request) is { } guest ? Results.Json(guest) : Results.Json(new { error = "서버의 작전 공간이 가득 찼습니다." }, statusCode: 503)).RequireRateLimiting("entry");
app.MapPost("/api/rooms/{code}/join", (string code, EntryRequest request, RoomHost host) =>
    !request.Valid ? Results.Json(new { error = "선택할 수 없는 장비입니다." }, statusCode: 400) :
    code.Length == 6 && host.Join(code, request) is { } guest ? Results.Json(guest) : Results.Json(new { error = "방이 없거나, 가득 찼거나, 종료된 작전입니다." }, statusCode: 409)).RequireRateLimiting("entry");

app.Map("/ws", async (HttpContext context, RoomHost host) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    Room? room = null;
    Argus.Server.Game.Player? player = null;
    Peer? peer = null;
    try
    {
        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var hello = await ReadMessage(socket, handshakeTimeout.Token);
        if (hello is null || Text(hello.RootElement, "type") != "hello") return;
        var attached = host.Attach(Text(hello.RootElement, "room"), Text(hello.RootElement, "token"));
        if (attached is null)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "참가 정보가 만료되었습니다", lifetime.Token);
            return;
        }
        (room, player, peer) = attached.Value;
        var receive = Receive(socket, room, player, peer, lifetime.Token);
        var send = Send(socket, peer, lifetime.Token);
        await Task.WhenAny(receive, send);
        if (peer.Replaced)
        {
            // Cancelling a pending ReceiveAsync aborts .NET's WebSocket. Send the replacement code first.
            using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            closeTimeout.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await send.WaitAsync(closeTimeout.Token);
                if (socket.State == WebSocketState.Open)
                    await socket.CloseOutputAsync((WebSocketCloseStatus)4001, "다른 연결로 교체됨", closeTimeout.Token);
            }
            catch (OperationCanceledException) { }
        }
        await lifetime.CancelAsync();
        try { await Task.WhenAll(receive, send); } catch (OperationCanceledException) { }
    }
    catch (Exception error) when (error is WebSocketException or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException) { }
    finally
    {
        if (room is not null && player is not null && peer is not null) host.Detach(room, player, peer);
        if (socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var replaced = peer?.Replaced == true;
            try { await socket.CloseOutputAsync(replaced ? (WebSocketCloseStatus)4001 : WebSocketCloseStatus.NormalClosure, replaced ? "다른 연결로 교체됨" : "연결 종료", timeout.Token); } catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
        }
    }
});
app.Run();

static async Task<JsonDocument?> ReadMessage(WebSocket socket, CancellationToken token)
{
    var buffer = new byte[4096];
    var length = 0;
    while (true)
    {
        if (length == buffer.Length) throw new JsonException("Message too large");
        var result = await socket.ReceiveAsync(buffer.AsMemory(length), token);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        if (result.MessageType != WebSocketMessageType.Text) throw new JsonException("Text frames required");
        length += result.Count;
        if (result.EndOfMessage) return JsonDocument.Parse(buffer.AsMemory(0, length));
    }
}

static async Task Receive(WebSocket socket, Room room, Argus.Server.Game.Player player, Peer peer, CancellationToken token)
{
    var window = DateTime.UtcNow;
    var count = 0;
    while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
        using var document = await ReadMessage(socket, token);
        if (document is null) return;
        if ((DateTime.UtcNow - window).TotalSeconds >= 1) { window = DateTime.UtcNow; count = 0; }
        if (++count > 90) throw new JsonException("Input rate exceeded");
        var root = document.RootElement;
        lock (room.Gate)
        {
            if (player.Connection != peer.Connection) return;
            switch (Text(root, "type"))
            {
                case "input":
                    if (root.TryGetProperty("seq", out var sequence) && sequence.TryGetInt64(out var seq))
                        room.Match.Input(player, seq, Number(root, "moveX"), Number(root, "moveY"), Number(root, "aim"), Flag(root, "fire"), Flag(root, "reload"), Flag(root, "secondary") || Flag(root, "grenade"));
                    break;
                case "deploy":
                    if (!room.Match.Deploy(player, Number(root, "x", double.NaN), Number(root, "y", double.NaN)))
                        peer.Outgoing.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(new { type = "error", message = "해당 위치에는 투입할 수 없습니다. 빈 지면을 선택하세요." }, RoomHost.Json));
                    break;
                case "loadout":
                    room.Match.SetLoadout(player,
                        root.TryGetProperty("passive", out _) ? Text(root, "passive") : player.Passive,
                        root.TryGetProperty("weapon", out _) ? Text(root, "weapon") : player.Weapon,
                        root.TryGetProperty("secondary", out _) ? Text(root, "secondary") : player.Secondary);
                    break;
                case "ping":
                    peer.Outgoing.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(new { type = "pong", sent = Number(root, "sent") }, RoomHost.Json));
                    break;
            }
        }
    }
}

static async Task Send(WebSocket socket, Peer peer, CancellationToken token)
{
    await foreach (var bytes in peer.Outgoing.Reader.ReadAllAsync(token))
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token);
}

static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
static double Number(JsonElement element, string name, double fallback = 0) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : fallback;
static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
