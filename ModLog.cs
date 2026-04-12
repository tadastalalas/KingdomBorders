using System;
using System.IO;
using Path = System.IO.Path;

namespace KingdomBorders
{
    public static class ModLog
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord",
            "Configs",
            "KingdomBorders.log"
        );

        public static void Clear()
        {
            try { File.Delete(LogPath); } catch { }
        }

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
            catch { }
        }
    }
}