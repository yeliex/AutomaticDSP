using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

static class BeltTopologyValidation
{
    public static void Run(Assembly mod)
    {
        var game = Assembly.Load("Assembly-CSharp");
        object Native(string name) => RuntimeHelpers.GetUninitializedObject(game.GetType(name, true)!);
        void Set(object obj, string name, object value)
        {
            var field = obj.GetType().GetField(name)!;
            if (field == null) { obj.GetType().GetProperty(name)!.SetValue(obj, value); return; }
            field.SetValue(obj, value.GetType() == field.FieldType ? value : Convert.ChangeType(value, field.FieldType));
        }
        var vector = Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.Vector3", true)!;
        object Position(float x) => Activator.CreateInstance(vector, x, 0f, 0f)!;
        var factory = Native("PlanetFactory");
        var traffic = Native("CargoTraffic");
        Set(factory, "cargoTraffic", traffic);
        var planet = Native("PlanetData"); Set(planet, "id", 101); Set(factory, "planet", planet);
        var entities = Array.CreateInstance(game.GetType("EntityData", true)!, 6);
        var belts = Array.CreateInstance(game.GetType("BeltComponent", true)!, 3);
        for (var i = 1; i <= 4; i++)
        {
            var entity = Activator.CreateInstance(entities.GetType().GetElementType()!)!;
            Set(entity, "id", i); Set(entity, "protoId", i <= 2 ? 2001 : i == 3 ? 2303 : 2011);
            Set(entity, "pos", Position(i == 1 ? 200 : 210));
            if (i <= 2) Set(entity, "beltId", i);
            if (i == 4) Set(entity, "inserterId", 1);
            entities.SetValue(entity, i);
            if (i > 2) continue;
            var belt = Activator.CreateInstance(belts.GetType().GetElementType()!)!;
            Set(belt, "id", i); Set(belt, "entityId", i); Set(belt, "segPathId", 1);
            Set(belt, "segIndex", i == 1 ? 20 : 0); Set(belt, "segLength", 10);
            belts.SetValue(belt, i);
        }
        Set(factory, "entityPool", entities); Set(factory, "entityConnPool", new int[96]);
        Set(traffic, "beltPool", belts); Set(traffic, "beltCursor", 3);
        var path = Native("CargoPath"); Set(path, "id", 1);
        Set(path, "belts", new List<int> { 1, 2 }); Set(path, "inputPaths", new List<int> { 2 });
        var paths = Array.CreateInstance(path.GetType(), 3); paths.SetValue(path, 1); Set(traffic, "pathPool", paths);
        var write = factory.GetType().GetMethod("WriteObjectConn")!;
        void Connect(int a, int slot, bool output, int b, int otherSlot) => write.Invoke(factory, new object[] { a, slot, output, b, otherSlot });
        Connect(1, 4, true, 4, 1); Connect(4, 1, false, 1, 4);
        Connect(4, 0, true, 3, 2); Connect(3, 2, false, 4, 0);
        var parser = mod.GetType("AutomaticDSP.State.StateQueryParser", true)!.GetMethod("Parse")!;
        object Field(string args)
        {
            var plan = parser.Invoke(null, new object[] { "{ beltTopology(" + args + ") { pathCount } }", null! })!;
            return ((IList)plan.GetType().GetProperty("Fields")!.GetValue(plan)!)[0]!;
        }
        var service = mod.GetType("AutomaticDSP.State.GameStateQueryService", true)!;
        var capture = service.GetMethod("CaptureBeltTopology", BindingFlags.NonPublic | BindingFlags.Static)!;
        IDictionary Query(string args) => (IDictionary)capture.Invoke(null, new[] { factory, Field(args) })!;
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("通过：" + text); }
        var response = Query("entityId:3");
        Check((int)response["pathCount"]! == 1, "建筑通过分拣器定位原生传送带路径");
        var row = (IDictionary)((IList)response["paths"]!)[0]!;
        var nodes = (IList)row["nodes"]!;
        Check((int)((IDictionary)nodes[0]!)["entityId"]! == 2 && (int)row["nodeCount"]! == 2, "整段节点按 segIndex 排列，不使用成员插入顺序");
        var connection = (IDictionary)((IList)row["connections"]!)[0]!;
        Check((int)connection["otherEntityId"]! == 3 && (int)connection["viaSorterEntityId"]! == 4 && (bool)connection["isOutput"]!, "路径返回沿途建筑、分拣器与输送方向");
        Check((int)Query("x:200,y:0,z:0,radius:1")["matchCount"]! == 1, "坐标半径只选中附近带节点并返回其整段路径");
        Check((int)Query("pathId:2")["pathCount"]! == 0 && (int)Query("entityId:5")["pathCount"]! == 0, "已回收路径和实体不产生虚假拓扑");
        foreach (var args in new[] { "entityId:0", "pathId:-1", "entityId:1,pathId:1", "x:1,y:0,z:0,radius:0", "entityId:1.2", "x:1,y:0,radius:1" })
        {
            try { Field(args); throw new Exception("接受非法拓扑参数：" + args); }
            catch (TargetInvocationException ex) when (ex.InnerException!.GetType().Name == "StateQueryParseException") { }
        }
        Check(true, "拓扑查询拒绝歧义入口、无效 ID 与不完整坐标");
        var queryField = Field("entityId:3");
        var cloned = queryField.GetType().GetMethod("Clone")!.Invoke(queryField, null)!;
        Check((int)cloned.GetType().GetProperty("EntityId")!.GetValue(cloned)! == 3, "查询计划克隆保留传送带入口");
    }
}
