namespace DeskLink.Client.Services;

/// <summary>
/// 客户端启动参数。管道名与 Service 的 <c>{PipeNamePrefix}.{InstanceId}</c> 约定保持一致：
///   - RPC 管道：<c>DeskLink.Client.{instance}</c>（默认 <c>DeskLink.Client.default</c>）
///   - 媒体管道：<c>DeskLink.Media.{instance}</c>（默认 <c>DeskLink.Media.default</c>）
/// 允许用 <c>--pipe</c> / <c>--media-pipe</c> 覆盖，便于冒烟脚本/单机双实例联调。
/// </summary>
public sealed class ClientOptions
{
    public const string DefaultPrefix = "DeskLink";
    public const string DefaultInstance = "default";

    public string PipeName { get; init; } = DefaultClientPipeName();

    public string MediaPipeName { get; init; } = DefaultMediaPipeName();

    public static string DefaultClientPipeName(string prefix = DefaultPrefix, string instance = DefaultInstance)
        => $"{prefix}.Client.{NormalizeInstance(instance)}";

    public static string DefaultMediaPipeName(string prefix = DefaultPrefix, string instance = DefaultInstance)
        => $"{prefix}.Media.{NormalizeInstance(instance)}";

    /// <summary>
    /// 实例名归一化：**必须与 Service 的 <c>CommandLineParser.DeriveInstanceId</c> 保持一致**
    /// （转小写）。Service 从 <c>--data-dir</c> 末段推导实例名时会 <c>ToLowerInvariant()</c>，
    /// 客户端若原样使用用户给的实例名，<c>--instance DL-CTRL</c> 会连到
    /// <c>DeskLink.Client.DL-CTRL</c>，而服务监听的是 <c>DeskLink.Client.dl-ctrl</c>，
    /// 两边永远对不上。管道名大小写不敏感这条"保险"在 Windows 上并不可靠，不要依赖。
    /// </summary>
    private static string NormalizeInstance(string instance) =>
        string.IsNullOrWhiteSpace(instance) ? DefaultInstance : instance.Trim().ToLowerInvariant();

    /// <summary>解析命令行。未知参数忽略（WPF 宿主可能传入 <c>-p:...</c> 之类）。</summary>
    public static ClientOptions Parse(string[]? args)
    {
        var pipe = DefaultClientPipeName();
        var media = DefaultMediaPipeName();

        if (args is not null)
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--pipe" when i + 1 < args.Length:
                        pipe = args[++i];
                        break;
                    case "--media-pipe" when i + 1 < args.Length:
                        media = args[++i];
                        break;
                    case "--instance" when i + 1 < args.Length:
                    {
                        var inst = args[++i];
                        pipe = DefaultClientPipeName(instance: inst);
                        media = DefaultMediaPipeName(instance: inst);
                        break;
                    }
                }
            }
        }

        return new ClientOptions { PipeName = pipe, MediaPipeName = media };
    }
}
