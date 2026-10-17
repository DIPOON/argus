using System.IO.Compression;
using System.Text.Json;
using Argus.Server.Game;

namespace Argus.Server.Recording;

// 기록 파일에서 읽은 내용이다.
public sealed class RecordingContent
{
    public RecordingHeader Header { get; }
    public List<MatchRecord> Records { get; } = new List<MatchRecord>();
    // 끝까지 읽지 못하고 멈췄다. 서버가 갑자기 꺼졌거나 아직 쓰는 중인 파일이면 마지막 줄이 잘려 있을 수 있다.
    public bool Truncated { get; set; }

    public RecordingContent(RecordingHeader header)
    {
        Header = header;
    }
}

public static class RecordingReader
{
    public static RecordingContent Read(string path)
    {
        // 서버가 아직 쓰고 있는 파일도 읽을 수 있게 쓰기 공유를 허락한다.
        using FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using GZipStream zip = new GZipStream(file, CompressionMode.Decompress);
        using StreamReader reader = new StreamReader(zip);
        string? first = reader.ReadLine();
        if (first == null)
        {
            throw new InvalidDataException("기록 파일에 헤더가 없습니다.");
        }
        RecordingHeader? header = JsonSerializer.Deserialize<RecordingHeader>(first, RecordingFormat.Json);
        if (header == null)
        {
            throw new InvalidDataException("기록 파일의 헤더를 읽지 못했습니다.");
        }
        RecordingContent content = new RecordingContent(header);
        while (true)
        {
            string? line;
            try
            {
                line = reader.ReadLine();
            }
            catch (InvalidDataException)
            {
                // 압축이 중간에 끊겼다. 그때까지 읽은 줄로 재생한다.
                content.Truncated = true;
                break;
            }
            if (line == null)
            {
                break;
            }
            MatchRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<MatchRecord>(line, RecordingFormat.Json);
            }
            catch (JsonException)
            {
                // 마지막 줄이 쓰다 만 채로 잘렸다.
                content.Truncated = true;
                break;
            }
            if (record != null)
            {
                content.Records.Add(record);
            }
        }
        return content;
    }
}
