using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Umcp.Agent
{
    /// <summary>
    /// The one socket to the daemon. Unity is always the client; the Editor never listens for
    /// inbound work, so nothing about the transport depends on the Editor being reachable.
    ///
    /// Framing is a 4-byte little-endian length followed by UTF-8 JSON. Deliberately not a
    /// WebSocket: Unity's Mono <c>ClientWebSocket</c> does not auto-pong, and a protocol-level
    /// ping/pong is the wrong liveness signal anyway (§6.4 — health is a completed round trip).
    ///
    /// Threading contract: this class owns two background threads and touches no Unity API at all.
    /// Everything it receives goes into a <see cref="ConcurrentQueue{T}"/> drained by the main thread.
    /// </summary>
    internal sealed class UmcpConnection : IDisposable
    {
        const int MaxFrame = 64 * 1024 * 1024;

        readonly int _port;
        readonly ConcurrentQueue<string> _inbox;
        readonly BlockingCollection<string> _outbox = new BlockingCollection<string>(new ConcurrentQueue<string>());
        readonly Action _onConnected;

        TcpClient _client;
        NetworkStream _stream;
        Thread _reader, _writer;
        volatile bool _running;
        volatile bool _connected;
        int _attempt;

        public bool Connected { get { return _connected; } }
        public int Attempts { get { return _attempt; } }

        public UmcpConnection(int port, ConcurrentQueue<string> inbox, Action onConnected)
        {
            _port = port;
            _inbox = inbox;
            _onConnected = onConnected;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "umcp-agent-read" };
            _reader.Start();
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "umcp-agent-write" };
            _writer.Start();
        }

        public void Send(string json)
        {
            if (!_running) return;
            try { _outbox.Add(json); } catch (InvalidOperationException) { /* completed */ }
        }

        /// <summary>Blocking best-effort send used on the way into a domain reload.</summary>
        public void SendNow(string json, int timeoutMs = 250)
        {
            var s = _stream;
            if (s == null || !_connected) return;
            try
            {
                var payload = Encoding.UTF8.GetBytes(json);
                var header = BitConverter.GetBytes(payload.Length);
                lock (_writeLock)
                {
                    s.WriteTimeout = timeoutMs;
                    s.Write(header, 0, 4);
                    s.Write(payload, 0, payload.Length);
                    s.Flush();
                }
            }
            catch { /* the daemon will notice the disconnect; never throw on the way out */ }
        }

        readonly object _writeLock = new object();

        void ReadLoop()
        {
            var header = new byte[4];
            while (_running)
            {
                try
                {
                    var c = new TcpClient();
                    // Loopback, explicitly. Never a bare port with no host.
                    c.Connect(IPAddress.Loopback, _port);
                    c.NoDelay = true;
                    _client = c;
                    _stream = c.GetStream();
                    _connected = true;
                    _attempt = 0;
                    if (_onConnected != null) _onConnected();

                    while (_running)
                    {
                        if (!ReadExactly(_stream, header, 4)) break;
                        int len = BitConverter.ToInt32(header, 0);
                        if (len <= 0 || len > MaxFrame) break;
                        var body = new byte[len];
                        if (!ReadExactly(_stream, body, len)) break;
                        var frame = Encoding.UTF8.GetString(body);

                        // A cancel has to be seen *before* the operation it cancels is
                        // dequeued, and both arrive on the same socket. Queueing it would
                        // put it behind that operation in the same tick, which is the one
                        // ordering in which cancellation can never work. It carries no
                        // Unity API call, so handling it here — on the reader thread — does
                        // not break the "no Unity API off the main thread" rule.
                        if (frame.IndexOf("\"t\":\"cancel\"", StringComparison.Ordinal) >= 0)
                        {
                            UmcpCancellation.Note(frame);
                            continue;
                        }

                        _inbox.Enqueue(frame);
                    }
                }
                catch (Exception)
                {
                    // Connection refused / reset / torn down. Fall through to backoff.
                }
                finally
                {
                    _connected = false;
                    try { if (_stream != null) _stream.Dispose(); } catch { }
                    try { if (_client != null) _client.Close(); } catch { }
                    _stream = null; _client = null;
                }

                if (!_running) break;
                Thread.Sleep(Backoff());
            }
        }

        int Backoff()
        {
            // Jittered, capped, cheap. The agent retries forever: the daemon outliving Unity and
            // Unity outliving the daemon are both normal.
            _attempt = Math.Min(_attempt + 1, 12);
            var baseMs = Math.Min(250 * (1 << Math.Min(_attempt, 5)), 5000);
            return baseMs / 2 + new Random(Environment.TickCount + _attempt).Next(baseMs / 2);
        }

        void WriteLoop()
        {
            while (_running)
            {
                string json;
                try { json = _outbox.Take(); }
                catch (Exception) { break; }

                for (int i = 0; i < 200 && _running; i++)
                {
                    if (_connected) break;
                    Thread.Sleep(10);
                }
                var s = _stream;
                if (s == null) continue;

                try
                {
                    var payload = Encoding.UTF8.GetBytes(json);
                    var header = BitConverter.GetBytes(payload.Length);
                    lock (_writeLock)
                    {
                        s.WriteTimeout = 15000;
                        s.Write(header, 0, 4);
                        s.Write(payload, 0, payload.Length);
                        s.Flush();
                    }
                }
                catch (Exception)
                {
                    // Dropped. The daemon re-dispatches held ops on reconnect (§6.1).
                }
            }
        }

        static bool ReadExactly(Stream s, byte[] buf, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        public void Dispose()
        {
            _running = false;
            _connected = false;
            try { _outbox.CompleteAdding(); } catch { }
            try { if (_stream != null) _stream.Dispose(); } catch { }
            try { if (_client != null) _client.Close(); } catch { }
            try { if (_reader != null && !_reader.Join(300)) { } } catch { }
            try { if (_writer != null && !_writer.Join(300)) { } } catch { }
        }
    }
}
