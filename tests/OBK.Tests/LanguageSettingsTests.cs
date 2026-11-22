using OBK.Core;
using OBK.Core.Localization;
using Xunit;

namespace OBK.Tests;

[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageCollection;

[Collection("Language")]
public sealed class LanguageSettingsTests
{
    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("cs-CZ", "cs")]
    [InlineData("de-DE", "cs")]
    [InlineData("uk-UA", "cs")]
    [InlineData("und", "cs")]
    [InlineData("", "cs")]
    public void DetectsSupportedOsLanguagesAndFallsBackToCzech(string osLanguage, string expected) =>
        Assert.Equal(expected, LanguageSettings.Detect(osLanguage));

    [Fact]
    public async Task ExplicitChoiceOverridesOsLanguageAndInvalidSettingsAreReported()
    {
        var directory = Path.Combine(Path.GetTempPath(), "obk-language-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "language.json");
        try
        {
            Assert.Equal("cs", await LanguageSettings.LoadAsync(path, "de-DE"));
            await LanguageSettings.SaveAsync(path, "en");
            Assert.Equal("en", await LanguageSettings.LoadAsync(path, "cs-CZ"));
            await LanguageSettings.SaveAsync(path, "cs");
            Assert.Equal("cs", await LanguageSettings.LoadAsync(path, "en-US"));
            Assert.Single(Directory.GetFiles(directory));
            await File.WriteAllTextAsync(path, "{\"Language\":\"de\"}");
            await Assert.ThrowsAsync<ObkException>(() => LanguageSettings.LoadAsync(path, "en-US"));
            await File.WriteAllTextAsync(path, "{broken");
            await Assert.ThrowsAsync<ObkException>(() => LanguageSettings.LoadAsync(path, "en-US"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SaveFailureIsExplicitAndDoesNotLeaveTemporaryFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "obk-language-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, "not a directory");
        try
        {
            await Assert.ThrowsAsync<ObkException>(() => LanguageSettings.SaveAsync(Path.Combine(path, "language.json"), "cs"));
            Assert.Equal("not a directory", await File.ReadAllTextAsync(path));
            await Assert.ThrowsAsync<ObkException>(() => LanguageSettings.SaveAsync(path, "de"));
        }
        finally { File.Delete(path); }
    }
}
