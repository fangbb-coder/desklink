// P5.5 内存自签证书（测试/本地直连用）。
//
// 生产环境：installer 部署统一自签证书到 %ProgramData%\DeskLink\certs\；
// 本类只负责"数据目录下没有时生成一张临时证书"的兜底。

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DeskLink.Service.Direct;

public static class SelfSignedCert
{
    /// <summary>加载数据目录下的 direct.pfx；不存在则生成并保存。</summary>
    public static async Task<X509Certificate2> LoadOrCreateAsync(string dataDir, string name)
    {
        var path = Path.Combine(dataDir, $"{name}.pfx");
        if (File.Exists(path))
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            // 用 X509CertificateLoader 而不是已过时的构造函数：
            // 构造函数式加载在 .NET 9 起标记为过时（SYSLIB0057），
            // 新 API 语义相同（PKCS#12 + 可导出私钥），且后续版本会继续维护。
            return X509CertificateLoader.LoadPkcs12(bytes, password: null, X509KeyStorageFlags.Exportable);
        }

        var cert = Generate(name);
        var pfx = cert.Export(X509ContentType.Pfx);
        Directory.CreateDirectory(dataDir);
        await File.WriteAllBytesAsync(path, pfx).ConfigureAwait(false);
        return cert;
    }

    public static X509Certificate2 Generate(string cn)
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={cn}", ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(10));
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);
    }
}
