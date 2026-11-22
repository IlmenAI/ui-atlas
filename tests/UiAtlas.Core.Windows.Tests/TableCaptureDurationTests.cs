using System.IO;
using UiAtlas.Presentation;

namespace UiAtlas.Core.Windows.Tests;

public sealed class TableCaptureDurationTests
{
    [Fact]
    public void NewAndExistingInstallationsUseTenMinutesUntilChanged()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "table-capture-settings.json"), """{"MaximumImages":150}""");
            var path = Path.Combine(directory.FullName, "table-capture-duration.json");
            var store = new TableCaptureDurationStore(path);
            Assert.Equal(600_000, store.Load().ToMilliseconds());
            store.Save(new(19));
            Assert.Equal(1_140_000, new TableCaptureDurationStore(path).Load().ToMilliseconds());
            foreach (var value in new[] { 0, -1, TableCaptureDuration.MaximumMinutes + 1 })
                Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(new(value)));
            Assert.Equal(19, store.Load().Minutes);
        }
        finally { directory.Delete(true); }
    }
}
