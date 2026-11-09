using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace MASA.Lingua.Server.Startup;

/// <summary>
/// DataProtection 密钥的文件系统仓库（默认 FileSystemXmlRepository 的容错替换，经 DI 以 IXmlRepository 注册）。
/// 生产背景：/data/dataprotection 挂载卷里会沉淀「历史运行 UID 写下的 key 文件」（0600 只属主可读）——
/// 运行用户换 UID（如对齐 specdoc 的 1000）后这些文件读不了，而默认仓库 GetAllElements 枚举到就整环加载失败，
/// 首个 OIDC 登录直接 500（2026-11 生产实测）。
/// 语义：读路径逐文件容错——单个文件读不了（EACCES / 损坏 / 枚举后消失）就跳过并告警，其余密钥照常参与密钥环，
/// 一把可用密钥都没有则按框架原生路径新建；写路径保持严格——目录不可写说明部署真错了，让它炸，不静默。
/// 刻意不吞「目录本身枚举失败」（挂载卷属主与运行 UID 不匹配）：那是部署错误，必须保持可见，与单个遗留文件不同。
/// </summary>
public sealed class LinguaXmlKeyRepository : IXmlRepository
{
    private readonly string _directory;
    private readonly ILogger _logger;

    public LinguaXmlKeyRepository(string keysDirectory, ILoggerFactory loggerFactory)
    {
        _directory = keysDirectory;
        _logger = loggerFactory.CreateLogger<LinguaXmlKeyRepository>();
    }

    /// <summary>与 FileSystemXmlRepository 同款过滤：跳过隐藏文件、只认 .xml（不区分大小写）。</summary>
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var elements = new List<XElement>();
        foreach (var file in new DirectoryInfo(_directory).EnumerateFileSystemInfos())
        {
            try
            {
                if ((file.Attributes & FileAttributes.Hidden) != 0)
                {
                    continue;
                }

                if (!string.Equals(file.Extension, ".xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                elements.Add(XElement.Load(file.FullName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                // 典型形态：挂载卷里旧 UID 写的 0600 key 文件（属主与当前运行用户不匹配）。
                // 跳过而非整环失败；这条告警同时是给运维的清理提示（chown 1000:1000 或删除遗留文件）。
                _logger.LogWarning(ex,
                    "跳过不可读的 DataProtection 密钥文件 {KeyFile}：多为旧运行 UID 的遗留文件，可 chown 1000:1000 或删除，不影响登录。",
                    file.FullName);
            }
        }

        return elements;
    }

    /// <summary>与默认仓库同款命名（key-{guid}.xml）与排他创建；Linux 上保持 0600（密钥材料只对运行用户可读）。</summary>
    public void StoreElement(XElement element, string friendlyName)
    {
        var fileName = (string.IsNullOrWhiteSpace(friendlyName)
                ? "key-" + Guid.NewGuid().ToString("D")
                : friendlyName)
            + ".xml";
        var fullPath = Path.Combine(_directory, fileName);
        using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
        {
            element.Save(stream);
        }

        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
