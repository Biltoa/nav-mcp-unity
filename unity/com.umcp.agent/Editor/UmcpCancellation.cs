using System;
using System.Collections.Generic;

namespace Umcp.Agent
{
    /// <summary>
    /// Keys the daemon has cancelled.
    ///
    /// Cancellation here means exactly one thing: **an operation that has not started yet will not
    /// start.** It never interrupts an operation already running on the main thread, because the
    /// only ways to do that are aborting a thread (which corrupts the Editor) or checking a flag
    /// inside every tool (which is a lie the moment a tool calls into Unity and blocks).
    /// Promising more than that would be worse than promising nothing.
    ///
    /// Touched from the socket reader thread and from the main thread, so every access is locked.
    /// No Unity API is called here, which is what makes it safe off the main thread.
    /// </summary>
    internal static class UmcpCancellation
    {
        const int Ring = 256;

        static readonly HashSet<string> _cancelled = new HashSet<string>();
        static readonly Queue<string> _order = new Queue<string>();
        static readonly object _gate = new object();

        /// <summary>
        /// Record a cancel frame. Parsed by hand rather than with Newtonsoft: this runs on the
        /// socket thread in the middle of the read loop, and the frame shape is ours.
        /// </summary>
        public static void Note(string frame)
        {
            var key = Extract(frame, "key");
            if (string.IsNullOrEmpty(key)) return;

            lock (_gate)
            {
                if (_cancelled.Add(key)) _order.Enqueue(key);
                while (_order.Count > Ring)
                {
                    var oldest = _order.Dequeue();
                    _cancelled.Remove(oldest);
                }
            }
        }

        /// <summary>True once, per key: consuming it keeps a replayed key from being refused forever.</summary>
        public static bool Take(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (_gate) return _cancelled.Remove(key);
        }

        public static int Pending { get { lock (_gate) return _cancelled.Count; } }

        static string Extract(string json, string field)
        {
            var needle = "\"" + field + "\":\"";
            var at = json.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return null;
            var start = at + needle.Length;
            var end = json.IndexOf('"', start);
            return end < 0 ? null : json.Substring(start, end - start);
        }
    }
}
