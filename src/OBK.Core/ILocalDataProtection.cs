namespace OBK.Core;

public interface ILocalDataProtection
{
    byte[] Protect(byte[] value);
    byte[] Unprotect(byte[] value);
}
