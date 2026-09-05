using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Umcp.Agent
{
    /// <summary>
    /// The out-of-band control channel (§6.4). A second, minimal loopback listener serviced from a
    /// background thread.
    ///
    /// This exists because the recovery path must not travel the path that breaks. When a modal
    /// dialog blocks <c>EditorApplication.update</c> — the message pump — every queued operation
    /// stalls while the socket still reports healthy, and a reconnect tool dispatched over that
    /// same pump is equally stuck. This listener needs no Editor tick, so it can still answer
    /// "the main thread has not ticked for 12.4 s" and name the problem.
    ///
    /// It touches no Unity API. It reads volatile fields written by the main-thread pump and
    /// nothing else.
    /// </summary>
    internal sealed class UmcpControlServer : IDisposable
    {
        readonly Func<string> _statusJson;
        readonly Action _forceReconnect;
        TcpListener _listener;
        Thread _thread;
        volatile bool _running;

        public int Port { get; private set; }

        public UmcpControlServer(Func<string> statusJson, Action forceReconnect)
        {
            _statusJson = statusJson;
            _forceReconnect = forceReconnect;
        }

        public void Start()
        {
            if (_running) return;
            // Loopback only, ephemeral port. The daemon learns the port from the hello frame.
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "umcp-agent-control" };
            _thread.Start();
        }

        void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    using (var stream = client.GetStream())
                    {
                        stream.ReadTimeout = 3000;
                        stream.WriteTimeout = 3000;
                        var line = ReadLine(stream);
                        var response = Handle(line);
                        var bytes = Encoding.UTF8.GetBytes(response + "\n");
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }
                }
                catch (SocketException) { if (!_running) break; }
                catch (Exception) { /* one bad control connection must never take the listener down */ }
                finally { try { if (client != null) client.Close(); } catch { } }
            }
        }

        string Handle(string line)
        {
            if (string.IsNullOrEmpty(line)) return "{\"ok\":false,\"error\":\"empty request\"}";
            if (line.IndexOf("\"reconnect\"", StringComparison.Ordinal) >= 0)
            {
                try { if (_forceReconnect != null) _forceReconnect(); } catch { }
                return "{\"ok\":true,\"reconnecting\":true}";
            }
            if (line.IndexOf("\"status\"", StringComparison.Ordinal) >= 0)
            {
                try { return _statusJson(); }
                catch (Exception e) { return "{\"ok\":false,\"error\":\"" + Escape(e.Message) + "\"}"; }
            }
            return "{\"ok\":false,\"error\":\"unknown command\"}";
        }

        static string Escape(string s)
        {
            return (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
        }

        static string ReadLine(NetworkStream s)
        {
            var sb = new StringBuilder(128);
            var one = new byte[1];
            while (sb.Length < 8192)
            {
                int n = s.Read(one, 0, 1);
                if (n <= 0) break;
                if (one[0] == (byte)'\n') break;
                sb.Append((char)one[0]);
            }
            return sb.ToString().Trim();
        }

        public void Dispose()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            try { if (_thread != null) _thread.Join(300); } catch { }
        }
    }
}
