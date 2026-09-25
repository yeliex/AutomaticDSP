using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System.IO;
using AutomaticDSP.Api;
using AutomaticDSP.GameControl;
using AutomaticDSP.State;
using AutomaticDSP.Storage;
using AutomaticDSP.Tasks;
using AutomaticDSP.UI;

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
        private ConfigEntry<int> queryIntervalTicks;
        private HistoryStore historyStore;
        private HttpApiServer httpServer;
        private GameControlService gameControlService;
        private GameStateQueryService stateQueryService;
        private TaskQueueService taskQueueService;
        private TaskStatusOverlay taskStatusOverlay;

        private void Awake()
        {
            LogSource = Logger;
            httpEnabled = Config.Bind("HTTP", "Enabled", true, "Enable local read-only HTTP API.");
            httpHost = Config.Bind("HTTP", "Host", "127.0.0.1", "Local HTTP API bind host. Use 0.0.0.0 to listen on all interfaces; Windows may require an HTTP URLACL for that.");
            httpPort = Config.Bind("HTTP", "Port", 39270, "Local HTTP API port.");
            queryIntervalTicks = Config.Bind("State", "QueryIntervalTicks", 60, "Game ticks between state query batches.");

            var cacheRoot = Path.Combine(Paths.CachePath, PluginName);
            historyStore = new HistoryStore(cacheRoot, Logger);
            historyStore.Initialize();

            taskQueueService = new TaskQueueService(historyStore, Logger);
            gameControlService = new GameControlService(Logger);
            stateQueryService = new GameStateQueryService(queryIntervalTicks.Value, Logger);
            taskStatusOverlay = new TaskStatusOverlay(taskQueueService, Logger);

            if (httpEnabled.Value)
            {
                try
                {
                    httpServer = new HttpApiServer(httpHost.Value, httpPort.Value, stateQueryService, gameControlService, taskQueueService, historyStore, Logger);
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
            GameNoticeService.Update();
            stateQueryService?.Update();
            gameControlService?.Update();
            taskQueueService?.Update();
            taskStatusOverlay?.Update();
        }

        private void OnDestroy()
        {
            taskStatusOverlay?.Dispose();
            httpServer?.Dispose();
            historyStore?.Dispose();
        }
    }
}
