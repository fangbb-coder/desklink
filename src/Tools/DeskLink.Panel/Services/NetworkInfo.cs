// DeskLink.Panel —— 本机网络信息
//
// 被控端要把「IP:端口」告诉控制端，面板得能自己算出这个 IP，而不是让用户去 ipconfig 里抄。
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DeskLink.Panel.Services;

public static class NetworkInfo
{
    /// <summary>
    /// 猜测本机的局域网 IPv4。优先私有网段（192.168 / 10 / 172.16-31），
    /// 跳过回环与 169.254 自动配置地址。返回 null 表示没找到可用的局域网地址。
    /// </summary>
    public static string? GuessLanIPv4()
    {
        var candidates = EnumerateIPv4();
        return candidates.FirstOrDefault(IsPrivate) ?? candidates.FirstOrDefault();
    }

    /// <summary>列出所有处于 Up 状态的非回环 IPv4（按私有优先排序）。</summary>
    public static IReadOnlyList<string> EnumerateIPv4()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    var s = addr.Address.ToString();
                    if (s.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    list.Add(s);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // 取不到网卡信息不该让面板崩掉：调用方把 null 翻译成"没找到局域网地址，请手填"。
        }

        return list.OrderByDescending(s => IsPrivate(s)).ThenBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>是否是 RFC1918 私有网段。</summary>
    public static bool IsPrivate(string? ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return false;
        var b = a.GetAddressBytes();
        if (b[0] == 10) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        return false;
    }

    /// <summary>本机对外可被访问的「IP:端口」展示串；没有局域网地址时返回空串。</summary>
    public static string DescribeEndpoint(int port)
    {
        var ip = GuessLanIPv4();
        return string.IsNullOrEmpty(ip) ? string.Empty : $"{ip}:{port}";
    }
}
