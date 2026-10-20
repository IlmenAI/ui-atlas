using System.IO;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class NativeRecorderDeploymentTests
{
    [Theory]
    [InlineData(true, ".exe")]
    [InlineData(false, ".dll")]
    public void PackagedWorkerUsesItsApphostWhileFrameworkBuildUsesDll(bool hasApphost, string expectedExtension)
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-worker-deployment-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "native-recorder");
        Directory.CreateDirectory(folder);
        try
        {
            var worker = Path.Combine(folder, "UIMaps.Recorder.ProviderProbe");
            File.WriteAllBytes(worker + ".dll", []);
            if (hasApphost) File.WriteAllBytes(worker + ".exe", []);

            Assert.Equal(worker + expectedExtension, NativeRecorderCapture.ResolveProviderPath(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
