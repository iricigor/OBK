using OBK.Core;
using OBK.Core.Localization;
using System.Globalization;
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
    [InlineData("uk-UA", "uk")]
    [InlineData("UK", "uk")]
    [InlineData("de-DE", "de")]
    [InlineData("de_AT", "de")]
    [InlineData("und", "cs")]
    [InlineData("", "cs")]
    public void DetectsSupportedOsLanguagesAndFallsBackToCzech(string osLanguage, string expected) =>
        Assert.Equal(expected, LanguageSettings.Detect(osLanguage));

    [Fact]
    public void CyclesEnglishCzechUkrainianAndGerman()
    {
        Assert.Equal(["en", "cs", "uk", "de"], LanguageSettings.SupportedCodes);
        var original = SharedStrings.Culture;
        try
        {
            foreach (var (current, next, name) in new[]
            {
                ("en", "cs", "čeština"), ("cs", "uk", "українська"),
                ("uk", "de", "Deutsch"), ("de", "en", "English")
            })
            {
                SharedStrings.Culture = CultureInfo.GetCultureInfo(current);
                Assert.Equal(next, LanguageSettings.NextCode);
                Assert.Equal(name, LanguageSettings.NextName);
            }
        }
        finally { SharedStrings.Culture = original; }
    }

    [Fact]
    public async Task ExplicitChoiceOverridesOsLanguageAndInvalidSettingsAreReported()
    {
        var directory = Path.Combine(Path.GetTempPath(), "obk-language-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "language.json");
        try
        {
            Assert.Equal("de", await LanguageSettings.LoadAsync(path, "de-DE"));
            foreach (var code in LanguageSettings.SupportedCodes)
            {
                await LanguageSettings.SaveAsync(path, code);
                Assert.Equal(code, await LanguageSettings.LoadAsync(path, "en-US"));
            }
            Assert.Single(Directory.GetFiles(directory));
            await File.WriteAllTextAsync(path, "{\"Language\":\"fr\"}");
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
            await Assert.ThrowsAsync<ObkException>(() => LanguageSettings.SaveAsync(path, "fr"));
        }
        finally { File.Delete(path); }
    }
}
