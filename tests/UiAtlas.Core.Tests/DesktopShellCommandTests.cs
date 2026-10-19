using UiAtlas.Core.Cli;

namespace UiAtlas.Core.Tests;

public sealed class DesktopShellCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopResolvesFromInstalledCliAndBundledTools(bool bundled)
    {
        using var temp = new TempDirectory();
        var launcher = Path.Combine(temp.Path, "launcher");
        Directory.CreateDirectory(launcher);
        File.WriteAllText(Path.Combine(launcher, "UiAtlas.dll"), "fixture");
        File.WriteAllText(Path.Combine(launcher, "UiAtlas.runtimeconfig.json"), "{}");
        var source = bundled ? Path.Combine(launcher, "tools") : temp.Path;
        Directory.CreateDirectory(source);
        Assert.Equal(Path.Combine(launcher, "UiAtlas.dll"), DesktopShellCommand.ResolveLauncher(source));
    }
    [Fact]
    public void MissingDesktopAndInvalidWorkspaceFailWithoutOpeningAnything()
    {
        using var temp = new TempDirectory();
        Assert.Throws<FileNotFoundException>(() => DesktopShellCommand.ResolveLauncher(temp.Path));
        Assert.Throws<ArgumentException>(() => DesktopShellCommand.Open(["--workspace", "work"]));
        Assert.Throws<ArgumentException>(() => DesktopShellCommand.Open(["--unknown"]));
    }
}
