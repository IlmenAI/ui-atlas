using System.Text.Json;
using UiAtlas.Catalog;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class HeaderProviderCatalogTests
{
    [Fact]
    public async Task ProviderRefreshPreservesLocalSelectionAndAutomationWithoutCopyingKeys()
    {
        using var directory = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("UIATLAS_HEADER_SETTINGS_PATH");
        var path = Path.Combine(directory.Path, "headers.json");
        Environment.SetEnvironmentVariable("UIATLAS_HEADER_SETTINGS_PATH", path);
        try
        {
            var saved = await AtlasCatalogCommand.ExecuteAsync(new("header-settings-save", directory.Path,
                HeaderSettingsUpdate: new(false, "", "", Provider: "local", AutoAnalyze: true)));
            Assert.False(saved.HeaderSettings!.Enabled); Assert.True(saved.HeaderSettings.AutoAnalyze);
            var refreshed = await AtlasCatalogCommand.ExecuteAsync(new("header-providers-sync", directory.Path,
                HeaderSettingsUpdate: new(false, "", "", Providers: [new("openai", "saved-model", "", "UIMaps/Skills/OpenAI")])));
            Assert.Equal("local", refreshed.HeaderSettings!.Provider); Assert.True(refreshed.HeaderSettings.AutoAnalyze);
            var settings = new AzureHeaderSettingsStore().Load();
            Assert.Equal("saved-model", Assert.Single(settings.Providers!).Model);
            Assert.DoesNotContain("CredentialReference", JsonSerializer.Serialize(refreshed));
            await AtlasCatalogCommand.ExecuteAsync(new("header-settings-save", directory.Path,
                HeaderSettingsUpdate: new(false, "", "", Provider: "local", AutoAnalyze: false)));
            Assert.Single(new AzureHeaderSettingsStore().Load().Providers!);
            Assert.False(new AzureHeaderSettingsStore().Load().AutoAnalyze);
        }
        finally { Environment.SetEnvironmentVariable("UIATLAS_HEADER_SETTINGS_PATH", previous); }
    }
}
