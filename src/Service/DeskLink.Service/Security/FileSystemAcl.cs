// 文件/目录 ACL 工具：限制为 SYSTEM + Administrators。
//
// 用途：DESIGN.md 安全原则——"文件 ACL 用 FileSystemAcl 限制 SYSTEM/Admins"。
//      任何在 DataDir 下创建的文件/目录都应继承这套权限（无 BUILTIN\Users）。
//
// 行为：
//   - 删除目录/文件上的继承 ACL
//   - 仅保留 SYSTEM (FullControl) + Administrators (FullControl)
//   - 创建时即应用；存量目录在 EnsureDataDir 时一次性收紧
//
// 注意：
//   - 仅 Windows 下生效；非 Windows 平台（跨平台单测）走空操作但仍 mkdir。
//   - 不要对 Program Files 下的可执行文件路径用此函数（那需要单独规则）。
using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace DeskLink.Service.Security;

public static class FileSystemAcl
{
    public const string SystemSid = "SY";
    public const string AdminsSid = "BA";

    public sealed class AclOutcome
    {
        public bool Applied { get; init; }
        public string Path { get; init; } = "";
        public string? SkippedReason { get; init; }
    }

    /// <summary>
    /// 收紧目录 ACL：SYSTEM + Administrators FullControl，其余继承一律删除。
    ///
    /// 运行时分两种调用者：
    ///   - DeskLinkService（LocalSystem，生产路径）：SYSTEM ACE 命中，收紧成功。
    ///   - --console 开发模式 / 单元测试（普通用户，非提升）：收紧后调用者自身
    ///     将失去对该目录的写权限，会导致 KeyStore 后续写 keystore.json 直接
    ///     UnauthorizedAccessException。因此此处先探测"收紧后本进程是否仍可写"，
    ///     不可写则回退为不收紧（Applied=false + SkippedReason），保留可调试性。
    ///
    /// 该回退只影响开发/测试体验，不削弱生产安全性：生产路径以 LocalSystem 运行，
    /// 收紧必然成功；DataDir 默认在 ProgramData 下，本来也只有 SYSTEM/Admins 可写。
    /// </summary>
    public static AclOutcome LockDownDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new AclOutcome { Path = path, Applied = false, SkippedReason = "non-windows" };
        }
        if (!Directory.Exists(path))
        {
            return new AclOutcome { Path = path, Applied = false, SkippedReason = "missing" };
        }

        var ds = new DirectorySecurity();
        // 先清空所有 inherited + explicit rules
        ds.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        ds.AddAccessRule(new FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        ds.AddAccessRule(new FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        // 应用前先探测：调用者若不在目标 SID 集合内，收紧后即失去写权限。
        if (!CanWriteAfterLockDown(path, ds))
        {
            return new AclOutcome
            {
                Path = path,
                Applied = false,
                SkippedReason = "caller-loses-write-access",
            };
        }

        // Directory.CreateDirectory 只有 (string) 和 (string, UnixFileMode) 两个重载，
        // 没有 (string, DirectorySecurity)：不存在这种"带 ACL 创建"的静态重载。
        // 正确做法是目录建好后再用扩展方法应用 ACL（见下方 SetAccessControl）。
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        new DirectoryInfo(path).SetAccessControl(ds);
        return new AclOutcome { Path = path, Applied = true };
    }

    /// <summary>
    /// 探测"若把 <paramref name="dir"/> 收紧为 SYSTEM+Admins，当前进程是否仍可写"。
    ///
    /// 实现：真正把 ACL 应用到一个临时子目录上并试写，验证调用者仍具备写权限；
    /// 无论结果如何都在返回前删除该临时目录，不留副作用。
    ///
    /// 不用 WindowsPrincipal.IsInRole(Administrator) 判断：UAC 拆分令牌下该判断
    /// 与"是否真的能写 SYSTEM/Admins-only 目录"并不等价，直接实测更可靠。
    /// </summary>
    private static bool CanWriteAfterLockDown(string dir, DirectorySecurity probe)
    {
        var testDir = Path.Combine(dir, $".acl-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testDir);
            new DirectoryInfo(testDir).SetAccessControl(probe);

            // 收紧后试写：能写 → 调用者属于 SYSTEM 或 Administrators
            var probeFile = Path.Combine(testDir, "probe.tmp");
            File.WriteAllBytes(probeFile, Array.Empty<byte>());
            File.Delete(probeFile);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            // 恢复可删除权限后清理（收紧过的目录需要先打开继承或用 SYSTEM 权限删除，
            // 这里 best-effort：失败也不影响测试，最终随根目录一起清理）
            try { new DirectoryInfo(testDir).SetAccessControl(new DirectorySecurity()); } catch { }
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 收紧文件 ACL：SYSTEM + Administrators FullControl。
    /// </summary>
    public static AclOutcome LockDownFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new AclOutcome { Path = path, Applied = false, SkippedReason = "non-windows" };
        }
        if (!File.Exists(path))
        {
            return new AclOutcome { Path = path, Applied = false, SkippedReason = "missing" };
        }

        var fs = new FileSecurity();
        fs.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        fs.AddAccessRule(new FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        fs.AddAccessRule(new FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        // 同 LockDownDirectory：非提升进程收紧后自己就写不了文件了，
        // 这里同样探测一次，写不了就不收紧（Applied=false 由调用方按需记录）。
        if (!CanWriteAfterFileLockDown(path, fs))
        {
            return new AclOutcome
            {
                Path = path,
                Applied = false,
                SkippedReason = "caller-loses-write-access",
            };
        }

        new FileInfo(path).SetAccessControl(fs);
        return new AclOutcome { Path = path, Applied = true };
    }

    /// <summary>
    /// 探测"把 <paramref name="file"/> 收紧为 SYSTEM+Admins 后，当前进程是否仍可写"。
    /// 通过在该文件所在目录建临时文件实测（不修改目标文件本身）。
    /// </summary>
    private static bool CanWriteAfterFileLockDown(string file, FileSecurity probe)
    {
        var dir = Path.GetDirectoryName(file);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return false;
        }

        var testFile = Path.Combine(dir, $".acl-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(testFile, Array.Empty<byte>());
            new FileInfo(testFile).SetAccessControl(probe);

            // 收紧后追加写入：能写 → 调用者属于 SYSTEM 或 Administrators
            using (var fs = new FileStream(testFile, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(testFile); } catch { }
        }
    }

    /// <summary>
    /// 验证某路径的 ACL 是否仅含 SYSTEM + Administrators（用于单测 / 修复入口）。
    /// 返回 null 表示通过；否则返回第一条违规规则描述。
    /// </summary>
    public static string? VerifyLockDown(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null; // 非 Windows 平台不验证
        }

        AuthorizationRuleCollection rules = isDirectory
            ? new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            : new FileInfo(path).GetAccessControl().GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier));

        foreach (FileSystemAccessRule rule in rules)
        {
            var sid = (System.Security.Principal.SecurityIdentifier)rule.IdentityReference;
            // 允许 SYSTEM、Administrators、CREATOR OWNER（容器继承，新建文件用）
            if (sid.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid)) continue;
            if (sid.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid)) continue;
            if (sid.IsWellKnown(System.Security.Principal.WellKnownSidType.CreatorOwnerSid)) continue;
            return $"unexpected ACE: {sid.Value} {rule.AccessControlType} {rule.FileSystemRights}";
        }
        return null;
    }
}
