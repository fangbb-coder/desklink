// 文件传输引擎（P6）。
//
// 职责：在一个已加密的会话（SessionPump 的 File 逻辑流）上，实现 DESIGN.md 要求
// 的双向文件传输：scope 授权、分块、ack 位图驱动的断点续传、暂停/继续/取消、
// BLAKE3 完整性校验、`.part` 临时文件 + 校验通过后原子改名、覆盖/重命名/跳过冲突策略、
// 磁盘满与校验失败的明确失败原因。
//
// 与 SessionPump 的耦合方式：引擎不直接依赖 SessionPump，而是依赖一个极小的
// IFileFrameSink（只负责"把某个类型的负载发到 File 流"）。这样单测可以：
//   - 用内存 sink 把两个引擎对接起来跑完整传输；或
//   - 单独驱动某一侧，精确断言状态机行为。
//
// 线程模型：
//   - OnFrame 由 SessionPump 的读循环调用，**只入队不阻塞**（读循环被阻塞会拖慢整条会话）。
//   - 单个 worker Task 顺序消费入站帧，因此引擎内部状态无需加锁。
//   - 发送循环在各自的任务里跑。
//
// 协议要点（详见 FileTransferFrames 的负载注释）：
//   - 只有 FileAck 位图能作为"对端已收到"的凭据（DESIGN 明确要求）。
//   - 接收端在断点续传时，先根据已存在的 .part 长度回一个初始 FileAck，
//     发送端据此跳过已确认的块 —— 这是"重连后从已确认分块继续"的实现。
//   - 发送端发完最后一块后发 FileComplete（携带整文件 BLAKE3）；
//     接收端若发现缺块，**回 FileAck 而不是状态**，发送端补传后重发 FileComplete。
//
// 两条发起路径：
//   UploadAsync   —— 本端 FileOpen(Push)：本端发、对端收（对端按 policy 落盘）。
//   DownloadAsync —— 本端 FileOpen(Pull)：对端收到后回 FileOpen(Push) 带真实元数据，
//                    本端转为接收端。因为"最终元数据"只有发送端才知道，
//                    本端先用 _pendingPulls 记住本地目标与策略，等 Push 回来再接管。
using System.Collections.Concurrent;
using System.Threading.Channels;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Frames;
using DeskLink.Service.Session;

namespace DeskLink.Service.FileTransfer;

/// <summary>把负载发到 File 逻辑流（SessionPump 适配）。</summary>
public interface IFileFrameSink
{
    Task SendAsync(ProtocolConstants.FrameType type, byte[] payload, CancellationToken ct);
}

/// <summary>传输方向（从本端视角）。</summary>
public enum TransferDirection
{
    Sending,
    Receiving,
}

/// <summary>传输进度快照。</summary>
public readonly record struct FileTransferProgress(
    uint TransferId,
    TransferDirection Direction,
    string Path,
    long TotalBytes,
    long TransferredBytes,
    string State)
{
    public double Percent => TotalBytes <= 0 ? 100 : Math.Round(TransferredBytes * 100.0 / TotalBytes, 2);
}

/// <summary>一次传输的结果。</summary>
public readonly record struct FileTransferOutcome(
    bool Ok,
    uint TransferId,
    string? TargetPath,
    long BytesTransferred,
    string? Error)
{
    public static FileTransferOutcome Fail(uint id, string error) => new(false, id, null, 0, error);
}

/// <summary>目录列举结果。</summary>
public readonly record struct FileListResult(bool Ok, IReadOnlyList<FileEntry> Entries, string? Error);

/// <summary>文件传输引擎。</summary>
public sealed class FileTransferEngine : IAsyncDisposable
{
    /// <summary>每批发送多少块后等待一次 ack（同时是接收端的 ack 节奏）。</summary>
    public const int BatchChunks = 32;

    /// <summary>等待对端 ack / 状态的上限。</summary>
    public static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(60);

    /// <summary>补传重试上限。</summary>
    public const int MaxRetransmitRounds = 3;

    /// <summary>
    /// 等待对端"接受传输"的上限。
    ///
    /// 为什么需要两阶段开启：接收端可能在 FileOpen 阶段就拒绝（scope 越界 / 冲突跳过 /
    /// 磁盘满）。若发送端不先等"接受"就狂发数据，会白白传输整个文件后才发现被拒
    /// （冲突"跳过"策略下尤其荒谬：用户明确选了跳过，却先传完再丢）。
    /// </summary>
    public static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(15);

    private readonly FileTransferScope _scope;
    private readonly IFileTransferIo _io;
    private readonly Action<string>? _log;

    private readonly Channel<InboundFrame> _inbound =
        Channel.CreateUnbounded<InboundFrame>(new UnboundedChannelOptions { SingleReader = true });

    private IFileFrameSink? _sink;
    private Task? _worker;
    private CancellationTokenSource? _cts;
    // 随机起点：避免双端同时发起传输时撞号（见 NextTransferId 注释）。
    private uint _nextTransferId = (uint)Random.Shared.Next(1, int.MaxValue);

    private readonly ConcurrentDictionary<uint, SendTransfer> _sends = new();
    private readonly ConcurrentDictionary<uint, ReceiveTransfer> _receives = new();
    private readonly ConcurrentDictionary<uint, PendingPull> _pendingPulls = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<FileStatusFrame>> _statusWaiters = new();
    private readonly ConcurrentQueue<TaskCompletionSource<FileListResult>> _listWaiters = new();

    /// <summary>
    /// 最近一次传输的最终状态（transferId → 状态）。
    ///
    /// 为什么需要：接收端在回完最终状态后就把传输从 _receives 摘掉了；若发送端
    /// 因为并发到达的 ack 误判为"需要补传"而重发 FileComplete，接收端此时已无该传输，
    /// 若不回任何东西，发送端会白等到超时并误报失败。缓存最终状态即可幂等应答。
    /// </summary>
    private readonly ConcurrentDictionary<uint, FileStatusFrame> _lastStatus = new();

    /// <summary>进度变化通知。</summary>
    public event Action<FileTransferProgress>? OnProgress;

    /// <summary>当前活跃传输数（供状态上报）。</summary>
    public int ActiveTransfers => _sends.Count + _receives.Count;

    public FileTransferEngine(FileTransferScope scope, IFileTransferIo? io = null, Action<string>? log = null)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _io = io ?? RealFileTransferIo.Instance;
        _log = log;
    }

    public FileTransferScope Scope => _scope;

    /// <summary>绑定到一条会话的 File 流并启动 worker。</summary>
    public void Attach(IFileFrameSink sink, CancellationToken external = default)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    /// <summary>SessionPump 的 File 流处理器：只入队。</summary>
    public void OnFrame(InboundFrame frame) => _inbound.Writer.TryWrite(frame);

    // ────────────────────────────────────────────────────────────────────────
    // 对外 API
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>列出对端授权范围内的目录。</summary>
    public async Task<FileListResult> ListAsync(string relativePath, CancellationToken ct = default)
    {
        var sink = RequireSink();
        var tcs = new TaskCompletionSource<FileListResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _listWaiters.Enqueue(tcs);
        try
        {
            await sink.SendAsync(ProtocolConstants.FrameType.FileListRequest,
                FileTransferFrames.EncodeListRequest(relativePath), ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(PeerTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new FileListResult(false, Array.Empty<FileEntry>(), "对端未响应目录请求（超时）");
        }
    }

    /// <summary>把本机 scope 内的文件推给对端（upload）。</summary>
    public async Task<FileTransferOutcome> UploadAsync(
        string localRelativePath,
        string remoteRelativePath,
        FileConflictPolicy policy,
        CancellationToken ct = default)
    {
        var sink = RequireSink();
        if (!_scope.TryResolve(localRelativePath, out var localPath, out var scopeErr))
        {
            return FileTransferOutcome.Fail(0, scopeErr!);
        }
        if (!_io.FileExists(localPath))
        {
            return FileTransferOutcome.Fail(0, $"本地文件不存在：{localRelativePath}");
        }

        var size = _io.GetFileLength(localPath);
        var layout = ChunkLayout.Create(size);
        var digest = _io.HashFile(localPath);
        var id = NextTransferId();

        var transfer = new SendTransfer(id, localPath, remoteRelativePath, layout, digest);
        // accept waiter 必须在 FileOpen 发出之前注册（防丢失唤醒，见 AcceptWaiter 注释）。
        if (!_statusWaiters.TryAdd(id, transfer.AcceptWaiter))
        {
            return FileTransferOutcome.Fail(id, "transfer id conflict");
        }
        _sends[id] = transfer;
        try
        {
            await sink.SendAsync(ProtocolConstants.FrameType.FileOpen,
                FileTransferFrames.EncodeOpen(new FileOpenFrame(
                    FileTransferDirection.Push, id, layout.ChunkSize, size, digest, remoteRelativePath,
                    (byte)policy)),
                ct).ConfigureAwait(false);

            var outcome = await RunSenderAsync(transfer, ct).ConfigureAwait(false);
            return outcome;
        }
        finally
        {
            _sends.TryRemove(id, out _);
            _statusWaiters.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// 向对端请求一个文件并写入本机 scope（download）。
    /// 冲突策略作用于**本机落盘目标**。
    /// </summary>
    public async Task<FileTransferOutcome> DownloadAsync(
        string remoteRelativePath,
        string localRelativePath,
        FileConflictPolicy policy,
        CancellationToken ct = default)
    {
        var sink = RequireSink();
        if (!_scope.TryResolve(localRelativePath, out var localPath, out var scopeErr))
        {
            return FileTransferOutcome.Fail(0, scopeErr!);
        }

        var id = NextTransferId();
        var pending = new PendingPull(localPath, policy);
        _pendingPulls[id] = pending;
        try
        {
            await sink.SendAsync(ProtocolConstants.FrameType.FileOpen,
                FileTransferFrames.EncodeOpen(new FileOpenFrame(
                    FileTransferDirection.Pull, id, ChunkLayout.DefaultChunkSize, 0,
                    new byte[FileTransferFrames.DigestSize], remoteRelativePath, (byte)policy)),
                ct).ConfigureAwait(false);

            return await pending.Completion.Task.WaitAsync(PeerTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return FileTransferOutcome.Fail(id, "对端未在超时内完成传输");
        }
        finally
        {
            _pendingPulls.TryRemove(id, out _);
        }
    }

    /// <summary>暂停一条传输（本端发起）。</summary>
    public void Pause(uint transferId)
    {
        if (_sends.TryGetValue(transferId, out var s))
        {
            s.Paused = true;
            _ = TrySendStatusAsync(transferId, FileTransferStatus.Paused, "paused by user");
        }
    }

    /// <summary>继续一条传输。</summary>
    public void Resume(uint transferId)
    {
        if (_sends.TryGetValue(transferId, out var s))
        {
            s.Paused = false;
            s.ResumeSignal.Pulse();
            _ = TrySendStatusAsync(transferId, FileTransferStatus.Resumed, "resumed by user");
        }
    }

    /// <summary>取消一条传输。</summary>
    public void Cancel(uint transferId)
    {
        if (_sends.TryGetValue(transferId, out var s))
        {
            s.Cancelled = true;
            s.ResumeSignal.Pulse();
            _ = TrySendStatusAsync(transferId, FileTransferStatus.Cancelled, "cancelled by user");
        }
        if (_receives.TryGetValue(transferId, out var r))
        {
            r.Cancelled = true;
        }
    }

    /// <summary>当前所有传输的进度快照。</summary>
    public IReadOnlyList<FileTransferProgress> Snapshot()
    {
        var list = new List<FileTransferProgress>();
        foreach (var s in _sends.Values)
        {
            list.Add(new FileTransferProgress(s.Id, TransferDirection.Sending, s.RemotePath,
                s.Layout.TotalSize, Math.Min((long)s.Tracker.AckedCount * s.Layout.ChunkSize, s.Layout.TotalSize),
                s.Cancelled ? "cancelled" : s.Paused ? "paused" : "sending"));
        }
        foreach (var r in _receives.Values)
        {
            list.Add(new FileTransferProgress(r.Id, TransferDirection.Receiving, r.RemotePath,
                r.Layout?.TotalSize ?? 0, r.ReceivedBytes,
                r.Cancelled ? "cancelled" : "receiving"));
        }
        return list;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 入站处理（单 worker 串行）
    // ────────────────────────────────────────────────────────────────────────

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _inbound.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_inbound.Reader.TryRead(out var frame))
                {
                    try
                    {
                        await HandleInboundAsync(frame, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"FileTransfer: 处理入站帧失败 {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex)
        {
            _log?.Invoke($"FileTransfer: worker 结束 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HandleInboundAsync(InboundFrame frame, CancellationToken ct)
    {
        switch (frame.Type)
        {
            case ProtocolConstants.FrameType.FileListRequest:
                await HandleListRequestAsync(frame, ct).ConfigureAwait(false);
                break;
            case ProtocolConstants.FrameType.FileListResponse:
                HandleListResponse(frame);
                break;
            case ProtocolConstants.FrameType.FileOpen:
                await HandleOpenAsync(frame, ct).ConfigureAwait(false);
                break;
            case ProtocolConstants.FrameType.FileChunk:
                await HandleChunkAsync(frame, ct).ConfigureAwait(false);
                break;
            case ProtocolConstants.FrameType.FileAck:
                HandleAck(frame);
                break;
            case ProtocolConstants.FrameType.FileComplete:
                await HandleCompleteAsync(frame, ct).ConfigureAwait(false);
                break;
            case ProtocolConstants.FrameType.FilePause:
            case ProtocolConstants.FrameType.FileResume:
            case ProtocolConstants.FrameType.FileCancel:
            case ProtocolConstants.FrameType.FileStatus:
                HandleStatusFrame(frame);
                break;
            default:
                _log?.Invoke($"FileTransfer: 未知文件帧 0x{(byte)frame.Type:x2}");
                break;
        }
    }

    private async Task HandleListRequestAsync(InboundFrame frame, CancellationToken ct)
    {
        string path;
        try
        {
            path = FileTransferFrames.DecodeListRequest(frame.Payload);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FileTransfer: 非法 FileListRequest {ex.Message}");
            return;
        }

        var sink = RequireSink();
        if (!_scope.TryResolve(path, out var dir, out var err))
        {
            await sink.SendAsync(ProtocolConstants.FrameType.FileListResponse,
                FileTransferFrames.EncodeListResponse(Array.Empty<FileEntry>(), err), ct).ConfigureAwait(false);
            return;
        }
        try
        {
            if (!_io.DirectoryExists(dir))
            {
                await sink.SendAsync(ProtocolConstants.FrameType.FileListResponse,
                    FileTransferFrames.EncodeListResponse(Array.Empty<FileEntry>(), "目录不存在"), ct)
                    .ConfigureAwait(false);
                return;
            }
            var entries = _io.List(dir);
            await sink.SendAsync(ProtocolConstants.FrameType.FileListResponse,
                FileTransferFrames.EncodeListResponse(entries, null), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await sink.SendAsync(ProtocolConstants.FrameType.FileListResponse,
                FileTransferFrames.EncodeListResponse(Array.Empty<FileEntry>(), $"{ex.GetType().Name}: {ex.Message}"),
                ct).ConfigureAwait(false);
        }
    }

    private void HandleListResponse(InboundFrame frame)
    {
        if (!_listWaiters.TryDequeue(out var waiter)) return;
        try
        {
            var (err, entries) = FileTransferFrames.DecodeListResponse(frame.Payload);
            waiter.TrySetResult(new FileListResult(err is null, entries, err));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FileTransfer: 非法 FileListResponse {ex.Message}");
            waiter.TrySetResult(new FileListResult(false, Array.Empty<FileEntry>(), ex.Message));
        }
    }

    private async Task HandleOpenAsync(InboundFrame frame, CancellationToken ct)
    {
        FileOpenFrame open;
        try
        {
            open = FileTransferFrames.DecodeOpen(frame.Payload);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FileTransfer: 非法 FileOpen {ex.Message}");
            return;
        }

        if (open.Direction == FileTransferDirection.Push)
        {
            await BeginReceiveAsync(open, ct).ConfigureAwait(false);
        }
        else
        {
            await BeginServePullAsync(open, ct).ConfigureAwait(false);
        }
    }

    private async Task BeginReceiveAsync(FileOpenFrame open, CancellationToken ct)
    {
        var sink = RequireSink();

        // 若是本端主动拉取（DownloadAsync）触发的 Push，用调用方给的本地目标与策略。
        _pendingPulls.TryRemove(open.TransferId, out var pending);

        string target;
        FileConflictPolicy policy;
        TaskCompletionSource<FileTransferOutcome>? external = null;

        if (pending is not null)
        {
            // 本端主动拉取（DownloadAsync）触发的 Push。冲突策略依然必须作用于
            // **最终目标文件**（.part 是断点续传依据，不受冲突策略影响）：
            // 目标已存在时 skip/rename 要生效，否则会静默覆盖用户数据。
            var pullResolution = FileConflictResolver.Resolve(pending.LocalAbsolutePath, pending.Policy, _io.FileExists);
            if (pullResolution.Skipped)
            {
                pending.Completion.TrySetResult(new FileTransferOutcome(
                    true, open.TransferId, null, 0, pullResolution.Reason ?? "skipped"));
                await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
                    FileTransferFrames.EncodeStatus(open.TransferId, FileTransferStatus.Skipped, pullResolution.Reason),
                    ct).ConfigureAwait(false);
                return;
            }
            target = pullResolution.TargetPath!;
            policy = pending.Policy;
            external = pending.Completion;
        }
        else
        {
            if (!_scope.TryResolve(open.RelativePath, out var desired, out var scopeErr))
            {
                RememberStatus(open.TransferId, FileTransferStatus.ScopeDenied, scopeErr ?? "scope denied");
                await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
                    FileTransferFrames.EncodeStatus(open.TransferId, FileTransferStatus.ScopeDenied, scopeErr),
                    ct).ConfigureAwait(false);
                return;
            }
            policy = ParsePolicy(open.Policy);
            var resolution = FileConflictResolver.Resolve(desired, policy, _io.FileExists);
            if (resolution.Skipped)
            {
                RememberStatus(open.TransferId, FileTransferStatus.Skipped, resolution.Reason ?? "skipped");
                await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
                    FileTransferFrames.EncodeStatus(open.TransferId, FileTransferStatus.Skipped, resolution.Reason),
                    ct).ConfigureAwait(false);
                return;
            }
            target = resolution.TargetPath!;
        }

        var layout = ChunkLayout.Create(open.TotalSize, open.ChunkSize);
        var transfer = new ReceiveTransfer(open.TransferId, target, open.RelativePath, policy, external)
        {
            Layout = layout,
            ExpectedDigest = open.Digest,
        };
        transfer.Tracker.Init((ulong)layout.ChunkCount);
        transfer.PartPath = target + ".part";

        // id 占用检查：同 id 的传输已存在说明撞号（双端同时发起或对端异常重发）。
        // 无条件覆盖会让旧传输的 .part 写句柄无人释放（句柄泄漏），
        // 也会把旧传输的状态/ack 记到新传输上。
        if (_receives.ContainsKey(open.TransferId) || _sends.ContainsKey(open.TransferId)
            || _pendingPulls.ContainsKey(open.TransferId))
        {
            RememberStatus(open.TransferId, FileTransferStatus.ProtocolError, "transfer id conflict");
            await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
                FileTransferFrames.EncodeStatus(open.TransferId, FileTransferStatus.ProtocolError, "transfer id conflict"),
                ct).ConfigureAwait(false);
            return;
        }

        try
        {
            // 断点续传：.part 已存在的**连续前缀**视为已确认（并回初始 FileAck）。
            var existing = _io.FileExists(transfer.PartPath) ? _io.GetFileLength(transfer.PartPath) : 0;
            if (existing > layout.TotalSize)
            {
                // 旧 .part 比本次文件长（同目标改传更小文件）：OpenOrCreate 不会
                // 截断，残留旧尾部字节会让最终 BLAKE3 整体校验必然失败。删除重来。
                try { _io.Delete(transfer.PartPath); } catch { /* best-effort */ }
                existing = 0;
            }
            transfer.Part = _io.CreatePart(transfer.PartPath);
            if (existing > 0)
            {
                var contiguous = existing / layout.ChunkSize;
                if (contiguous > 0)
                {
                    var bits = new bool[contiguous];
                    Array.Fill(bits, true);
                    transfer.Tracker.RecordAck(0, bits);
                    transfer.ReceivedBytes = contiguous * layout.ChunkSize;
                }
                _log?.Invoke($"FileTransfer: 断点续传 id={open.TransferId} 已有 {contiguous}/{layout.ChunkCount} 块");
            }
        }
        catch (DiskFullException ex)
        {
            await FailReceiveAsync(transfer, FileTransferStatus.DiskFull, ex.Message, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await FailReceiveAsync(transfer, FileTransferStatus.IoError, ex.Message, ct).ConfigureAwait(false);
            return;
        }

        _receives[open.TransferId] = transfer;

        // 两阶段开启第 2 步：先明确"接受"，发送端收到后才开始送块。
        // 必须早于初始 FileAck —— 否则 ack 会被发送端的"接受等待"当成补传请求。
        await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
            FileTransferFrames.EncodeStatus(open.TransferId, FileTransferStatus.Ok, "accepted"), ct)
            .ConfigureAwait(false);

        if (transfer.Tracker.AckedCount > 0)
        {
            await SendAckAsync(transfer, ct).ConfigureAwait(false);
        }

        _log?.Invoke($"FileTransfer: 接收开始 id={open.TransferId} 目标={target} 大小={layout.TotalSize}");
        Report(transfer);
    }

    private async Task BeginServePullAsync(FileOpenFrame open, CancellationToken ct)
    {
        var sink = RequireSink();
        if (!_scope.TryResolve(open.RelativePath, out var localPath, out var scopeErr))
        {
            RememberStatus(open.TransferId, FileTransferStatus.ScopeDenied, scopeErr ?? "scope denied");
            await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
                FileTransferFrames.EncodeStatus(open.TransferId, FileTransferStatus.ScopeDenied, scopeErr),
                ct).ConfigureAwait(false);
            return;
        }
        if (!_io.FileExists(localPath))
        {
            RememberStatus(open.TransferId, FileTransferStatus.NotFound, "文件不存在");
            await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
                FileTransferFrames.EncodeStatus(open.TransferId, FileTransferStatus.NotFound, "文件不存在"),
                ct).ConfigureAwait(false);
            return;
        }

        var size = _io.GetFileLength(localPath);
        var layout = ChunkLayout.Create(size);
        var digest = _io.HashFile(localPath);
        var transfer = new SendTransfer(open.TransferId, localPath, open.RelativePath, layout, digest);

        // id 占用检查（同 BeginReceiveAsync）：撞号时拒绝，不覆盖既有传输。
        // accept waiter 必须在 FileOpen 发出之前注册（防丢失唤醒，见 AcceptWaiter 注释）。
        if (_sends.ContainsKey(open.TransferId) || _receives.ContainsKey(open.TransferId)
            || _pendingPulls.ContainsKey(open.TransferId)
            || !_statusWaiters.TryAdd(open.TransferId, transfer.AcceptWaiter))
        {
            RememberStatus(open.TransferId, FileTransferStatus.ProtocolError, "transfer id conflict");
            await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
                FileTransferFrames.EncodeStatus(open.TransferId, FileTransferStatus.ProtocolError, "transfer id conflict"),
                ct).ConfigureAwait(false);
            return;
        }
        _sends[open.TransferId] = transfer;

        // 先把真实元数据回给接收端（它先前只知道一个占位 FileOpen）。
        await sink.SendAsync(ProtocolConstants.FrameType.FileOpen,
            FileTransferFrames.EncodeOpen(new FileOpenFrame(
                FileTransferDirection.Push, open.TransferId, layout.ChunkSize, size, digest, open.RelativePath,
                open.Policy)),
            ct).ConfigureAwait(false);

        // 发送循环必须放到**独立任务**里跑，绝不能 inline await 在本 worker 上：
        // 发送循环要等对端 ack（WaitAckAdvanceAsync），而 ack 是由本 worker 处理的
        // ——inline 会让 worker 阻塞在"等 ack"，永远处理不到 ack，直接死锁。
        // （accept 等待没有这个问题：其 waiter 已预先注册，由 worker 喂入。）
        var id = open.TransferId;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunSenderAsync(transfer, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"FileTransfer: 服务拉取发送失败 {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _sends.TryRemove(id, out _);
                _statusWaiters.TryRemove(id, out _);
            }
        }, ct);
    }

    private async Task HandleChunkAsync(InboundFrame frame, CancellationToken ct)
    {
        uint id;
        long index;
        byte[] data;
        try
        {
            (id, index, data) = FileTransferFrames.DecodeChunk(frame.Payload);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FileTransfer: 非法 FileChunk {ex.Message}");
            return;
        }

        if (!_receives.TryGetValue(id, out var t) || t.Layout is null || t.Part is null)
        {
            _log?.Invoke($"FileTransfer: 收到未知传输的块 id={id} index={index}");
            return;
        }
        if (t.Cancelled) return;

        var layout = t.Layout.Value;
        if (index < 0 || index >= layout.ChunkCount)
        {
            _log?.Invoke($"FileTransfer: 块序号越界 id={id} index={index}/{layout.ChunkCount}");
            return;
        }
        var expected = layout.LengthOf(index);
        if (data.Length != expected)
        {
            _log?.Invoke($"FileTransfer: 块长度不符 id={id} index={index} 期望={expected} 实际={data.Length}");
            return;
        }

        try
        {
            t.Part.WriteAt(layout.OffsetOf(index), data);
        }
        catch (DiskFullException ex)
        {
            await FailReceiveAsync(t, FileTransferStatus.DiskFull, ex.Message, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await FailReceiveAsync(t, FileTransferStatus.IoError, ex.Message, ct).ConfigureAwait(false);
            return;
        }

        var one = new bool[1];
        one[0] = true;
        t.Tracker.RecordAck((ulong)index, one);
        t.ReceivedBytes = Math.Min((long)t.Tracker.AckedCount * layout.ChunkSize, layout.TotalSize);
        t.ChunksSinceAck++;
        Report(t);

        // 每 BatchChunks 块回一次 ack（同时给发送端提供流控节奏）。
        if (t.ChunksSinceAck >= BatchChunks)
        {
            t.ChunksSinceAck = 0;
            await SendAckAsync(t, ct).ConfigureAwait(false);
        }
    }

    private void HandleAck(InboundFrame frame)
    {
        uint id;
        FileAckFrame ack;
        try
        {
            (id, ack) = FileTransferFrames.DecodeAck(frame.Payload);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FileTransfer: 非法 FileAck {ex.Message}");
            return;
        }
        if (!_sends.TryGetValue(id, out var t))
        {
            _log?.Invoke($"FileTransfer: 收到未知传输的 FileAck id={id}");
            return;
        }
        try
        {
            var bits = FileAckFrame.UnpackBitmap(ack.Bitmap, (int)ack.BitCount);
            t.Tracker.RecordAck(ack.BaseChunk, bits);
            Report(t);
            t.AckSignal.Pulse();

            // 发送端在发完 FileComplete 后等的是"对端状态"；但对端在缺块时回的是
            // **FileAck**（这是设计：只有位图能作为收到凭据）。因此这里也要唤醒
            // 状态等待者，把它翻译成"请求补传"，否则发送端会白等到超时。
            if (_statusWaiters.TryRemove(id, out var waiter))
            {
                waiter.TrySetResult(new FileStatusFrame(id, FileTransferStatus.ProtocolError, "retransmit requested"));
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FileTransfer: 应用 FileAck 失败 {ex.Message}");
        }
    }

    private async Task HandleCompleteAsync(InboundFrame frame, CancellationToken ct)
    {
        uint id;
        byte[] digest;
        try
        {
            (id, digest) = FileTransferFrames.DecodeComplete(frame.Payload);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FileTransfer: 非法 FileComplete {ex.Message}");
            return;
        }

        var sink = RequireSink();

        if (_receives.TryGetValue(id, out var t))
        {
            if (t.Layout is null || t.Part is null)
            {
                await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
                    FileTransferFrames.EncodeStatus(id, FileTransferStatus.ProtocolError, "未收到 FileOpen 元数据"),
                    ct).ConfigureAwait(false);
                return;
            }

            if (!t.Tracker.IsComplete())
            {
                // 缺块：回 ack 位图（而不是状态），发送端据此补传后重发 FileComplete。
                _log?.Invoke($"FileTransfer: id={id} 仍有缺块，回 ack 请求补传");
                await SendAckAsync(t, ct).ConfigureAwait(false);
                return;
            }

            try
            {
                t.Part.Flush();
                t.Part.Dispose();
                t.Part = null;

                var actual = _io.HashFile(t.PartPath!);
                if (!actual.AsSpan().SequenceEqual(digest))
                {
                    // 校验失败：删除 .part（无法恢复），并明确报错。目标文件绝不出现。
                    _io.Delete(t.PartPath!);
                    RememberStatus(id, FileTransferStatus.DigestMismatch, "BLAKE3 校验失败");
                    await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
                        FileTransferFrames.EncodeStatus(id, FileTransferStatus.DigestMismatch,
                            $"BLAKE3 校验失败：期望 {Convert.ToHexString(digest)[..16]}… " +
                            $"实际 {Convert.ToHexString(actual)[..16]}…"),
                        ct).ConfigureAwait(false);
                    t.Completion.TrySetResult(FileTransferOutcome.Fail(id, "BLAKE3 校验失败"));
                    _receives.TryRemove(id, out _);
                    return;
                }

                // 校验通过 → 原子改名（同卷 File.Move(overwrite) 是原子的）。
                _io.MoveReplace(t.PartPath!, t.TargetPath);
                RememberStatus(id, FileTransferStatus.Ok, "ok");
                await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
                    FileTransferFrames.EncodeStatus(id, FileTransferStatus.Ok, "ok"), ct).ConfigureAwait(false);
                t.Completion.TrySetResult(new FileTransferOutcome(true, id, t.TargetPath, t.ReceivedBytes, null));
                _log?.Invoke($"FileTransfer: 接收完成 id={id} 目标={t.TargetPath} 字节={t.ReceivedBytes}");
                _receives.TryRemove(id, out _);
            }
            catch (DiskFullException ex)
            {
                await FailReceiveAsync(t, FileTransferStatus.DiskFull, ex.Message, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await FailReceiveAsync(t, FileTransferStatus.IoError, ex.Message, ct).ConfigureAwait(false);
            }
            return;
        }

        if (_sends.TryGetValue(id, out var s))
        {
            s.Completed = true;
            s.AckSignal.Pulse();
            return;
        }

        // 已完成的传输又收到重复的 FileComplete（对端没收到我们的最终状态时的重发）：
        // 必须把上一次的结论再回一次，否则发送端会白等到超时并误判为失败。
        if (_lastStatus.TryGetValue(id, out var cached))
        {
            await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
                FileTransferFrames.EncodeStatus(cached.TransferId, cached.Status, cached.Message), ct)
                .ConfigureAwait(false);
            return;
        }

        _log?.Invoke($"FileTransfer: 收到未知传输的 FileComplete id={id}");
    }

    private void HandleStatusFrame(InboundFrame frame)
    {
        FileStatusFrame st;
        try
        {
            st = FileTransferFrames.DecodeStatus(frame.Payload);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FileTransfer: 非法 FileStatus {ex.Message}");
            return;
        }

        if (_sends.TryGetValue(st.TransferId, out var s))
        {
            if (st.Status == FileTransferStatus.Cancelled) { s.Cancelled = true; s.ResumeSignal.Pulse(); }
            else if (st.Status == FileTransferStatus.Paused) { s.Paused = true; }
            else if (st.Status == FileTransferStatus.Resumed) { s.Paused = false; s.ResumeSignal.Pulse(); }
        }

        // 唤醒等待者：只喂"结论性"状态。Paused/Resumed 是流控指令，由上面的
        // _sends 字段分支消化——如果把它们也喂给 accept/final 等待者，发送循环
        // 会把"被暂停"误判成传输失败。
        if (st.Status is not (FileTransferStatus.Paused or FileTransferStatus.Resumed))
        {
            if (_statusWaiters.TryRemove(st.TransferId, out var waiter))
            {
                waiter.TrySetResult(st);
            }
        }
        if (_receives.TryGetValue(st.TransferId, out var r) && st.Status == FileTransferStatus.Cancelled)
        {
            r.Cancelled = true;
            // 必须先释放 .part 写句柄再删文件：.part 以 FileShare.None 打开，
            // Windows 上"开着就删"会抛 IOException，泄漏句柄且传输永不完成。
            CleanupReceive(r);
            try { _io.Delete(r.PartPath!); } catch { /* best-effort */ }
            r.Completion.TrySetResult(FileTransferOutcome.Fail(st.TransferId, "对端取消了传输"));
            _receives.TryRemove(st.TransferId, out _);
        }

        _log?.Invoke($"FileTransfer: 状态 id={st.TransferId} {FileTransferStatus.Describe(st.Status)} {st.Message}");
    }

    // ────────────────────────────────────────────────────────────────────────
    // 发送循环
    // ────────────────────────────────────────────────────────────────────────

    private async Task<FileTransferOutcome> RunSenderAsync(SendTransfer t, CancellationToken ct)
    {
        var sink = RequireSink();
        var layout = t.Layout;

        try
        {
            // 两阶段开启第 1 步：等对端明确"接受"再送数据。
            // waiter 已由发起方（UploadAsync / BeginServePullAsync）在 FileOpen
            // 发出之前注册，这里只 await——不存在"注册前状态先到被丢弃"的窗口。
            FileStatusFrame accept;
            try
            {
                accept = await t.AcceptWaiter.Task.WaitAsync(AcceptTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return FileTransferOutcome.Fail(t.Id, "对端未在超时内接受传输");
            }
            if (accept.Status == FileTransferStatus.Skipped)
            {
                // 对端按"跳过"策略拒绝了本次传输：对用户而言是成功（无事发生）。
                _log?.Invoke($"FileTransfer: id={t.Id} 对端按冲突策略跳过：{accept.Message}");
                return new FileTransferOutcome(true, t.Id, null, 0, accept.Message);
            }
            if (accept.Status != FileTransferStatus.Ok)
            {
                return FileTransferOutcome.Fail(t.Id, DescribeFailure(accept));
            }

            if (layout.ChunkCount == 0)
            {
                // 空文件：直接发 FileComplete（摘要为空文件的 BLAKE3）。
                var st0 = await SendCompleteAndWaitStatusAsync(sink, t, ct).ConfigureAwait(false);
                return st0.Status == FileTransferStatus.Ok
                    ? new FileTransferOutcome(true, t.Id, t.RemotePath, 0, null)
                    : FileTransferOutcome.Fail(t.Id, DescribeFailure(st0));
            }

            using var stream = _io.OpenRead(t.LocalPath);
            var buf = new byte[layout.ChunkSize];
            var batchStart = 0L;

            while (batchStart < layout.ChunkCount)
            {
                ct.ThrowIfCancellationRequested();
                await WaitWhilePausedAsync(t, ct).ConfigureAwait(false);
                if (t.Cancelled) return FileTransferOutcome.Fail(t.Id, "已取消");

                var batchEnd = Math.Min(batchStart + BatchChunks, layout.ChunkCount);
                for (var i = batchStart; i < batchEnd; i++)
                {
                    if (t.Tracker.IsAcked((ulong)i)) continue;
                    var len = layout.LengthOf(i);
                    ReadExactAt(stream, layout.OffsetOf(i), buf.AsSpan(0, len));
                    await sink.SendAsync(ProtocolConstants.FrameType.FileChunk,
                        FileTransferFrames.EncodeChunk(t.Id, i, buf.AsSpan(0, len)), ct).ConfigureAwait(false);
                }

                if (batchEnd < layout.ChunkCount)
                {
                    await WaitAckAdvanceAsync(t, batchEnd, ct).ConfigureAwait(false);
                }
                batchStart = batchEnd;
            }

            // 发完成帧并等待对端状态；对端若回 ack（缺块）则补传后重发。
            for (var round = 0; round < MaxRetransmitRounds; round++)
            {
                var st = await SendCompleteAndWaitStatusAsync(sink, t, ct).ConfigureAwait(false);
                if (st.Status == FileTransferStatus.Ok)
                {
                    return new FileTransferOutcome(true, t.Id, t.RemotePath, layout.TotalSize, null);
                }
                if (st.Status != FileTransferStatus.ProtocolError || !st.Message.Contains("retransmit", StringComparison.Ordinal))
                {
                    // 明确失败（磁盘满/校验失败/越界/取消/跳过…）→ 立即返回，不重试。
                    return FileTransferOutcome.Fail(t.Id, DescribeFailure(st));
                }

                // 对端要求补传：重发缺失块。
                _log?.Invoke($"FileTransfer: id={t.Id} 对端要求补传，重发缺失块（第 {round + 1} 轮）");
                var missing = t.Tracker.MissingChunks(int.MaxValue);
                foreach (var idx in missing)
                {
                    var len = layout.LengthOf((long)idx);
                    ReadExactAt(stream, layout.OffsetOf((long)idx), buf.AsSpan(0, len));
                    await sink.SendAsync(ProtocolConstants.FrameType.FileChunk,
                        FileTransferFrames.EncodeChunk(t.Id, (long)idx, buf.AsSpan(0, len)), ct).ConfigureAwait(false);
                }
            }
            return FileTransferOutcome.Fail(t.Id, $"对端多次要求补传仍未完成（{MaxRetransmitRounds} 轮）");
        }
        catch (DiskFullException ex)
        {
            return FileTransferOutcome.Fail(t.Id, $"磁盘空间不足：{ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return FileTransferOutcome.Fail(t.Id, "已取消");
        }
        catch (Exception ex)
        {
            return FileTransferOutcome.Fail(t.Id, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ReadExactAt(Stream stream, long offset, Span<byte> dest)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var got = 0;
        while (got < dest.Length)
        {
            var n = stream.Read(dest[got..]);
            if (n <= 0) throw new EndOfStreamException("读取源文件时提前结束");
            got += n;
        }
    }

    private async Task WaitWhilePausedAsync(SendTransfer t, CancellationToken ct)
    {
        while (t.Paused && !t.Cancelled)
        {
            await t.ResumeSignal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task WaitAckAdvanceAsync(SendTransfer t, long upToChunk, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + PeerTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (t.Cancelled) return;
            var missing = t.Tracker.MissingChunks(1);
            if (missing.Length == 0 || missing[0] >= (ulong)upToChunk) return;
            if (t.Completed) return;
            try
            {
                // 带超时等待：对端假死（进程挂起但连接未断，不发 ack 也不 RST）时
                // 必须能按时醒来检查 deadline，否则发送任务会无限期阻塞在这里。
                await t.AckSignal.WaitAsync(deadline - DateTime.UtcNow, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return;
            }
        }
        _log?.Invoke($"FileTransfer: id={t.Id} 等待 ack 超时，继续发送剩余块");
    }

    /// <summary>
    /// 发送 FileComplete 并等待对端的最终状态帧。
    ///
    /// 顺序是本方法的灵魂：**先注册 waiter，再发帧**。反过来（先发帧再注册）的话，
    /// 对端的状态回复可能在本端注册之前到达，HandleStatusFrame 找不到 waiter 就会
    /// 把状态丢弃（丢失唤醒），发送端只能白等到超时——高负载下 Task.Run 调度延迟
    /// 放大了这个窗口，曾表现为 E2E 测试偶发失败。
    /// </summary>
    private async Task<FileStatusFrame> SendCompleteAndWaitStatusAsync(IFileFrameSink sink, SendTransfer t, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<FileStatusFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_statusWaiters.TryAdd(t.Id, tcs))
        {
            return new FileStatusFrame(t.Id, FileTransferStatus.ProtocolError, "transfer id conflict");
        }
        try
        {
            await sink.SendAsync(ProtocolConstants.FrameType.FileComplete,
                FileTransferFrames.EncodeComplete(t.Id, t.Digest), ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(PeerTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new FileStatusFrame(t.Id, FileTransferStatus.ProtocolError, "等待对端状态超时");
        }
        finally
        {
            _statusWaiters.TryRemove(t.Id, out _);
        }
    }

    private async Task SendAckAsync(ReceiveTransfer t, CancellationToken ct)
    {
        var sink = RequireSink();
        var layout = t.Layout!.Value;
        var total = layout.ChunkCount;
        if (total == 0) return;

        // 单个 ack 帧能覆盖的位数有限（内层帧 u16 负载），超大文件要分段发送，
        // baseChunk 递增。这是"位图帧"本身的约束，不是引擎的取舍。
        var maxBits = FileTransferFrames.MaxAckBits;
        for (long baseChunk = 0; baseChunk < total; baseChunk += maxBits)
        {
            var count = (int)Math.Min(maxBits, total - baseChunk);
            var bitmap = t.Tracker.BuildBitmap((ulong)baseChunk, count);
            var payload = FileTransferFrames.EncodeAckBitmap(t.Id, (ulong)baseChunk, count, bitmap);
            await sink.SendAsync(ProtocolConstants.FrameType.FileAck, payload, ct).ConfigureAwait(false);
        }
    }

    private async Task FailReceiveAsync(ReceiveTransfer t, byte status, string message, CancellationToken ct)
    {
        RememberStatus(t.Id, status, message);
        try
        {
            var sink = RequireSink();
            await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
                FileTransferFrames.EncodeStatus(t.Id, status, message), ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }

        // 保留 .part（可恢复），但目标文件绝不出现半截。
        CleanupReceive(t);
        t.Completion.TrySetResult(FileTransferOutcome.Fail(t.Id, $"{FileTransferStatus.Describe(status)}: {message}"));
        _receives.TryRemove(t.Id, out _);
    }

    private static void CleanupReceive(ReceiveTransfer t)
    {
        try { t.Part?.Dispose(); } catch { }
        t.Part = null;
    }

    private void Report(SendTransfer t)
        => OnProgress?.Invoke(new FileTransferProgress(t.Id, TransferDirection.Sending, t.RemotePath,
            t.Layout.TotalSize, Math.Min((long)t.Tracker.AckedCount * t.Layout.ChunkSize, t.Layout.TotalSize),
            t.Cancelled ? "cancelled" : t.Paused ? "paused" : "sending"));

    private void Report(ReceiveTransfer t)
        => OnProgress?.Invoke(new FileTransferProgress(t.Id, TransferDirection.Receiving, t.RemotePath,
            t.Layout?.TotalSize ?? 0, t.ReceivedBytes, t.Cancelled ? "cancelled" : "receiving"));

    private IFileFrameSink RequireSink()
        => _sink ?? throw new InvalidOperationException("FileTransferEngine 尚未 Attach 到会话");

    /// <summary>缓存最终状态（容量上限保护：只保留最近 256 条）。</summary>
    private void RememberStatus(uint id, byte status, string message)
    {
        if (_lastStatus.Count > 256)
        {
            foreach (var key in _lastStatus.Keys.Take(_lastStatus.Count - 256)) _lastStatus.TryRemove(key, out _);
        }
        _lastStatus[id] = new FileStatusFrame(id, status, message);
    }

    private async Task TrySendStatusAsync(uint id, byte status, string message)
    {
        try
        {
            var sink = _sink;
            if (sink is null) return;
            await sink.SendAsync(ProtocolConstants.FrameType.FileStatus,
                FileTransferFrames.EncodeStatus(id, status, message), CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    private uint NextTransferId()
    {
        // 两端各自独立分配 id，且本端主动发起的传输与对端发来的 FileOpen
        // 共享同一命名空间（_sends/_receives/_statusWaiters 均按 id 索引）。
        // 若两端都从 1 递增，双端**同时**发起传输（A 上传、B 同时下载）必然撞号，
        // 互相覆盖字典条目（ack 记错传输、状态等待者被顶掉）。
        // 随机起点把撞号概率降到 ~1/2^31；分配时再查占用表兜底。
        while (true)
        {
            var v = Interlocked.Increment(ref _nextTransferId);
            if (v == 0) continue;
            if (_sends.ContainsKey(v) || _receives.ContainsKey(v) || _pendingPulls.ContainsKey(v)
                || _statusWaiters.ContainsKey(v))
            {
                continue;
            }
            return v;
        }
    }

    private static FileConflictPolicy ParsePolicy(byte raw) => raw switch
    {
        (byte)FileConflictPolicy.Overwrite => FileConflictPolicy.Overwrite,
        (byte)FileConflictPolicy.Rename => FileConflictPolicy.Rename,
        (byte)FileConflictPolicy.Skip => FileConflictPolicy.Skip,
        _ => FileConflictPolicy.Overwrite,
    };

    private static string DescribeFailure(FileStatusFrame st)
        => st.Status == FileTransferStatus.ProtocolError
            ? st.Message
            : $"{FileTransferStatus.Describe(st.Status)}: {st.Message}";

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        _inbound.Writer.TryComplete();
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        }
        foreach (var r in _receives.Values) CleanupReceive(r);
        _receives.Clear();
        _sends.Clear();
        _pendingPulls.Clear();
        _cts?.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────────
    // 内部状态
    // ────────────────────────────────────────────────────────────────────────

    private sealed class SendTransfer
    {
        public uint Id { get; }
        public string LocalPath { get; }
        public string RemotePath { get; }
        public ChunkLayout Layout { get; }
        public byte[] Digest { get; }
        public FileAckTracker Tracker { get; } = new();
        public AckSignal AckSignal { get; } = new();
        public ResumeSignal ResumeSignal { get; } = new();
        public volatile bool Paused;
        public volatile bool Cancelled;
        public volatile bool Completed;

        /// <summary>
        /// 对端"接受"状态的等待器。**必须在发送 FileOpen 之前**注册进
        /// _statusWaiters（见 UploadAsync / BeginServePullAsync）——
        /// 若注册晚于发送（如放在 Task.Run 里），对端的 Status(Ok) 可能先于
        /// 注册到达而被 HandleStatusFrame 丢弃（丢失唤醒），发送端只能干等
        /// AcceptTimeout 超时。
        /// </summary>
        public TaskCompletionSource<FileStatusFrame> AcceptWaiter { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public SendTransfer(uint id, string localPath, string remotePath, ChunkLayout layout, byte[] digest)
        {
            Id = id;
            LocalPath = localPath;
            RemotePath = remotePath;
            Layout = layout;
            Digest = digest;
            Tracker.Init((ulong)layout.ChunkCount);
        }
    }

    private sealed class ReceiveTransfer
    {
        public uint Id { get; }
        public string TargetPath { get; }
        public string RemotePath { get; }
        public FileConflictPolicy Policy { get; }
        public ChunkLayout? Layout { get; set; }
        public byte[] ExpectedDigest { get; set; } = Array.Empty<byte>();
        public string? PartPath { get; set; }
        public IPartFileWriter? Part { get; set; }
        public FileAckTracker Tracker { get; } = new();
        public TaskCompletionSource<FileTransferOutcome> Completion { get; }
        public long ReceivedBytes { get; set; }
        public int ChunksSinceAck { get; set; }
        public volatile bool Cancelled;

        public ReceiveTransfer(uint id, string targetPath, string remotePath, FileConflictPolicy policy,
            TaskCompletionSource<FileTransferOutcome>? completion = null)
        {
            Id = id;
            TargetPath = targetPath;
            RemotePath = remotePath;
            Policy = policy;
            Completion = completion ?? new TaskCompletionSource<FileTransferOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>本端发起的拉取：等对端的 Push FileOpen 回来接管。</summary>
    private sealed class PendingPull
    {
        public string LocalAbsolutePath { get; }
        public FileConflictPolicy Policy { get; }
        public TaskCompletionSource<FileTransferOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingPull(string localAbsolutePath, FileConflictPolicy policy)
        {
            LocalAbsolutePath = localAbsolutePath;
            Policy = policy;
        }
    }

    /// <summary>可复位的等待信号（用于"等 ack 推进"）。</summary>
    private sealed class AckSignal
    {
        private TaskCompletionSource _tcs = New();

        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitAsync(CancellationToken ct) => Volatile.Read(ref _tcs).Task.WaitAsync(ct);

        public Task WaitAsync(TimeSpan timeout, CancellationToken ct)
            => Volatile.Read(ref _tcs).Task.WaitAsync(timeout, ct);

        public void Pulse()
        {
            var old = Interlocked.Exchange(ref _tcs, New());
            old.TrySetResult();
        }
    }

    /// <summary>可复位的等待信号（用于暂停/继续）。</summary>
    private sealed class ResumeSignal
    {
        private TaskCompletionSource _tcs = New();

        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitAsync(CancellationToken ct) => Volatile.Read(ref _tcs).Task.WaitAsync(ct);

        public void Pulse()
        {
            var old = Interlocked.Exchange(ref _tcs, New());
            old.TrySetResult();
        }
    }
}
