using SQLitePCL;

namespace Biliardo.App.Cache_Locale.SQLite
{
    public static class SQLiteBootstrap
    {
        private static bool _initialized;
        private static readonly object Gate = new();

        public static void Initialize()
        {
            lock (Gate)
            {
                if (_initialized)
                    return;

                Batteries_V2.Init();
                SQLiteDatabase.EnsureCreated();
                _initialized = true;
            }
        }
    }
}
