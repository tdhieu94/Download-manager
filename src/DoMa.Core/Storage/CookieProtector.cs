using System.Security.Cryptography;
using System.Text;

namespace DoMa.Core.Storage;

public static class CookieProtector
{
    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string blob) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser));
}
