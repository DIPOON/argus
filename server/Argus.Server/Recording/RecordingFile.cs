using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Argus.Server.Game;

namespace Argus.Server.Recording;

// 방 하나의 기록 파일이다. Match는 방 잠금 안에서 Write를 부르므로, 여기서는 기록을 통로에 넣기만 하고 바로 돌아온다.
// 직렬화와 디스크 쓰기는 따로 도는 작업(WriteLoop)이 맡는다. 디스크가 느려도 게임 진행을 붙잡지 않는다.
public sealed class RecordingFile : IMatchRecorder
{
    // 쓰지 못하고 쌓인 기록이 이만큼 넘으면 이 방의 기록을 그만둔다. 디스크가 멈춰도 메모리가 끝없이 늘지 않게 한다.
    // 입력이 초당 30번씩 들어오는 대원 넷이면 30분 남짓 쌓여야 닿는 양이다.
    private const int MaxPending = 200_000;
    private readonly Channel<MatchRecord> _queue;
    private readonly RecordingHeader _header;
    private readonly ILogger _logger;
    private readonly Action<RecordingFile> _released;
    // 방 잠금 안에서만 바꾸고 읽는다.
    private bool _closed;
    // 쓰는 작업이 실패하면 켠다. 다른 스레드가 읽으므로 volatile로 둔다.
    private volatile bool _failed;
    // 통로에 넣었지만 아직 쓰지 않은 기록 수다. 읽는 쪽을 하나로 정한 통로는 개수를 세 주지 않아 직접 센다.
    private int _pending;
    // 쓰는 작업만 읽고 쓴다. 이 작업이 파일을 만들었는지, 대원이 한 번이라도 접속했는지다.
    private bool _created;
    private bool _connected;

    public string Path { get; }
    // 쓰는 작업이 끝나면 완료된다. 서버를 끌 때 기록이 다 쓰일 때까지 기다리는 데 쓴다.
    public Task Completion { get; }

    // 아직 기록을 받고 있는지다. 닫았거나 쓰기에 실패했으면 false다. 방 잠금 안에서 읽는다.
    // 쓰기 실패는 따로 도는 작업이 알아채므로, 막 실패한 순간에는 잠깐 true로 남을 수 있다.
    public bool Accepting
    {
        get
        {
            return !_closed && !_failed;
        }
    }

    public RecordingFile(string path, RecordingHeader header, ILogger logger, Action<RecordingFile> released)
    {
        Path = path;
        _header = header;
        _logger = logger;
        _released = released;
        UnboundedChannelOptions options = new UnboundedChannelOptions();
        options.SingleReader = true;
        _queue = Channel.CreateUnbounded<MatchRecord>(options);
        Completion = Task.Run(WriteLoop);
    }

    public void Write(MatchRecord record)
    {
        if (_closed || _failed)
        {
            return;
        }
        if (Interlocked.Increment(ref _pending) > MaxPending)
        {
            _failed = true;
            _logger.LogWarning("Recording stopped because the disk is not keeping up: {Path}", Path);
            _queue.Writer.TryComplete();
            return;
        }
        _queue.Writer.TryWrite(record);
    }

    // 끝 줄(end)을 넣고 통로를 닫는다. 쓰는 작업은 남은 기록을 마저 쓰고 파일을 닫는다. 방 잠금 안에서 부르며 기다리지 않는다.
    public void Close(long tick, string reason)
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        MatchRecord end = new MatchRecord(tick, "end");
        end.Text = reason;
        _queue.Writer.TryWrite(end);
        _queue.Writer.TryComplete();
    }

    private async Task WriteLoop()
    {
        try
        {
            await WriteFile();
        }
        catch (Exception error)
        {
            _failed = true;
            _logger.LogError(error, "Recording failed: {Path}", Path);
        }
        // 한 번도 접속하지 않은 방(HTTP로 참가만 한 방, 만든 직후 참가에 실패한 방)의 기록은 지운다.
        // 다시 볼 일이 없고, 남겨 두면 보관 자리를 차지해 실제로 플레이한 기록을 밀어낸다.
        // 이 작업이 만든 파일만 지운다. 같은 이름의 다른 파일이 있어 만들지 못했다면 그 파일은 건드리지 않는다.
        if (_created && !_connected)
        {
            Delete();
        }
        _released(this);
    }

    // 기록을 파일에 쓴다. 통로가 닫히면 남은 기록을 마저 쓰고, 이 메서드를 나가면서 파일을 닫는다.
    private async Task WriteFile()
    {
        // 쓰는 동안에도 재생 도구가 읽을 수 있게 읽기 공유를 허락한다.
        using FileStream file = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _created = true;
        using GZipStream zip = new GZipStream(file, CompressionLevel.Fastest);
        using StreamWriter writer = new StreamWriter(zip, new UTF8Encoding(false));
        await writer.WriteLineAsync(JsonSerializer.Serialize(_header, RecordingFormat.Json));
        DateTime flushed = DateTime.UtcNow;
        bool reported = false;
        while (await _queue.Reader.WaitToReadAsync())
        {
            while (_queue.Reader.TryRead(out MatchRecord? record))
            {
                Interlocked.Decrement(ref _pending);
                await writer.WriteLineAsync(JsonSerializer.Serialize(record, RecordingFormat.Json));
                if (record.Type == "connect")
                {
                    _connected = true;
                }
                if (record.Type == "report" && !reported)
                {
                    reported = true;
                    // 제보가 들어온 기록은 표시 파일을 남겨 일반 기록과 따로 보관하게 한다.
                    File.WriteAllBytes(Path + RecordingFormat.ReportedSuffix, Array.Empty<byte>());
                }
            }
            // 1초마다 압축 스트림까지 비운다. 서버가 갑자기 꺼져도 그때까지의 기록은 읽을 수 있다.
            // 방은 1초마다 상태 요약값을 남기므로 조용한 방에서도 1초마다 여기까지 온다.
            if (DateTime.UtcNow - flushed >= TimeSpan.FromSeconds(1))
            {
                await writer.FlushAsync();
                await zip.FlushAsync();
                flushed = DateTime.UtcNow;
            }
        }
        await writer.FlushAsync();
    }

    private void Delete()
    {
        try
        {
            File.Delete(Path);
            File.Delete(Path + RecordingFormat.ReportedSuffix);
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Could not delete an unused recording: {Path}", Path);
        }
    }
}
