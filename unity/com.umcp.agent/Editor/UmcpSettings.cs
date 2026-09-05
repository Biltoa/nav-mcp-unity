using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Umcp.Agent
{
    /// <summary>
    /// Project identity and daemon endpoint.
    ///
    /// The project id is a GUID minted once and stored under ProjectSettings/, never a port number.
    /// Port-per-project identity is exactly how one Unity project's bridge ends up serving another
    /// project's requests.
    /// </summary>
    internal static class UmcpSettings
    {
        public const int DefaultDaemonPort = 8731;   // agent channel. 8086/8090 are taken on this machine.
        const string SettingsFile = "ProjectSettings/UnityMCP.json";
        const string PortEnv = "UMCP_AGENT_PORT";
        const string DisableEnv = "UMCP_DISABLE";

        [Serializable]
        class Model
        {
            public string projectId;
            public int daemonPort = DefaultDaemonPort;
            public bool enabled = true;
        }

        static Model _model;

        static Model Load()
        {
            if (_model != null) return _model;
            var path = Path.Combine(ProjectRoot, SettingsFile);
            try
            {
                if (File.Exists(path)) _model = JsonUtility.FromJson<Model>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[umcp] could not read " + SettingsFile + ": " + e.Message);
            }
            if (_model == null) _model = new Model();
            if (string.IsNullOrEmpty(_model.projectId))
            {
                _model.projectId = Guid.NewGuid().ToString("N");
                Save();
            }
            return _model;
        }

        static void Save()
        {
            try
            {
                var path = Path.Combine(ProjectRoot, SettingsFile);
                File.WriteAllText(path, JsonUtility.ToJson(_model, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[umcp] could not write " + SettingsFile + ": " + e.Message);
            }
        }

        public static string ProjectRoot
        {
            get { return Path.GetDirectoryName(Application.dataPath).Replace('\\', '/'); }
        }

        public static string ProjectId { get { return Load().projectId; } }

        public static int DaemonPort
        {
            get
            {
                var env = Environment.GetEnvironmentVariable(PortEnv);
                int p;
                if (!string.IsNullOrEmpty(env) && int.TryParse(env, out p)) return p;
                return Load().daemonPort;
            }
        }

        public static bool Enabled
        {
            get
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DisableEnv))) return false;
                return Load().enabled;
            }
        }

        public static void SetEnabled(bool value)
        {
            Load().enabled = value;
            Save();
        }
    }
}
