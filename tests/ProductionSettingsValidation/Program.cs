using AutomaticDSP.Tasks;
using Newtonsoft.Json.Linq;

// 链接真实命令实现；假对象仅记录原生方法调用，不证明游戏内产量或 UI 行为。
int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; Console.WriteLine("通过：" + message); }
CommandState Run(string type, params (string, object)[] args) {
    var c = new CommandState { NormalizedType = type, Args = new JObject() };
    foreach (var (key, value) in args) c.Args[key] = new JValue(value);
    new TaskCommandExecutor().Run(c);
    return c;
}
GameMain.localPlanet = new Planet { factory = new PlanetFactory() };
GameMain.mainPlayer = new Player();
GameMain.history = new GameHistoryData();
var f = GameMain.localPlanet.factory;
f.entityPool[1] = new EntityData { id = 1, protoId = 2208, powerGenId = 1 };
f.entityPool[2] = new EntityData { id = 2, assemblerId = 1 };
f.entityPool[3] = new EntityData { id = 3, labId = 1 };
f.entityPool[4] = new EntityData { id = 4, ejectorId = 1 };
f.powerSystem.genPool[1] = new PowerGeneratorComponent { id = 1, gamma = true };
f.factorySystem.assemblerPool[1] = new AssemblerComponent { id = 1, recipeExecuteData = new RecipeExecuteData { productive = true } };
f.factorySystem.labPool[1] = new LabComponent { id = 1, matrixMode = true };
f.factorySystem.ejectorPool[1] = new EjectorComponent { id = 1 };
f.dysonSphere.swarm.orbits[1] = new Orbit { id = 1, enabled = true };

GameMain.history.itemUnlocked = false;
var c = Run("setrayreceivermode", ("entityId", 1), ("mode", "photon"));
Check(c.Error == "item_locked" && f.powerSystem.genPool[1].productId == 0, "光子锁定拒绝且不改模式");
GameMain.history.itemUnlocked = true;
c = Run("setrayreceivermode", ("entityId", 1), ("mode", "photon"));
Check(c.Status == "SUCCEEDED" && f.powerSystem.genPool[1].productId == 1208, "光子模式使用建筑原型指定产物");
f.powerSystem.genPool[1].productCount = 7.75f;
Run("setrayreceivermode", ("entityId", 1), ("mode", "photon"));
Check(f.powerSystem.genPool[1].productCount == 7.75f && GameMain.mainPlayer.refundCount == 0, "重复光子设置不清空缓存");
c = Run("setrayreceivermode", ("entityId", 1), ("mode", "power"));
Check(c.Status == "SUCCEEDED" && f.powerSystem.genPool[1].productId == 0 && f.powerSystem.genPool[1].productCount == 0 &&
    GameMain.mainPlayer.refundItem == 1208 && GameMain.mainPlayer.refundCount == 7 && GameMain.mainPlayer.throwTrash,
    "切回发电通过原生背包入口返还整数产物并允许溢出垃圾");
Run("setrayreceivermode", ("entityId", 1), ("mode", "power"));
Check(GameMain.mainPlayer.refundCount == 7, "重复发电设置不重复退款");
f.powerSystem.genPool[1].gamma = false;
Check(Run("setrayreceivermode", ("entityId", 1), ("mode", "photon")).Error == "invalid_target", "普通发电机拒绝射线模式");
f.powerSystem.genPool[1].gamma = true;
Check(Run("setrayreceivermode", ("entityId", 1), ("mode", true)).Error == "invalid_command", "模式类型严格校验");

GameMain.history.techUnlocked = false;
Check(Run("setproliferatormode", ("entityId", 2), ("mode", "speed")).Error == "tech_locked" &&
    !f.factorySystem.assemblerPool[1].forceAccMode, "增产科技未解锁拒绝切换");
GameMain.history.techUnlocked = true;
c = Run("setproliferatormode", ("entityId", 2), ("mode", "speed"));
Check(c.Status == "SUCCEEDED" && f.factorySystem.assemblerPool[1].forceAccMode, "制造组件切换加速");
Run("setproliferatormode", ("entityId", 2), ("mode", "extra"));
Check(!f.factorySystem.assemblerPool[1].forceAccMode, "制造组件恢复额外产出");
f.factorySystem.assemblerPool[1].recipeExecuteData.productive = false;
Check(Run("setproliferatormode", ("entityId", 2), ("mode", "speed")).Error == "invalid_recipe" &&
    !f.factorySystem.assemblerPool[1].forceAccMode, "不支持额外产出的配方拒绝切换");
f.factorySystem.assemblerPool[1].recipeExecuteData = null;
Check(Run("setproliferatormode", ("entityId", 2), ("mode", "speed")).Error == "invalid_recipe", "未设配方拒绝切换");
c = Run("setproliferatormode", ("entityId", 3), ("mode", "speed"));
Check(c.Status == "SUCCEEDED" && f.factorySystem.labPool[1].forceAccMode && f.factorySystem.syncLabId == 1,
    "矩阵生产模式调用原生堆叠同步入口");
f.factorySystem.syncLabId = 0;
f.factorySystem.labPool[1].matrixMode = false;
Check(Run("setproliferatormode", ("entityId", 3), ("mode", "extra")).Error == "invalid_recipe" &&
    f.factorySystem.labPool[1].forceAccMode && f.factorySystem.syncLabId == 0, "科研模式拒绝写入及同步");

c = Run("setejectororbit", ("entityId", 4), ("orbitId", 1), ("autoOrbit", true));
Check(c.Status == "SUCCEEDED" && f.factorySystem.ejectorPool[1].orbitId == 1 &&
    f.factorySystem.ejectorPool[1].autoOrbit && f.factorySystem.ejectorPool[1].calls == 1, "轨道设置调用原生 SetOrbit 并设置自动换轨");
Run("setejectororbit", ("entityId", 4), ("orbitId", 0));
Check(f.factorySystem.ejectorPool[1].orbitId == 0 && f.factorySystem.ejectorPool[1].autoOrbit, "清除轨道保留未提供的自动换轨设置");
f.dysonSphere.swarm.orbits[1].enabled = false;
Check(Run("setejectororbit", ("entityId", 4), ("orbitId", 1), ("autoOrbit", false)).Error == "invalid_orbit" &&
    f.factorySystem.ejectorPool[1].autoOrbit, "禁用轨道拒绝且不部分修改自动换轨");
Check(Run("setejectororbit", ("entityId", 4), ("orbitId", 2)).Error == "invalid_orbit", "不存在轨道拒绝");
foreach (object bad in new object[] { -1, 1.5, "1", 2147483648L, true })
    Check(Run("setejectororbit", ("entityId", 4), ("orbitId", bad)).Error == "invalid_command", "非法轨道值拒绝：" + bad);
Check(Run("setejectororbit", ("entityId", 4), ("orbitId", 0), ("autoOrbit", "false")).Error == "invalid_command", "自动换轨必须是布尔值");
Check(f.factorySystem.ejectorPool[1].calls == 2, "所有失败轨道命令未调用原生修改入口");
f.entityPool[4].pos = new UnityEngine.Vector3 { x = 81 };
Check(Run("setejectororbit", ("entityId", 4), ("orbitId", 0)).Error == "out_of_range", "超建造范围拒绝");
Check(Run("setejectororbit", ("entityId", 999), ("orbitId", 0)).Error == "target_not_found", "无效实体拒绝");
Console.WriteLine($"完成 {checks} 项控制边界验证。");

namespace AutomaticDSP.Tasks {
    internal static class TaskStatusNames { public const string CommandSucceeded = "SUCCEEDED", CommandFailed = "FAILED"; }
    internal class TaskState { }
    internal class CommandState { public string NormalizedType, Status, Error; public JObject Args; }
    internal sealed partial class TaskCommandExecutor {
        public void Run(CommandState command) => ExecuteProductionSetting(new TaskState(), command, DateTimeOffset.UtcNow);
        private void finishCommand(CommandState c, string status, string code, string message, DateTimeOffset now, object result) { c.Status = status; c.Error = code; }
        private static bool TryGetPlayer(out Player p, out string code, out string message) { p = GameMain.mainPlayer; code = message = null; return p != null; }
        private static bool TryValidateCurrentPlanet(CommandState c, Player p, out string code, out string message) { code = message = null; return true; }
        private static bool TryGetTargetEntityId(TaskState t, CommandState c, out int id, out string message) { id = c.Args.Value<int>("entityId"); message = null; return true; }
        private static bool TryGetEntity(PlanetFactory f, int id, out EntityData e) { e = id > 0 && id < f.entityPool.Length ? f.entityPool[id] : default; return e.id == id && id > 0; }
        private static bool TryGetToken(CommandState c, string name, out JToken token) => c.Args.TryGetValue(name, out token);
        private static object RangeFailureResult(float distance, float range, UnityEngine.Vector3 pos) => null;
    }
}
namespace UnityEngine {
    public struct Vector3 { public float x, y, z; public static float Distance(Vector3 a, Vector3 b) => MathF.Sqrt((a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y)+(a.z-b.z)*(a.z-b.z)); }
}
public static class GameMain { public static Player mainPlayer; public static Planet localPlanet; public static GameHistoryData history; }
public class Planet { public PlanetFactory factory; }
public class Player {
    public UnityEngine.Vector3 position; public Mecha mecha = new(); public int refundItem, refundCount; public bool throwTrash;
    public int TryAddItemToPackage(int item, int count, int inc, bool throwTrash) { refundItem = item; refundCount += count; this.throwTrash = throwTrash; return count; }
}
public class Mecha { public float buildArea = 80; }
public class GameHistoryData { public bool itemUnlocked = true, techUnlocked = true; public bool ItemUnlocked(int id) => itemUnlocked; public bool TechUnlocked(int id) => id == 1151 && techUnlocked; }
public static class LDB { public static Items items = new(); }
public class Items { public ItemProto Select(int id) => id == 2208 || id == 1208 ? new ItemProto() : null; }
public class ItemProto { public PrefabDesc prefabDesc = new(); }
public class PrefabDesc { public int powerProductId = 1208; }
public struct EntityData { public int id, protoId, powerGenId, ejectorId, assemblerId, labId; public UnityEngine.Vector3 pos; }
public class PlanetFactory { public EntityData[] entityPool = new EntityData[8]; public PowerSystem powerSystem = new(); public FactorySystem factorySystem = new(); public DysonSphere dysonSphere = new(); }
public class PowerSystem { public PowerGeneratorComponent[] genPool = new PowerGeneratorComponent[2]; }
public struct PowerGeneratorComponent { public int id, productId; public bool gamma; public float productCount; }
public class FactorySystem {
    public AssemblerComponent[] assemblerPool = new AssemblerComponent[2]; public LabComponent[] labPool = new LabComponent[2]; public EjectorComponent[] ejectorPool = new EjectorComponent[2]; public int syncLabId;
    public void SyncLabForceAccMode(Player p, int id) { syncLabId = id; }
}
public struct AssemblerComponent { public int id; public RecipeExecuteData recipeExecuteData; public bool forceAccMode; }
public class RecipeExecuteData { public bool productive; }
public struct LabComponent { public int id; public bool matrixMode, forceAccMode; }
public struct EjectorComponent { public int id, orbitId, calls; public bool autoOrbit; public void SetOrbit(int id) { orbitId = id; calls++; } }
public class DysonSphere { public DysonSwarm swarm = new(); }
public class DysonSwarm { public int orbitCursor = 2; public Orbit[] orbits = new Orbit[3]; }
public struct Orbit { public int id; public bool enabled; }
