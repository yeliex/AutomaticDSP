using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System.IO;
using AutomaticDSP.Api;
using AutomaticDSP.State;
using AutomaticDSP.Storage;
using AutomaticDSP.Tasks;

namespace AutomaticDSP
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.yeliex.automaticdsp";
        public const string PluginName = "AutomaticDSP";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource LogSource { get; private set; }

        private ConfigEntry<bool> httpEnabled;
        private ConfigEntry<string> httpHost;
        private ConfigEntry<int> httpPort;
        private ConfigEntry<int> snapshotIntervalTicks;
        private HistoryStore historyStore;
        private HttpApiServer httpServer;
        private StateSnapshotService snapshotService;
        private TaskStateStore taskStateStore;

        private void Awake()
        {
            LogSource = Logger;
            httpEnabled = Config.Bind("HTTP", "Enabled", true, "Enable local read-only HTTP API.");
            httpHost = Config.Bind("HTTP", "Host", "127.0.0.1", "Local HTTP API bind host. Use 0.0.0.0 to listen on all interfaces.");
            httpPort = Config.Bind("HTTP", "Port", 39270, "Local HTTP API port.");
            snapshotIntervalTicks = Config.Bind("State", "SnapshotIntervalTicks", 60, "Game ticks between state snapshots.");

            var cacheRoot = Path.Combine(Paths.CachePath, PluginName);
            historyStore = new HistoryStore(cacheRoot, Logger);
            historyStore.Initialize();

            taskStateStore = new TaskStateStore();
            snapshotService = new StateSnapshotService(snapshotIntervalTicks.Value, cacheRoot, Logger);

            if (httpEnabled.Value)
            {
                try
                {
                    httpServer = new HttpApiServer(httpHost.Value, httpPort.Value, snapshotService, taskStateStore, historyStore, Logger);
                    httpServer.Start();
                }
                catch (System.Exception ex)
                {
                    Logger.LogError($"Failed to start AutomaticDSP HTTP API: {ex}");
                    httpServer?.Dispose();
                    httpServer = null;
                }
            }

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update()
        {
            snapshotService?.Update();
        }

        private void OnDestroy()
        {
            httpServer?.Dispose();
            historyStore?.Dispose();
        }
    }
}
