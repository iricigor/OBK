using Android.Security.Keystore;
using Android.Runtime;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using OBK.Core;

namespace OBK.AndroidApp;

public sealed class AndroidDataProtection : ILocalDataProtection
{
    private const string Alias = "OBK.local-data.v1";
    private static readonly byte[] Context = "OBK.local-data.v1"u8.ToArray();
    private readonly ISecretKey key;
    private readonly object gate = new();

    public AndroidDataProtection(bool hasStoredData)
    {
        try
        {
            using var store = KeyStore.GetInstance("AndroidKeyStore") ??
                throw new ObkException("Android Keystore is unavailable; no local data was opened.");
            store.Load(null);
            if (!store.ContainsAlias(Alias))
            {
                if (hasStoredData)
                    throw new ObkException("The Android encryption key is missing. Existing local data cannot be decrypted; no replacement key was created.");
                using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore") ??
                    throw new ObkException("Android could not create the local encryption key.");
                using var specification = new KeyGenParameterSpec.Builder(Alias,
                    KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                    .SetKeySize(256)
                    .SetBlockModes(KeyProperties.BlockModeGcm)
                    .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                    .SetRandomizedEncryptionRequired(true)
                    .Build();
                generator.Init(specification);
                using var generated = generator.GenerateKey();
            }
            var storedKey = store.GetKey(Alias, null) ??
                throw new ObkException("Android could not access the local encryption key.");
            key = storedKey.JavaCast<ISecretKey>();
        }
        catch (Exception error) when (error is GeneralSecurityException or Java.IO.IOException)
        {
            throw new ObkException("Android could not initialize encrypted storage. No plaintext fallback is used.", error);
        }
    }

    public byte[] Protect(byte[] value)
    {
        lock (gate)
        {
            try
            {
                using var cipher = Cipher.GetInstance("AES/GCM/NoPadding") ??
                    throw new ObkException("Android authenticated encryption is unavailable.");
                cipher.Init(CipherMode.EncryptMode, key);
                cipher.UpdateAAD(Context);
                var nonce = cipher.GetIV() ?? throw new ObkException("Android did not provide an encryption nonce.");
                if (nonce.Length != 12) throw new ObkException("Android returned an unsupported encryption nonce.");
                var ciphertext = cipher.DoFinal(value) ?? throw new ObkException("Android did not encrypt local data.");
                var result = new byte[1 + nonce.Length + ciphertext.Length];
                result[0] = 1;
                nonce.CopyTo(result, 1);
                ciphertext.CopyTo(result, 13);
                return result;
            }
            catch (GeneralSecurityException error)
            {
                throw new ObkException("Android could not encrypt local data. No plaintext fallback is used.", error);
            }
        }
    }

    public byte[] Unprotect(byte[] value)
    {
        if (value.Length < 29 || value[0] != 1)
            throw new ObkException("Local Android data has an invalid encrypted format.");
        lock (gate)
        {
            try
            {
                using var cipher = Cipher.GetInstance("AES/GCM/NoPadding") ??
                    throw new ObkException("Android authenticated encryption is unavailable.");
                using var specification = new GCMParameterSpec(128, value[1..13]);
                cipher.Init(CipherMode.DecryptMode, key, specification);
                cipher.UpdateAAD(Context);
                return cipher.DoFinal(value[13..]) ?? throw new ObkException("Android did not decrypt local data.");
            }
            catch (GeneralSecurityException error)
            {
                throw new ObkException("Android could not decrypt local data. Its key may be unavailable or the data damaged; no plaintext fallback is used.", error);
            }
        }
    }
}
