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
        // 역방향 프록시 뒤에서는 전달 헤더를 처리하도록 설정해야 사용자마다 따로 센다(CLAUDE.md).
        return RateLimitPartition.GetFixedWindowLimiter(AddressKeys.ForRateLimit(context.Connection.RemoteIpAddress),
            CreateEntryLimiterOptions);
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

    private static IResult CreateRoom(EntryRequest request, RoomHost host, HttpContext context)
    {
        EntryFailure failure;
        Guest? guest = host.Create(request, AddressKeys.ForRoomLimit(context.Connection.RemoteIpAddress), out failure);
        if (guest != null)
        {
            return Results.Json(guest);
        }
        // 방을 만든 직후 참가에 실패한 경우다. 지금 규칙으로는 생기지 않지만 서버가 가득 찼다고 잘못 알리지 않는다.
        if (failure == EntryFailure.Unavailable)
        {
            return Results.Json(new { error = "작전을 만들지 못했습니다. 잠시 뒤 다시 시도하세요." }, statusCode: 500);
        }
        return EntryError(failure);
    }

    private static IResult JoinRoom(string code, EntryRequest request, RoomHost host)
    {
        EntryFailure failure;
        Guest? guest = host.Join(code, request, out failure);
        if (guest != null)
        {
            return Results.Json(guest);
        }
        return EntryError(failure);
    }

    // 방 만들기와 참가가 실패한 이유를 HTTP 응답으로 바꾼다. 장비 검증은 RoomHost가 한 곳에서 한다.
    private static IResult EntryError(EntryFailure failure)
    {
        if (failure == EntryFailure.InvalidRequest)
        {
            return Results.Json(new { error = "선택할 수 없는 장비입니다." }, statusCode: 400);
        }
        if (failure == EntryFailure.AddressFull)
        {
            return Results.Json(new { error = "이 주소에서 연 작전이 너무 많습니다. 이전 작전이 정리된 뒤 다시 시도하세요." }, statusCode: 429);
        }
        if (failure == EntryFailure.ServerFull)
        {
            return Results.Json(new { error = "서버의 작전 공간이 가득 찼습니다." }, statusCode: 503);
        }
        return Results.Json(new { error = "방이 없거나, 가득 찼거나, 종료된 작전입니다." }, statusCode: 409);
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
                // 상대의 종료 응답은 1초까지만 기다린다. 무기한 기다리면 종료에 답하지 않는 클라이언트가
                // 참가 정보 없이 연결을 붙잡아 둘 수 있다. keep-alive가 TCP 연결을 계속 살려 둔다.
                using CancellationTokenSource closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                closeTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "참가 정보가 만료되었습니다", closeTimeout.Token);
                return;
            }
            room = attached.Value.Room;
            player = attached.Value.Player;
            peer = attached.Value.Peer;

            // 입력 수신과 상태 전송을 함께 시작하고, 어느 한쪽이 끝나면 연결을 정리한다.
            Task receive = Receive(socket, room, player, peer, lifetime.Token);
            Task send = peer.Send(socket, lifetime.Token);
            try
            {
                await Task.WhenAny(receive, send);
                // 수신이 아직 기다리는 중이면 송신 쪽이 먼저 끝난 것이다. 연결 교체나 방 닫힘처럼 서버가 먼저 닫는 경우다.
                // 종료 메시지를 먼저 보내고 상대의 응답을 기다린다. 기다리는 수신을 그냥 취소하면 소켓이 중단되어
                // 상대는 정상 종료 대신 1006을 받는다.
                if (!receive.IsCompleted)
                {
                    WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure;
                    string reason = "연결 종료";
                    if (peer.Replaced)
                    {
                        status = ReplacedStatus;
                        reason = ReplacedReason;
                    }
                    await CloseFirst(socket, receive, send, status, reason, lifetime.Token);
                }
            }
            finally
            {
                // 어느 길로 나가든 송수신 작업을 멈추고 끝날 때까지 기다린다. 기다리지 않고 나가면 소켓이 해제된 뒤에도
                // 수신 작업이 남아 있다가 아무도 받지 않는 예외로 끝난다.
                await lifetime.CancelAsync();
                try
                {
                    await Task.WhenAll(receive, send);
                }
                catch (Exception error) when (IsConnectionError(error))
                {
                    // 위에서 취소한 송수신 작업이 끝날 때 발생할 수 있다.
                }
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
            // 클라이언트가 먼저 닫았으면(CloseReceived) 종료 응답을 보내 닫기를 마친다. 응답 없이 소켓을 해제하면
            // 상대는 정상 종료 대신 비정상 종료(1006)를 받고, 이를 네트워크 장애로 보는 클라이언트는 다시 접속한다.
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure;
                string reason = "연결 종료";
                if (peer != null && peer.Replaced)
                {
                    status = ReplacedStatus;
                    reason = ReplacedReason;
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

    // 다른 연결로 교체된 연결을 닫을 때의 종료 코드와 이유다. 클라이언트는 4001을 받으면 다시 접속하지 않는다.
    private const WebSocketCloseStatus ReplacedStatus = (WebSocketCloseStatus)4001;
    private const string ReplacedReason = "다른 연결로 교체됨";

    // 서버가 먼저 닫을 때 종료 메시지를 보낸다. 수신을 먼저 취소하면 소켓이 바로 중단되어 정상 종료 대신
    // 비정상 종료(1006)로 보이므로, 남은 전송을 마치고 종료 메시지를 보낸 뒤 상대의 종료 응답까지 기다린다.
    // 1초 안에 끝나지 않으면 그만둔다.
    private static async Task CloseFirst(WebSocket socket, Task receive, Task send, WebSocketCloseStatus status, string reason,
        CancellationToken token)
    {
        using CancellationTokenSource closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        closeTimeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            try
            {
                await send.WaitAsync(closeTimeout.Token);
            }
            catch (WebSocketException)
            {
                // 마지막 전송이 실패해도 소켓이 아직 열려 있으면 아래에서 종료 코드를 보내 본다.
            }
            if (socket.State != WebSocketState.Open)
            {
                return;
            }
            await socket.CloseOutputAsync(status, reason, closeTimeout.Token);
            await receive.WaitAsync(closeTimeout.Token);
            if (socket.State == WebSocketState.CloseSent)
            {
                await socket.CloseAsync(status, reason, closeTimeout.Token);
            }
        }
        catch (Exception error) when (IsConnectionError(error))
        {
            // 종료 메시지가 늦어지거나 연결이 이미 끊겼어도 연결 정리는 계속한다.
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
