using LectorMd.Core.Desktop;

namespace LectorMd.Core.Tests;

public class EditorLocatorTests
{
    [Fact]
    public void VsCode_IsFoundNextToTheCodeCommandInPath()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Apps\VS Code\bin\code.cmd",
            @"C:\Apps\VS Code\Code.exe",
        };

        string? found = EditorLocator.FindVsCode(@"C:\Windows;C:\Apps\VS Code\bin", [], files.Contains);

        Assert.Equal(@"C:\Apps\VS Code\Code.exe", found);
    }

    [Fact]
    public void VsCode_FallsBackToItsUsualInstallFolders()
    {
        string? found = EditorLocator.FindVsCode(@"C:\Windows", [@"C:\Users\x\AppData\Local\Programs\Microsoft VS Code\Code.exe"],
            path => path == @"C:\Users\x\AppData\Local\Programs\Microsoft VS Code\Code.exe");

        Assert.Equal(@"C:\Users\x\AppData\Local\Programs\Microsoft VS Code\Code.exe", found);
    }

    [Fact]
    public void VsCode_IsNullWhenNotInstalled()
    {
        Assert.Null(EditorLocator.FindVsCode(@"C:\Windows;;C:\bad""path", [@"C:\nada\Code.exe"], _ => false));
    }
}
