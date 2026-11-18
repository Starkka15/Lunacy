using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LunaKit
{
    /// The few SQLite calls WebSql needs, from winsqlite3.dll: the SQLite Windows itself ships
    /// (Windows 10 Mobile included), so the package carries no native library. Its exports are
    /// WINAPI, P/Invoke's default.
    internal static class Sqlite
    {
        private const string Lib = "winsqlite3.dll";
        public const int OK = 0, ROW = 100, DONE = 101, CONSTRAINT = 19, FULL = 13;
        public const int INTEGER = 1, FLOAT = 2, TEXT = 3, BLOB = 4, NULL = 5;
        public const int OPEN_READWRITE = 0x2, OPEN_CREATE = 0x4, OPEN_FULLMUTEX = 0x10000;
        /// SQLITE_TRANSIENT: SQLite copies what it is given.
        private static readonly IntPtr Transient = new IntPtr(-1);

        [DllImport(Lib)] private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib)] public static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Lib)] private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, IntPtr errmsg);
        [DllImport(Lib)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib)] public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib)] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib)] public static extern int sqlite3_bind_null(IntPtr stmt, int i);
        [DllImport(Lib)] public static extern int sqlite3_bind_int64(IntPtr stmt, int i, long v);
        [DllImport(Lib)] public static extern int sqlite3_bind_double(IntPtr stmt, int i, double v);
        [DllImport(Lib)] private static extern int sqlite3_bind_text(IntPtr stmt, int i, byte[] v, int n, IntPtr destructor);
        [DllImport(Lib)] public static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Lib)] private static extern IntPtr sqlite3_column_name(IntPtr stmt, int i);
        [DllImport(Lib)] public static extern int sqlite3_column_type(IntPtr stmt, int i);
        [DllImport(Lib)] public static extern long sqlite3_column_int64(IntPtr stmt, int i);
        [DllImport(Lib)] public static extern double sqlite3_column_double(IntPtr stmt, int i);
        [DllImport(Lib)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
        [DllImport(Lib)] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int i);
        [DllImport(Lib)] public static extern int sqlite3_column_bytes(IntPtr stmt, int i);
        [DllImport(Lib)] public static extern int sqlite3_changes(IntPtr db);
        [DllImport(Lib)] public static extern long sqlite3_last_insert_rowid(IntPtr db);
        [DllImport(Lib)] private static extern IntPtr sqlite3_errmsg(IntPtr db);

        private static byte[] Z(string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            Array.Resize(ref b, b.Length + 1);
            return b;
        }

        private static string Utf8(IntPtr p, int n = -1)
        {
            if (p == IntPtr.Zero) return "";
            if (n < 0) { n = 0; while (Marshal.ReadByte(p, n) != 0) n++; }
            var b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return Encoding.UTF8.GetString(b);
        }

        public static int Open(string path, out IntPtr db) { return sqlite3_open_v2(Z(path), out db, OPEN_READWRITE | OPEN_CREATE | OPEN_FULLMUTEX, IntPtr.Zero); }
        public static int Exec(IntPtr db, string sql) { return sqlite3_exec(db, Z(sql), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
        public static int Prepare(IntPtr db, string sql, out IntPtr stmt) { return sqlite3_prepare_v2(db, Z(sql), -1, out stmt, IntPtr.Zero); }
        public static int BindText(IntPtr stmt, int i, string v) { var b = Encoding.UTF8.GetBytes(v ?? ""); return sqlite3_bind_text(stmt, i, b, b.Length, Transient); }
        public static string ColumnName(IntPtr stmt, int i) { return Utf8(sqlite3_column_name(stmt, i)); }
        public static string ColumnText(IntPtr stmt, int i) { var p = sqlite3_column_text(stmt, i); return Utf8(p, sqlite3_column_bytes(stmt, i)); }
        public static byte[] ColumnBlob(IntPtr stmt, int i)
        {
            var p = sqlite3_column_blob(stmt, i);
            var n = sqlite3_column_bytes(stmt, i);
            var b = new byte[n];
            if (p != IntPtr.Zero && n > 0) Marshal.Copy(p, b, 0, n);
            return b;
        }
        public static string ErrMsg(IntPtr db) { return Utf8(sqlite3_errmsg(db)); }
    }
}
