using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Argus.Server.Game;

// 방의 비밀 시드 하나에서 정해진 순서로 난수를 뽑는다. 같은 시드면 어느 기계에서든 같은 수가 같은 순서로 나온다.
// 순번(0, 1, 2, ...)마다 시드를 키로 HMAC-SHA256을 계산하고, 그 32바이트를 8바이트씩 잘라 쓴다(카운터 방식).
// System.Random 같은 일반 생성기는 출력 몇 개로 내부 상태를 되짚을 수 있다. 그러면 스냅샷에 보이는 적 id만으로
// 안개 속 적이 생기는 자리나 적이 다음에 고를 행동을 미리 알 수 있다. 이 방식은 시드를 모르면 출력으로 다음 값을 알 수 없다.
public sealed class SeededRandom
{
    public const int SeedLength = 32;
    private const int BlockLength = 32;
    private readonly byte[] _seed;
    private readonly byte[] _counterBytes = new byte[8];
    private readonly byte[] _block = new byte[BlockLength];
    // 다음에 계산할 블록의 순번이다.
    private long _counter;
    // 지금 블록에서 다음에 읽을 위치다. 블록 길이와 같으면 다 쓴 것이다.
    private int _offset = BlockLength;

    public SeededRandom(byte[] seed)
    {
        if (seed.Length != SeedLength)
        {
            throw new ArgumentException($"시드는 {SeedLength}바이트여야 합니다.", nameof(seed));
        }
        // 호출자가 원본 배열을 나중에 바꾸더라도 뽑는 순서가 바뀌지 않게 복사한다.
        _seed = (byte[])seed.Clone();
    }

    // 검사와 재현용이다. 정수 하나를 SHA-256으로 늘려 32바이트 시드를 만든다.
    // 운영 방은 이것을 쓰지 않고 RandomNumberGenerator로 만든 시드를 쓴다. 정수 시드는 쉽게 맞힐 수 있다.
    public static byte[] SeedFromNumber(int number)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, number);
        return SHA256.HashData(bytes);
    }

    // 지금까지 뽑은 양이다. 같은 시드로 같은 만큼 뽑았으면 같은 값이다. 상태 요약에 쓴다.
    public long Position
    {
        get
        {
            return _counter * BlockLength + _offset - BlockLength;
        }
    }

    public ulong NextUInt64()
    {
        if (_offset + 8 > BlockLength)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_counterBytes, _counter);
            HMACSHA256.HashData(_seed, _counterBytes, _block);
            _counter++;
            _offset = 0;
        }
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_block.AsSpan(_offset, 8));
        _offset += 8;
        return value;
    }

    // 0 이상 1 미만의 실수다. 위쪽 53비트로 만들어, 나올 수 있는 값이 모두 같은 간격으로 고르게 나온다.
    public double NextDouble()
    {
        ulong bits = NextUInt64() >> 11;
        return bits * (1.0 / 9007199254740992.0);
    }

    // minimum 이상 maximum 미만의 정수다. 나머지 연산으로 줄이면 작은 수가 조금 더 자주 나오므로,
    // 범위 크기의 배수로 딱 나누어떨어지는 구간 밖의 값은 버리고 다시 뽑는다.
    public int NextInt(int minimum, int maximum)
    {
        if (minimum >= maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }
        ulong range = (ulong)((long)maximum - minimum);
        // 2^64를 range로 나눈 나머지다. ulong.MaxValue + 1이 2^64이므로 이렇게 구한다.
        ulong remainder = (ulong.MaxValue % range + 1) % range;
        ulong limit = ulong.MaxValue - remainder;
        while (true)
        {
            ulong value = NextUInt64();
            if (value <= limit)
            {
                return (int)(minimum + (long)(value % range));
            }
        }
    }
}
