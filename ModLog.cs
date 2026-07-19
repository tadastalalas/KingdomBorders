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

        // Kept open for the process lifetime instead of opening/closing the file handle on
        // every Log() call. A single regen can log 10-20+ lines; repeated CreateFile/CloseHandle
        // syscalls (worse under OneDrive/AV file-filter hooks on the Documents folder) were
        // adding real, avoidable cost inside the same frame as the regen itself.
        private static StreamWriter _writer;

        public static void Clear()
        {
            try
            {
                _writer?.Dispose();
                _writer = null;
                File.Delete(LogPath);
            }
            catch { }
        }

        public static void Log(string message)
        {
            try
            {
                if (_writer == null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    _writer = new StreamWriter(LogPath, append: true) { AutoFlush = true };
                }

                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            }
            catch { }
        }

        // Releases the file handle. Call on game end so the log isn't held open across
        // campaign loads within the same game session.
        public static void Close()
        {
            try
            {
                _writer?.Dispose();
                _writer = null;
            }
            catch { }
        }
    }
}