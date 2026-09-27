using System.IO;
using System.Text;
using DeskLink.Client.Security;
using Xunit;

namespace DeskLink.Client.Tests;

/// <summary>
/// 对端指纹 TOFU 存储的语义测试（含"公钥变更必须重新确认"这条安全红线）。
/// </summary>
public class PeerFingerprintStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public PeerFingerprintStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "desklink-fp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "client-fingerprints.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private static string Pub(byte seed) => Convert.ToBase64String(new[] { seed, seed, seed, seed });

    [Fact]
    public void First_Use_Requires_Confirmation()
    {
        var store = new PeerFingerprintStore(_path);
        Assert.True(store.NeedsConfirmation(Pub(1)));
        Assert.Null(store.GetConfirmedFingerprint(Pub(1)));
    }

    [Fact]
    public void After_Confirm_Same_Key_Is_AutoApproved()
    {
        var store = new PeerFingerprintStore(_path);
        store.Confirm(Pub(1));

        Assert.False(store.NeedsConfirmation(Pub(1)));
        Assert.NotNull(store.GetConfirmedFingerprint(Pub(1)));
    }

    [Fact]
    public void Changed_PublicKey_Requires_ReConfirmation()
    {
        var store = new PeerFingerprintStore(_path);
        store.Confirm(Pub(1));

        // 对端换了一把公钥 → 必须再次确认，绝不自动放行（TOFU 的核心）。
        Assert.True(store.NeedsConfirmation(Pub(2)));

        // 且确认新公钥后，旧公钥不会被"顺带"认为已确认。
        store.Confirm(Pub(2));
        Assert.False(store.NeedsConfirmation(Pub(2)));
        Assert.False(store.NeedsConfirmation(Pub(1)));
    }

    [Fact]
    public void Confirm_Persists_Across_Store_Instances()
    {
        var first = new PeerFingerprintStore(_path);
        first.Confirm(Pub(7));
        var fingerprint = first.GetConfirmedFingerprint(Pub(7));

        // 全新实例（模拟重启客户端）必须能读回同一份信任。
        var second = new PeerFingerprintStore(_path);
        Assert.False(second.NeedsConfirmation(Pub(7)));
        Assert.Equal(fingerprint, second.GetConfirmedFingerprint(Pub(7)));
    }

    [Fact]
    public void Atomic_Write_Leaves_No_Tmp_File_Behind()
    {
        var store = new PeerFingerprintStore(_path);
        store.Confirm(Pub(1));
        store.Confirm(Pub(2));

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Corrupt_File_Fails_Safe_To_Empty_Store()
    {
        File.WriteAllText(_path, "{ this is not json");
        var store = new PeerFingerprintStore(_path);

        // 读不出来时按"空信任库"处理：所有对端都要重新确认，绝不因为损坏而放行。
        Assert.True(store.NeedsConfirmation(Pub(1)));
    }

    [Fact]
    public void Clear_Resets_All_Trust()
    {
        var store = new PeerFingerprintStore(_path);
        store.Confirm(Pub(1));
        store.Clear();

        Assert.True(store.NeedsConfirmation(Pub(1)));
        // 清空也要落盘：新实例读回来同样没有任何信任。
        Assert.True(new PeerFingerprintStore(_path).NeedsConfirmation(Pub(1)));
    }

    [Fact]
    public void ComputeFingerprint_Is_Deterministic_And_Grouped()
    {
        var pub = Encoding.UTF8.GetBytes("ed25519-public-key");

        var fp1 = PeerFingerprintStore.ComputeFingerprint(pub);
        var fp2 = PeerFingerprintStore.ComputeFingerprint(pub);

        Assert.Equal(fp1, fp2);
        // SHA-256 = 32 字节 → 64 个十六进制字符，每 2 字节（4 字符）一组，共 16 组，组间空格。
        Assert.Equal(16, fp1.Split(' ').Length);
        Assert.All(fp1.Split(' '), g => Assert.Equal(4, g.Length));
        Assert.All(fp1.Where(c => c != ' '), c => Assert.True(Uri.IsHexDigit(c)));

        // 不同输入 → 不同指纹。
        Assert.NotEqual(fp1, PeerFingerprintStore.ComputeFingerprint(Encoding.UTF8.GetBytes("other")));
    }

    [Fact]
    public void NeedsConfirmation_Treats_Empty_Key_As_Unconfirmed()
    {
        var store = new PeerFingerprintStore(_path);
        Assert.True(store.NeedsConfirmation(""));
        Assert.True(store.NeedsConfirmation("   "));
    }
}
