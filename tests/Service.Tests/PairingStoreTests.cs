using DeskLink.Service.Security;
using Xunit;

namespace DeskLink.Service.Tests;

public class PairingStoreTests : IDisposable
{
    private readonly string _tmp;

    public PairingStoreTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"desklink-pairing-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    [Fact]
    public void Add_Then_List_Returns_Pairing()
    {
        var store = new PairingStore(_tmp);
        var pub = new byte[32];
        for (int i = 0; i < 32; i++) pub[i] = (byte)i;

        store.Add(pub, "alpha");

        var list = store.List();
        Assert.Single(list);
        Assert.Equal("alpha", list[0].Label);
        Assert.Equal(Convert.ToBase64String(pub), list[0].PeerPubB64);
    }

    [Fact]
    public void Add_Twice_Same_Pub_Replaces_Label()
    {
        var store = new PairingStore(_tmp);
        var pub = new byte[32];
        store.Add(pub, "alpha");
        store.Add(pub, "alpha-renamed");
        Assert.Single(store.List());
        Assert.Equal("alpha-renamed", store.List()[0].Label);
    }

    [Fact]
    public void IsPaired_Returns_True_For_Added_Pub()
    {
        var store = new PairingStore(_tmp);
        var pub = new byte[32];
        pub[5] = 0xAB;
        store.Add(pub, "alpha");
        Assert.True(store.IsPaired(pub));
        Assert.False(store.IsPaired(new byte[32]));
    }

    [Fact]
    public void Remove_Deletes_Pairing()
    {
        var store = new PairingStore(_tmp);
        var pub = new byte[32];
        store.Add(pub, "alpha");
        Assert.True(store.Remove(pub));
        Assert.False(store.IsPaired(pub));
        Assert.False(store.Remove(pub)); // 二次移除返回 false
    }

    [Fact]
    public void Persistence_Across_New_Store_Instance()
    {
        var store1 = new PairingStore(_tmp);
        var pub = new byte[32];
        pub[3] = 7;
        store1.Add(pub, "persist");

        var store2 = new PairingStore(_tmp);
        Assert.True(store2.IsPaired(pub));
        Assert.Equal("persist", store2.List()[0].Label);
    }
}
