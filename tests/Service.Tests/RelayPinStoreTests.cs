// P5 TOFU 证书指纹持久化测试。
//
// 覆盖场景：
//   1) 首次连接 → 首次记录（RecordFirstUse）→ 后续匹配（Get）。
//   2) 证书轮换（不同指纹）→ Get 返回不同值。
//   3) 多 host 隔离：pins 是按 host:port 分 key 的。
//   4) 重新加载（new 实例）：进程重启后能从文件恢复。
//   5) 并发安全：多线程同时 RecordFirstUse/Get 同一个 host 不会崩溃或产生脏数据。

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeskLink.Service.Relay;
using DeskLink.Service.Security;
using Xunit;
using Xunit.Abstractions;

namespace Service.Tests;

public sealed class RelayPinStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dsk-pin-{Guid.NewGuid():N}");
    private readonly ITestOutputHelper _out;

    public RelayPinStoreTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 测试残骸允许残留 */ }
    }

    [Fact]
    public void RecordFirstUseThenGet_Succeeds()
    {
        var store = new RelayPinStore(_dir);
        var pin = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        store.RecordFirstUse("relay.example.com", 8443, pin);
        var got = store.Get("relay.example.com", 8443);

        Assert.Equal(pin, got);
    }

    [Fact]
    public void Get_UnknownHost_ReturnsNull()
    {
        var store = new RelayPinStore(_dir);
        var got = store.Get("unknown.example.com", 8443);
        Assert.Null(got);
    }

    [Fact]
    public void RecordFirstUse_DifferentPin_ReturnsDifferent()
    {
        var store = new RelayPinStore(_dir);
        var pinA = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var pinB = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        store.RecordFirstUse("relay.example.com", 8443, pinA);
        // RecordFirstUse 对已存在的不覆盖
        store.RecordFirstUse("relay.example.com", 8443, pinB);

        Assert.Equal(pinA, store.Get("relay.example.com", 8443));
    }

    [Fact]
    public void RecordFirstUse_PerHost_Isolated()
    {
        var store = new RelayPinStore(_dir);
        var pin = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        store.RecordFirstUse("relay-a.example.com", 8443, pin);

        Assert.Equal(pin, store.Get("relay-a.example.com", 8443));
        Assert.Null(store.Get("relay-b.example.com", 8443));
    }

    [Fact]
    public void Reload_PersistsAcrossInstances()
    {
        var pin = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var storeA = new RelayPinStore(_dir);
        storeA.RecordFirstUse("relay.example.com", 8443, pin);

        var storeB = new RelayPinStore(_dir);
        Assert.Equal(pin, storeB.Get("relay.example.com", 8443));
    }

    [Fact]
    public void Clear_RemovesPin()
    {
        var store = new RelayPinStore(_dir);
        store.RecordFirstUse("relay.example.com", 8443, "pin1");
        Assert.True(store.Clear("relay.example.com", 8443));
        Assert.Null(store.Get("relay.example.com", 8443));
        Assert.False(store.Clear("relay.example.com", 8443), "再次清除应返回 false");
    }

    [Fact]
    public void List_ReturnsAllEntries()
    {
        var store = new RelayPinStore(_dir);
        store.RecordFirstUse("a.example.com", 8443, "p1");
        store.RecordFirstUse("b.example.com", 8443, "p2");

        var list = store.List();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, e => e.Endpoint == "a.example.com:8443");
        Assert.Contains(list, e => e.Endpoint == "b.example.com:8443");
    }

    [Fact]
    public async Task ConcurrentRecordFirstUse_DoesNotCrash()
    {
        var store = new RelayPinStore(_dir);
        var pin = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        store.RecordFirstUse("relay.example.com", 8443, pin);

        var tasks = new List<Task>();
        for (var i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                store.RecordFirstUse("relay.example.com", 8443, pin);
                _ = store.Get("relay.example.com", 8443);
            }));
        }
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(pin, store.Get("relay.example.com", 8443));
    }
}

public sealed class RelayTrustPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dsk-trust-{Guid.NewGuid():N}");
    private readonly List<string> _logs = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static X509Certificate2 MakeTestCert()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=test-relay", ecdsa, HashAlgorithmName.SHA256);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddHours(1));
    }

    [Fact]
    public void FirstConnect_PinAndAccept()
    {
        var store = new RelayPinStore(_dir);
        var policy = new RelayTrustPolicy(store, msg => _logs.Add(msg));
        var cert = MakeTestCert();

        var decision = policy.Evaluate("relay.example.com", 8443, cert, SslPolicyErrors.None);

        Assert.True(decision.Trusted, "首次连接必须接受");
        Assert.Equal("TOFU first use", decision.Reason);
        Assert.Contains(_logs, m => m.Contains("TOFU 首次信任"));
        // pin 应已写入 store
        Assert.NotNull(store.Get("relay.example.com", 8443));
    }

    [Fact]
    public void SameCert_SecondConnect_Accepts()
    {
        var store = new RelayPinStore(_dir);
        var policy = new RelayTrustPolicy(store, _ => { });
        var cert = MakeTestCert();

        _ = policy.Evaluate("relay.example.com", 8443, cert, SslPolicyErrors.None);
        var decision = policy.Evaluate("relay.example.com", 8443, cert, SslPolicyErrors.None);

        Assert.True(decision.Trusted, "同一证书第二次连接必须接受");
        Assert.Equal("fingerprint match", decision.Reason);
    }

    [Fact]
    public void DifferentCert_Rejected()
    {
        var store = new RelayPinStore(_dir);
        var policy = new RelayTrustPolicy(store, _ => { });

        var certA = MakeTestCert();
        var certB = MakeTestCert();

        _ = policy.Evaluate("relay.example.com", 8443, certA, SslPolicyErrors.None);
        var decision = policy.Evaluate("relay.example.com", 8443, certB, SslPolicyErrors.None);

        Assert.False(decision.Trusted, "证书指纹变更必须拒绝");
        Assert.Contains("不一致", decision.Reason);
    }

    [Fact]
    public void NoCertificate_Rejected()
    {
        var store = new RelayPinStore(_dir);
        var policy = new RelayTrustPolicy(store, _ => { });

        var decision = policy.Evaluate("relay.example.com", 8443, null, SslPolicyErrors.RemoteCertificateNotAvailable);

        Assert.False(decision.Trusted);
        Assert.Contains("未提供证书", decision.Reason);
    }

    [Fact]
    public void AcceptAny_AlwaysTrue()
    {
        var decision = RelayTrustPolicy.AcceptAny.Evaluate(
            "any", 8443, null, SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.True(decision.Trusted);
        Assert.Contains("AcceptAny", decision.Reason);
    }
}
