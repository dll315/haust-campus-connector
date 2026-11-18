using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

sealed class CampusAccount
{
    public static readonly string[] IspCodes = { "local", "wcmcc", "unicom", "wtelecom" };
    public readonly string User, Password, Isp;

    public CampusAccount(string user, string password, string isp)
    {
        if (string.IsNullOrWhiteSpace(user) || user.Length > 128 ||
            string.IsNullOrEmpty(password) || password.Length > 256 ||
            Array.IndexOf(IspCodes, isp) < 0) throw new ArgumentException("Invalid campus account fields.");
        User = user;
        Password = password;
        Isp = isp;
    }
}

static class AccountVault
{
    const string Key = @"Software\CampusConnector";
    const string Value = "ProtectedCampusAccount";
    static readonly byte[] Purpose = Encoding.UTF8.GetBytes("CampusConnector.HAUST.Account.1");

    public static bool Exists()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(Key))
            return key != null && key.GetValue(Value) is byte[];
    }

    public static CampusAccount Load()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(Key))
        {
            var data = key == null ? null : key.GetValue(Value) as byte[];
            return data == null ? null : Unprotect(data);
        }
    }

    public static void Save(CampusAccount account)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(Key))
            key.SetValue(Value, Protect(account), RegistryValueKind.Binary);
    }

    public static void Clear()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(Key, true))
            if (key != null) key.DeleteValue(Value, false);
    }

    internal static byte[] Protect(CampusAccount account)
    {
        var buffer = new byte[2048];
        try
        {
            using (var stream = new MemoryStream(buffer))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(account.User);
                writer.Write(account.Password);
                writer.Write(account.Isp);
                return ProtectedData.Protect(buffer, Purpose, DataProtectionScope.CurrentUser);
            }
        }
        finally { Array.Clear(buffer, 0, buffer.Length); }
    }

    internal static CampusAccount Unprotect(byte[] protectedData)
    {
        var buffer = ProtectedData.Unprotect(protectedData, Purpose, DataProtectionScope.CurrentUser);
        try
        {
            using (var stream = new MemoryStream(buffer))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
                return new CampusAccount(reader.ReadString(), reader.ReadString(), reader.ReadString());
        }
        finally { Array.Clear(buffer, 0, buffer.Length); }
    }
}
