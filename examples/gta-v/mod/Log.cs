using System;
using System.IO;

namespace NpcVoice
{
    /// <summary>scripts/NpcVoice/NpcVoice.log: startup, warnings, every line spoken and its timings.</summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static void Init(string path)
        {
            _path = path;
            try { File.WriteAllText(path, ""); } catch { }
        }

        public static void Write(string message)
        {
            if (_path == null) return;
            lock (Gate)
            {
                try { File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
                catch { }
            }
        }
    }
}
