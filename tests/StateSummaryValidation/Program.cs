using AutomaticDSP.Serialization;
using AutomaticDSP.State;

GameStateQueryService.Validate();

namespace AutomaticDSP.State
{
    // 只隔离游戏数据和数学类型；汇总逻辑链接真实源文件，不模拟 Unity 行为。
    internal sealed partial class GameStateQueryService
    {
        private static JsonObject Unavailable(string reason) => new() { ["available"] = false, ["reason"] = reason };
        private static object Vector(UnityEngine.Vector3 value) => value;
        private static void Check(bool value, string name)
        {
            if (!value) throw new Exception(name);
            Console.WriteLine("通过：" + name);
        }
        public static void Validate()
        {
            var local = new PlanetFactory { planetId = 101, prebuildCursor = 5, prebuildPool = new[] {
                new PrebuildData(), new() {id=1,protoId=2301}, new() {id=2,protoId=2301,isDestroyed=true},
                new() {id=0,protoId=2301}, new() {id=99,protoId=2301} } };
            var remote = new PlanetFactory { planetId = 102, prebuildCursor = 3, prebuildPool = new[] {
                new PrebuildData(), new() {id=1,protoId=2301,isDestroyed=true}, new() {id=2,protoId=2001} } };
            var game = new GameData { factories = new[] {local, null, remote} };
            var result = CaptureConstructionSummary(game, 101);
            Check((int)result["count"]==4 && (int)result["pendingCount"]==2 && (int)result["destroyedCount"]==2, "全局预建分类，忽略空工厂及回收槽");
            Check((int)result["localCount"]==2 && (int)result["localPendingCount"]==1 && (int)result["localDestroyedCount"]==1, "本星球与全局数量分别汇总");
            var item=((List<JsonObject>)result["items"])[0];
            Check((string)item["name"]=="采矿机" && (int)item["pendingCount"]==1 && (int)item["destroyedCount"]==2 && (int)item["localDestroyedCount"]==1, "建筑名称及两种范围的分类数量");
            result=CaptureConstructionSummary(game, 0);
            Check((int)result["count"]==4 && (int)result["localCount"]==0, "离开星球后保留全局待办");
            local.prebuildPool=new PrebuildData[11]; local.prebuildCursor=11;
            for(var i=1;i<=9;i++) local.prebuildPool[i]=new(){id=i,protoId=(short)(3000+i)};
            local.prebuildPool[10]=new(){id=10,protoId=3001,isDestroyed=true};
            result=CaptureConstructionSummary(new(){factories=new[]{local}},101);
            item=((List<JsonObject>)result["items"])[0];
            Check((int)result["count"]==10 && (bool)result["itemsTruncated"] && ((List<JsonObject>)result["items"]).Count==8 && (int)item["destroyedCount"]==1, "截断种类不漏计全局或已列种类的后续对象");
            Check((int)CaptureConstructionSummary(new(){factories=Array.Empty<PlanetFactory>()},0)["count"]==0 && !(bool)CaptureConstructionSummary(null,0)["available"], "空工厂与数据不可用分别表达");

            var container=new TrashContainer {trashCursor=5,trashObjPool=new TrashObject[12],trashDataPool=new TrashData[12]};
            container.trashObjPool[0]=new(){item=1001,count=10}; container.trashDataPool[0]=new(){landPlanetId=101};
            container.trashObjPool[1]=new(){item=1001,count=20}; container.trashDataPool[1]=new(){landPlanetId=102};
            container.trashObjPool[2]=new(){item=1002,count=5};
            container.trashObjPool[3]=new(){item=1001,count=2}; container.trashDataPool[3]=new(){landPlanetId=101};
            GameMain.data=new(){trashSystem=new(){container=container}}; GameMain.localPlanet=new(){id=101};
            result=CaptureTrash(false); item=((List<JsonObject>)result["items"])[0];
            Check((int)result["count"]==4 && (int)result["localPlanetCount"]==2 && (long)item["count"]==32 && (long)item["localCount"]==12, "垃圾块数与物品数区分，按当前星球汇总");
            Check(!result.ContainsKey("entries") && !(bool)result["itemsTruncated"], "默认摘要不输出逐块位置");
            var details=CaptureTrash(true);
            Check(((List<object>)details["entries"]).Count==4 && (int)details["otherPlanetCount"]==1 && (int)details["floatingCount"]==1, "按需明细仍保留全量记录与范围统计");
            container.trashCursor=11;
            for(var i=0;i<10;i++)container.trashObjPool[i]=new(){item=1000+i,count=1};
            container.trashObjPool[10]=new(){item=1000,count=6};
            result=CaptureTrash(false); item=((List<JsonObject>)result["items"])[0];
            Check((int)result["count"]==11 && (bool)result["itemsTruncated"] && (long)item["count"]==7, "垃圾种类截断后继续累计已列种类");
        }
    }
}

class GameData { public PlanetFactory[] factories; public TrashSystem trashSystem; }
class PlanetFactory { public int planetId,prebuildCursor; public PrebuildData[] prebuildPool; }
struct PrebuildData { public int id; public short protoId; public bool isDestroyed; }
class PlanetData { public int id; public float realRadius=200; }
class Player { public UnityEngine.Vector3 position; }
class TrashSystem { public TrashContainer container; }
class TrashContainer { public int trashCursor; public TrashObject[] trashObjPool; public TrashData[] trashDataPool; }
struct TrashObject { public int item,count,inc,expire; public UnityEngine.Vector3 rPos; }
struct TrashData { public int landPlanetId,nearPlanetId,nearStarId,life; public UnityEngine.Vector3 lPos,uPos; }
static class GameMain { public static GameData data; public static Player mainPlayer=null; public static PlanetData localPlanet; }
class ItemProto { public string name; }
class ItemSet { public ItemProto Select(int id)=>new(){name=id==2301?"采矿机":"物品"+id}; }
static class LDB { public static ItemSet items=new(); }
namespace UnityEngine
{
    struct Vector3 { public float x,y,z; public float magnitude=>(float)Math.Sqrt(x*x+y*y+z*z); public static float Distance(Vector3 a,Vector3 b)=>(float)Math.Sqrt(Math.Pow(a.x-b.x,2)+Math.Pow(a.y-b.y,2)+Math.Pow(a.z-b.z,2)); }
    static class Mathf { public static float Clamp(float value,float min,float max)=>Math.Clamp(value,min,max); }
}
