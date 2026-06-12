using BepInEx;
using BepInEx.Logging;

namespace AutomaticDSP
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.yeliex.automaticdsp";
        public const string PluginName = "AutomaticDSP";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource LogSource { get; private set; }

        private void Awake()
        {
            LogSource = Logger;
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }
    }
}
