using Argus.Server.Game;

namespace Argus.Server.Recording;

// 방마다 기록 파일을 만들고, 오래된 파일을 지운다. 일반 기록은 최근 MaxFiles개까지 남긴다.
// 제보가 들어온 기록(표시 파일이 있는 기록)은 일반 기록에 밀려 지워지지 않도록 따로 세어 최근 MaxFiles개까지 남긴다.
// 제보를 끝없이 남기지 않는 것은 제보만 반복해 디스크를 채우지 못하게 하려는 것이다.
// 기록에는 방의 비밀 시드가 들어 있으므로 이 폴더를 웹으로 내보내지 않는다.
public sealed class RecordingStore
{
    private readonly ILogger<RecordingStore> _logger;
    private readonly object _gate = new object();
    // 아직 쓰고 있는 기록 파일이다. 정리할 때 건드리지 않는다.
    private readonly List<RecordingFile> _open = new List<RecordingFile>();
    // 폴더를 만들지 못한 것처럼 기록할 수 없는 상태를 한 번만 알리려고 둔다.
    private bool _warned;

    public string Folder { get; }
    public int MaxFiles { get; }

    public RecordingStore(string folder, int maxFiles, ILogger<RecordingStore> logger)
    {
        Folder = folder;
        MaxFiles = maxFiles;
        _logger = logger;
    }

    // 방의 기록 파일을 연다. 기록할 수 없으면(폴더 권한 등) 알리고 null을 돌려준다. 그래도 방은 그대로 만들어진다.
    public RecordingFile? Open(string room, byte[] seed, Rules rules)
    {
        lock (_gate)
        {
            if (MaxFiles <= 0)
            {
                return null;
            }
            try
            {
                Directory.CreateDirectory(Folder);
                Prune();
                DateTime created = DateTime.UtcNow;
                // 이름이 만든 시각으로 시작하므로 이름 순서가 곧 만든 순서다.
                string name = created.ToString("yyyyMMdd-HHmmss-fff") + "-" + room + RecordingFormat.Extension;
                RecordingHeader header = new RecordingHeader();
                header.Version = RecordingFormat.CodeVersion;
                header.Room = room;
                header.Seed = Convert.ToHexString(seed);
                header.Rules = rules;
                header.Created = created;
                RecordingFile file = new RecordingFile(Path.Combine(Folder, name), header, _logger, Released);
                _open.Add(file);
                return file;
            }
            catch (Exception error)
            {
                if (!_warned)
                {
                    _warned = true;
                    _logger.LogError(error, "Cannot write recordings to {Folder}", Folder);
                }
                return null;
            }
        }
    }

    // 지금 쓰고 있는 기록이 모두 다 쓰일 때까지 기다린다. 서버를 끌 때 쓴다.
    public Task WhenAllWritten()
    {
        List<Task> tasks = new List<Task>();
        lock (_gate)
        {
            for (int i = 0; i < _open.Count; i++)
            {
                tasks.Add(_open[i].Completion);
            }
        }
        return Task.WhenAll(tasks);
    }

    private void Released(RecordingFile file)
    {
        lock (_gate)
        {
            _open.Remove(file);
        }
    }

    // 새 기록을 하나 더하기 전에 일반 기록을 MaxFiles - 1개로, 제보된 기록을 MaxFiles개로 줄인다. 쓰고 있는 파일은 건드리지 않는다.
    private void Prune()
    {
        string[] files = Directory.GetFiles(Folder, "*" + RecordingFormat.Extension);
        Array.Sort(files, StringComparer.Ordinal);
        List<string> normal = new List<string>();
        List<string> reported = new List<string>();
        for (int i = 0; i < files.Length; i++)
        {
            if (IsOpen(files[i]))
            {
                continue;
            }
            if (File.Exists(files[i] + RecordingFormat.ReportedSuffix))
            {
                reported.Add(files[i]);
            }
            else
            {
                normal.Add(files[i]);
            }
        }
        Delete(normal, normal.Count - (MaxFiles - 1));
        Delete(reported, reported.Count - MaxFiles);
    }

    private bool IsOpen(string path)
    {
        for (int i = 0; i < _open.Count; i++)
        {
            if (_open[i].Path == path)
            {
                return true;
            }
        }
        return false;
    }

    // 앞(오래된 것)부터 count개를 지운다. 표시 파일도 함께 지운다.
    private void Delete(List<string> files, int count)
    {
        for (int i = 0; i < count && i < files.Count; i++)
        {
            File.Delete(files[i]);
            File.Delete(files[i] + RecordingFormat.ReportedSuffix);
        }
    }
}
