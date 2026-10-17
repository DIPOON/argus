using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Argus.Server.Game;

namespace Argus.Server.Recording;

// 기록 파일의 형식이다. 첫 줄은 헤더(RecordingHeader), 그 뒤로 한 줄에 기록(MatchRecord) 하나씩 JSON으로 적고(JSON Lines),
// 파일 전체를 gzip으로 압축한다. 사람이 풀어서 읽을 수 있고, 줄 단위라 쓰다가 끊겨도 앞부분은 읽힌다.
public static class RecordingFormat
{
    public const string Extension = ".jsonl.gz";
    // 제보가 들어온 기록 옆에 두는 빈 표시 파일의 꼬리다. 표시가 있는 기록은 일반 기록과 따로 보관한다.
    public const string ReportedSuffix = ".reported";
    public static readonly JsonSerializerOptions Json = CreateOptions();

    // 기록한 서버의 코드 버전이다. .NET SDK가 어셈블리 버전 뒤에 git 커밋을 붙인다(1.0.0+커밋).
    // git 정보 없이 빌드하면 커밋이 빠진다.
    public static string CodeVersion { get; } = ReadCodeVersion();

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // 종류마다 쓰지 않는 값은 줄에서 뺀다.
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        // 투입 좌표가 빠진 요청처럼 NaN이 들어올 수 있다. JSON 숫자로 쓸 수 없으므로 "NaN" 같은 문자열로 적고 그대로 읽는다.
        options.NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals;
        return options;
    }

    private static string ReadCodeVersion()
    {
        AssemblyInformationalVersionAttribute? attribute =
            typeof(Match).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (attribute == null)
        {
            return "unknown";
        }
        return attribute.InformationalVersion;
    }
}
