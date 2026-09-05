using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Ring-buffered console capture. The log callback fires on arbitrary threads, so it only ever
    /// touches a <see cref="ConcurrentQueue{T}"/> — no Unity API is called off the main thread.
    /// </summary>
    [InitializeOnLoad]
    internal static class UmcpConsole
    {
        public const int Capacity = 2000;

        internal struct Entry
        {
            public long TimeMs;
            public string Type;
            public string Message;
            public string Stack;
        }

        static readonly ConcurrentQueue<Entry> _pending = new ConcurrentQueue<Entry>();
        static readonly List<Entry> _buffer = new List<Entry>(Capacity);
        static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        static readonly Regex CompilerLine = new Regex(
            @"^(?<file>[^(\r\n]+)\((?<line>\d+),(?<col>\d+)\):\s*(?<sev>error|warning)\s+(?<code>[A-Z]+\d+):\s*(?<msg>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static UmcpConsole()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
        }

        static void OnLog(string message, string stack, LogType type)
        {
            _pending.Enqueue(new Entry
            {
                TimeMs = _clock.ElapsedMilliseconds,
                Type = type.ToString(),
                Message = message,
                Stack = stack
            });
        }

        /// <summary>Called from the main-thread pump. Drains the cross-thread queue into the buffer.</summary>
        public static void Drain()
        {
            Entry e;
            while (_pending.TryDequeue(out e))
            {
                _buffer.Add(e);
                if (_buffer.Count > Capacity) _buffer.RemoveRange(0, _buffer.Count - Capacity);
            }
        }

        public static object Read(string[] types, string contains, int limit, bool stackTrace)
        {
            Drain();
            IEnumerable<Entry> q = _buffer;
            if (types != null && types.Length > 0)
            {
                var set = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
                q = q.Where(e => set.Contains(e.Type));
            }
            if (!string.IsNullOrEmpty(contains))
                q = q.Where(e => e.Message.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0);

            var all = q.ToList();
            var page = all.Skip(Math.Max(0, all.Count - limit)).Select(e => new
            {
                type = e.Type,
                message = Truncate(e.Message, 2000),
                stack = stackTrace ? Truncate(e.Stack, 4000) : null
            }).ToArray();

            return Res.Page(page, all.Count, Math.Max(0, all.Count - limit), page.Length);
        }

        public static int Clear()
        {
            Drain();
            int n = _buffer.Count;
            _buffer.Clear();
            var t = Resolve.FindType("UnityEditor.LogEntries") ?? Resolve.FindType("UnityEditorInternal.LogEntries");
            if (t != null)
            {
                var m = t.GetMethod("Clear", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (m != null) m.Invoke(null, null);
            }
            return n;
        }

        /// <summary>
        /// Structured compiler diagnostics parsed out of the captured log, instead of asking the
        /// agent to scrape console text.
        /// </summary>
        public static object[] CompilerMessages(bool includeWarnings)
        {
            Drain();
            var result = new List<object>();
            foreach (var e in _buffer)
            {
                var m = CompilerLine.Match(e.Message.Split('\n')[0]);
                if (!m.Success) continue;
                var sev = m.Groups["sev"].Value.ToLowerInvariant();
                if (sev == "warning" && !includeWarnings) continue;
                result.Add(new
                {
                    severity = sev,
                    file = m.Groups["file"].Value.Trim(),
                    line = int.Parse(m.Groups["line"].Value),
                    column = int.Parse(m.Groups["col"].Value),
                    code = m.Groups["code"].Value,
                    message = m.Groups["msg"].Value
                });
            }
            return result.ToArray();
        }

        static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= n ? s : s.Substring(0, n) + "… [+" + (s.Length - n) + " chars]";
        }
    }
}
