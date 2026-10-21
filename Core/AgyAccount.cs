using System.Net.Mail;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Core;

public static class AgyAccount
{
    public static string? ReadName()
    {
        if (!CredRead("gemini:antigravity", 1, 0, out var handle)) return null;
        byte[] bytes = [];
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.Size is 0 or > 65536 || credential.Blob == IntPtr.Zero) return null;
            bytes = new byte[credential.Size];
            Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
            return NameFromCredential(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CredFree(handle); }
    }

    // The local ID-token payload supplies a display label only, never an authentication decision.
    public static string? NameFromCredential(byte[] bytes)
    {
        if (bytes.Length is 0 or > 65536) return null;
        byte[] json = bytes, payload = [];
        try
        {
            if (bytes.Length > 3 && bytes[1] == 0 && bytes[3] == 0)
                json = Encoding.Convert(Encoding.Unicode, Encoding.UTF8, bytes);
            using var credential = JsonDocument.Parse(json);
            if (credential.RootElement.ValueKind != JsonValueKind.Object ||
                !credential.RootElement.TryGetProperty("id_token", out var token) || token.ValueKind != JsonValueKind.String)
                return null;
            var parts = token.GetString()!.Split('.');
            if (parts.Length != 3 || parts[1].Length == 0) return null;
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            payload = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            using var claims = JsonDocument.Parse(payload);
            var root = claims.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.Str("iss") is not ("https://accounts.google.com" or "accounts.google.com") ||
                root.Bool("email_verified") != true || root.Str("email") is not { Length: > 0 and <= 254 } email ||
                !MailAddress.TryCreate(email, out var address) || !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
                return null;
            return email;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException) { return null; }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (!ReferenceEquals(json, bytes)) CryptographicOperations.ZeroMemory(json);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Credential
    {
        public uint Flags, Type;
        public IntPtr Target, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME Written;
        public uint Size;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes, Alias, User;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr credential);
}
