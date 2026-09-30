using System.Collections.ObjectModel;
using DeskLink.Client.Services;
using DeskLink.Protocol.Pipe;

namespace DeskLink.Client.ViewModels;

/// <summary>文件冲突策略（DESIGN「文件安全」三选项）。</summary>
public enum FileConflictPolicy
{
    Overwrite,
    Rename,
    Skip,
}

public static class FileConflictPolicyExtensions
{
    /// <summary>映射为线上字符串（与 Service 的 FileOpen policy 字段一致）。</summary>
    public static string ToWire(this FileConflictPolicy policy) => policy switch
    {
        FileConflictPolicy.Overwrite => "overwrite",
        FileConflictPolicy.Rename => "rename",
        FileConflictPolicy.Skip => "skip",
        _ => "overwrite",
    };

    public static string ToDisplay(this FileConflictPolicy policy) => policy switch
    {
        FileConflictPolicy.Overwrite => "覆盖",
        FileConflictPolicy.Rename => "重命名",
        FileConflictPolicy.Skip => "跳过",
        _ => "覆盖",
    };
}

/// <summary>冲突策略下拉项（值 + 中文显示）。</summary>
public sealed record FileConflictPolicyOption(FileConflictPolicy Policy, string Display);

/// <summary>文件条目（本地 scope 或远端列表）。</summary>
public sealed class FileEntry
{
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    /// <summary>远端条目用相对路径；本地 scope 根用绝对路径。</summary>
    public required string Path { get; init; }
}

public enum TransferState
{
    Running,
    Paused,
    Completed,
    Failed,
}

/// <summary>一个传输任务（含暂停/继续）。</summary>
public sealed class TransferItem : ObservableObject
{
    private double _progress;
    private TransferState _state = TransferState.Running;
    private string _message = "";
    private bool _progressKnown = true;
    private long _transferredBytes;
    private long _totalBytes;

    public required string Name { get; init; }
    public required bool IsUpload { get; init; }
    public required string LocalPath { get; init; }
    public required string RemotePath { get; init; }
    public FileConflictPolicy Policy { get; init; }

    /// <summary>
    /// 进度百分比（0~100），来自 Service 的 <c>file_progress</c> 真实分块进度。
    ///
    /// 老实现这里是"进行中恒 0、返回时跳 100"的假进度条；现在由
    /// <see cref="FilesViewModel"/> 在等长调用期间轮询填入。
    /// 若始终拿不到（Service 太老 / 没有会话），<see cref="ProgressKnown"/> 会转 false，
    /// 界面改为显示不确定态，而不是假装 0%。
    /// </summary>
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    /// <summary>本机是否真的拿到了分块进度。false 时界面不该把 Progress 当真值展示。</summary>
    public bool ProgressKnown
    {
        get => _progressKnown;
        set
        {
            if (SetProperty(ref _progressKnown, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public long TransferredBytes
    {
        get => _transferredBytes;
        set { if (SetProperty(ref _transferredBytes, value)) OnPropertyChanged(nameof(ProgressText)); }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set { if (SetProperty(ref _totalBytes, value)) OnPropertyChanged(nameof(ProgressText)); }
    }

    /// <summary>进度条右侧的一行真实数字（"12.3 MB / 48.0 MB"），比一条光秃秃的条更可信。</summary>
    public string ProgressText => ProgressKnown
        ? $"{Format(TransferredBytes)} / {Format(TotalBytes)}（{Progress:0.#}%）"
        : "未获取到分块进度";

    internal static string Format(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        int u = -1;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }

    public TransferState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public bool IsPaused => _state == TransferState.Paused;

    public string StateText => _state switch
    {
        TransferState.Running => "进行中",
        TransferState.Paused => "已暂停",
        TransferState.Completed => "完成",
        TransferState.Failed => "失败",
        _ => "",
    };

    /// <summary>
    /// 在途调用的取消源（暂停用）。由 <see cref="FilesViewModel"/> 在传输开始时创建、
    /// 结束（含取消）后清空并释放；null 表示当前没有在途调用可暂停。
    /// </summary>
    internal CancellationTokenSource? Cts { get; set; }
}

/// <summary>
/// 文件页视图模型：浏览授权范围、上传下载、暂停继续、冲突策略。
///
/// 冲突策略由用户在**发起端**选择（DESIGN：策略随 FileOpen 携带，接收端据此决定落盘目标），
/// 因此 <see cref="ConflictPolicy"/> 必须在每次上传/下载时透传给 Service，不能只在 UI 显示。
/// </summary>
public sealed class FilesViewModel : ObservableObject
{
    private readonly IServiceApi _api;

    private FileConflictPolicy _conflictPolicy = FileConflictPolicy.Overwrite;
    private FileEntry? _selectedRemoteEntry;
    private FileEntry? _selectedLocalEntry;
    private string _statusMessage = "";
    private bool _busy;

    public FilesViewModel(IServiceApi api)
    {
        _api = api;

        LoadScopeCommand = new RelayCommand(() => _ = LoadScopeAsync());
        BrowseRemoteCommand = new RelayCommand(() => _ = BrowseRemoteAsync());
        UploadCommand = new RelayCommand(() =>
        {
            if (SelectedLocalEntry is not null && SelectedRemoteEntry is not null)
            {
                _ = UploadAsync(SelectedLocalEntry.Path, SelectedRemoteEntry.Path);
            }
        });
        DownloadCommand = new RelayCommand(() =>
        {
            if (SelectedRemoteEntry is not null && SelectedLocalEntry is not null)
            {
                _ = DownloadAsync(SelectedRemoteEntry.Path, SelectedLocalEntry.Path);
            }
        });
        PauseTransferCommand = new RelayCommand<TransferItem>(
            it =>
            {
                // 暂停 = 取消在途 RPC（Service 端保留 .part 与已确认分块）；
                // RunTransferCoreAsync 捕获取消后把条目标为 Paused。
                if (it?.State == TransferState.Running) it.Cts?.Cancel();
            },
            it => it?.State == TransferState.Running);
        ResumeTransferCommand = new RelayCommand<TransferItem>(
            it =>
            {
                if (it?.State == TransferState.Paused) _ = ResumeAsync(it);
            },
            it => it?.State == TransferState.Paused);
    }

    public RelayCommand LoadScopeCommand { get; }
    public RelayCommand BrowseRemoteCommand { get; }
    public RelayCommand UploadCommand { get; }
    public RelayCommand DownloadCommand { get; }

    /// <summary>暂停指定传输（取消在途调用；Service 侧 .part 保留，继续时续传）。</summary>
    public RelayCommand<TransferItem> PauseTransferCommand { get; }

    /// <summary>继续指定传输（同路径同策略重新发起，Service 从 ack 位图续传）。</summary>
    public RelayCommand<TransferItem> ResumeTransferCommand { get; }

    /// <summary>冲突策略下拉选项（覆盖 / 重命名 / 跳过）。</summary>
    public IReadOnlyList<FileConflictPolicyOption> ConflictPolicies { get; } = new[]
    {
        new FileConflictPolicyOption(FileConflictPolicy.Overwrite, "覆盖"),
        new FileConflictPolicyOption(FileConflictPolicy.Rename, "重命名"),
        new FileConflictPolicyOption(FileConflictPolicy.Skip, "跳过"),
    };

    /// <summary>本地授权范围根目录（来自 Service 的 file_scope；为空表示一律拒绝）。</summary>
    public ObservableCollection<FileEntry> LocalRoots { get; } = new();

    /// <summary>远端授权范围内的条目。</summary>
    public ObservableCollection<FileEntry> RemoteEntries { get; } = new();

    public ObservableCollection<TransferItem> Transfers { get; } = new();

    /// <summary>当前冲突策略（上传/下载时透传）。</summary>
    public FileConflictPolicy ConflictPolicy
    {
        get => _conflictPolicy;
        set
        {
            if (SetProperty(ref _conflictPolicy, value))
            {
                OnPropertyChanged(nameof(ConflictPolicyDisplay));
            }
        }
    }

    public string ConflictPolicyDisplay => _conflictPolicy.ToDisplay();

    public FileEntry? SelectedRemoteEntry
    {
        get => _selectedRemoteEntry;
        set => SetProperty(ref _selectedRemoteEntry, value);
    }

    public FileEntry? SelectedLocalEntry
    {
        get => _selectedLocalEntry;
        set => SetProperty(ref _selectedLocalEntry, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool Busy
    {
        get => _busy;
        private set => SetProperty(ref _busy, value);
    }

    /// <summary>加载本地授权范围。</summary>
    public async Task LoadScopeAsync(CancellationToken ct = default)
    {
        Busy = true;
        try
        {
            var scope = await _api.GetFileScopeAsync(ct);
            LocalRoots.Clear();
            foreach (var root in scope.Roots)
            {
                LocalRoots.Add(new FileEntry
                {
                    Name = System.IO.Path.GetFileName(root.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : root,
                    IsDirectory = true,
                    Path = root,
                });
            }
            StatusMessage = LocalRoots.Count == 0
                ? "未授权任何目录：文件传输一律被拒绝（DESIGN：不提供默认全盘浏览）"
                : $"已授权 {LocalRoots.Count} 个目录";
        }
        catch (PipeUnavailableException)
        {
            StatusMessage = "Service 未运行：无法读取文件授权范围";
        }
        catch (PipeRpcException ex)
        {
            StatusMessage = $"读取授权范围失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>浏览远端目录（路径相对对端授权根）。</summary>
    public async Task BrowseRemoteAsync(string path = "", CancellationToken ct = default)
    {
        Busy = true;
        try
        {
            var result = await _api.ListRemoteFilesAsync(path, ct);
            RemoteEntries.Clear();
            if (!result.Ok)
            {
                StatusMessage = $"列目录失败：{result.Error}";
                return;
            }
            foreach (var e in result.Entries)
            {
                RemoteEntries.Add(new FileEntry
                {
                    Name = e.Name,
                    IsDirectory = e.IsDirectory,
                    Size = e.Size,
                    Path = string.IsNullOrEmpty(path) ? e.Name : $"{path}/{e.Name}",
                });
            }
            StatusMessage = $"远端 {RemoteEntries.Count} 项";
        }
        catch (PipeUnavailableException)
        {
            StatusMessage = "Service 未运行：无法列远端目录";
        }
        catch (PipeRpcException ex)
        {
            StatusMessage = $"列远端目录失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>上传本地文件到远端，冲突策略取当前选择。</summary>
    public Task<TransferItem> UploadAsync(string localPath, string remotePath, CancellationToken ct = default)
        => RunTransferAsync(new TransferItem
        {
            Name = System.IO.Path.GetFileName(localPath),
            IsUpload = true,
            LocalPath = localPath,
            RemotePath = remotePath,
            Policy = ConflictPolicy,
        }, ct);

    /// <summary>从远端下载文件到本地，冲突策略取当前选择。</summary>
    public Task<TransferItem> DownloadAsync(string remotePath, string localPath, CancellationToken ct = default)
        => RunTransferAsync(new TransferItem
        {
            Name = System.IO.Path.GetFileName(remotePath),
            IsUpload = false,
            LocalPath = localPath,
            RemotePath = remotePath,
            Policy = ConflictPolicy,
        }, ct);

    /// <summary>用指定策略上传（测试/批处理用，避免依赖 UI 当前选择）。</summary>
    public Task<TransferItem> UploadWithPolicyAsync(string localPath, string remotePath, FileConflictPolicy policy, CancellationToken ct = default)
        => RunTransferAsync(new TransferItem
        {
            Name = System.IO.Path.GetFileName(localPath),
            IsUpload = true,
            LocalPath = localPath,
            RemotePath = remotePath,
            Policy = policy,
        }, ct);

    private async Task<TransferItem> RunTransferAsync(TransferItem item, CancellationToken ct)
    {
        Transfers.Add(item);
        return await RunTransferCoreAsync(item, ct);
    }

    /// <summary>
    /// 继续一个已暂停的传输：同路径同策略重新发起（条目不重复入列表），
    /// Service 端按 ack 位图只传缺失分块（断点续传，DESIGN「文件安全」）。
    /// </summary>
    public async Task<TransferItem> ResumeAsync(TransferItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.State != TransferState.Paused) return item;

        item.State = TransferState.Running;
        item.Message = "";
        return await RunTransferCoreAsync(item, ct);
    }

    private async Task<TransferItem> RunTransferCoreAsync(TransferItem item, CancellationToken ct)
    {
        Busy = true;

        // 暂停 = 取消在途 RPC（Service 侧按取消处理，保留 .part 与已确认分块）。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        item.Cts = cts;
        item.ProgressKnown = true;

        // 与长调用并行轮询真实进度；传输一结束就停。
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = PollProgressAsync(item, progressCts.Token);

        try
        {
            // 冲突策略在此透传给 Service；不同文件可用不同策略（每次调用各自携带）。
            var result = item.IsUpload
                ? await _api.UploadFileAsync(item.LocalPath, item.RemotePath, item.Policy.ToWire(), cts.Token)
                : await _api.DownloadFileAsync(item.RemotePath, item.LocalPath, item.Policy.ToWire(), cts.Token);

            if (result.Ok)
            {
                item.Progress = 100;
                item.State = TransferState.Completed;
                item.Message = result.Target ?? "";
                item.TransferredBytes = result.Bytes;
                if (item.TotalBytes <= 0) item.TotalBytes = result.Bytes;

                // 传完了就是"全部传完"——这是我们确知的真值，不该因为中途没轮询到分块进度
                // 而在完成瞬间还挂着"未获取到分块进度"。那会让用户以为结果不可信。
                item.ProgressKnown = true;

                StatusMessage = $"{item.Name} 传输完成（{TransferItem.Format(result.Bytes)}）";
            }
            else
            {
                item.State = TransferState.Failed;
                item.Message = result.Error ?? "未知错误";
                StatusMessage = $"{item.Name} 传输失败：{item.Message}";
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // 用户点"暂停"取消在途调用：不是失败，条目进入 Paused，可继续。
            item.State = TransferState.Paused;
            item.Message = "已暂停";
            StatusMessage = $"{item.Name} 已暂停（Service 保留进度，可继续）";
        }
        catch (PipeUnavailableException)
        {
            item.State = TransferState.Failed;
            item.Message = "Service 未运行";
            StatusMessage = "Service 未运行：传输中止";
        }
        catch (PipeRpcException ex)
        {
            item.State = TransferState.Failed;
            item.Message = ex.Message;
            StatusMessage = $"传输失败：{ex.Message}";
        }
        finally
        {
            progressCts.Cancel();
            await progress.ConfigureAwait(false);   // 不留下"孤儿轮询"继续刷进度

            // 取消只是"请求"：还挂在管道上、没被 observe 到的那次 file_progress 会在
            // 上面这次 await 之前完成回写，把 ProgressKnown 又改回 false。
            // 于是"已完成"的条目挂着"未获取到分块进度"——那是在说谎，不是保守。
            // 轮询到这里已经彻底收尾，传输的终态才是最终事实，重新钉回去。
            if (item.State == TransferState.Completed) item.ProgressKnown = true;

            item.Cts = null;
            Busy = false;
        }

        return item;
    }

    /// <summary>
    /// 轮询 Service 的 <c>file_progress</c>，把真实分块进度填回 <paramref name="item"/>。
    ///
    /// 匹配规则：方向 + 对端可见的相对路径。上传是 sending、下载是 receiving，
    /// 路径都等于 <see cref="TransferItem.RemotePath"/>。同路径并发（界面上不会发生，
    /// 因为 Busy 门禁）时取百分比最大的那条。
    ///
    /// 拿不到时把 <see cref="TransferItem.ProgressKnown"/> 置 false——**不假装 0%**。
    /// 那正是本缺陷原来被投诉的地方：一条永远停在 0%、最后跳到 100% 的进度条。
    /// </summary>
    internal async Task PollProgressAsync(TransferItem item, CancellationToken ct)
    {
        var wantSending = item.IsUpload;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _api.GetFileProgressAsync(ct).ConfigureAwait(false);
                var match = snapshot.Transfers
                    .Where(t => string.Equals(t.Direction, wantSending ? "sending" : "receiving", StringComparison.OrdinalIgnoreCase)
                             && string.Equals(Normalize(t.Path), Normalize(item.RemotePath), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(t => t.Percent)
                    .FirstOrDefault();

                if (match is not null)
                {
                    item.Progress = Math.Clamp(match.Percent, 0, 100);
                    item.TransferredBytes = match.TransferredBytes;
                    item.TotalBytes = match.TotalBytes;
                }
                else
                {
                    item.ProgressKnown = false;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // 轮询失败（Service 重启、管道断开、老版本不认识 file_progress）都不该
                // 影响传输本身——传输的结论由长调用的返回值给出。
                item.ProgressKnown = false;
            }

            try { await Task.Delay(ProgressPollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>进度轮询间隔。250ms 足够跟手，又不至于把管道刷爆。</summary>
    public static readonly TimeSpan ProgressPollInterval = TimeSpan.FromMilliseconds(250);

    private static string Normalize(string path) => (path ?? "").Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// 仅切换 UI 状态（不取消在途调用）。供无取消源的场景（已完成/已失败条目重置）；
    /// 真正的暂停请用 <see cref="PauseTransferCommand"/>。
    /// </summary>
    public void MarkPaused(TransferItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.State == TransferState.Running && item.Cts is null) item.State = TransferState.Paused;
    }

    public void MarkResumed(TransferItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.State == TransferState.Paused) item.State = TransferState.Running;
    }
}
