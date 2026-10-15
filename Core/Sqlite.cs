using System.IO;
using System.Runtime.InteropServices;

namespace AIUsageMonitor.Core;

internal sealed class SqliteException(int code)
    : IOException($"SQLite 파일을 읽을 수 없습니다 (SQLite {code}).")
{
    public int Code { get; } = code;
}

/// <summary>Windows 기본 winsqlite3 로 다른 앱의 SQLite 파일을 읽기 전용으로 읽는다.</summary>
internal static class SqliteReadOnly
{
    const int OpenReadOnly = 0x1, OpenUri = 0x40;
    const int Row = 100, Done = 101;

    /// <summary>
    /// sql 결과의 각 행을 read 로 바꿔 돌려준다. 열 값은 read 안에서만 유효하다.
    /// WAL 모드 파일을 쓰는 앱이 닫혀 -wal 이 없으면, 읽기 전용 연결도 그 폴더에 -wal/-shm 을 새로 만들고 남긴다.
    /// 그래서 -wal 이 없을 때는 immutable 로 연다 (-wal 이 없으니 놓치는 변경이 없다).
    /// 남은 위험: 확인 직후 그 앱이 쓰기를 시작하면 immutable 연결은 잠금 없이 읽으므로 한 번은 이전 값이나 손상 오류가 날 수 있다.
    /// 호출자는 오류 때 이전 결과를 유지하고 다음 주기에 다시 읽는다.
    /// </summary>
    public static List<T> Query<T>(string path, string sql, Func<IntPtr, T> read, int busyTimeoutMs = 1000)
    {
        if (!IsWalFile(path) || File.Exists(path + "-wal")) return Run(path, OpenReadOnly, sql, read, busyTimeoutMs);
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1";
        return Run(uri, OpenReadOnly | OpenUri, sql, read, busyTimeoutMs);
    }

    /// <summary>파일 헤더의 읽기·쓰기 형식 번호가 2 면 WAL 모드다.</summary>
    static bool IsWalFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[20];
            return fs.Read(header, 0, header.Length) == header.Length && header[18] == 2 && header[19] == 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    static List<T> Run<T>(string target, int flags, string sql, Func<IntPtr, T> read, int busyTimeoutMs)
    {
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            Check(SqliteOpen(target, out db, flags, IntPtr.Zero));
            SqliteBusyTimeout(db, busyTimeoutMs);
            Check(SqlitePrepare(db, sql, -1, out statement, IntPtr.Zero));
            var result = new List<T>();
            int status;
            while ((status = SqliteStep(statement)) == Row) result.Add(read(statement));
            if (status != Done) Check(status);
            return result;
        }
        finally
        {
            if (statement != IntPtr.Zero) SqliteFinalize(statement);
            if (db != IntPtr.Zero) SqliteClose(db);
        }
    }

    public static string? Text(IntPtr row, int column) =>
        SqliteColumnType(row, column) == 5 ? null : Marshal.PtrToStringUTF8(SqliteColumnText(row, column)); // 5 = SQLITE_NULL

    public static long Int(IntPtr row, int column) => SqliteColumnInt64(row, column);

    static void Check(int code)
    {
        if (code != 0) throw new SqliteException(code);
    }

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)] static extern int SqliteOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)] static extern int SqliteClose(IntPtr db);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)] static extern int SqliteBusyTimeout(IntPtr db, int milliseconds);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)] static extern int SqlitePrepare(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)] static extern int SqliteStep(IntPtr statement);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)] static extern int SqliteFinalize(IntPtr statement);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr SqliteColumnText(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)] static extern long SqliteColumnInt64(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.Cdecl)] static extern int SqliteColumnType(IntPtr statement, int column);
}
