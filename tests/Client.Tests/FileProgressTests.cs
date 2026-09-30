// 缺陷①「文件传输假进度条」的回归测试。
//
// ## 原来的行为（被投诉的那条）
//
// FilesViewModel 把 Progress 初始化成 0，等上传/下载的**长调用**返回后直接置 100。
// 界面上就是一条永远停在 0%、最后"啪"一下跳满的进度条——它没有反映任何真实分块进度。
//
// ## 现在的约定
//
// 1. 传输进行中，客户端并行轮询 Service 的 `file_progress`，把真实百分比与字节数填回条目。
// 2. **拿不到就明说拿不到**（ProgressKnown=false → 界面走不确定态 + "未获取到分块进度"），
//    绝不停在 0% 装作"在传"。这是本组测试最核心的一条。
// 3. 轮询不能影响传输本身：轮询失败 / 被取消 / 老版本 Service 不认识这个方法，
//    都不许把传输搞挂。
using System.IO;
using DeskLink.Client.Services;
using DeskLink.Client.ViewModels;
using DeskLink.Protocol.Pipe;
using Xunit;

namespace DeskLink.Client.Tests;

/// <summary>
/// 只测 FilesViewModel 对外可见的行为（走 <c>UploadAsync</c> / <c>DownloadAsync</c> 公共入口），
/// 不直接调 internal 的轮询方法——这样测试锁住的是"用户能看见的东西"，
/// 而不是某个私有实现细节。
/// </summary>
public class FileProgressTests
{
    private static FileProgressEntryDto Entry(string direction, string path, long total, long done, double percent) =>
        new()
        {
            TransferId = 1,
            Direction = direction,
            Path = path,
            TotalBytes = total,
            TransferredBytes = done,
            Percent = percent,
            State = "active",
        };

    /// <summary>等轮询至少落一次值。轮询间隔 250ms，给足余量。</summary>
    private static async Task WaitForAsync(Func<bool> until, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (until()) return;
            await Task.Delay(40);
        }
        Assert.True(until(), "等待超时：进度轮询没有在预期时间内把值填回条目");
    }

    [Fact]
    public async Task 上传中_进度取自Service回报的真实分块字节()
    {
        var api = new FakeServiceApi
        {
            TransferGate = new TaskCompletionSource(),
            FileProgressEntries =
            {
                Entry("sending", @"share\big.bin", 1000, 375, 37.5),
            },
        };
        var vm = new FilesViewModel(api);

        var run = vm.UploadAsync("local/big.bin", @"share\big.bin");
        await WaitForAsync(() => vm.Transfers.Count > 0 && vm.Transfers[0].TransferredBytes > 0);

        var item = vm.Transfers[0];
        Assert.True(item.ProgressKnown);
        Assert.Equal(375, item.TransferredBytes);
        Assert.Equal(1000, item.TotalBytes);
        Assert.Equal(37.5, item.Progress, 3);

        api.ReleaseTransfer();
        await run;
    }

    [Fact]
    public async Task 下载中_方向按receiving匹配而不是sending()
    {
        var api = new FakeServiceApi
        {
            TransferGate = new TaskCompletionSource(),
            FileProgressEntries =
            {
                Entry("receiving", @"share\down.bin", 800, 200, 25),
            },
        };
        var vm = new FilesViewModel(api);

        var run = vm.DownloadAsync(@"share\down.bin", "local/down.bin");
        await WaitForAsync(() => vm.Transfers.Count > 0 && vm.Transfers[0].TransferredBytes == 200);

        Assert.Equal(25, vm.Transfers[0].Progress, 3);

        api.ReleaseTransfer();
        await run;
    }

    [Fact]
    public async Task 同名不同方向_不会把上传的进度安到下载头上()
    {
        // 只有一条 sending 记录，而当前在下载：绝不能拿它当自己的进度。
        var api = new FakeServiceApi
        {
            TransferGate = new TaskCompletionSource(),
            FileProgressEntries =
            {
                Entry("sending", @"share\x.bin", 900, 800, 88),
            },
        };
        var vm = new FilesViewModel(api);

        var run = vm.DownloadAsync(@"share\x.bin", "local/x.bin");
        await WaitForAsync(() => vm.Transfers.Count > 0);

        Assert.False(vm.Transfers[0].ProgressKnown);
        Assert.Equal(0, vm.Transfers[0].TransferredBytes);

        api.ReleaseTransfer();
        await run;
    }

    [Fact]
    public async Task Service报不出来进度_转不确定态而不是假装0()
    {
        // 这就是原缺陷的形状：调用没返回，所以条子绝不能靠猜。
        var api = new FakeServiceApi { TransferGate = new TaskCompletionSource() };
        var vm = new FilesViewModel(api);

        var run = vm.UploadAsync("a.bin", "b.bin");
        await WaitForAsync(() => vm.Transfers.Count > 0);

        Assert.False(vm.Transfers[0].ProgressKnown);
        Assert.Equal("未获取到分块进度", vm.Transfers[0].ProgressText);

        api.ReleaseTransfer();
        await run;
    }

    [Fact]
    public async Task 轮询抛异常_不让传输失败只把进度标成未知()
    {
        var api = new FakeServiceApi
        {
            TransferGate = new TaskCompletionSource(),
            FileProgressThrows = true,
        };
        var vm = new FilesViewModel(api);

        var run = vm.UploadAsync("a.bin", "b.bin");
        await WaitForAsync(() => vm.Transfers.Count > 0);
        await Task.Delay(600); // 让轮询至少撞一次异常

        // 传输期间：拿不到就明说拿不到。
        Assert.False(vm.Transfers[0].ProgressKnown);

        api.ReleaseTransfer();
        var item = await run;

        // 传输期间轮询失败绝不能改变传输本身的结论。
        Assert.Equal(TransferState.Completed, item.State);
    }

    [Fact]
    public async Task 传输成功_即使中途没轮询到进度也报已完成字节()
    {
        // 传完了就是全部传完——这是确知的真值。
        // 挂着一句"未获取到分块进度"会让用户以为结果不可信。
        var api = new FakeServiceApi
        {
            TransferGate = new TaskCompletionSource(),
            TransferResult = new FileTransferResultDto { Ok = true, Bytes = 512, Target = "b.bin" },
        };
        var vm = new FilesViewModel(api);

        var run = vm.UploadAsync("a.bin", "b.bin");
        await WaitForAsync(() => vm.Transfers.Count > 0);
        await Task.Delay(400); // 先让轮询跑一轮，把 ProgressKnown 打成 false
        api.ReleaseTransfer();
        var item = await run;

        Assert.True(item.ProgressKnown);
        Assert.Equal(100, item.Progress, 3);
        Assert.Equal("512 B / 512 B（100%）", item.ProgressText);
    }

    [Fact]
    public async Task 传输结束_不会留下还在跑的孤儿轮询()
    {
        var api = new FakeServiceApi { TransferGate = new TaskCompletionSource() };
        var vm = new FilesViewModel(api);

        var run = vm.UploadAsync("a.bin", "b.bin");
        await WaitForAsync(() => vm.Transfers.Count > 0);
        api.ReleaseTransfer();
        await run;

        var after = api.GetFileProgressCallCount;
        await Task.Delay(FilesViewModel.ProgressPollInterval + TimeSpan.FromMilliseconds(400));
        Assert.Equal(after, api.GetFileProgressCallCount);
    }

    [Fact]
    public async Task 传输完成后_ProgressText显示真实字节而不是百分比()
    {
        var api = new FakeServiceApi
        {
            TransferGate = new TaskCompletionSource(),
            TransferResult = new FileTransferResultDto { Ok = true, Bytes = 2048, Target = "b.bin" },
        };
        var vm = new FilesViewModel(api);

        var run = vm.UploadAsync("a.bin", "b.bin");
        await WaitForAsync(() => vm.Transfers.Count > 0);
        api.ReleaseTransfer();
        var item = await run;

        Assert.Equal(2048, item.TransferredBytes);
        Assert.Equal(2048, item.TotalBytes);
        Assert.Contains("2 KB", item.ProgressText);
        Assert.Contains("100%", item.ProgressText);
    }

    [Fact]
    public void 路径分隔符不同_也该匹配上同一次传输()
    {
        // Service 报的是对端可见的相对路径，客户端持有的是本机路径；斜杠形态不一致很常见。
        var api = new FakeServiceApi
        {
            TransferGate = new TaskCompletionSource(),
            FileProgressEntries =
            {
                Entry("sending", "share/sub/deep.bin", 100, 50, 50),
            },
        };
        var vm = new FilesViewModel(api);

        var run = vm.UploadAsync("local", @"share\sub\deep.bin");
        var spin = DateTime.UtcNow.AddSeconds(4);
        while (vm.Transfers.Count == 0 || vm.Transfers[0].TransferredBytes == 0)
        {
            if (DateTime.UtcNow > spin) break;
            Task.Delay(40).GetAwaiter().GetResult();
        }

        Assert.Equal(50, vm.Transfers[0].TransferredBytes);
        api.ReleaseTransfer();
        run.GetAwaiter().GetResult();
    }

    [Fact]
    public void Service给出的百分比超出0到100_被夹住()
    {
        var api = new FakeServiceApi
        {
            TransferGate = new TaskCompletionSource(),
            FileProgressEntries = { Entry("sending", "b.bin", 10, 10, 250) },
        };
        var vm = new FilesViewModel(api);

        var run = vm.UploadAsync("a", "b.bin");
        var spin = DateTime.UtcNow.AddSeconds(4);
        while (vm.Transfers.Count == 0 || !vm.Transfers[0].ProgressKnown)
        {
            if (DateTime.UtcNow > spin) break;
            Task.Delay(40).GetAwaiter().GetResult();
        }

        Assert.InRange(vm.Transfers[0].Progress, 0, 100);
        api.ReleaseTransfer();
        run.GetAwaiter().GetResult();
    }

    [Fact]
    public void 多个匹配项_取百分比最大的那条()
    {
        var api = new FakeServiceApi
        {
            TransferGate = new TaskCompletionSource(),
            FileProgressEntries =
            {
                Entry("sending", "b.bin", 100, 10, 10),
                Entry("sending", "b.bin", 100, 70, 70),
            },
        };
        var vm = new FilesViewModel(api);

        var run = vm.UploadAsync("a", "b.bin");
        var spin = DateTime.UtcNow.AddSeconds(4);
        while (vm.Transfers.Count == 0 || vm.Transfers[0].Progress < 70)
        {
            if (DateTime.UtcNow > spin) break;
            Task.Delay(40).GetAwaiter().GetResult();
        }

        Assert.Equal(70, vm.Transfers[0].Progress, 3);
        api.ReleaseTransfer();
        run.GetAwaiter().GetResult();
    }

    [Fact]
    public void ProgressText_拿不到进度时不出现任何百分比()
    {
        // ProgressText 是界面上唯一会被人读的进度文字，必须明说"没拿到"，
        // 而不是显示 0 B / 0 B / 0% 这种看起来像真值的假象。
        var api = new FakeServiceApi();
        var vm = new FilesViewModel(api);
        var item = new TransferItem { Name = "x", IsUpload = true, LocalPath = "a", RemotePath = "b" };

        // 传输刚建好时是"假定有进度"（正在传），一次没轮询到才转 false。
        item.TransferredBytes = 12345;
        item.TotalBytes = 99999;
        item.Progress = 42;
        item.ProgressKnown = false;

        Assert.Equal("未获取到分块进度", item.ProgressText);
        Assert.DoesNotContain("%", item.ProgressText);
        Assert.DoesNotContain("12345", item.ProgressText);

        item.ProgressKnown = true;
        Assert.Contains("42%", item.ProgressText);
        Assert.Contains("12.1 KB", item.ProgressText);
        Assert.Contains("97.7 KB", item.ProgressText);
    }
}
