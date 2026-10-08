using AutomaticDSP.Serialization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x;
        public float sqrMagnitude => x * x;
        public static float Distance(Vector3 a, Vector3 b) => Math.Abs(a.x - b.x);
    }
}
public enum EPlanetType { Desert, Gas }
public enum EObjectType { None }
public class Player
{
    public bool isAlive = true, sailing;
    public PlanetFactory factory = new();
    public Controller controller = new();
    public Mecha mecha = new();
    public Package package = new();
    public Vector3 position;
    public int inhandItemId, inhandItemCount, HandSelections, Dropped;
    public long sandCount = 10;
    // 模拟原生 SetHandItems 的库存收起语义，记录调用而非直接清空测试断言。
    public void SetHandItems(int id, int count)
    {
        HandSelections++;
        var accepted = Math.Min(inhandItemCount, package.Capacity - package.Count);
        package.Count += accepted;
        Dropped += inhandItemCount - accepted;
        inhandItemId = id;
        inhandItemCount = count;
    }
    public void ClearOrders() { }
}
public class Controller { public Build actionBuild = new(); public Mine actionMine = new(); public string cmd = "无指令"; }
public class Build { public void SetFactoryReferences() { } }
public class Mine { public EObjectType miningType; public int miningId, miningTick; }
public class Mecha { public float buildArea = 10; }
public class Package { public int Count = 10, Capacity = 100; public int GetItemCount(int id) => Count; }
public class PlanetFactory { public Planet planet = new(); public Platform platformSystem = new(); }
public class Planet { public bool factoryLoaded = true; public EPlanetType type; public PlanetData data = new(); }
public class PlanetData { public float QueryModifiedHeight(Vector3 v) => 0; }
public class Platform
{
    public byte[] reformData = { 32 };
    public int maxReformCount => 1;
    public int GetReformType(int i) => reformData[i] >> 5;
    public int GetReformColor(int i) => reformData[i] & 31;
}
public static class GameMain
{
    public static Player mainPlayer;
    public static Planet localPlanet;
    public static GameData data = new();
    public static History history = new();
    public static Scenario gameScenario = new();
    public static bool sandboxToolsEnabled;
}
public class GameData { public bool guideRunning; }
public class History { public bool ItemUnlocked(int id) => true; public bool HasFeatureKey(int id) => false; }
public class Scenario { public void NotifyOnDoReformOpt(int stage) { } }
public static class LDB { public static Items items = new(); }
public class Items { public object Select(int id) => new(); }
namespace AutomaticDSP.Tasks
{
    internal class CommandState { public string Mode, Status, Error; }
    internal static class TaskStatusNames { public const string CommandFailed = "FAILED", CommandSucceeded = "SUCCEEDED"; }
    internal sealed partial class TaskCommandExecutor
    {
        public void Run(CommandState command) => ExecuteReformTerrain(command, DateTimeOffset.Now);
        private void finishCommand(CommandState c, string status, string code, string message, DateTimeOffset now, object result)
        { c.Status = status; c.Error = code; }
        private bool TryGetPlayer(out Player player, out string code, out string message)
        { player = GameMain.mainPlayer; code = message = null; return true; }
        private bool TryValidateCurrentPlanet(CommandState c, Player p, out string code, out string message)
        { code = message = null; return true; }
        private static string GetString(CommandState c, string name, string fallback) => c.Mode;
        private static bool TryGetToken(CommandState c, string name, out JToken token) { token = null; return false; }
        private static int GetInt(CommandState c, string name, int fallback) => fallback;
        private static bool GetBool(CommandState c, string name, bool fallback) => fallback;
        private static bool TryGetVector(CommandState c, string name, out Vector3 v, out string error)
        { v = new Vector3 { x = 1 }; error = null; return true; }
        private static object RangeFailureResult(float distance, float range, Vector3 position) => null;
        private static object Vector(Vector3 v) => null;
    }
    internal class AutomationReformTool
    {
        public static bool Available => true;
        public static Action ApplyEffect;
        public static bool ThrowAfterApply, Freed;
        public object handItem;
        public int reformMode, brushSize, brushType, brushColor, disableOrExtra;
        public bool buryVeins;
        public Vector3 reformCenterPoint;
        public int cursorPointCount = 1;
        public int[] cursorIndices = { 0 };
        public void _Init(GameData data) { }
        public void _Free() => Freed = true;
        public void SetFactoryReferences() { }
        public void Prepare(Vector3 position) => reformCenterPoint = position;
        public int CalculateSand() => 0;
        public void Apply(int sand)
        {
            var p = GameMain.mainPlayer;
            if (reformMode == 1)
            {
                p.factory.platformSystem.reformData[0] = 0;
                p.inhandItemCount++;
            }
            else if (p.inhandItemCount > 0) p.inhandItemCount--;
            else p.package.Count--;
            ApplyEffect?.Invoke();
            if (ThrowAfterApply) throw new InvalidOperationException("模拟原生执行异常");
        }
    }
}
