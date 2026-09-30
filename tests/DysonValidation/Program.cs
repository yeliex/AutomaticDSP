using AutomaticDSP.Tasks;
using Newtonsoft.Json.Linq;

// 链接真实命令入口；假原生对象记录写入，不模拟几何或材料生产。
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("通过：" + name); }
CommandState Run(string type, string json) {
    var c = new CommandState { NormalizedType = type.ToLowerInvariant(), Args = JObject.Parse(json) };
    new TaskCommandExecutor().Run(c); return c;
}
var sphere = GameMain.data.Sphere;
var swarm = sphere.swarm;
const string orbit = "{starId:1,radius:12000,inclination:30,longitude:60}";
GameMain.history.dysonSphereSystemUnlocked = false;
Check(Run("createDysonOrbit", orbit).Error == "tech_locked" && GameMain.data.Calls == 0, "云科技锁定不初始化恒星对象");
GameMain.history.dysonSphereSystemUnlocked = true;
foreach (var input in new[]{"{starId:'1',radius:12000,inclination:0,longitude:0}", "{starId:1.5,radius:12000,inclination:0,longitude:0}", "{starId:99,radius:12000,inclination:0,longitude:0}", "{starId:1,radius:'12000',inclination:0,longitude:0}", "{starId:1,radius:12000,inclination:181,longitude:0}", "{starId:1,radius:12000,inclination:0,longitude:-1}", "{starId:1,radius:NaN,inclination:0,longitude:0}"})
    Check(Run("createDysonOrbit", input).Error == "invalid_command" && GameMain.data.Calls == 0, "拒绝错误类型或越界轨道参数 " + input);
sphere.Condition = -3;
Check(Run("createDysonOrbit", orbit).Error == "invalid_orbit" && swarm.Writes == 0, "原生半径拒绝不创建轨道");
sphere.Condition = 0;
var created = Run("createDysonOrbit", orbit);
Check(created.Status == "SUCCEEDED" && swarm.Writes == 1 && swarm.orbits[2].radius == 12000, "通过原生校验后创建轨道");
var writes = swarm.Writes;
foreach (var input in new[]{"{starId:1,orbitId:30,enabled:true}", "{starId:1,orbitId:2,enabled:'false'}"})
    Check(Run("setDysonOrbitEnabled", input).Error == "invalid_command" && swarm.Writes == writes, "无效开关不写入");
Check(Run("setDysonOrbitEnabled", "{starId:1,orbitId:1,enabled:false}").Error == "protected_orbit" && swarm.Writes == writes, "默认轨道不能停用");
Check(Run("removeDysonOrbit", "{starId:1,orbitId:1}").Error == "protected_orbit" && swarm.Writes == writes, "默认轨道不能删除");
swarm.Sails = 1;
Check(Run("removeDysonOrbit", "{starId:1,orbitId:2}").Error == "orbit_not_empty" && swarm.Writes == writes, "有帆轨道不能直接删除");
swarm.Sails = 0;
Check(Run("removeDysonOrbit", "{starId:1,orbitId:2}").Status == "SUCCEEDED" && !swarm.OrbitExist(2), "空轨道使用原生删除");
Check(Run("editDysonOrbit", "{starId:1,orbitId:2,radius:12000,inclination:0,longitude:0}").Error == "target_not_found", "不存在轨道不能编辑");
swarm.Full = true;
Check(Run("createDysonOrbit", orbit).Error == "orbit_limit", "保留原生轨道数量限制");
GameMain.history.dysonSphereLayerPanelUnlocked = false;
var calls = GameMain.data.Calls;
Check(Run("createDysonLayer", orbit).Error == "tech_locked" && GameMain.data.Calls == calls, "球层科技锁定不初始化对象");
GameMain.history.dysonSphereLayerPanelUnlocked = true;
sphere.Condition = -1;
Check(Run("createDysonLayer", orbit).Error == "invalid_orbit" && sphere.Writes == 0, "原生层间距拒绝不创建层");
sphere.Condition = 0; sphere.Full = true;
Check(Run("createDysonLayer", orbit).Error == "layer_limit" && sphere.Writes == 0, "保留十层限制");
sphere.Full = false;
Check(Run("createDysonLayer", orbit).Status == "SUCCEEDED" && sphere.layersIdBased[1].orbitRadius == 12000, "原生半径与角速度创建球层");
Check(Run("editDysonLayer", "{starId:1,layerId:1,radius:13000,inclination:0,longitude:0}").Error == "invalid_command" && sphere.layersIdBased[1].orbitRadius == 12000, "禁止修改已建层半径");
Check(Run("editDysonLayer", "{starId:1,layerId:11,inclination:0,longitude:0}").Error == "invalid_command", "层 ID 上限");
Check(Run("editDysonLayer", "{starId:1,layerId:1,inclination:10,longitude:20}").Status == "SUCCEEDED" && sphere.layersIdBased[1].Rotations == 1, "原生朝向过渡");
var layer = sphere.layersIdBased[1];
Check(Run("removeDysonLayer", "{starId:1,layerId:1}").Status == "SUCCEEDED" && layer.Removed && sphere.layersIdBased[1] == null, "先拆结构再移除球层");
Console.WriteLine($"戴森边界验证通过：{checks} 项");

namespace UnityEngine {
    public struct Quaternion { public static Quaternion Euler(float x,float y,float z) => new(); }
}
public class History { public bool dysonSphereSystemUnlocked = true, dysonSphereLayerPanelUnlocked = true; }
public class Star { public int index; }
public class Galaxy { public Star StarById(int id) => id == 1 ? new Star() : null; }
public class Data { public readonly Sphere Sphere = new(); public int Calls; public Sphere CreateDysonSphere(int i) { Calls++; return Sphere; } }
public static class GameMain { public static Data data = new(); public static History history = new(); public static Galaxy galaxy = new(); }
public class Orbit { public float radius; public bool enabled = true; }
public class Swarm {
    public Orbit[] orbits = new Orbit[21]; public int Writes, Sails; public bool Full;
    public Swarm() { orbits[1] = new(); }
    public bool OrbitExist(int id) => orbits[id] != null;
    public bool OrbitEnabled(int id) => orbits[id].enabled;
    public int NewOrbit(float r,UnityEngine.Quaternion q) { if(Full)return -1; Writes++;orbits[2]=new(){radius=r};return 2; }
    public void EditOrbit(int id,float r,UnityEngine.Quaternion q) { Writes++;orbits[id].radius=r; }
    public void SetOrbitEnable(int id,bool e) { Writes++;orbits[id].enabled=e; }
    public int SailCountOnOrbit(int id) => Sails;
    public void RemoveOrbit(int id) { Writes++;orbits[id]=null; }
}
public class Layer {
    public int id,Rotations; public float orbitRadius; public bool Removed;
    public UnityEngine.Quaternion targetOrbitRotation,orbitRotation;
    public void InitOrbitRotation(UnityEngine.Quaternion a,UnityEngine.Quaternion b) { Rotations++; }
    public void RemoveAllStructure() { Removed=true; }
}
public class Sphere {
    public readonly Swarm swarm = new(); public Layer[] layersIdBased = new Layer[11]; public int Condition,Writes; public bool Full;
    public int CheckSwarmRadius(float r) => Condition;
    public int CheckLayerRadius(float r) => Condition;
    public int QueryLayerId() => Full ? 0 : 1;
    public bool QueryLayerRadius(ref float r,out float speed) {speed=1;return true;}
    public Layer AddLayer(float r,UnityEngine.Quaternion q,float speed) {Writes++;return layersIdBased[1]=new(){id=1,orbitRadius=r};}
    public void RemoveLayer(Layer l) {if(!l.Removed)throw new Exception("必须先拆结构");Writes++;layersIdBased[l.id]=null;}
}
namespace AutomaticDSP.Tasks {
    internal static class TaskStatusNames { public const string CommandFailed="FAILED",CommandSucceeded="SUCCEEDED"; }
    internal class CommandState { public string NormalizedType,Status,Error; public JObject Args; public object Result; }
    internal sealed partial class TaskCommandExecutor {
        public void Run(CommandState c) { if(c.NormalizedType.EndsWith("layer")) ExecuteDysonLayer(c,DateTimeOffset.UtcNow);else ExecuteDysonOrbit(c,DateTimeOffset.UtcNow); }
        private static bool TryGetToken(CommandState c,string name,out JToken token) => c.Args.TryGetValue(name,out token);
        private static void finishCommand(CommandState c,string s,string e,string m,DateTimeOffset n,object r) {c.Status=s;c.Error=e;c.Result=r;}
    }
}
