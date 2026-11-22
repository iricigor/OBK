using Android.Security.Keystore;
using Android.Runtime;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using OBK.AndroidApp.Localization;
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
                throw new ObkException(AppStrings.ErrorKeystoreUnavailable);
            store.Load(null);
            if (!store.ContainsAlias(Alias))
            {
                if (hasStoredData)
                    throw new ObkException(AppStrings.ErrorEncryptionKeyMissing);
                using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore") ??
                    throw new ObkException(AppStrings.ErrorKeyCreationFailed);
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
                throw new ObkException(AppStrings.ErrorKeyAccessFailed);
            key = storedKey.JavaCast<ISecretKey>();
        }
        catch (Exception error) when (error is GeneralSecurityException or Java.IO.IOException)
        {
            throw new ObkException(AppStrings.ErrorStorageInitFailed, error);
        }
    }

    public byte[] Protect(byte[] value)
    {
        lock (gate)
        {
            try
            {
                using var cipher = Cipher.GetInstance("AES/GCM/NoPadding") ??
                    throw new ObkException(AppStrings.ErrorAuthenticatedEncryptionUnavailable);
                cipher.Init(CipherMode.EncryptMode, key);
                cipher.UpdateAAD(Context);
                var nonce = cipher.GetIV() ?? throw new ObkException(AppStrings.ErrorNoEncryptionNonce);
                if (nonce.Length != 12) throw new ObkException(AppStrings.ErrorUnsupportedNonce);
                var ciphertext = cipher.DoFinal(value) ?? throw new ObkException(AppStrings.ErrorLocalEncryptNoResult);
                var result = new byte[1 + nonce.Length + ciphertext.Length];
                result[0] = 1;
                nonce.CopyTo(result, 1);
                ciphertext.CopyTo(result, 13);
                return result;
            }
            catch (GeneralSecurityException error)
            {
                throw new ObkException(AppStrings.ErrorLocalEncryptFailed, error);
            }
        }
    }

    public byte[] Unprotect(byte[] value)
    {
        if (value.Length < 29 || value[0] != 1)
            throw new ObkException(AppStrings.ErrorInvalidEncryptedFormat);
        lock (gate)
        {
            try
            {
                using var cipher = Cipher.GetInstance("AES/GCM/NoPadding") ??
                    throw new ObkException(AppStrings.ErrorAuthenticatedEncryptionUnavailable);
                using var specification = new GCMParameterSpec(128, value[1..13]);
                cipher.Init(CipherMode.DecryptMode, key, specification);
                cipher.UpdateAAD(Context);
                return cipher.DoFinal(value[13..]) ?? throw new ObkException(AppStrings.ErrorLocalDecryptNoResult);
            }
            catch (GeneralSecurityException error)
            {
                throw new ObkException(AppStrings.ErrorLocalDecryptFailed, error);
            }
        }
    }
}
