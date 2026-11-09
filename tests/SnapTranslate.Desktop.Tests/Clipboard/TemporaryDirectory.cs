namespace SnapTranslate.Desktop.Tests.Clipboard;

/// <summary>测试用临时目录，测试结束时尽量删除。</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "SnapTranslate.Desktop.Tests",
            "clipboard",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
        catch (IOException)
        {
            // 数据库文件可能仍被短暂占用，忽略清理失败。
        }
        catch (UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论。
        }
    }
}
