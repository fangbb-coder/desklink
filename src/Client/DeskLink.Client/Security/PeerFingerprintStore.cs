using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskLink.Client.Security;

/// <summary>
/// 对端设备公钥指纹的 TOFU（Trust On First Use）存储——**仅用于中继路径**。
///
/// 为什么"公钥变更必须重新确认"（TOFU 的核心语义）：
///   首次连接时用户确认的是"当前这把公钥属于对方设备"。若日后同一对端返回一把
///   不同的公钥，只可能是两种情况——对方重装/换设备，或有人在中间替换了公钥
///   （MITM）。二者对客户端而言无法区分，因此**绝不能自动放行**，必须重新弹指纹
///   让用户判断。若此处图省事按"设备名/ID"复用旧信任，就正好把 TOFU 降级成
///   "首次信任后无条件信任"，MITM 防线形同虚设。
///   本实现以**公钥本身**为键：换了公钥就是"没见过的键"，自然需要重新确认。
///
/// 局域网直连**不使用**本存储：配对已是公钥信任的根来源，直连直接复用已配对公钥
/// （见 DESIGN「设备身份和权限」与「传输路径」表）。
///
/// 落盘：<c>%APPDATA%\DeskLink\client-fingerprints.json</c>，原子写（临时文件 + File.Replace），
/// 避免写到一半崩溃留下半截 JSON 把整份信任库弄坏。
/// </summary>
public sealed class PeerFingerprintStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, Entry> _entries;

    /// <param name="path">显式路径（测试用）。为空则用 %APPDATA%\DeskLink\client-fingerprints.json。</param>
    public PeerFingerprintStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
        _entries = Load();
    }

    public string FilePath => _path;

    /// <summary>默认信任库路径。</summary>
    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "DeskLink", "client-fingerprints.json");
    }

    /// <summary>
    /// 计算对端 Ed25519 公钥的指纹：SHA-256 → 十六进制（大写）→ 每 4 个字符一组用空格分隔。
    /// 分组只是为了让用户肉眼比对时不易看错；比对语义仍是完整的 32 字节摘要。
    /// </summary>
    public static string ComputeFingerprint(byte[] ed25519Pub)
    {
        ArgumentNullException.ThrowIfNull(ed25519Pub);
        var hash = SHA256.HashData(ed25519Pub);
        return FormatHex(hash);
    }

    /// <summary>把字节摘要格式化为分组大写十六进制。</summary>
    public static string FormatHex(ReadOnlySpan<byte> digest)
    {
        var sb = new System.Text.StringBuilder(digest.Length * 3);
        for (int i = 0; i < digest.Length; i++)
        {
            if (i > 0 && i % 2 == 0) sb.Append(' ');
            sb.Append(digest[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>该公钥是否尚未确认（true = 需要弹指纹对话框）。</summary>
    public bool NeedsConfirmation(string peerPubB64)
    {
        if (string.IsNullOrWhiteSpace(peerPubB64)) return true;
        lock (_gate)
        {
            return !_entries.ContainsKey(peerPubB64);
        }
    }

    /// <summary>记录"用户已确认这把公钥"，并立即原子落盘。</summary>
    public void Confirm(string peerPubB64)
    {
        if (string.IsNullOrWhiteSpace(peerPubB64)) throw new ArgumentException("peerPubB64 不能为空", nameof(peerPubB64));

        lock (_gate)
        {
            _entries[peerPubB64] = new Entry
            {
                Fingerprint = FingerprintOfB64(peerPubB64),
                ConfirmedUtc = DateTime.UtcNow,
            };
            Save();
        }
    }

    /// <summary>取出已记录的指纹（未确认返回 null）。</summary>
    public string? GetConfirmedFingerprint(string peerPubB64)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(peerPubB64, out var e) ? e.Fingerprint : null;
        }
    }

    /// <summary>清空信任库（排障用：怀疑被 MITM 时重置 TOFU）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries = new Dictionary<string, Entry>();
            Save();
        }
    }

    private static string FingerprintOfB64(string peerPubB64)
    {
        try
        {
            return ComputeFingerprint(Convert.FromBase64String(peerPubB64));
        }
        catch (FormatException)
        {
            // 公钥不是合法 base64 时仍要能存（否则确认动作会崩）；用原始字符串摘要兜底。
            return ComputeFingerprint(System.Text.Encoding.UTF8.GetBytes(peerPubB64));
        }
    }

    private Dictionary<string, Entry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, Entry>();
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, Entry>();
            var doc = JsonSerializer.Deserialize<StoreFile>(json, JsonOpts);
            return doc?.Peers ?? new Dictionary<string, Entry>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 信任库损坏/不可读时**安全侧失败**：当作空库（所有对端都要重新确认），
            // 而不是崩溃，也绝不因为读不出来就放行任何对端。
            return new Dictionary<string, Entry>();
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(new StoreFile { Peers = _entries }, JsonOpts);
        var tmp = _path + ".tmp";

        // 原子写：先写临时文件并 flush 到盘，再替换目标。
        // 直接覆盖目标文件时若中途断电会留下半截 JSON，下次启动整库读不出来。
        File.WriteAllText(tmp, json);
        try
        {
            if (File.Exists(_path))
            {
                File.Replace(tmp, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, _path);
            }
        }
        finally
        {
            // File.Replace/Move 成功后会移除 tmp；异常路径下清理，确保不留 .tmp。
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* 清理失败不掩盖主异常 */ }
            }
        }
    }

    public sealed class Entry
    {
        [JsonPropertyName("fingerprint")]
        public string Fingerprint { get; set; } = "";

        [JsonPropertyName("confirmed_utc")]
        public DateTime ConfirmedUtc { get; set; }
    }

    private sealed class StoreFile
    {
        [JsonPropertyName("peers")]
        public Dictionary<string, Entry> Peers { get; set; } = new();
    }
}
