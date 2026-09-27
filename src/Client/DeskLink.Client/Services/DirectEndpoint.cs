namespace DeskLink.Client.Services;

/// <summary>
/// 局域网直连目标（用户在 DirectConnectDialog 里输入的 <c>IP:端口</c>）。
///
/// 纯函数式解析，便于单测覆盖所有边界（空串、缺端口、端口越界、IPv6 方括号形式）。
/// 端口范围取 1..65535：0 是"让 OS 分配临时端口"的语义，对直连目标是非法值。
/// </summary>
public readonly record struct DirectEndpoint(string Host, int Port)
{
    public const int MinPort = 1;
    public const int MaxPort = 65535;

    /// <summary>解析失败时给出人类可读原因（供对话框提示）。</summary>
    public static bool TryParse(string? input, out DirectEndpoint endpoint, out string error)
    {
        endpoint = default;
        error = "";

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "请输入 IP:端口";
            return false;
        }

        var text = input.Trim();

        string host;
        string portText;

        if (text.StartsWith('['))
        {
            // IPv6 方括号形式：[::1]:47200
            var close = text.IndexOf(']');
            if (close < 0)
            {
                error = "IPv6 地址缺少右方括号 ']'";
                return false;
            }
            host = text.Substring(1, close - 1);
            var rest = text[(close + 1)..];
            if (!rest.StartsWith(':'))
            {
                error = "缺少端口（IPv6 形式应为 [地址]:端口）";
                return false;
            }
            portText = rest[1..];
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon < 0)
            {
                error = "缺少端口，格式应为 IP:端口";
                return false;
            }
            host = text[..colon];
            portText = text[(colon + 1)..];
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            error = "IP/主机名为空";
            return false;
        }

        if (!int.TryParse(portText.Trim(), out var port))
        {
            error = $"端口不是合法整数：'{portText}'";
            return false;
        }

        if (port < MinPort || port > MaxPort)
        {
            error = $"端口必须在 {MinPort}..{MaxPort} 之间，收到 {port}";
            return false;
        }

        endpoint = new DirectEndpoint(host.Trim(), port);
        return true;
    }

    /// <summary>解析，失败抛 <see cref="FormatException"/>。</summary>
    public static DirectEndpoint Parse(string? input)
    {
        if (!TryParse(input, out var endpoint, out var error))
        {
            throw new FormatException(error);
        }
        return endpoint;
    }

    /// <summary>回显为可再解析的形式（IPv6 自动补方括号）。</summary>
    public override string ToString()
        => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}
