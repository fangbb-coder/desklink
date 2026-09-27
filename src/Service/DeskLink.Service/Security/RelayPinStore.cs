// 中继 TLS 证书指纹固定存储（TOFU：Trust On First Use）。
//
// 为什么需要它（DESIGN.md P5 验收项"首次对端连接走 TOFU 流程（中继）"）：
//   中继路径上，客户端与 relayd 之间是标准 TLS/QUIC，而证书在自用场景下是自签的
//   （installer/vps/gen-cert.sh 生成）。若像早期实现那样无条件接受任何证书
//   （`RemoteCertificateValidationCallback => true`），那么任何能劫持到该连接的人
//   都可以做中间人——E2E 加密虽然保护业务内容，但 relay 控制层（Hello/Dial、
//   设备挑战签名）会暴露在伪造的 relay 之下，攻击者可据此做流量分析与定向拒绝。
//
//   因此采用 TOFU：**首次**连接时记录证书指纹并信任，之后每次都必须与记录一致。
//   指纹变化 → 拒绝连接（而不是自动更新），避免"证书被换掉后静默接受"。
//   要接受新指纹必须显式清除 pin（运维动作），这是刻意的取舍。
//
// 存储：<data-dir>/relay-pins.json，ACL 与 KeyStore/PairingStore 一致（SYSTEM+Admins）。
//   { "version": 1, "items": [ { "endpoint": "host:port", "sha256B64": "...", "firstSeenUtc": "..." } ] }
//
// 指纹算法：SHA-256 over 证书 DER（即 X509Certificate.RawData）。
//   与 X509Certificate2.Thumbprint 不同——后者是 SHA-1，已不适合作为安全标识。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskLink.Service.Security;

/// <summary>中继端点证书指纹的 TOFU 存储。</summary>
public sealed class RelayPinStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public string Path => _path;

    public RelayPinStore(string dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            throw new ArgumentException("dataDir must not be empty", nameof(dataDir));
        }
        _path = System.IO.Path.Combine(System.IO.Path.GetFullPath(dataDir), "relay-pins.json");
    }

    /// <summary>把 (host, port) 规范化为存储键。主机名统一小写，避免大小写导致漏配。</summary>
    public static string EndpointKey(string host, int port)
        => $"{host.Trim().ToLowerInvariant()}:{port}";

    /// <summary>计算证书的 SHA-256 指纹（Base64）。</summary>
    public static string FingerprintOf(byte[] certDer)
        => Convert.ToBase64String(SHA256.HashData(certDer));

    /// <summary>读取某端点已记录的指纹；无记录返回 null。</summary>
    public string? Get(string host, int port)
    {
        lock (_gate)
        {
            var key = EndpointKey(host, port);
            foreach (var item in Load().Items)
            {
                if (item.Endpoint == key) return item.Sha256B64;
            }
            return null;
        }
    }

    /// <summary>记录首次信任的指纹（已存在则不覆盖——覆盖只允许显式 Clear 后重来）。</summary>
    public void RecordFirstUse(string host, int port, string sha256B64)
    {
        lock (_gate)
        {
            var key = EndpointKey(host, port);
            var doc = Load();
            foreach (var item in doc.Items)
            {
                if (item.Endpoint == key) return; // 已有记录：不覆盖
            }
            doc.Items.Add(new PinEntry
            {
                Endpoint = key,
                Sha256B64 = sha256B64,
                FirstSeenUtc = DateTime.UtcNow,
            });
            Write(doc);
        }
    }

    /// <summary>
    /// 清除某端点的 pin（运维动作）。返回是否确实删除了记录。
    /// 用法：relay 换证书后，若确认新证书可信，清 pin 再重连即可重新 TOFU。
    /// </summary>
    public bool Clear(string host, int port)
    {
        lock (_gate)
        {
            var key = EndpointKey(host, port);
            var doc = Load();
            var removed = doc.Items.RemoveAll(i => i.Endpoint == key) > 0;
            if (removed) Write(doc);
            return removed;
        }
    }

    /// <summary>列出全部 pin（状态查询/诊断用）。</summary>
    public IReadOnlyList<PinEntry> List()
    {
        lock (_gate)
        {
            return Load().Items.ToList();
        }
    }

    private PinsDocument Load()
    {
        if (!File.Exists(_path)) return new PinsDocument();
        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            return JsonSerializer.Deserialize<PinsDocument>(json, JsonOpts) ?? new PinsDocument();
        }
        catch (Exception)
        {
            // 文件损坏时不静默当作"无 pin"——那等于把 TOFU 降级成"接受任何证书"。
            // 抛出去让上层明确失败，由用户决定是修文件还是清 pin。
            throw new InvalidDataException(
                $"relay-pins.json 无法解析（{_path}）；请修复或删除该文件后重试" +
                "（删除等于清空所有 TOFU 记录）");
        }
    }

    private void Write(PinsDocument doc)
    {
        var dir = System.IO.Path.GetDirectoryName(_path)!;
        System.IO.Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOpts), new UTF8Encoding(false));
        if (File.Exists(_path))
        {
            File.Replace(tmp, _path, null);
        }
        else
        {
            File.Move(tmp, _path);
        }
        FileSystemAcl.LockDownFile(_path);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed class PinEntry
    {
        /// <summary>"host:port"（host 已小写）。</summary>
        public string Endpoint { get; set; } = "";
        /// <summary>证书 DER 的 SHA-256，Base64。</summary>
        public string Sha256B64 { get; set; } = "";
        public DateTime FirstSeenUtc { get; set; }
    }

    private sealed class PinsDocument
    {
        public int Version { get; set; } = 1;
        public List<PinEntry> Items { get; set; } = new();
    }
}
