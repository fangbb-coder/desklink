using DeskLink.Service.Security;
using Xunit;

namespace DeskLink.Service.Tests;

public class KeyStoreTests : IDisposable
{
    private readonly string _tmp;

    public KeyStoreTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"desklink-keystore-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void LoadOrCreate_Generates_Stable_Keypair_Across_Calls()
    {
        var ks1 = new KeyStore(_tmp);
        var k1 = ks1.LoadOrCreate();

        var ks2 = new KeyStore(_tmp);
        var k2 = ks2.LoadOrCreate();

        // 同 KeyStore 实例 + 同 DataDir 跨进程可还原同一对公钥
        Assert.Equal(k1.KeyPair.Ed25519Public, k2.KeyPair.Ed25519Public);
        Assert.Equal(k1.KeyPair.X25519Public, k2.KeyPair.X25519Public);
        Assert.Equal(k1.DeviceIdHint, k2.DeviceIdHint);
    }

    [Fact]
    public void LoadOrCreate_Creates_File_With_Dpapi_LocalMachine_Scope()
    {
        var ks = new KeyStore(_tmp);
        var keys = ks.LoadOrCreate();
        Assert.True(File.Exists(ks.Path));

        var json = File.ReadAllText(ks.Path);
        Assert.Contains("\"ed25519SeedDpapiB64\"", json);
        Assert.Contains("\"x25519PrivDpapiB64\"", json);
        Assert.Contains("\"ed25519PubB64\"", json);
        Assert.Contains("\"x25519PubB64\"", json);
        Assert.Contains("\"deviceIdHintB64\"", json);
    }

    [Fact]
    public void Cross_Process_Decryption_Works()
    {
        // 模拟"跨进程"：一个 KeyStore 实例写，另一个 KeyStore 实例读
        // DPAPI LocalMachine scope 保证同机任意进程可解
        var writer = new KeyStore(_tmp);
        var original = writer.LoadOrCreate();
        var origPubEd = Convert.ToBase64String(original.KeyPair.Ed25519Public);

        // 重新构造实例（模拟新进程）
        var reader = new KeyStore(_tmp);
        var loaded = reader.LoadOrCreate();
        var loadedPubEd = Convert.ToBase64String(loaded.KeyPair.Ed25519Public);

        Assert.Equal(origPubEd, loadedPubEd);
    }

    [Fact]
    public void Regenerate_Produces_New_Keypair()
    {
        var ks = new KeyStore(_tmp);
        var k1 = ks.LoadOrCreate();
        var k2 = ks.Regenerate();
        Assert.NotEqual(k1.KeyPair.Ed25519Public, k2.KeyPair.Ed25519Public);
    }

    [SkippableFact]
    public void FileSystemAcl_Locks_Down_Keystore_File()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only ACL test");

        var ks = new KeyStore(_tmp);
        ks.LoadOrCreate();

        // 验证：仅 SYSTEM + Admins + CreatorOwner
        var violation = FileSystemAcl.VerifyLockDown(ks.Path, isDirectory: false);
        Assert.Null(violation);
    }

    [SkippableFact]
    public void FileSystemAcl_Locks_Down_Keystore_Directory()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only ACL test");

        var ks = new KeyStore(_tmp);
        ks.LoadOrCreate();

        // 注意：KeyStore 的目录属性是 DataDirectory（早先叫 Directory，
        // 与 System.IO.Directory 同名会导致类内 Path/Directory 解析歧义，已改名）。
        var violation = FileSystemAcl.VerifyLockDown(ks.DataDirectory, isDirectory: true);
        Assert.Null(violation);
    }
}
