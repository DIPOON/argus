using System.Net;
using System.Net.Sockets;

namespace Argus.Server.Networking;

// 접속 주소로 참가 제한과 주소별 방 수 상한을 셀 단위를 정한다.
// IPv6 사용자는 보통 /64 대역을 통째로 받아 요청마다 주소를 바꿀 수 있으므로, 두 제한 모두 /64를 한 주소로 센다.
// IPv6에 담긴 IPv4 주소(::ffff:1.2.3.4)는 IPv4 주소로 센다.
public static class AddressKeys
{
    // 참가 제한에 쓰는 단위다. 모든 주소에 건다. 주소를 모르면 하나로 묶는다.
    public static string ForRateLimit(IPAddress? address)
    {
        if (address == null)
        {
            return "local";
        }
        return Group(Normalize(address));
    }

    // 주소별 방 수 상한에 쓰는 단위다. 같은 기계와 사설망에서 온 접속은 상한에서 빼고 null을 돌려준다.
    // 개발과 브라우저 검사는 한 주소에서 방을 계속 만들고, Docker로 띄우면 모든 요청이 브리지 주소에서 온다.
    // 그래서 이 주소들까지 세면 방이 4개 남은 동안 모든 사용자의 방 생성이 막힌다.
    public static string? ForRoomLimit(IPAddress? address)
    {
        if (address == null)
        {
            return null;
        }
        IPAddress normalized = Normalize(address);
        if (IsLocalNetwork(normalized))
        {
            return null;
        }
        return Group(normalized);
    }

    private static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }
        return address;
    }

    // IPv4는 주소 하나, IPv6는 앞 64비트(/64 대역)를 단위로 한다.
    private static string Group(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }
        byte[] bytes = address.GetAddressBytes();
        for (int i = 8; i < bytes.Length; i++)
        {
            bytes[i] = 0;
        }
        return new IPAddress(bytes).ToString() + "/64";
    }

    // 같은 기계(루프백)와 사설망 주소인지 본다. IPv4는 10/8, 172.16/12, 192.168/16, 링크 로컬 169.254/16이고,
    // IPv6는 고유 지역 주소 fc00::/7과 링크 로컬 fe80::/10이다.
    private static bool IsLocalNetwork(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal;
        }
        byte[] bytes = address.GetAddressBytes();
        if (bytes[0] == 10)
        {
            return true;
        }
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        {
            return true;
        }
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return true;
        }
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
