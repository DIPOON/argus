using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Argus.Server.Game;

// 상태를 정해진 순서의 바이트로 적어 SHA-256 하나로 요약한다. 두 진행이 같은 상태인지 비교할 때 쓴다.
// 실수는 반올림하지 않고 비트 그대로 적는다. 마지막 자리 하나만 달라도 요약이 달라져야 어긋난 곳을 놓치지 않는다.
// 숫자는 기계와 관계없이 리틀 엔디언으로 적는다.
public sealed class StateHasher : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _buffer = new byte[8];
    // 기본 설정으로 고정한다. 공개 속성을 선언 순서대로 쓰고, 실수는 원래 값으로 되돌릴 수 있는 가장 짧은 표기로 쓴다.
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions();

    // 규칙처럼 값이 많은 객체를 정해진 방식의 JSON 바이트로 바꾼다. 값을 하나씩 적으면 나중에 추가한 값을 빠뜨리기 쉽다.
    public static byte[] ToJson<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
    }

    public void AddInt(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_buffer, value);
        _hash.AppendData(_buffer, 0, 4);
    }

    public void AddLong(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_buffer, value);
        _hash.AppendData(_buffer, 0, 8);
    }

    public void AddBool(bool value)
    {
        if (value)
        {
            AddInt(1);
        }
        else
        {
            AddInt(0);
        }
    }

    public void AddDouble(double value)
    {
        AddLong(BitConverter.DoubleToInt64Bits(value));
    }

    // 고정소수점 좌표는 정수 Raw를 그대로 적는다.
    public void AddPoint(FixedVector value)
    {
        AddLong(value.X.Raw);
        AddLong(value.Y.Raw);
    }

    // 길이를 먼저 적는다. 그러지 않으면 "ab"+"c"와 "a"+"bc"가 같은 바이트가 된다.
    public void AddText(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AddInt(bytes.Length);
        _hash.AppendData(bytes);
    }

    public void AddBytes(byte[] value)
    {
        AddInt(value.Length);
        _hash.AppendData(value);
    }

    public void AddBoolArray(bool[] values)
    {
        AddInt(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            AddBool(values[i]);
        }
    }

    public void AddIntArray(int[] values)
    {
        AddInt(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            AddInt(values[i]);
        }
    }

    public void AddDoubleArray(double[] values)
    {
        AddInt(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            AddDouble(values[i]);
        }
    }

    public void AddTextArray(string[] values)
    {
        AddInt(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            AddText(values[i]);
        }
    }

    // 값이 없는 경우와 0이나 빈 문자열인 경우를 구분하려고 있는지 여부를 먼저 적는다.
    public void AddOptionalText(string? value)
    {
        AddBool(value != null);
        if (value != null)
        {
            AddText(value);
        }
    }

    public void AddOptionalLong(long? value)
    {
        AddBool(value.HasValue);
        if (value.HasValue)
        {
            AddLong(value.Value);
        }
    }

    public void AddOptionalInt(int? value)
    {
        AddBool(value.HasValue);
        if (value.HasValue)
        {
            AddInt(value.Value);
        }
    }

    // HashSet은 순회 순서를 약속하지 않으므로 정렬해서 적는다.
    public void AddIntSet(HashSet<int> values)
    {
        int[] sorted = new int[values.Count];
        values.CopyTo(sorted);
        Array.Sort(sorted);
        AddInt(sorted.Length);
        for (int i = 0; i < sorted.Length; i++)
        {
            AddInt(sorted[i]);
        }
    }

    public void AddTextSet(HashSet<string> values)
    {
        string[] sorted = new string[values.Count];
        values.CopyTo(sorted);
        // 문화권에 따라 순서가 달라지지 않도록 문자 코드 순으로 정렬한다.
        Array.Sort(sorted, StringComparer.Ordinal);
        AddInt(sorted.Length);
        for (int i = 0; i < sorted.Length; i++)
        {
            AddText(sorted[i]);
        }
    }

    // Dictionary도 순회 순서를 약속하지 않으므로 키를 정렬해서 적는다.
    public void AddTextLongMap(Dictionary<string, long> values)
    {
        string[] keys = new string[values.Count];
        values.Keys.CopyTo(keys, 0);
        Array.Sort(keys, StringComparer.Ordinal);
        AddInt(keys.Length);
        for (int i = 0; i < keys.Length; i++)
        {
            AddText(keys[i]);
            AddLong(values[keys[i]]);
        }
    }

    // 지금까지 적은 내용의 요약을 16진수 문자열로 돌려준다.
    public string Finish()
    {
        byte[] digest = _hash.GetHashAndReset();
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public void Dispose()
    {
        _hash.Dispose();
    }
}
