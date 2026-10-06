using AutomaticDSP.Serialization;
using UnityEngine;

// 仅替换原生状态与订单下达，不模拟游戏物理；执行真实 Mod 的移动、接管和清理代码。
namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public float sqrMagnitude => x * x + y * y + z * z;
        public static float Distance(Vector3 a, Vector3 b) => MathF.Sqrt(
            (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z));
    }
}
public enum ECommand { None, Build }
public enum EViewMode { Normal, Globe }
public static class UIGame { public static EViewMode viewMode; }
public sealed class KeyState { public bool onDown; }
public static class VFInput
{
    public static bool inFullscreenGUI, inputing, inScreenshotMode, _warpKey, _sailSpeedUp, _sailLockCursor;
    public static KeyState rtsStop = new();
}
public sealed class OrderNode
{
    public Vector3 target;
    public bool targetReached;
    public static OrderNode MoveTo(Vector3 target) => new() { target = target };
}
public sealed class NativeCommand { public ECommand type; public void SetNoneCommand() => type = ECommand.None; }
public sealed class BuildAction { public void Close() { } }
public sealed class PlayerController
{
    public NativeCommand cmd = new();
    public Vector3 input0, input1;
    public BuildAction actionBuild = new();
}
public sealed class PlayerNavigation { public bool navigating; }
public sealed class Player
{
    public Vector3 position;
    public int planetId = 1;
    public PlayerController controller = new();
    public PlayerNavigation navigation = new();
    public OrderNode currentOrder;
    public int issued, cleared;
    public void Order(OrderNode order, bool multiple) { currentOrder = order; issued++; }
    public void ClearOrders() { currentOrder = null; cleared++; }
}
public static class GameMain { public static Player mainPlayer = new(); public static long gameTick; }
namespace AutomaticDSP.Tasks
{
    internal static class TaskStatusNames { public const string CommandFailed = "FAILED", CommandSucceeded = "SUCCEEDED"; }
    internal sealed class FlightInput { public void Dispose() { } }
    internal sealed class CommandState
    {
        public bool ActionIssued, EnteredBuildMode, OwnsPlayerOrders = true;
        public OrderNode NativeMoveOrder;
        public int MovePlanetId;
        public long MoveProgressTick;
        public double MoveProgressDistance;
        public Vector3 Target = new() { x = 100 };
        public double Tolerance = 2;
        public string Phase, Status = "RUNNING", ErrorCode, NormalizedType = "moveto";
        public object Result;
        public FlightInput Flight;
    }
    internal sealed partial class TaskCommandExecutor
    {
        private void finishCommand(CommandState c, string status, string code, string message, DateTimeOffset now, object result)
        { c.Status = status; c.ErrorCode = code; c.Result = result; }
        private static bool TryGetPlayer(out Player player, out string code, out string message)
        { player = GameMain.mainPlayer; code = message = null; return true; }
        private static bool TryValidateCurrentPlanet(CommandState c, Player p, out string code, out string message)
        { code = message = null; return true; }
        private static bool TryGetVector(CommandState c, string name, out Vector3 target, out string message)
        { target = c.Target; message = null; return true; }
        private static double GetDouble(CommandState c, string name, double fallback) => c.Tolerance;
        private static object Vector(Vector3 v) => new JsonObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
        public void Execute(CommandState c) => ExecuteMoveToLocked(c, DateTimeOffset.UtcNow);
    }
}
