using System.Reflection;
using System.Reflection.Emit;

// 컴파일된 코드(IL)에서 부르는 메서드를 모두 찾아, 기계마다 결과가 다를 수 있는 것을 쓰는지 본다.
// 소스를 글자로 찾으면 using static, double.Sin 같은 형식 메서드, 람다, 문자열 보간 속 호출, 하위 폴더를 놓치기 쉽다.
// IL에는 실제로 부르는 메서드가 그대로 남으므로 어떤 문법으로 썼든 잡는다. 주석과 문자열은 IL에 없으니 따로 뺄 것도 없다.
internal static class PortabilityCheck
{
    // 실수 수학 형식(Math, MathF, double, float)은 이 메서드만 쓸 수 있다. 실수 규칙(IEEE 754)이 결과를 정확히 정해 어디서나 같다.
    // 삼각함수·지수·로그·거듭제곱과 ReciprocalEstimate·MultiplyAddEstimate처럼 CPU에 따라 달라지는 것은 모두 빠진다.
    private static readonly string[] ExactMath = new string[]
    {
        "Abs", "Min", "Max", "Clamp", "Sign", "Floor", "Ceiling", "Round", "Truncate", "Sqrt",
        "IsFinite", "IsNaN", "IsInfinity", "IsPositiveInfinity", "IsNegativeInfinity",
        "Equals", "GetHashCode", "CompareTo", "ToString"
    };

    // 일반화 수학 인터페이스의 연산자와 기본값이다. 사칙연산과 비교라 결과가 정해져 있다.
    private static readonly string[] ExactOperators = new string[]
    {
        "op_Addition", "op_Subtraction", "op_Multiply", "op_Division", "op_Modulus", "op_UnaryNegation", "op_UnaryPlus",
        "op_Increment", "op_Decrement", "op_Equality", "op_Inequality", "op_LessThan", "op_GreaterThan",
        "op_LessThanOrEqual", "op_GreaterThanOrEqual", "get_Zero", "get_One", "get_AdditiveIdentity", "get_MultiplicativeIdentity"
    };

    private static readonly OpCode[] OneByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];

    // IL 명령은 1바이트 또는 0xFE로 시작하는 2바이트다. 명령마다 뒤에 붙는 값의 크기가 달라서 명령표를 만들어 둔다.
    static PortabilityCheck()
    {
        FieldInfo[] fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
        for (int i = 0; i < fields.Length; i++)
        {
            object? value = fields[i].GetValue(null);
            if (value is OpCode code)
            {
                int number = (ushort)code.Value;
                if (code.Size == 1)
                {
                    OneByte[number] = code;
                }
                else
                {
                    TwoByte[number & 0xFF] = code;
                }
            }
        }
    }

    // 이름공간 space에 속한 모든 형식(중첩 형식, 람다와 지역 함수를 담은 컴파일러 형식 포함)에서 걸리는 호출을 모은다.
    // 한 줄은 "부르는 메서드 → 불린 메서드"다. inspected는 살펴본 메서드 수다. 이름공간을 잘못 적어 아무것도 보지 않은 채 통과하지 않게 한다.
    public static List<string> Violations(Assembly assembly, string space, out int inspected)
    {
        List<string> found = new List<string>();
        inspected = 0;
        Type[] types = assembly.GetTypes();
        BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;
        for (int i = 0; i < types.Length; i++)
        {
            Type type = types[i];
            if (type.Namespace != space)
            {
                continue;
            }
            List<MethodBase> methods = new List<MethodBase>();
            methods.AddRange(type.GetMethods(flags));
            methods.AddRange(type.GetConstructors(flags));
            // 정적 필드의 초깃값은 형식 초기화 메서드에서 계산한다.
            ConstructorInfo? initializer = type.TypeInitializer;
            if (initializer != null && !methods.Contains(initializer))
            {
                methods.Add(initializer);
            }
            for (int j = 0; j < methods.Count; j++)
            {
                if (Inspect(methods[j], found))
                {
                    inspected++;
                }
            }
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    // 본문이 있는 메서드를 살펴봤으면 true다.
    private static bool Inspect(MethodBase method, List<string> found)
    {
        MethodBody? body = method.GetMethodBody();
        if (body == null)
        {
            return false;
        }
        byte[]? il = body.GetILAsByteArray();
        if (il == null)
        {
            return false;
        }
        // 제네릭 형식이나 메서드 안의 호출은 형식 인자를 알아야 어떤 메서드인지 정해진다.
        Type[]? typeArguments = null;
        if (method.DeclaringType != null && method.DeclaringType.IsGenericType)
        {
            typeArguments = method.DeclaringType.GetGenericArguments();
        }
        Type[]? methodArguments = null;
        if (method.IsGenericMethod)
        {
            methodArguments = method.GetGenericArguments();
        }
        int position = 0;
        while (position < il.Length)
        {
            OpCode code;
            if (il[position] == 0xFE)
            {
                code = TwoByte[il[position + 1]];
                position += 2;
            }
            else
            {
                code = OneByte[il[position]];
                position += 1;
            }
            // 메서드를 부르거나(call, callvirt), 만들거나(newobj), 대리자로 가리키는(ldftn) 명령은 뒤에 메서드 토큰이 온다.
            if (code.OperandType == OperandType.InlineMethod)
            {
                int token = BitConverter.ToInt32(il, position);
                string? reason = Forbidden(Resolve(method.Module, token, typeArguments, methodArguments));
                if (reason != null)
                {
                    found.Add($"{Describe(method)} → {reason}");
                }
            }
            position += OperandSize(code, il, position);
        }
        return true;
    }

    private static MethodBase? Resolve(Module module, int token, Type[]? typeArguments, Type[]? methodArguments)
    {
        try
        {
            return module.ResolveMethod(token, typeArguments, methodArguments);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // 불린 메서드가 기계마다 결과가 다를 수 있으면 그 이름을, 괜찮으면 null을 돌려준다.
    private static string? Forbidden(MethodBase? callee)
    {
        if (callee == null)
        {
            return "어떤 메서드인지 알 수 없는 호출";
        }
        Type? owner = callee.DeclaringType;
        if (owner == null)
        {
            return null;
        }
        string ownerName = owner.Namespace + "." + owner.Name;
        string name = callee.Name;
        string full = ownerName + "." + name;
        if (ownerName == "System.Math" || ownerName == "System.MathF" || ownerName == "System.Double" ||
            ownerName == "System.Single" || ownerName == "System.Half")
        {
            if (Array.IndexOf(ExactMath, name) >= 0)
            {
                return null;
            }
            return full;
        }
        // 일반화 수학(T.Sin(x) 같은 호출)은 IL에서 System.Numerics의 인터페이스 메서드(ITrigonometricFunctions`1.Sin,
        // INumberBase`1.MultiplyAddEstimate 등)를 부른다. 소유 형식이 double이 아니어도 같은 계산이므로 같은 허용 목록으로 본다.
        if (owner.IsInterface && owner.Namespace == "System.Numerics")
        {
            if (Array.IndexOf(ExactMath, name) >= 0 || Array.IndexOf(ExactOperators, name) >= 0)
            {
                return null;
            }
            return full;
        }
        // 실수 벡터(Vector2 등)·행렬·하드웨어 가속 형식은 하나도 쓰지 않는다. 좌표와 거리는 고정소수점(FixedVector)으로 계산한다.
        // Vector2.Lerp처럼 곱셈과 덧셈을 한 번에(FMA) 하는지가 CPU에 따라 달라지는 연산도 있다. 정수 연산인 BitOperations는 여기에 들지 않는다.
        if (ownerName.StartsWith("System.Numerics.Vector", StringComparison.Ordinal) ||
            ownerName.StartsWith("System.Numerics.Matrix", StringComparison.Ordinal) ||
            ownerName == "System.Numerics.Quaternion" || ownerName == "System.Numerics.Plane" ||
            ownerName == "System.Numerics.Complex" ||
            ownerName.StartsWith("System.Runtime.Intrinsics", StringComparison.Ordinal))
        {
            return full;
        }
        // 시드 없는 난수와 시계는 실행할 때마다 달라진다.
        if (ownerName == "System.Random" || ownerName == "System.Diagnostics.Stopwatch" ||
            ownerName == "System.Security.Cryptography.RandomNumberGenerator" || ownerName == "System.TimeProvider")
        {
            return full;
        }
        if ((ownerName == "System.DateTime" || ownerName == "System.DateTimeOffset") &&
            (name == "get_Now" || name == "get_UtcNow" || name == "get_Today"))
        {
            return full;
        }
        if (ownerName == "System.Environment" && (name == "get_TickCount" || name == "get_TickCount64"))
        {
            return full;
        }
        if (ownerName == "System.Guid" && name == "NewGuid")
        {
            return full;
        }
        return null;
    }

    // 명령 뒤에 붙는 값의 바이트 수다.
    private static int OperandSize(OpCode code, byte[] il, int position)
    {
        OperandType type = code.OperandType;
        if (type == OperandType.InlineNone)
        {
            return 0;
        }
        if (type == OperandType.ShortInlineBrTarget || type == OperandType.ShortInlineI || type == OperandType.ShortInlineVar)
        {
            return 1;
        }
        if (type == OperandType.InlineVar)
        {
            return 2;
        }
        if (type == OperandType.InlineI8 || type == OperandType.InlineR)
        {
            return 8;
        }
        if (type == OperandType.InlineSwitch)
        {
            int count = BitConverter.ToInt32(il, position);
            return 4 + count * 4;
        }
        return 4;
    }

    private static string Describe(MethodBase method)
    {
        string owner = "?";
        if (method.DeclaringType != null)
        {
            owner = method.DeclaringType.FullName ?? method.DeclaringType.Name;
        }
        return owner + "." + method.Name;
    }
}
