using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

static class BuildWaitValidation
{
    public static void Run(Assembly mod)
    {
        var game = Assembly.Load("Assembly-CSharp");
        object Native(string name) => RuntimeHelpers.GetUninitializedObject(game.GetType(name, true)!);
        void Set(object value, string name, object fieldValue) => value.GetType().GetField(name)!.SetValue(value, fieldValue);
        var commandType = mod.GetType("AutomaticDSP.Tasks.CommandState", true)!;
        var requestType = mod.GetType("AutomaticDSP.Tasks.TaskCommandRequest", true)!;
        var targetType = mod.GetType("AutomaticDSP.Tasks.BuildWaitTarget", true)!;
        var executorType = mod.GetType("AutomaticDSP.Tasks.TaskCommandExecutor", true)!;
        var finisherType = mod.GetType("AutomaticDSP.Tasks.TaskCommandFinisher", true)!;
        var parameters = finisherType.GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var finisher = Expression.Lambda(finisherType, Expression.Block(
            Expression.Assign(Expression.Property(parameters[0], "Status"), parameters[1]),
            Expression.Assign(Expression.Property(parameters[0], "ErrorCode"), parameters[2]),
            Expression.Assign(Expression.Property(parameters[0], "Result"), parameters[5]),
            Expression.Empty()), parameters).Compile();
        var executor = Activator.CreateInstance(executorType, finisher)!;
        var wait = executorType.GetMethod("CompleteBuildSubmission", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dataField = game.GetType("GameMain", true)!.GetField("data")!;
        var before = dataField.GetValue(null);
        try
        {
            var data = Native("GameData");
            var galaxy = Native("GalaxyData");
            var star = Native("StarData");
            var planet = Native("PlanetData");
            var stars = Array.CreateInstance(star.GetType(), 1); stars.SetValue(star, 0);
            var planets = Array.CreateInstance(planet.GetType(), 1); planets.SetValue(planet, 0);
            Set(star, "planets", planets); Set(galaxy, "stars", stars); Set(data, "galaxy", galaxy);
            dataField.SetValue(null, data);
            var command = Activator.CreateInstance(commandType, "bp", "applyFactoryBlueprint", Activator.CreateInstance(requestType))!;
            commandType.GetProperty("BuildPlanetId")!.SetValue(command, 101);
            commandType.GetProperty("Status")!.SetValue(command, "RUNNING");
            void Tick() => wait.Invoke(executor, new[] { command, (object)DateTimeOffset.UtcNow });
            void Assert(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("通过：" + text); }
            Set(planet, "id", 101);
            var factory = Native("PlanetFactory"); Set(planet, "factory", factory);
            var entities = Array.CreateInstance(game.GetType("EntityData", true)!, 2);
            var prebuilds = Array.CreateInstance(game.GetType("PrebuildData", true)!, 2);
            Set(factory, "entityPool", entities); Set(factory, "entityCursor", 2); Set(factory, "prebuildPool", prebuilds);
            var prebuild = Activator.CreateInstance(prebuilds.GetType().GetElementType()!)!;
            Set(prebuild, "id", 1); Set(prebuild, "itemRequired", 1); prebuilds.SetValue(prebuild, 1);
            var vector = Activator.CreateInstance(Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.Vector3", true)!, 200f, 0f, 0f)!;
            var target = Activator.CreateInstance(targetType, -1, 2001, vector, null)!;
            var targets = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(targetType))!; targets.Add(target);
            commandType.GetProperty("BuildTargets")!.SetValue(command, targets);
            var result = (IDictionary)Activator.CreateInstance(mod.GetType("AutomaticDSP.Serialization.JsonObject", true)!)!;
            result["planetId"] = 101; result["partial"] = true;
            commandType.GetProperty("Result")!.SetValue(command, result);
            Tick();
            Assert((string)commandType.GetProperty("Status")!.GetValue(command)! == "SUCCEEDED", "缺料预建下达即完成任务");
            Assert((bool)result["submitted"]! && ((IList)result["prebuildIds"]!)[0]!.Equals(1), "下达结果明确区分预建与实体");
            var resolve = executorType.GetMethod("TryResolveBuildTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
            object[] Resolve()
            {
                var args = new object[] { command, 0, 0, false };
                resolve.Invoke(null, args);
                return args;
            }
            Assert((bool)Resolve()[3], "实体引用按需等待缺料预建");
            Set(prebuild, "id", 0); prebuilds.SetValue(prebuild, 1);
            var entity = Activator.CreateInstance(entities.GetType().GetElementType()!)!;
            Set(entity, "id", 1); Set(entity, "protoId", (short)2001); Set(entity, "pos", vector); entities.SetValue(entity, 1);
            Assert((int)Resolve()[2] == 1, "实体引用按需解析原行星已落成建筑");
            targets.Clear();
            targets.Add(Activator.CreateInstance(targetType, 1, 2002, vector, null)!);
            Assert((int)Resolve()[2] == 0, "覆盖升级未改变型号时不提供错误实体引用");
            Assert(ReferenceEquals(result, commandType.GetProperty("Result")!.GetValue(command)) && (bool)result["partial"]!, "实体引用查询不改写已完成指令的下达结果");
            var preview = Native("BuildPreview");
            var item = Native("ItemProto"); Set(item, "ID", 2002);
            Set(preview, "item", item); Set(preview, "objId", 1); Set(preview, "coverObjId", 1); Set(preview, "willRemoveCover", true);
            var previews = Array.CreateInstance(preview.GetType(), 1); previews.SetValue(preview, 0);
            var upgradeFailures = executorType.GetMethod("GetBlueprintUpgradeFailures", BindingFlags.Static | BindingFlags.NonPublic)!;
            IList Failures() => (IList)upgradeFailures.Invoke(null, new object[] { factory, previews })!;
            Assert(Failures().Count == 1, "原生返回旧实体 ID 时仍报告升级未执行");
            Set(entity, "protoId", (short)2002); entities.SetValue(entity, 1);
            Assert(Failures().Count == 0, "实体型号已升级时不误报失败");
            Set(preview, "objId", -1); Set(preview, "coverObjId", -1);
            Set(prebuild, "id", 1); Set(prebuild, "protoId", (short)2001); prebuilds.SetValue(prebuild, 1);
            Assert(Failures().Count == 1, "预建升级也核对实际型号");
            Set(prebuild, "protoId", (short)2002); prebuilds.SetValue(prebuild, 1);
            Assert(Failures().Count == 0, "缺料预建成功换型时不误报失败");
            var desc = Native("GameDesc");
            Set(desc, "isSandboxMode", false); Set(data, "gameDesc", desc);
            data.GetType().GetProperty("mainPlayer")!.SetValue(data, Native("Player"));
            data.GetType().GetProperty("localPlanet")!.SetValue(data, planet);
            executorType.GetMethod("ExecuteSandboxUnlockTechs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(executor, new[] { command, (object)DateTimeOffset.UtcNow });
            Assert((string)commandType.GetProperty("ErrorCode")!.GetValue(command)! == "sandbox_required", "普通存档不能调用沙盒一键解锁");
        }
        finally { dataField.SetValue(null, before); }
    }
}
