namespace KeysightScopeApp.Infrastructure.Files;

public static class FileFailure
{
    public static string Describe(Exception exception, string path)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string target = string.IsNullOrWhiteSpace(path) ? "目标位置" : $"“{Path.GetFullPath(path)}”";
        Exception cause = exception.GetBaseException();
        if (cause is UnauthorizedAccessException)
            return $"无权访问{target}。请确认当前账户具有读写权限，或选择其他目录。";
        if (cause is IOException io && IsDiskFull(io))
            return $"保存到{target}失败：磁盘空间不足。请释放空间后重试。";
        if (cause is IOException)
            return $"访问{target}失败：{cause.Message}";
        return cause.Message;
    }

    private static bool IsDiskFull(IOException exception)
    {
        int code = exception.HResult & 0xFFFF;
        return code is 0x27 or 0x70;
    }
}
