using KeysightScopeApp.Infrastructure.Files;

namespace KeysightScopeApp.Infrastructure.Tests;

public sealed class FileFailureTests
{
    [Fact]
    public void PermissionErrorIncludesTargetAndAction()
    {
        string message = FileFailure.Describe(
            new UnauthorizedAccessException("denied"),
            Path.Combine(Path.GetTempPath(), "波形.csv"));
        Assert.Contains("无权访问", message);
        Assert.Contains("波形.csv", message);
        Assert.Contains("权限", message);
    }

    [Fact]
    public void DiskFullHasSpecificChineseGuidance()
    {
        string message = FileFailure.Describe(
            new IOException("disk full", unchecked((int)0x80070070)),
            Path.Combine(Path.GetTempPath(), "report.html"));
        Assert.Contains("磁盘空间不足", message);
        Assert.Contains("report.html", message);
    }
}
