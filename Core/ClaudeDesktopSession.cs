using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Core;

/// <summary>현재 Windows 사용자의 Claude Desktop 로그인만 읽는다. 원본 DB는 읽기 전용으로 연다.</summary>
internal static class ClaudeDesktopSession
{
    internal sealed class Cookie
    {
        public required string Name { get; init; }
        public required string Value { get; init; }
        public required string Domain { get; init; }
        public required string Path { get; init; }
        public bool HttpOnly { get; init; }
        public bool Secure { get; init; }
        public override string ToString() => Name; // 인증값이 진단 출력에 섞이지 않도록 한다.
    }

    internal static string ProfileDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");

    internal static List<Cookie> Read(string? profileDirectory = null)
    {
        var directory = profileDirectory ?? ProfileDirectory;
        var database = System.IO.Path.Combine(directory, "Network", "Cookies");
        var state = System.IO.Path.Combine(directory, "Local State");
        if (!File.Exists(database) || !File.Exists(state)) return [];

        using var document = JsonDocument.Parse(ClaudeLocalReader.ReadShared(state));
        var encryptedKey = Convert.FromBase64String(document.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()!);
        if (!encryptedKey.AsSpan().StartsWith("DPAPI"u8))
            throw new CryptographicException("지원하지 않는 Claude Desktop 로그인 저장 형식입니다.");
        var key = Unprotect(encryptedKey.AsSpan(5).ToArray());
        IntPtr db = IntPtr.Zero;
        try
        {
            Check(SqliteOpen(database, out db, 1, IntPtr.Zero)); // SQLITE_OPEN_READONLY
            SqliteBusyTimeout(db, 1500);
            var version = Query(db, "SELECT value FROM meta WHERE key='version'", row =>
                int.TryParse(Text(row, 0), out var value) ? value : 0).Single();
            var now = (DateTimeOffset.UtcNow - new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero)).Ticks / 10;
            // Claude의 인증·선택 조직·브라우저 확인 cookie만 읽는다.
            var sql = "SELECT host_key,name,path,value,encrypted_value,is_httponly,is_secure FROM cookies " +
                "WHERE host_key IN ('.claude.ai','claude.ai') AND name IN ('sessionKey','lastActiveOrg','cf_clearance') " +
                $"AND (expires_utc=0 OR expires_utc>{now})";
            return Query(db, sql, row =>
            {
                var host = Text(row, 0);
                var value = Text(row, 3);
                if (value.Length == 0)
                {
                    int length = SqliteColumnBytes(row, 4);
                    if (length is < 1 or > 16384) throw new CryptographicException("Claude 로그인 데이터 크기가 잘못되었습니다.");
                    var encrypted = new byte[length];
                    Marshal.Copy(SqliteColumnBlob(row, 4), encrypted, 0, length);
                    value = DecodeCookie(encrypted, key, host, version);
                }
                return new Cookie
                {
                    Domain = host, Name = Text(row, 1), Path = Text(row, 2), Value = value,
                    HttpOnly = SqliteColumnInt(row, 5) != 0, Secure = SqliteColumnInt(row, 6) != 0,
                };
            });
        }
        finally
        {
            if (db != IntPtr.Zero) SqliteClose(db);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static string DecodeCookie(byte[] encrypted, byte[] key, string host, int version)
    {
        if (encrypted.Length < 31 || !(encrypted.AsSpan().StartsWith("v10"u8) || encrypted.AsSpan().StartsWith("v11"u8)))
            throw new CryptographicException("지원하지 않는 Claude Desktop cookie 암호화 형식입니다.");
        var plain = new byte[encrypted.Length - 31];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(encrypted.AsSpan(3, 12), encrypted.AsSpan(15, plain.Length), encrypted.AsSpan(encrypted.Length - 16), plain);
            int offset = version >= 24 ? 32 : 0;
            if (offset > 0 && (plain.Length < offset || !CryptographicOperations.FixedTimeEquals(
                    plain.AsSpan(0, offset), SHA256.HashData(Encoding.UTF8.GetBytes(host)))))
                throw new CryptographicException("Claude Desktop cookie의 도메인이 일치하지 않습니다.");
            return new UTF8Encoding(false, true).GetString(plain, offset, plain.Length - offset);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    static List<T> Query<T>(IntPtr db, string sql, Func<IntPtr, T> read)
    {
        IntPtr statement = IntPtr.Zero;
        try
        {
            Check(SqlitePrepare(db, sql, -1, out statement, IntPtr.Zero));
            var result = new List<T>();
            int status;
            while ((status = SqliteStep(statement)) == 100) result.Add(read(statement));
            if (status != 101) Check(status);
            return result;
        }
        finally { if (statement != IntPtr.Zero) SqliteFinalize(statement); }
    }

    static string Text(IntPtr row, int column) => Marshal.PtrToStringUTF8(SqliteColumnText(row, column)) ?? "";
    static void Check(int code)
    {
        if (code != 0) throw new IOException($"Claude Desktop 로그인 저장소를 읽을 수 없습니다 (SQLite {code}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Length; public IntPtr Data; }

    static byte[] Unprotect(byte[] encrypted)
    {
        var input = new Blob { Length = encrypted.Length, Data = Marshal.AllocHGlobal(encrypted.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(encrypted, 0, input.Data, encrypted.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output))
                throw new CryptographicException("현재 Windows 계정으로 Claude Desktop 로그인을 읽을 수 없습니다.");
            var bytes = new byte[output.Length];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
                LocalFree(output.Data);
            }
        }
    }

    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)] static extern int SqliteOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)] static extern int SqliteClose(IntPtr db);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)] static extern int SqliteBusyTimeout(IntPtr db, int milliseconds);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)] static extern int SqlitePrepare(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)] static extern int SqliteStep(IntPtr statement);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)] static extern int SqliteFinalize(IntPtr statement);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SqliteColumnText(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SqliteColumnBlob(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)] static extern int SqliteColumnBytes(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int", CallingConvention = CallingConvention.Cdecl)] static extern int SqliteColumnInt(IntPtr statement, int column);
}
