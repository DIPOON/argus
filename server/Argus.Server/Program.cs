using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.RateLimiting;
using Argus.Server.Game;
using Argus.Server.Networking;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Argus.Server;

internal static class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<RoomHost>();
        builder.Services.AddHostedService(GetRoomHost);
        builder.Services.AddRateLimiter(ConfigureRateLimiter);
        builder.WebHost.ConfigureKestrel(ConfigureKestrel);

        WebApplication app = builder.Build();
        app.UseRateLimiter();
        WebSocketOptions socketOptions = new WebSocketOptions();
        socketOptions.KeepAliveInterval = TimeSpan.FromSeconds(20);
        app.UseWebSockets(socketOptions);
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // 접속 경로마다 아래에 정의한 처리 함수를 연결한다.
        app.MapGet("/health", Health);
        app.MapPost("/api/rooms", CreateRoom).RequireRateLimiting("entry");
        app.MapPost("/api/rooms/{code}/join", JoinRoom).RequireRateLimiting("entry");
        app.Map("/ws", HandleWebSocket);
        app.Run();
    }

    private static RoomHost GetRoomHost(IServiceProvider services)
    {
        return services.GetRequiredService<RoomHost>();
    }

    private static void ConfigureKestrel(KestrelServerOptions options)
    {
        options.Limits.MaxRequestBodySize = 4096;
    }

    private static void ConfigureRateLimiter(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy("entry", GetEntryPartition);
    }

    private static RateLimitPartition<string> GetEntryPartition(HttpContext context)
    {
        string address = "local";
        if (context.Connection.RemoteIpAddress != null)
        {
            address = context.Connection.RemoteIpAddress.ToString();
        }
        return RateLimitPartition.GetFixedWindowLimiter(address, CreateEntryLimiterOptions);
    }

    private static FixedWindowRateLimiterOptions CreateEntryLimiterOptions(string address)
    {
        FixedWindowRateLimiterOptions options = new FixedWindowRateLimiterOptions();
        options.PermitLimit = 30;
        options.Window = TimeSpan.FromMinutes(1);
        options.QueueLimit = 0;
        return options;
    }

    private static IResult Health()
    {
        return Results.Ok(new { status = "ok", protocol = 3 });
    }

    private static IResult CreateRoom(EntryRequest request, RoomHost host)
    {
        if (!request.Valid)
        {
            return Results.Json(new { error = "선택할 수 없는 장비입니다." }, statusCode: 400);
        }

        Guest? guest = host.Create(request);
        if (guest == null)
        {
            return Results.Json(new { error = "서버의 작전 공간이 가득 찼습니다." }, statusCode: 503);
        }
        return Results.Json(guest);
    }

    private static IResult JoinRoom(string code, EntryRequest request, RoomHost host)
    {
        if (!request.Valid)
        {
            return Results.Json(new { error = "선택할 수 없는 장비입니다." }, statusCode: 400);
        }

        Guest? guest = null;
        if (code.Length == 6)
        {
            guest = host.Join(code, request);
        }
        if (guest == null)
        {
            return Results.Json(new { error = "방이 없거나, 가득 찼거나, 종료된 작전입니다." }, statusCode: 409);
        }
        return Results.Json(guest);
    }

    private static async Task HandleWebSocket(HttpContext context, RoomHost host, ILoggerFactory loggers)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        // 스냅샷은 매번 비슷한 JSON 텍스트라 압축 효과가 크다. 압축은 접속할 때 협상하며
        // 지원하지 않는 클라이언트는 압축 없이 받는다.
        // 이 옵션이 Dangerous인 이유는 비밀과 공격자가 바꿀 수 있는 값이 같은 압축 문맥에 들어가면
        // 압축된 크기로 비밀을 추측할 수 있기 때문이다(CRIME/BREACH). 서버 메시지에는 복구 토큰이 없고
        // 숨겨진 적은 직렬화 전에 빠지므로 수신자가 이미 볼 수 있는 정보만 압축된다.
        // 이후에도 서버가 보내는 메시지에 비밀을 넣지 않는다.
        WebSocketAcceptContext acceptContext = new WebSocketAcceptContext();
        acceptContext.DangerousEnableCompression = true;
        // using은 이 함수를 나갈 때 소켓과 취소 토큰의 자원을 정리한다.
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(acceptContext);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        Room? room = null;
        Player? player = null;
        Peer? peer = null;
        try
        {
            using CancellationTokenSource handshakeTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            using JsonDocument? hello = await ReadMessage(socket, handshakeTimeout.Token);
            if (hello == null || Text(hello.RootElement, "type") != "hello")
            {
                return;
            }

            (Room Room, Player Player, Peer Peer)? attached =
                host.Attach(Text(hello.RootElement, "room"), Text(hello.RootElement, "token"));
            if (!attached.HasValue)
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                    "참가 정보가 만료되었습니다", lifetime.Token);
                return;
            }
            room = attached.Value.Room;
            player = attached.Value.Player;
            peer = attached.Value.Peer;

            // 입력 수신과 상태 전송을 함께 시작하고, 어느 한쪽이 끝나면 연결을 정리한다.
            Task receive = Receive(socket, room, player, peer, lifetime.Token);
            Task send = peer.Send(socket, lifetime.Token);
            await Task.WhenAny(receive, send);
            if (peer.Replaced)
            {
                // 수신 취소 시 소켓이 즉시 중단될 수 있으므로 연결 교체 코드부터 보낸다.
                using CancellationTokenSource closeTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                closeTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    await send.WaitAsync(closeTimeout.Token);
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.CloseOutputAsync((WebSocketCloseStatus)4001,
                            "다른 연결로 교체됨", closeTimeout.Token);
                        // 수신을 바로 취소하면 소켓이 중단되어 4001 대신 비정상 종료로 보일 수 있다.
                        // 상대의 종료 응답까지 받은 뒤 송수신 작업을 정리한다.
                        await receive.WaitAsync(closeTimeout.Token);
                        if (socket.State == WebSocketState.CloseSent)
                        {
                            await socket.CloseAsync((WebSocketCloseStatus)4001,
                                "다른 연결로 교체됨", closeTimeout.Token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 종료 메시지 전송이 늦어져도 연결 정리는 계속한다.
                }
            }

            await lifetime.CancelAsync();
            try
            {
                await Task.WhenAll(receive, send);
            }
            catch (OperationCanceledException)
            {
                // 위에서 취소한 송수신 작업이 끝날 때 발생할 수 있다.
            }
        }
        catch (Exception error) when (IsConnectionError(error))
        {
            // 잘못된 메시지나 끊어진 연결은 아래 finally에서 정리한다.
        }
        catch (Exception error)
        {
            // 연결 문제가 아닌 예외는 입력 처리나 게임 로직의 결함이다. 기록한 뒤 아래 finally에서 연결을 정리한다.
            // 클라이언트가 보낸 값은 종류를 확인한 뒤 읽으므로, 잘못된 메시지로는 여기까지 오지 않는다.
            string code = "-";
            if (room != null)
            {
                code = room.Match.Code;
            }
            ILogger logger = loggers.CreateLogger("Argus.Server.WebSocket");
            logger.LogError(error, "WebSocket connection failed: {Room}", code);
        }
        finally
        {
            if (room != null && player != null && peer != null)
            {
                host.Detach(room, player, peer);
            }
            if (socket.State == WebSocketState.Open)
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure;
                string reason = "연결 종료";
                if (peer != null && peer.Replaced)
                {
                    status = (WebSocketCloseStatus)4001;
                    reason = "다른 연결로 교체됨";
                }
                try
                {
                    await socket.CloseOutputAsync(status, reason, timeout.Token);
                }
                catch (Exception error) when (error is WebSocketException || error is OperationCanceledException)
                {
                    // 이미 끊어진 소켓에는 종료 메시지를 보낼 수 없다.
                }
            }
        }
    }

    // 연결이 끊겼거나 클라이언트가 잘못된 메시지를 보낸 경우다. 기록하지 않고 연결만 정리한다.
    // InvalidOperationException과 ArgumentException은 넣지 않는다. 목록 범위 초과처럼 게임 로직의 결함도
    // 이 종류로 나오므로, 넣으면 결함이 기록 없이 사라진다.
    private static bool IsConnectionError(Exception error)
    {
        return error is WebSocketException ||
            error is OperationCanceledException ||
            error is JsonException;
    }

    private static async Task<JsonDocument?> ReadMessage(WebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                throw new JsonException("Message too large");
            }

            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(length), token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new JsonException("Text frames required");
            }

            length += result.Count;
            if (result.EndOfMessage)
            {
                return JsonDocument.Parse(buffer.AsMemory(0, length));
            }
        }
    }

    private static async Task Receive(WebSocket socket, Room room, Player player, Peer peer, CancellationToken token)
    {
        DateTime window = DateTime.UtcNow;
        int count = 0;
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using JsonDocument? document = await ReadMessage(socket, token);
            if (document == null)
            {
                return;
            }
            if ((DateTime.UtcNow - window).TotalSeconds >= 1)
            {
                window = DateTime.UtcNow;
                count = 0;
            }
            count++;
            if (count > 90)
            {
                throw new JsonException("Input rate exceeded");
            }

            JsonElement root = document.RootElement;
            lock (room.Gate)
            {
                if (player.Connection != peer.Connection)
                {
                    return;
                }

                string type = Text(root, "type");
                if (type == "input")
                {
                    // 숫자가 아닌 값에 TryGetInt64를 부르면 예외가 나므로 종류부터 확인한다.
                    if (root.TryGetProperty("seq", out JsonElement sequence) && sequence.ValueKind == JsonValueKind.Number &&
                        sequence.TryGetInt64(out long seq))
                    {
                        int slot = 0;
                        if (root.TryGetProperty("slot", out JsonElement requestedSlot) &&
                            (requestedSlot.ValueKind != JsonValueKind.Number || !requestedSlot.TryGetInt32(out slot)))
                        {
                            continue;
                        }
                        room.Match.Input(player, seq, Number(root, "moveX"), Number(root, "moveY"),
                            Number(root, "aim"), slot);
                    }
                }
                else if (type == "deploy")
                {
                    double x = Number(root, "x", double.NaN);
                    double y = Number(root, "y", double.NaN);
                    if (!room.Match.Deploy(player, x, y))
                    {
                        byte[] message = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            type = "error",
                            message = "해당 위치에는 투입할 수 없습니다. 빈 지면을 선택하세요."
                        }, RoomHost.Json);
                        peer.Control.Writer.TryWrite(new OutgoingMessage(message, -1, false));
                    }
                }
                else if (type == "loadout")
                {
                    SetLoadoutFromMessage(root, room, player);
                }
                else if (type == "ping")
                {
                    byte[] message = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        type = "pong",
                        sent = Number(root, "sent")
                    }, RoomHost.Json);
                    peer.Control.Writer.TryWrite(new OutgoingMessage(message, -1, false));
                }
            }
        }
    }

    private static void SetLoadoutFromMessage(JsonElement root, Room room, Player player)
    {
        if (!root.TryGetProperty("slots", out JsonElement slots) || slots.ValueKind != JsonValueKind.Array || slots.GetArrayLength() != 4)
        {
            return;
        }
        string[] selected = new string[4];
        for (int i = 0; i < selected.Length; i++)
        {
            if (slots[i].ValueKind != JsonValueKind.String)
            {
                return;
            }
            selected[i] = slots[i].GetString() ?? "";
        }
        room.Match.SetLoadout(player, selected);
    }

    private static string Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        string? text = value.GetString();
        if (text == null)
        {
            return "";
        }
        return text;
    }

    private static double Number(JsonElement element, string name, double fallback = 0)
    {
        if (element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out double number))
        {
            return number;
        }
        return fallback;
    }

}
