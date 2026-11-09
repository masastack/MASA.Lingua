using System.Xml.Linq;
using MASA.Lingua.Server.Startup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// DataProtection 自研密钥仓库的容错语义：单个读不了的密钥文件（生产形态：挂载卷里旧运行 UID 写下的 0600 文件，
/// 换 UID 后 EACCES）被跳过，而不是让整个密钥环加载失败、首个 OIDC 登录 500。
/// EACCES 形态在 root CI（root 绕过权限检查）与 Windows（ACL 语义）下都无法稳定制造，
/// 用「损坏 xml」（XmlException）与「名叫 *.xml 的目录」（Windows 上 UnauthorizedAccess / Linux 上 IOException）
/// 两种可移植形态覆盖同一个逐文件过滤器。
/// </summary>
public sealed class LinguaXmlKeyRepositoryTests
{
    private static string NewKeysDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lingua-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static LinguaXmlKeyRepository CreateRepository(string dir) =>
        new(dir, NullLoggerFactory.Instance);

    [Fact]
    public void StoreElement_Then_GetAllElements_Returns_Only_The_Stored_Key()
    {
        var dir = NewKeysDir();
        try
        {
            var repo = CreateRepository(dir);
            repo.StoreElement(
                new XElement("key", new XAttribute("id", "11111111-1111-1111-1111-111111111111")),
                "key-11111111-1111-1111-1111-111111111111");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "不是密钥"); // 非 .xml：不参与密钥环

            Assert.True(File.Exists(Path.Combine(dir, "key-11111111-1111-1111-1111-111111111111.xml")));

            var element = Assert.Single(repo.GetAllElements());
            Assert.Equal("11111111-1111-1111-1111-111111111111", (string?)element.Attribute("id"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GetAllElements_Skips_Unreadable_Entries_Instead_Of_Failing_The_Ring()
    {
        var dir = NewKeysDir();
        try
        {
            var repo = CreateRepository(dir);
            repo.StoreElement(
                new XElement("key", new XAttribute("id", "22222222-2222-2222-2222-222222222222")),
                "key-22222222-2222-2222-2222-222222222222");
            File.WriteAllText(Path.Combine(dir, "key-corrupt.xml"), "这不是 xml");
            Directory.CreateDirectory(Path.Combine(dir, "key-trap.xml")); // 读它会抛 IOException / UnauthorizedAccessException

            var element = Assert.Single(repo.GetAllElements()); // 两个「读不了」的都被跳过，不抛
            Assert.Equal("22222222-2222-2222-2222-222222222222", (string?)element.Attribute("id"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
