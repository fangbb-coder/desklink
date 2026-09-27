// 配对列表持久化（DESIGN.md 设备身份）。
//
// 内容：[{ peer_pub_b64, label, paired_utc }]
//   - peer_pub_b64：32 字节 Ed25519 公钥 Base64
//   - label：用户在配对时给的友好名
//   - paired_utc：配对成功时间
//
// 约束：
//   - 文件 ACL：SYSTEM + Admins（与 KeyStore 一致）
//   - 原子写入：临时文件 → File.Replace
//   - 撤销同时清配对 + 踢线（DESIGN.md 决策 #7）；该路径在 RelayClient / DirectServer 中订阅
//
// P4 范围：
//   - 列表 / 添加 / 删除 / IsPaired
//   - P5 / P5.5 接入签名挑战 + 直连公钥过滤时复用 IsPaired
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskLink.Service.Security;

public sealed class PairingStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public string Path => _path;

    public PairingStore(string dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            throw new ArgumentException("dataDir must not be empty", nameof(dataDir));
        }
        _path = System.IO.Path.Combine(System.IO.Path.GetFullPath(dataDir), "pairings.json");
    }

    public IReadOnlyList<PairingEntry> List()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return Array.Empty<PairingEntry>();
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var doc = JsonSerializer.Deserialize<PairingsDocument>(json, JsonOpts);
            return doc?.Items ?? new List<PairingEntry>();
        }
    }

    public bool IsPaired(ReadOnlySpan<byte> peerPub)
    {
        foreach (var entry in List())
        {
            var pub = Convert.FromBase64String(entry.PeerPubB64);
            if (pub.AsSpan().SequenceEqual(peerPub)) return true;
        }
        return false;
    }

    public void Add(ReadOnlySpan<byte> peerPub, string label)
    {
        if (peerPub.Length != 32) throw new ArgumentException("peer pub must be 32 bytes", nameof(peerPub));
        lock (_gate)
        {
            var list = List().ToList();
            var pubB64 = Convert.ToBase64String(peerPub);
            // 去重
            list.RemoveAll(e => e.PeerPubB64 == pubB64);
            list.Add(new PairingEntry
            {
                PeerPubB64 = pubB64,
                Label = string.IsNullOrWhiteSpace(label) ? "未命名设备" : label.Trim(),
                PairedUtc = DateTime.UtcNow,
            });
            Write(list);
        }
    }

    public bool Remove(ReadOnlySpan<byte> peerPub)
    {
        if (peerPub.Length != 32) throw new ArgumentException("peer pub must be 32 bytes", nameof(peerPub));
        lock (_gate)
        {
            var list = List().ToList();
            var pubB64 = Convert.ToBase64String(peerPub);
            var removed = list.RemoveAll(e => e.PeerPubB64 == pubB64) > 0;
            if (removed) Write(list);
            return removed;
        }
    }

    public int Count => List().Count;

    private void Write(List<PairingEntry> entries)
    {
        var doc = new PairingsDocument { Version = 1, Items = entries };
        var dir = System.IO.Path.GetDirectoryName(_path)!;
        System.IO.Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        var json = JsonSerializer.Serialize(doc, JsonOpts);
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        if (File.Exists(_path))
        {
            File.Replace(tmp, _path, null);
        }
        else
        {
            File.Move(tmp, _path);
        }
        Security.FileSystemAcl.LockDownFile(_path);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed class PairingEntry
    {
        public string PeerPubB64 { get; set; } = "";
        public string Label { get; set; } = "";
        public DateTime PairedUtc { get; set; }
    }

    private sealed class PairingsDocument
    {
        public int Version { get; set; }
        public List<PairingEntry> Items { get; set; } = new();
    }
}
