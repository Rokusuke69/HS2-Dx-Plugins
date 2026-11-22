using BepInEx;
using BepInEx.Logging;

namespace HS2_MyFirstPlugin
{
    [BepInPlugin(GUID, PluginName, PluginVersion)]
    [BepInProcess("StudioNEOV2.exe")]
    [BepInProcess("StudioNEOV2")]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.user.hs2.myfirstplugin";
        public const string PluginName = "My First Plugin";
        public const string PluginVersion = "1.0.0";

        internal static new ManualLogSource Logger;

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo($"Hello World! {PluginName} v{PluginVersion} loaded for Studio NEO V2.");
        }
    }
}
