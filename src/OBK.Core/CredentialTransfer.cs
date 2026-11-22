using OBK.Core.Localization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OBK.Core;

/// <summary>An encrypted, offline account transfer: a QR code text plus the words that unlock it.</summary>
public sealed record AccountTransfer(string Code, string Passphrase);

/// <summary>
/// Encrypts one saved account for an offline QR transfer to another OBK device. The code holds
/// AES-256-GCM ciphertext; the key is derived from randomly chosen words shown separately.
/// </summary>
public static class CredentialTransfer
{
    public const string Prefix = "OBK1:";
    public const int WordCount = 3;
    public const int Iterations = 600_000;
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16, KeySize = 32;
    private const int MaxCodeLength = 4096;
    private static readonly byte[] AssociatedData = Encoding.ASCII.GetBytes(Prefix);
    private static readonly Lazy<string[]> words = new(LoadWords);
    private static readonly Lazy<HashSet<string>> known = new(() => new HashSet<string>(words.Value, StringComparer.Ordinal));

    public static IReadOnlyList<string> WordList => words.Value;

    public static bool IsTransferCode(string? text) => text?.Trim().StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>Encrypts a remembered account under freshly generated random words.</summary>
    public static AccountTransfer Create(Login login)
    {
        var passphrase = string.Join(' ', Enumerable.Range(0, WordCount)
            .Select(_ => words.Value[RandomNumberGenerator.GetInt32(words.Value.Length)]));
        return new AccountTransfer(Encrypt(login, passphrase), passphrase);
    }

    public static string Encrypt(Login login, string passphrase)
    {
        if (string.IsNullOrEmpty(login.Password) || string.IsNullOrWhiteSpace(login.Username))
            throw new ObkException(SharedStrings.ErrorTransferNeedsPassword);
        var account = new Login(KomensClient.NormalizeUrl(login.Url), login.Username.Trim(), login.Password,
            login.ChildName, login.SchoolName);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(account);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var output = new byte[SaltSize + NonceSize + TagSize + plaintext.Length];
        salt.CopyTo(output, 0);
        nonce.CopyTo(output, SaltSize);
        var key = DeriveKey(NormalizePassphrase(passphrase), salt);
        try
        {
            using var aes = CreateAes(key);
            aes.Encrypt(nonce, plaintext, output.AsSpan(SaltSize + NonceSize + TagSize),
                output.AsSpan(SaltSize + NonceSize, TagSize), AssociatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
        return Prefix + Base64Url(output);
    }

    /// <summary>Decrypts a scanned code. Wrong words and damaged codes fail without revealing the account.</summary>
    public static Login Open(string code, string passphrase)
    {
        var normalized = NormalizePassphrase(passphrase);
        var entered = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (entered.Length != WordCount)
            throw new ObkException(Localized.Format(SharedStrings.ErrorTransferWordCount, WordCount));
        if (entered.FirstOrDefault(word => !known.Value.Contains(word)) is { } unknown)
            throw new ObkException(Localized.Format(SharedStrings.ErrorTransferUnknownWord, unknown));
        var data = Decode(code);
        var key = DeriveKey(normalized, data.AsSpan(0, SaltSize));
        var plaintext = new byte[data.Length - SaltSize - NonceSize - TagSize];
        try
        {
            using var aes = CreateAes(key);
            aes.Decrypt(data.AsSpan(SaltSize, NonceSize), data.AsSpan(SaltSize + NonceSize + TagSize),
                data.AsSpan(SaltSize + NonceSize, TagSize), plaintext, AssociatedData);
            var login = JsonSerializer.Deserialize<Login>(plaintext);
            if (login is null || string.IsNullOrWhiteSpace(login.Url) || string.IsNullOrWhiteSpace(login.Username) ||
                string.IsNullOrEmpty(login.Password))
                throw new ObkException(SharedStrings.ErrorTransferDamaged);
            return login with { Url = KomensClient.NormalizeUrl(login.Url) };
        }
        catch (CryptographicException)
        {
            throw new ObkException(SharedStrings.ErrorTransferWrongWords);
        }
        catch (JsonException)
        {
            throw new ObkException(SharedStrings.ErrorTransferDamaged);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Lowercases the words and collapses any spacing so typing differences do not matter.</summary>
    public static string NormalizePassphrase(string text) =>
        string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static byte[] Decode(string code)
    {
        var text = code.Trim();
        if (!IsTransferCode(text) || text.Length > MaxCodeLength)
            throw new ObkException(SharedStrings.ErrorTransferNotObkCode);
        try
        {
            var body = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            var data = Convert.FromBase64String(body.PadRight(body.Length + (4 - body.Length % 4) % 4, '='));
            if (data.Length <= SaltSize + NonceSize + TagSize) throw new FormatException();
            return data;
        }
        catch (FormatException) { throw new ObkException(SharedStrings.ErrorTransferDamaged); }
    }

    private static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, KeySize);

    private static AesGcm CreateAes(byte[] key)
    {
        if (!AesGcm.IsSupported) throw new ObkException(SharedStrings.ErrorTransferUnsupported);
        return new AesGcm(key, TagSize);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string[] LoadWords()
    {
        using var stream = typeof(CredentialTransfer).Assembly.GetManifestResourceStream("OBK.Core.eff_large_wordlist.txt") ??
            throw new ObkException(SharedStrings.ErrorTransferUnsupported);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var list = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            var tab = line.IndexOf('\t');
            if (tab >= 0) list.Add(line[(tab + 1)..].Trim());
        }
        if (list.Count != 7776 || list.Distinct(StringComparer.Ordinal).Count() != list.Count)
            throw new ObkException(SharedStrings.ErrorTransferUnsupported);
        return [.. list];
    }
}
