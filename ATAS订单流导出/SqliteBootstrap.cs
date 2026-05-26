using System;

namespace ATASOrderFlowExporter
{
    /// <summary>
    /// ATAS 插件宿主下初始化 SQLite（使用 Windows 内置 winsqlite3，避免 native DLL 路径问题）
    /// </summary>
    internal static class SqliteBootstrap
    {
        private static readonly object InitLock = new object();
        private static bool _initialized;
        private static Exception _initError;

        internal static void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            lock (InitLock)
            {
                if (_initialized)
                {
                    return;
                }

                if (_initError != null)
                {
                    throw new InvalidOperationException("SQLite 初始化已失败", _initError);
                }

                try
                {
                    SQLitePCL.Batteries_V2.Init();
                    _initialized = true;
                }
                catch (Exception ex)
                {
                    _initError = ex;
                    throw new InvalidOperationException(
                        $"SQLite 初始化失败: {ex.Message}" +
                        (ex.InnerException != null ? $" | Inner: {ex.InnerException.Message}" : string.Empty),
                        ex);
                }
            }
        }
    }
}
