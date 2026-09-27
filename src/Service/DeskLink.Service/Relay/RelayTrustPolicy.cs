// 中继 TLS 信任策略：把"接不接受这张证书"从传输实现里抽出来，便于单测与替换。
//
// 两种策略：
//   1. RelayTrustPolicy（默认，生产）：TOFU 指纹固定。首次记录并信任，之后必须一致。
//   2. RelayTrustPolicy.AcceptAny（仅测试/显式关闭）：接受任何证书。
//      **必须显式传入**——不给"默认不安全"留后门，任何接受全部证书的调用点
//      在代码里都能被 grep 出来。
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DeskLink.Service.Security;

namespace DeskLink.Service.Relay;

/// <summary>一次证书校验的结论（带人类可读原因，便于日志与排查）。</summary>
public readonly record struct RelayTrustDecision(bool Trusted, string Reason);

/// <summary>中继对端证书信任判定。</summary>
public interface IRelayTrustPolicy
{
    RelayTrustDecision Evaluate(string host, int port, X509Certificate? certificate, SslPolicyErrors errors);
}

/// <summary>
/// TOFU 指纹固定策略（生产默认）。
///
/// 关于 SslPolicyErrors 的处理：自签证书必然带 RemoteCertificateChainErrors
/// （链不信任）甚至 RemoteCertificateNameMismatch（CN 与连接主机不符）。
/// 我们**有意忽略**这两类错误——TOFU 的信任来源就是"指纹与首次一致"，而不是
/// 公信 CA 链。唯一不可忽略的是 RemoteCertificateNotAvailable（拿不到证书），
/// 那种情况必须拒绝，否则等于没校验。
/// </summary>
public sealed class RelayTrustPolicy : IRelayTrustPolicy
{
    private readonly RelayPinStore _pins;
    private readonly Action<string>? _log;

    public RelayTrustPolicy(RelayPinStore pins, Action<string>? log = null)
    {
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _log = log;
    }

    public RelayTrustDecision Evaluate(string host, int port, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return new RelayTrustDecision(false, "对端未提供证书（RemoteCertificateNotAvailable）");
        }

        string fingerprint;
        try
        {
            fingerprint = RelayPinStore.FingerprintOf(certificate.GetRawCertData());
        }
        catch (Exception ex)
        {
            return new RelayTrustDecision(false, $"无法计算证书指纹：{ex.GetType().Name}");
        }

        var known = _pins.Get(host, port);
        if (known is null)
        {
            // 首次使用：记录并信任（TOFU）。
            _pins.RecordFirstUse(host, port, fingerprint);
            _log?.Invoke($"relay trust: TOFU 首次信任 {RelayPinStore.EndpointKey(host, port)} " +
                         $"sha256={Short(fingerprint)}");
            return new RelayTrustDecision(true, "TOFU first use");
        }

        if (string.Equals(known, fingerprint, StringComparison.Ordinal))
        {
            return new RelayTrustDecision(true, "fingerprint match");
        }

        // 指纹变化：**拒绝**且不自动更新。自动更新会把 TOFU 变成"每次信任最新证书"，
        // 完全失去防中间人的意义。要接受新证书必须显式清 pin（运维动作）。
        _log?.Invoke(
            $"relay trust: 指纹不匹配，拒绝连接 {RelayPinStore.EndpointKey(host, port)} " +
            $"pinned={Short(known)} presented={Short(fingerprint)}");
        return new RelayTrustDecision(false,
            $"证书指纹与首次记录不一致（pinned={Short(known)} presented={Short(fingerprint)}）；" +
            "若确认 relay 已合法换证，请清除该端点的 TOFU 记录后重连");
    }

    /// <summary>日志用的短指纹（前 16 个 Base64 字符足够区分，也避免刷屏）。</summary>
    private static string Short(string s) => s.Length <= 16 ? s : s[..16] + "…";

    /// <summary>
    /// 接受任何证书的策略（**仅测试/显式关闭 TOFU**）。
    ///
    /// 刻意做成静态具名单例而不是"构造参数默认 null = 不安全"：
    /// 这样每个放行点都在代码里显式可见，便于审计。
    /// </summary>
    public static IRelayTrustPolicy AcceptAny { get; } = new AcceptAnyPolicy();

    private sealed class AcceptAnyPolicy : IRelayTrustPolicy
    {
        public RelayTrustDecision Evaluate(string host, int port, X509Certificate? certificate, SslPolicyErrors errors)
            => new(true, "AcceptAny（TOFU 已关闭）");
    }
}
