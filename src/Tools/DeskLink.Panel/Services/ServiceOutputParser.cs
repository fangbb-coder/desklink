// DeskLink.Panel —— Service 一次性命令的输出解析
//
// **全部是纯函数**：GUI 里最难排查的 bug 之一就是"按钮点了没反应"，
// 而根因往往只是一行输出没解析对。把解析独立成纯函数，就能脱离进程与 UI 直接单测。
using System.Text.Json;

namespace DeskLink.Panel.Services;

/// <summary>本机身份（来自 <c>--print-config</c>）。</summary>
public sealed record LocalIdentity(
    string Ed25519PubB64,
    string X25519PubB64,
    string DeviceIdHex,
    string KeyStorePath,
    bool QuicAvailable)
{
    /// <summary>公钥为空 = 没解析出来，不允许拿去配对。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Ed25519PubB64);

    /// <summary>取公钥的前 8 位做展示缩写，方便肉眼比对两台机器配没配对。</summary>
    public string ShortPub =>
        Ed25519PubB64.Length <= 8 ? Ed25519PubB64 : Ed25519PubB64[..8];
}

public static class ServiceOutputParser
{
    /// <summary>
    /// 解析 <c>--print-config</c> 的输出。输出形如：
    /// <code>
    /// ServiceOptions { ... }
    /// KeyStore: C:\...\keystore.json
    ///   ed25519_pub_b64 = AAAA...
    ///   x25519_pub_b64  = BBBB...
    ///   device_id_hint  = CCCC...
    ///   device_id       = DDDD...
    ///   quic_available  = true
    /// </code>
    /// 只认 `key = value` 形式；解析不到公钥时返回 <see cref="LocalIdentity"/> 且
    /// <see cref="LocalIdentity.IsValid"/> 为 false，调用方据此提示而不是拿空串去配对。
    /// </summary>
    public static LocalIdentity ParseIdentity(string? stdout)
    {
        var map = ParseKeyValueLines(stdout);
        map.TryGetValue("ed25519_pub_b64", out var ed);
        map.TryGetValue("x25519_pub_b64", out var x);
        map.TryGetValue("device_id", out var dev);
        map.TryGetValue("KeyStore", out var ks);
        map.TryGetValue("quic_available", out var quic);

        return new LocalIdentity(
            Ed25519PubB64: ed?.Trim() ?? "",
            X25519PubB64: x?.Trim() ?? "",
            DeviceIdHex: dev?.Trim() ?? "",
            KeyStorePath: ks?.Trim() ?? "",
            QuicAvailable: string.Equals(quic?.Trim(), "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>解析 <c>key = value</c> 与 <c>KeyStore: value</c> 两种行。</summary>
    private static Dictionary<string, string> ParseKeyValueLines(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return map;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // KeyStore: <path>
            var colon = line.IndexOf(':');
            if (colon > 0 && line.IndexOf('=') < 0)
            {
                var k = line[..colon].Trim();
                if (k.Length > 0) map[k] = line[(colon + 1)..].Trim();
                continue;
            }

            // key = value（Service 用空格对齐，值里可能还有 '='，故取第一个）
            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                var k = line[..eq].Trim();
                var v = line[(eq + 1)..].Trim();
                if (k.Length > 0) map[k] = v;
            }
        }
        return map;
    }

    /// <summary>解析 <c>--firewall-status</c> 的单行 JSON。解析失败返回 null（不当成"已放行"）。</summary>
    public static FirewallStatus? ParseFirewallStatus(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        // 输出可能前后带日志行，只截取第一个 { ... } 平衡片段。
        var text = stdout.Trim();
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var end = text.LastIndexOf('}');
        if (end <= start) return null;
        var json = text[start..(end + 1)];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new FirewallStatus(
                DirectPort: GetInt(root, "direct_port"),
                DirectEnabled: GetBool(root, "direct_enabled"),
                Elevated: GetBool(root, "elevated"),
                TcpRulePresent: GetBool(root, "rule_tcp_present"),
                UdpRulePresent: GetBool(root, "rule_udp_present"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}

/// <summary>防火墙/直连放行状态（来自 <c>--firewall-status</c>）。</summary>
public sealed record FirewallStatus(
    int DirectPort,
    bool DirectEnabled,
    bool Elevated,
    bool TcpRulePresent,
    bool UdpRulePresent)
{
    /// <summary>TCP 与 UDP 两条规则都齐才算真正放行（直连同时用 QUIC/UDP 和 TCP-TLS）。</summary>
    public bool FullyOpen => TcpRulePresent && UdpRulePresent;
}
