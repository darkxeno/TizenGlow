using System;
using System.Collections.Generic;
using System.Text;

namespace TizenGlow
{
    /// <summary>
    /// Thread-safe in-memory ring buffer of service logs exposed at /logs on the embedded Web UI server.
    /// </summary>
    public static class AppLog
    {
        private const int MaxLines = 250;
        private static readonly object _lock = new object();
        private static readonly Queue<string> _lines = new Queue<string>(MaxLines);

        public static void Info(string source, string message)
        {
            Write($"[{DateTime.UtcNow:HH:mm:ss.fff}] [{source}] {message}");
        }

        public static void Error(string source, Exception ex)
        {
            Write($"[{DateTime.UtcNow:HH:mm:ss.fff}] [{source}] ERROR: {ex?.GetType().Name}: {ex?.Message}");
        }

        public static void Write(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (_lock)
            {
                if (_lines.Count >= MaxLines) _lines.Dequeue();
                _lines.Enqueue(line);
            }
        }

        public static string Dump()
        {
            lock (_lock)
            {
                if (_lines.Count == 0) return "(no logs yet)";
                var sb = new StringBuilder();
                foreach (string line in _lines) sb.AppendLine(line);
                return sb.ToString();
            }
        }
    }
}
