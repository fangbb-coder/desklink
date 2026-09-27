// 设备密钥持久化：Ed25519 + X25519 长期密钥对。
//
// 存储格式（keystore.json）：
//   {
//     "version": 1,
//     "createdUtc": "2026-09-23T10:00:00Z",
//     "ed25519_seed_dpapi_b64":  "<DPAPI(LocalMachine) over 32B ed25519 seed, Base64>",
//     "x25519_priv_dpapi_b64":   "<DPAPI(LocalMachine) over 32B x25519 priv seed, Base64>",
//     "ed25519_pub_b64":         "<32B ed25519 pub>",
//     "x25519_pub_b64":          "<32B x25519 pub>",
//     "device_id_hint_b64":      "<16B stable hint>"
//   }
//
// 约束（DESIGN.md / 私有工作约定）：
//   - DPAPI 必须用 DataProtectionScope.LocalMachine：
//       Windows 服务（LocalSystem）+ console 模式（用户态）能跨进程解密。
//   - 文件 ACL：SYSTEM + Administrators only（FileSystemAcl.LockDownFile）。
//   - 原子写入：临时文件 → File.Replace → 不留半成品。
//   - 不存私钥明文；任何泄露面仅限本机 LocalMachine scope。
//   - 重装不可恢复（DPAPI 在系统重装后无法解密，DESIGN.md 风险回顾 #5）。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskLink.Protocol.Handshake;

namespace DeskLink.Service.Security;

public sealed class KeyStore
{
    private readonly string _path;
    private readonly string _dir;
    private readonly object _gate = new();

    public string Path => _path;
    public string DataDirectory => _dir;

    public KeyStore(string dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            throw new ArgumentException("dataDir must not be empty", nameof(dataDir));
        }
        _dir = System.IO.Path.GetFullPath(dataDir);
        _path = System.IO.Path.Combine(_dir, "keystore.json");
    }

    /// <summary>
    /// 确保数据目录存在、ACL 收紧、keystore.json 可读写。
    /// 不在此处生成密钥（让 LoadOrCreate 控制生成时机）。
    /// </summary>
    public void EnsureDataDir()
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(_dir);
            var result = FileSystemAcl.LockDownDirectory(_dir);
            if (OperatingSystem.IsWindows() && !result.Applied && result.SkippedReason == "missing")
            {
                // 极端竞态：刚 CreateDirectory 又被外部删了；不必 fatal，让 LoadOrCreate 报错。
            }
        }
    }

    /// <summary>
    /// 读取设备密钥对；不存在则生成并持久化。
    /// 线程安全：同一进程内串行；跨进程由文件级 fs.WriteThrough/Replace 序列化。
    /// </summary>
    public DeviceKeys LoadOrCreate()
    {
        lock (_gate)
        {
            EnsureDataDir();
            if (File.Exists(_path))
            {
                return Read();
            }
            var fresh = DeviceKeys.Generate();
            Write(fresh);
            return fresh;
        }
    }

    /// <summary>
    /// 强制重新生成（设备撤销场景；DESIGN.md P4 验收"重启密钥稳定"明确禁止此路径被默认调用）。
    /// </summary>
    public DeviceKeys Regenerate()
    {
        lock (_gate)
        {
            EnsureDataDir();
            var fresh = DeviceKeys.Generate();
            Write(fresh);
            return fresh;
        }
    }

    private DeviceKeys Read()
    {
        var json = File.ReadAllText(_path, Encoding.UTF8);
        var doc = JsonSerializer.Deserialize<KeystoreDocument>(json, JsonOpts)
            ?? throw new InvalidDataException($"keystore corrupt: {_path}");

        if (doc.Version != 1)
        {
            throw new InvalidDataException($"keystore version {doc.Version} unsupported");
        }

        var edSeed = ProtectedData.Unprotect(Convert.FromBase64String(doc.Ed25519SeedDpapiB64), null, DataProtectionScope.LocalMachine);
        var xSeed = ProtectedData.Unprotect(Convert.FromBase64String(doc.X25519PrivDpapiB64), null, DataProtectionScope.LocalMachine);

        var pair = HandshakeKeys.FromSeeds(edSeed, xSeed);

        // 校验持久化的公钥与种子推导一致（防篡改）
        var pubEd = Convert.FromBase64String(doc.Ed25519PubB64);
        var pubX = Convert.FromBase64String(doc.X25519PubB64);
        if (!pubEd.AsSpan().SequenceEqual(pair.Ed25519Public))
        {
            throw new CryptographicException("keystore ed25519 pub mismatch with seed");
        }
        if (!pubX.AsSpan().SequenceEqual(pair.X25519Public))
        {
            throw new CryptographicException("keystore x25519 pub mismatch with seed");
        }

        var hint = Convert.FromBase64String(doc.DeviceIdHintB64);
        return new DeviceKeys(pair, hint, DateTime.UtcNow);
    }

    private void Write(DeviceKeys keys)
    {
        var edBytes = ProtectedData.Protect(keys.KeyPair.Ed25519Seed, null, DataProtectionScope.LocalMachine);
        var xBytes = ProtectedData.Protect(keys.KeyPair.X25519PrivateSeed, null, DataProtectionScope.LocalMachine);

        var doc = new KeystoreDocument
        {
            Version = 1,
            CreatedUtc = DateTime.UtcNow,
            Ed25519SeedDpapiB64 = Convert.ToBase64String(edBytes),
            X25519PrivDpapiB64 = Convert.ToBase64String(xBytes),
            Ed25519PubB64 = Convert.ToBase64String(keys.KeyPair.Ed25519Public),
            X25519PubB64 = Convert.ToBase64String(keys.KeyPair.X25519Public),
            DeviceIdHintB64 = Convert.ToBase64String(keys.DeviceIdHint),
        };

        // 原子写入：写临时文件 → File.Replace（DESIGN.md P6 原子改名同模式）
        var tmp = _path + ".tmp";
        var json = JsonSerializer.Serialize(doc, JsonOpts);
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(_path))
        {
            // File.Replace 在 Windows 上是原子操作（同卷）
            File.Replace(tmp, _path, null);
        }
        else
        {
            File.Move(tmp, _path);
        }

        // 收紧文件 ACL
        FileSystemAcl.LockDownFile(_path);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class KeystoreDocument
    {
        public int Version { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Ed25519SeedDpapiB64 { get; set; } = "";
        public string X25519PrivDpapiB64 { get; set; } = "";
        public string Ed25519PubB64 { get; set; } = "";
        public string X25519PubB64 { get; set; } = "";
        public string DeviceIdHintB64 { get; set; } = "";
    }
}

/// <summary>
/// 设备密钥对 + 16 字节 device_id_hint（DESIGN.md P1 协议层字段预留）。
/// 包装 HandshakeKeys.DeviceKeyPair 以便携带 hint 与创建时间。
/// </summary>
public sealed class DeviceKeys
{
    public HandshakeKeys.DeviceKeyPair KeyPair { get; }
    public byte[] DeviceIdHint { get; }
    public DateTime CreatedUtc { get; }

    public DeviceKeys(HandshakeKeys.DeviceKeyPair pair, byte[] deviceIdHint, DateTime createdUtc)
    {
        if (deviceIdHint.Length != 16)
        {
            throw new ArgumentException("deviceIdHint must be 16 bytes", nameof(deviceIdHint));
        }
        KeyPair = pair;
        DeviceIdHint = (byte[])deviceIdHint.Clone();
        CreatedUtc = createdUtc;
    }

    public static DeviceKeys Generate()
    {
        var pair = HandshakeKeys.Generate();
        var hint = new byte[16];
        RandomNumberGenerator.Fill(hint);
        // 第一字节固定为版本，便于将来多版本共存时区分
        hint[0] = 0x01;
        return new DeviceKeys(pair, hint, DateTime.UtcNow);
    }
}
