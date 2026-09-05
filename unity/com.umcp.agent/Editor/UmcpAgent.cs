using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Debug = UnityEngine.Debug;
using Stopwatch = System.Diagnostics.Stopwatch;
using Process = System.Diagnostics.Process;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// The whole in-editor agent. Deliberately minimal, because everything in this AppDomain dies
    /// on every recompile: it connects out, pumps a queue on the main thread, executes ops, and
    /// reports lifecycle. The catalog, search, guidance, history, retries and caching all live in
    /// the daemon, where a recompile cannot touch them.
    ///
    /// Two rules this file exists to enforce:
    ///   1. No <c>EditorUtility.DisplayDialog</c>, ever. A modal blocks EditorApplication.update,
    ///      which is the message pump, and wedges the bridge while the socket looks healthy.
    ///   2. No Unity API call off the main thread. Cross-thread state is a ConcurrentQueue and
    ///      two interlocked longs. That is all.
    /// </summary>
    [InitializeOnLoad]
    internal static class UmcpAgent
    {
        const int MaxOpsPerTick = 4096;
        const int AppliedKeyRing = 512;

        const string EpochKey = "umcp.epoch";
        const string AppliedKey = "umcp.appliedKeys";

        static readonly ConcurrentQueue<string> _inbox = new ConcurrentQueue<string>();
        static readonly Stopwatch _clock = Stopwatch.StartNew();
        static long _lastTickMs;
        static long _lastOpMs;

        static UmcpConnection _conn;
        static UmcpControlServer _control;
        static readonly Queue<string> _appliedOrder = new Queue<string>();
        static readonly Dictionary<string, string> _appliedResults = new Dictionary<string, string>();
        static bool _appliedDirty;
        static int _opsExecuted;
        static volatile bool _helloPending;
        static int _daemonPort = UmcpSettings.DefaultDaemonPort;

        public static int Epoch { get; private set; }

        public static long MsSinceLastTick
        {
            get { return _clock.ElapsedMilliseconds - Interlocked.Read(ref _lastTickMs); }
        }

        static UmcpAgent()
        {
            if (!UmcpSettings.Enabled)
            {
                UnityEngine.Debug.Log("[umcp] agent disabled (UMCP_DISABLE or project setting).");
                return;
            }
            try { Start(); }
            catch (Exception e) { UnityEngine.Debug.LogError("[umcp] agent failed to start: " + e); }
        }

        static void Start()
        {
            Epoch = SessionState.GetInt(EpochKey, 0) + 1;
            SessionState.SetInt(EpochKey, Epoch);
            LoadAppliedKeys();

            Interlocked.Exchange(ref _lastTickMs, _clock.ElapsedMilliseconds);
            _daemonPort = UmcpSettings.DaemonPort;

            _control = new UmcpControlServer(ControlStatusJson, ForceReconnect);
            try { _control.Start(); }
            catch (Exception e) { Debug.LogWarning("[umcp] control channel unavailable: " + e.Message); }

            _conn = new UmcpConnection(_daemonPort, _inbox, OnSocketConnected);
            _conn.Start();
            UnityEngine.Debug.Log("[umcp] agent up — daemon 127.0.0.1:" + _daemonPort + ", control port " + (_control == null ? 0 : _control.Port) + ", epoch " + Epoch);

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;

            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            EditorApplication.quitting -= OnQuitting;
            EditorApplication.quitting += OnQuitting;
            CompilationPipeline.compilationStarted -= OnCompileStart;
            CompilationPipeline.compilationStarted += OnCompileStart;
            CompilationPipeline.compilationFinished -= OnCompileFinish;
            CompilationPipeline.compilationFinished += OnCompileFinish;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        // ---------------------------------------------------------------- main-thread pump

        static void Tick()
        {
            // Proof of life. Everything that reports health reads this, and only this.
            Interlocked.Exchange(ref _lastTickMs, _clock.ElapsedMilliseconds);

            // The hello is built here, on the main thread, and nowhere else. It reads
            // Application.productName and the project settings, and every Unity API is
            // main-thread-only — building it on the socket thread throws silently and turns
            // into an endless reconnect loop with no visible cause.
            if (_helloPending)
            {
                _helloPending = false;
                SendHello();
            }

            UmcpConsole.Drain();

            // Drain the whole queue in one tick. This is the finding the design rests on: the
            // Editor executes everything queued between ticks inside a single tick, so N queued
            // operations cost the wall time of one.
            string json;
            int n = 0;
            while (_inbox.TryDequeue(out json))
            {
                Handle(json);
                if (++n >= MaxOpsPerTick) break;
            }

            if (_appliedDirty) { SaveAppliedKeys(); _appliedDirty = false; }
        }

        static void Handle(string json)
        {
            var sw = Stopwatch.StartNew();
            JObject msg;
            string id = null;
            try
            {
                msg = JObject.Parse(json);
                id = (string)msg["id"];
                var t = (string)msg["t"];
                switch (t)
                {
                    case "op": HandleOp(msg, id, sw); break;
                    case "batch": HandleBatch(msg, id, sw); break;
                    default: SendError(id, "E_PROTOCOL", "Unknown message type '" + t + "'.", sw); break;
                }
            }
            catch (Exception e)
            {
                SendError(id, "E_INTERNAL", e.Message, sw);
            }
        }

        static void HandleOp(JObject msg, string id, Stopwatch sw)
        {
            var key = (string)msg["key"];
            string cached;
            if (!string.IsNullOrEmpty(key) && _appliedResults.TryGetValue(key, out cached))
            {
                // Replayed after a domain reload. Answer from the ring buffer rather than
                // applying the mutation twice.
                Send("{\"t\":\"result\",\"id\":" + JsonConvert.ToString(id) + ",\"ok\":true,\"replayed\":true,\"data\":" + cached + ",\"ms\":0}");
                return;
            }

            var tool = (string)msg["tool"];
            var args = msg["args"] as JObject ?? new JObject();
            bool dryRun = msg["dryRun"] != null && (bool)msg["dryRun"];

            try
            {
                var data = Execute(tool, args, dryRun);
                var payload = JsonConvert.SerializeObject(data, UmcpJson.Settings);
                if (!string.IsNullOrEmpty(key) && !dryRun) RememberApplied(key, payload);
                _opsExecuted++;
                Interlocked.Exchange(ref _lastOpMs, _clock.ElapsedMilliseconds);
                Send("{\"t\":\"result\",\"id\":" + JsonConvert.ToString(id) + ",\"ok\":true,\"data\":" + payload +
                     ",\"ms\":" + sw.ElapsedMilliseconds + "}");
            }
            catch (UmcpToolException te) { SendToolError(id, te, sw); }
            catch (Exception e) { SendError(id, "E_TOOL_FAILED", e.Message, sw, tool); }
        }

        internal static object Execute(string tool, JObject args, bool dryRun)
        {
            var meta = ToolDispatch.Meta(tool);
            if (meta == null)
                throw new UmcpToolException("E_TOOL_NOT_FOUND", "No tool named '" + tool + "'.",
                    "tool", tool, Suggest.Closest(tool, ToolDispatch.Ids, 3),
                    "Use unity.find to search the catalog.");

            if (dryRun)
            {
                if (!meta.Mutating)
                    return ToolDispatch.Invoke(tool, args);   // reads are safe to actually run
                return new
                {
                    dryRun = true,
                    tool,
                    mutating = true,
                    undo = meta.Undo,
                    args,
                    note = "Arguments validated and bound; nothing was applied."
                };
            }
            return ToolDispatch.Invoke(tool, args);
        }

        // ---------------------------------------------------------------- batch

        static readonly HashSet<string> BulkAssetOps = new HashSet<string>
        {
            "assets.createFolder", "assets.move", "assets.delete", "material.create", "prefab.create"
        };

        static void HandleBatch(JObject msg, string id, Stopwatch sw)
        {
            var key = (string)msg["key"];
            string cached;
            if (!string.IsNullOrEmpty(key) && _appliedResults.TryGetValue(key, out cached))
            {
                Send("{\"t\":\"result\",\"id\":" + JsonConvert.ToString(id) + ",\"ok\":true,\"replayed\":true,\"data\":" + cached + ",\"ms\":0}");
                return;
            }

            var ops = msg["ops"] as JArray ?? new JArray();
            bool atomic = msg["atomic"] != null && (bool)msg["atomic"];
            bool dryRun = msg["dryRun"] != null && (bool)msg["dryRun"];
            var returns = (string)msg["returns"] ?? "ids";
            var undoName = (string)msg["undoName"] ?? "MCP Batch";

            // One Ctrl+Z for the whole batch.
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);

            // StartAssetEditing is documented as taking a bulk import from ~3 hours to ~3 minutes,
            // but it also defers imports, which breaks any op in the same batch that reads back an
            // asset it just wrote. So: enable it only when the batch is actually asset-heavy.
            int assetWrites = ops.Count(o => BulkAssetOps.Contains((string)o["op"]));
            bool bulk = !dryRun && assetWrites >= 2;
            if (bulk) AssetDatabase.StartAssetEditing();

            var results = new List<object>(ops.Count);
            var refs = new List<JToken>(ops.Count);
            object failure = null;
            int failedIndex = -1;

            try
            {
                for (int i = 0; i < ops.Count; i++)
                {
                    var o = (JObject)ops[i];
                    var tool = (string)o["op"];
                    var args = o["args"] as JObject ?? new JObject();
                    try
                    {
                        var bound = BatchRefs.Resolve(args, refs);
                        var data = Execute(tool, bound, dryRun);
                        results.Add(data);
                        refs.Add(JToken.FromObject(data, UmcpJson.Serializer));
                        _opsExecuted++;
                    }
                    catch (Exception e)
                    {
                        failedIndex = i;
                        var te = e as UmcpToolException;
                        failure = te != null
                            ? (object)new { index = i, op = tool, code = te.Code, message = te.Message, param = te.Param, value = te.Value, didYouMean = te.DidYouMean, hint = te.Hint }
                            : new { index = i, op = tool, code = "E_TOOL_FAILED", message = e.Message, param = (string)null, value = (string)null, didYouMean = (string[])null, hint = (string)null };
                        break;
                    }
                }

                if (failure != null && atomic)
                {
                    Undo.RevertAllDownToGroup(group);
                    results.Clear();
                }
            }
            finally
            {
                if (bulk)
                {
                    AssetDatabase.StopAssetEditing();
                    AssetDatabase.Refresh();
                }
                Undo.CollapseUndoOperations(group);
            }

            Interlocked.Exchange(ref _lastOpMs, _clock.ElapsedMilliseconds);

            object payloadObj = new
            {
                count = results.Count,
                requested = ops.Count,
                atomic,
                reverted = failure != null && atomic,
                undoGroup = group,
                assetEditing = bulk,
                failedIndex = failedIndex < 0 ? (int?)null : failedIndex,
                error = failure,
                results = Project(results, returns)
            };

            var payload = JsonConvert.SerializeObject(payloadObj, UmcpJson.Settings);
            bool ok = failure == null;
            if (!string.IsNullOrEmpty(key) && ok && !dryRun) RememberApplied(key, payload);

            Send("{\"t\":\"result\",\"id\":" + JsonConvert.ToString(id) + ",\"ok\":" + (ok ? "true" : "false") +
                 ",\"data\":" + payload + ",\"ms\":" + sw.ElapsedMilliseconds + "}");
        }

        static object Project(List<object> results, string returns)
        {
            switch (returns)
            {
                case "none": return null;
                case "full": return results;
                case "summary":
                    return results.Select(r =>
                    {
                        var j = JToken.FromObject(r, UmcpJson.Serializer) as JObject;
                        if (j == null) return r;
                        var o = new JObject();
                        foreach (var n in new[] { "id", "name", "path", "count", "deleted", "type" })
                            if (j[n] != null) o[n] = j[n];
                        return o.Count > 0 ? (object)o : r;
                    }).ToList();
                default: // "ids"
                    return results.Select(r =>
                    {
                        var j = JToken.FromObject(r, UmcpJson.Serializer) as JObject;
                        return j != null && j["id"] != null ? j["id"].ToObject<int>() : (object)null;
                    }).ToList();
            }
        }

        // ---------------------------------------------------------------- idempotency ring

        static void RememberApplied(string key, string payload)
        {
            if (_appliedResults.ContainsKey(key)) return;
            _appliedResults[key] = payload.Length > 4096 ? "{\"_elided\":true}" : payload;
            _appliedOrder.Enqueue(key);
            while (_appliedOrder.Count > AppliedKeyRing)
                _appliedResults.Remove(_appliedOrder.Dequeue());
            _appliedDirty = true;
        }

        static void LoadAppliedKeys()
        {
            _appliedOrder.Clear();
            _appliedResults.Clear();
            var raw = SessionState.GetString(AppliedKey, null);
            if (string.IsNullOrEmpty(raw)) return;
            try
            {
                var o = JObject.Parse(raw);
                foreach (var kv in o)
                {
                    _appliedOrder.Enqueue(kv.Key);
                    _appliedResults[kv.Key] = kv.Value.ToString(Formatting.None);
                }
            }
            catch { /* a corrupt ring is not worth a failure; worst case an op replays */ }
        }

        static void SaveAppliedKeys()
        {
            try
            {
                var o = new JObject();
                foreach (var kv in _appliedResults) o[kv.Key] = JToken.Parse(kv.Value);
                // SessionState survives domain reloads, which is exactly the window we need it for.
                SessionState.SetString(AppliedKey, o.ToString(Formatting.None));
            }
            catch { }
        }

        // ---------------------------------------------------------------- lifecycle

        /// <summary>Called on the socket thread. Sets a flag and returns: no Unity API here.</summary>
        static void OnSocketConnected()
        {
            _helloPending = true;
        }

        static void SendHello()
        {
            var hello = new JObject
            {
                ["t"] = "hello",
                ["projectId"] = UmcpSettings.ProjectId,
                ["projectPath"] = UmcpSettings.ProjectRoot,
                ["projectName"] = Application.productName,
                ["unityVersion"] = Application.unityVersion,
                ["pid"] = Process.GetCurrentProcess().Id,
                ["epoch"] = Epoch,
                ["controlPort"] = _control != null ? _control.Port : 0,
                ["toolCount"] = ToolDispatch.Ids.Length,
                ["appliedKeys"] = new JArray(_appliedResults.Keys.Cast<object>().ToArray())
            };
            Send(hello.ToString(Formatting.None));
        }

        static void OnBeforeReload()
        {
            SaveAppliedKeys();
            // Tell the daemon deliberately, so it holds instead of guessing from a dropped socket.
            if (_conn != null) _conn.SendNow("{\"t\":\"event\",\"kind\":\"reload.begin\",\"epoch\":" + Epoch + "}");
            Shutdown();
        }

        static void OnQuitting()
        {
            if (_conn != null) _conn.SendNow("{\"t\":\"event\",\"kind\":\"quitting\",\"epoch\":" + Epoch + "}");
            Shutdown();
        }

        static void OnCompileStart(object _)
        {
            Send("{\"t\":\"event\",\"kind\":\"compile.begin\",\"epoch\":" + Epoch + "}");
        }

        static void OnCompileFinish(object _)
        {
            Send("{\"t\":\"event\",\"kind\":\"compile.end\",\"epoch\":" + Epoch + "}");
        }

        static void OnPlayMode(PlayModeStateChange state)
        {
            Send("{\"t\":\"event\",\"kind\":\"playmode\",\"state\":\"" + state + "\",\"epoch\":" + Epoch + "}");
        }

        static void Shutdown()
        {
            EditorApplication.update -= Tick;
            try { if (_conn != null) _conn.Dispose(); } catch { }
            try { if (_control != null) _control.Dispose(); } catch { }
            _conn = null; _control = null;
        }

        static void ForceReconnect()
        {
            // Called from the control thread. Must not touch Unity APIs: dropping the socket is
            // enough, the reader thread reconnects on its own.
            var c = _conn;
            if (c != null) c.Dispose();
            _conn = new UmcpConnection(_daemonPort, _inbox, OnSocketConnected);
            _conn.Start();
        }

        static string ControlStatusJson()
        {
            // Background thread. Reads interlocked counters and nothing else — no Unity API,
            // which is the entire point: this must answer while the main thread is wedged.
            long tickAge = _clock.ElapsedMilliseconds - Interlocked.Read(ref _lastTickMs);
            long opAge = _clock.ElapsedMilliseconds - Interlocked.Read(ref _lastOpMs);
            return "{\"ok\":true"
                 + ",\"msSinceTick\":" + tickAge
                 + ",\"msSinceOp\":" + opAge
                 + ",\"epoch\":" + Epoch
                 + ",\"queueDepth\":" + _inbox.Count
                 + ",\"opsExecuted\":" + _opsExecuted
                 + ",\"connected\":" + ((_conn != null && _conn.Connected) ? "true" : "false")
                 + ",\"pid\":" + Process.GetCurrentProcess().Id
                 + "}";
        }

        // ---------------------------------------------------------------- send helpers

        static void Send(string json)
        {
            var c = _conn;
            if (c != null) c.Send(json);
        }

        static void SendError(string id, string code, string message, Stopwatch sw, string tool = null)
        {
            var err = new JObject { ["code"] = code, ["message"] = message };
            if (tool != null) err["tool"] = tool;
            Send("{\"t\":\"result\",\"id\":" + JsonConvert.ToString(id) + ",\"ok\":false,\"error\":" +
                 err.ToString(Formatting.None) + ",\"ms\":" + sw.ElapsedMilliseconds + "}");
        }

        static void SendToolError(string id, UmcpToolException te, Stopwatch sw)
        {
            var err = new JObject
            {
                ["code"] = te.Code,
                ["message"] = te.Message,
                ["param"] = te.Param,
                ["value"] = te.Value,
                ["hint"] = te.Hint
            };
            if (te.DidYouMean != null) err["didYouMean"] = new JArray(te.DidYouMean.Cast<object>().ToArray());
            Send("{\"t\":\"result\",\"id\":" + JsonConvert.ToString(id) + ",\"ok\":false,\"error\":" +
                 err.ToString(Formatting.None) + ",\"ms\":" + sw.ElapsedMilliseconds + "}");
        }
    }

    /// <summary>Resolves <c>"$1"</c> / <c>"$1.path"</c> references against earlier results in a batch.</summary>
    internal static class BatchRefs
    {
        public static JObject Resolve(JObject args, List<JToken> previous)
        {
            if (previous.Count == 0) return args;
            var clone = (JObject)args.DeepClone();
            Walk(clone, previous);
            return clone;
        }

        static void Walk(JContainer node, List<JToken> previous)
        {
            foreach (var child in node.Children().ToList())
            {
                var prop = child as JProperty;
                var value = prop != null ? prop.Value : child;

                if (value.Type == JTokenType.String)
                {
                    var replaced = Substitute((string)value, previous);
                    if (replaced != null) value.Replace(replaced);
                }
                else if (value is JContainer)
                {
                    Walk((JContainer)value, previous);
                }
            }
        }

        static JToken Substitute(string s, List<JToken> previous)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '$' || s.Length < 2) return null;

            var body = s.Substring(1);
            var dot = body.IndexOf('.');
            var indexText = dot < 0 ? body : body.Substring(0, dot);
            int index;
            if (!int.TryParse(indexText, out index)) return null;
            if (index < 1 || index > previous.Count)
                throw new UmcpToolException("E_BATCH_REF",
                    "'" + s + "' refers to op " + index + ", which has not produced a result.",
                    null, s, null, "References are 1-based and may only point at earlier ops.");

            var result = previous[index - 1];
            if (dot < 0)
            {
                // Bare "$N" means "the thing op N made": prefer its instance id.
                var o = result as JObject;
                if (o != null && o["id"] != null) return new JValue("#" + o["id"].ToObject<int>());
                if (o != null && o["path"] != null) return o["path"].DeepClone();
                return result.DeepClone();
            }

            var field = body.Substring(dot + 1);
            var token = result.SelectToken(field);
            if (token == null)
                throw new UmcpToolException("E_BATCH_REF",
                    "Op " + index + " produced no field '" + field + "'.", null, s);
            return token.DeepClone();
        }
    }

    internal static class UmcpJson
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };

        public static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);
    }
}
