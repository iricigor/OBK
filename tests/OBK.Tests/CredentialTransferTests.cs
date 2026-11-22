using OBK.Core;
using Xunit;

namespace OBK.Tests;

public class CredentialTransferTests
{
    private static readonly Login Account = new("https://school.example.invalid/", "demo.parent", "s3cret pass", "Example child", "Example School");

    [Fact]
    public void RoundTripRestoresTheAccountWithAnyCaseOrSpacing()
    {
        var transfer = CredentialTransfer.Create(Account);

        Assert.StartsWith(CredentialTransfer.Prefix, transfer.Code);
        Assert.DoesNotContain(Account.Password!, transfer.Code);
        var words = transfer.Passphrase.Split(' ');
        Assert.Equal(CredentialTransfer.WordCount, words.Length);
        Assert.All(words, word => Assert.Contains(word, CredentialTransfer.WordList));

        var opened = CredentialTransfer.Open(transfer.Code, "  " + string.Join("   ", words).ToUpperInvariant() + "\n");
        Assert.Equal(Account with { Url = "https://school.example.invalid" }, opened);
    }

    [Fact]
    public void WrongWordsDoNotOpenTheCode()
    {
        var code = CredentialTransfer.Encrypt(Account, "abacus abdomen abdominal");

        var error = Assert.Throws<ObkException>(() => CredentialTransfer.Open(code, "abacus abdomen abide"));
        Assert.Contains("do not unlock", error.Message);
    }

    [Theory]
    [InlineData("abacus abdomen", "Enter the 3 words")]
    [InlineData("abacus abdomen notaword", "notaword")]
    public void InvalidWordsAreRejectedBeforeDecrypting(string words, string expected)
    {
        var error = Assert.Throws<ObkException>(() => CredentialTransfer.Open(CredentialTransfer.Prefix + "AAAA", words));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void TamperedOrForeignCodesAreRejected()
    {
        var code = CredentialTransfer.Encrypt(Account, "abacus abdomen abdominal");
        var flipped = code[..^3] + (code[^3] == 'A' ? 'B' : 'A') + code[^2..];

        Assert.Contains("do not unlock", Assert.Throws<ObkException>(() => CredentialTransfer.Open(flipped, "abacus abdomen abdominal")).Message);
        Assert.Contains("not an OBK", Assert.Throws<ObkException>(() => CredentialTransfer.Open("https://example.invalid", "abacus abdomen abdominal")).Message);
        Assert.Contains("damaged", Assert.Throws<ObkException>(() => CredentialTransfer.Open(CredentialTransfer.Prefix + "!!", "abacus abdomen abdominal")).Message);
    }

    [Fact]
    public void EachTransferUsesFreshWordsAndEncryption()
    {
        var first = CredentialTransfer.Create(Account);
        var second = CredentialTransfer.Create(Account);

        Assert.NotEqual(first.Code, second.Code);
        Assert.NotEqual(first.Passphrase, second.Passphrase);
        Assert.NotEqual(first.Code, CredentialTransfer.Encrypt(Account, first.Passphrase));
    }

    [Fact]
    public void AccountsWithoutPasswordsCannotBeTransferred() =>
        Assert.Throws<ObkException>(() => CredentialTransfer.Create(Account with { Password = null }));
}
