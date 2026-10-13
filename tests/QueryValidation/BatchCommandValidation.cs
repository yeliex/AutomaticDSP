using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

static class BatchCommandValidation
{
    private static int finishes;
    public static void Run(Assembly mod)
    {
        var game = Assembly.Load("Assembly-CSharp");
        object Native(string name) => RuntimeHelpers.GetUninitializedObject(game.GetType(name, true)!);
        void Set(object obj, string name, object value)
        {
            var field = obj.GetType().GetField(name);
            if (field != null) field.SetValue(obj, value);
            else obj.GetType().GetProperty(name)!.SetValue(obj, value);
        }
        var commandType = mod.GetType("AutomaticDSP.Tasks.CommandState", true)!;
        var requestType = mod.GetType("AutomaticDSP.Tasks.TaskCommandRequest", true)!;
        var executorType = mod.GetType("AutomaticDSP.Tasks.TaskCommandExecutor", true)!;
        var finisherType = mod.GetType("AutomaticDSP.Tasks.TaskCommandFinisher", true)!;
        var parameters = finisherType.GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var finisher = Expression.Lambda(finisherType, Expression.Block(
            Expression.Assign(Expression.Property(parameters[0], "Status"), parameters[1]),
            Expression.Assign(Expression.Property(parameters[0], "ErrorCode"), parameters[2]),
            Expression.Assign(Expression.Property(parameters[0], "Result"), parameters[5]),
            Expression.IfThen(Expression.Not(Expression.Property(parameters[0], "IsBatchChild")),
                Expression.Block(Expression.PostIncrementAssign(Expression.Field(null, typeof(BatchCommandValidation).GetField("finishes", BindingFlags.NonPublic | BindingFlags.Static)!)), Expression.Empty())),
            Expression.Empty()), parameters).Compile();
        var executor = Activator.CreateInstance(executorType, finisher)!;
        var tokenType = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.Linq.JToken", true)!;
        var parse = tokenType.GetMethod("Parse", new[] { typeof(string) })!;
        object Command(string type, string json, string id = "batch")
        {
            var request = Activator.CreateInstance(requestType)!;
            var fields = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), tokenType))!;
            var obj = parse.Invoke(null, new object[] { json })!;
            foreach (var property in (IEnumerable)obj.GetType().GetMethod("Properties")!.Invoke(obj, null)!)
                fields.Add(property.GetType().GetProperty("Name")!.GetValue(property)!, property.GetType().GetProperty("Value")!.GetValue(property));
            requestType.GetProperty("ExtensionData")!.SetValue(request, fields);
            return Activator.CreateInstance(commandType, id, type, request)!;
        }
        var commands = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(commandType))!;
        var task = Activator.CreateInstance(mod.GetType("AutomaticDSP.Tasks.TaskState", true)!, "task:test", "test", true, commands)!;
        void Execute(object c)
        {
            finishes = 0;
            executorType.GetMethod("Execute")!.Invoke(executor, new[] { task, c, DateTimeOffset.UtcNow });
            if (finishes != 1) throw new Exception("批量子项不应重复调用父级完成/历史回调");
        }
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("通过：" + text); }
        string Error(object c) => (string)commandType.GetProperty("ErrorCode")!.GetValue(c)!;
        IDictionary Result(object c) => (IDictionary)commandType.GetProperty("Result")!.GetValue(c)!;
        var dataField = game.GetType("GameMain", true)!.GetField("data")!;
        var oldData = dataField.GetValue(null);
        try
        {
            dataField.SetValue(null, null);
            var failed = Command("placeBuilding", "{batch:[{itemId:2001},{itemId:2001}]}"); Execute(failed);
            Check(Error(failed) == "batch_failed" && (int)Result(failed)["processedCount"]! == 1 &&
                (string)((IDictionary)((IList)Result(failed)["results"]!)[1]!)["status"]! == "SKIPPED", "批量默认遇错停止，剩余项明确跳过");
            var continued = Command("placeBuilding", "{stopOnFailure:false,batch:[{},{}]}"); Execute(continued);
            Check((int)Result(continued)["failedCount"]! == 2, "批量显式继续时保留每个失败结果");
            foreach (var json in new[] { "{batch:[]}", "{batch:[{} ,null]}", "{batch:[{type:'moveTo'}]}", "{batch:[{batch:[{}]}]}" })
            {
                var invalid = Command("placeBuilding", json); Execute(invalid);
                Check(Error(invalid) == "invalid_command", "批量执行前拒绝非法结构：" + json);
            }
            var data = Native("GameData"); var player = Native("Player"); var planet = Native("PlanetData");
            var factory = Native("PlanetFactory");
            Set(planet, "id", 101); Set(planet, "factory", factory); Set(factory, "planet", planet);
            player.GetType().GetField("_planetId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(player, 101);
            Set(data, "mainPlayer", player); Set(data, "localPlanet", planet);
            var galaxy = Native("GalaxyData"); var star = Native("StarData");
            var planets = Array.CreateInstance(planet.GetType(), 1); planets.SetValue(planet, 0);
            var stars = Array.CreateInstance(star.GetType(), 1); stars.SetValue(star, 0);
            Set(star, "planets", planets); Set(galaxy, "stars", stars); Set(data, "galaxy", galaxy);
            dataField.SetValue(null, data);
            var prebuilds = Array.CreateInstance(game.GetType("PrebuildData", true)!, 4);
            Set(factory, "prebuildPool", prebuilds);
            var absent = Command("cancelPrebuild", "{prebuildIds:[1,2],skipMissing:true}"); Execute(absent);
            Check((int)Result(absent)["succeededCount"]! == 2 && (string)((IDictionary)((IDictionary)((IList)Result(absent)["results"]!)[0]!)["result"]!)["state"]! == "not_pending", "批量取消已消失预建不等待落成、不调用实体拆除");
            var prebuild = Activator.CreateInstance(prebuilds.GetType().GetElementType()!)!;
            Set(prebuild, "id", 1); Set(prebuild, "protoId", (short)2002); prebuilds.SetValue(prebuild, 1);
            var changed = Command("cancelPrebuild", "{prebuildId:1,expectedItemId:2001,expectedPosition:[200,0,0]}"); Execute(changed);
            Check(Error(changed) == "target_changed" && (int)prebuilds.GetValue(1)!.GetType().GetField("id")!.GetValue(prebuilds.GetValue(1))! == 1, "预建 ID 被其他型号复用时拒绝取消且不修改目标");
            var source = Command("applyFactoryBlueprint", "{}");
            Set(source, "Status", "SUCCEEDED"); Set(source, "BuildPlanetId", 101); Set(source, "BuildSession", new object());
            var resolverType = executorType.GetProperty("FindCommand")!.PropertyType;
            var resolver = Expression.Lambda(resolverType, Expression.Constant(source, commandType), Expression.Parameter(typeof(string)), Expression.Parameter(typeof(string))).Compile();
            executorType.GetProperty("FindCommand")!.SetValue(executor, resolver);
            var stale = Command("cancelPrebuild", "{sourceTaskId:'task:test',sourceCommandId:'bp'}"); Execute(stale);
            Check(Error(stale) == "source_session_changed", "读档后拒绝旧建造命令的预建引用");
            var tracker = executorType.GetField("constructionTracker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(executor)!;
            Set(source, "BuildSession", tracker.GetType().GetProperty("Session")!.GetValue(tracker)!);
            var targetType = mod.GetType("AutomaticDSP.Tasks.BuildWaitTarget", true)!;
            var vector = Activator.CreateInstance(Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.Vector3", true)!, 200f, 0f, 0f)!;
            var targets = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(targetType))!;
            targets.Add(Activator.CreateInstance(targetType, -2, 2001, vector, null)!);
            targets.Add(Activator.CreateInstance(targetType, 3, 2001, vector, null)!);
            Set(source, "BuildTargets", targets);
            var cancel = Command("cancelPrebuild", "{sourceTaskId:'task:test',sourceCommandId:'bp'}"); Execute(cancel);
            Check((int)Result(cancel)["count"]! == 1 && (int)Result(cancel)["succeededCount"]! == 1, "取消蓝图仅处理新预建，已建或复用实体不进入拆除列表");
            tracker.GetType().GetMethod("Track")!.Invoke(tracker, new[] { source });
            factory.GetType().GetMethod("NotifyPrebuildChange")!.Invoke(factory, new object[] { 2, -1 });
            Set(prebuild, "id", 2); Set(prebuild, "protoId", (short)2001); Set(prebuild, "pos", vector); prebuilds.SetValue(prebuild, 2);
            var reused = Command("cancelPrebuild", "{sourceTaskId:'task:test',sourceCommandId:'bp'}"); Execute(reused);
            Check((int)Result(reused)["succeededCount"]! == 1 &&
                (int)prebuilds.GetValue(2)!.GetType().GetField("id")!.GetValue(prebuilds.GetValue(2))! == 2,
                "原生移除事件使旧目标失效，即使 ID、型号和坐标全部复用也不误拆");

            // 使用真实队列和执行器验证通道交接；恢复后以缺少 itemId 终止，不调用 Unity 建造。
            var queueType = mod.GetType("AutomaticDSP.Tasks.TaskQueueService", true)!;
            var log = Activator.CreateInstance(Assembly.Load("BepInEx").GetType("BepInEx.Logging.ManualLogSource", true)!, "批量调度验证")!;
            var queue = Activator.CreateInstance(queueType, new object[] { null, log })!;
            const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
            var advance = queueType.GetMethod("AdvanceAllTasksLocked", privateInstance)!;
            object Value(object c, string name) => commandType.GetProperty(name)!.GetValue(c)!;
            var waiting = Command("placeSorter", "{batch:[{input:{commandId:'batch'}}]}", "waiting");
            var independent = Command("noop", "{}", "independent");
            Set(Value(independent, "Request"), "DurationSeconds", 30.0);
            commands.Add(source); commands.Add(waiting); commands.Add(independent);
            Set(factory, "entityPool", Array.CreateInstance(game.GetType("EntityData", true)!, 4));
            ((IList)queueType.GetField("tasks", privateInstance)!.GetValue(queue)!).Add(task);
            var started = DateTimeOffset.UtcNow;
            try
            {
                advance.Invoke(queue, new object[] { started });
                Check((string)Value(waiting, "Status") == "PENDING" && !(bool)Value(waiting, "OwnsPlayerOrders") &&
                    (string)Value(independent, "Status") == "RUNNING", "批量等待施工归还机甲通道，独立指令能启动");
                var entity = Activator.CreateInstance(game.GetType("EntityData", true)!)!;
                Set(entity, "id", 1); Set(entity, "protoId", (short)2001); Set(entity, "pos", vector);
                ((Array)factory.GetType().GetField("entityPool")!.GetValue(factory)!).SetValue(entity, 1);
                Set(targets[0]!, "EntityId", 1);
                advance.Invoke(queue, new object[] { started.AddSeconds(2) });
                Check((string)Value(waiting, "Status") == "PENDING" && (int)Value(waiting, "BatchIndex") == 0,
                    "施工就绪后批量不会抢占仍运行的机甲指令");
                Set(Value(independent, "Request"), "DurationSeconds", 0.0);
                advance.Invoke(queue, new object[] { started.AddSeconds(3) });
                Check((int)Value(waiting, "BatchIndex") == 1 && Equals(Value(waiting, "StartedAt"), started) &&
                    Error(waiting) == "batch_failed", "通道释放后恢复批量，保留首次开始时间并记录子项参数失败");

                Set(targets[0]!, "EntityId", 0);
                var timeout = Command("placeSorter", "{batch:[{input:{commandId:'batch'}}]}", "timeout");
                Set(Value(timeout, "Request"), "TimeoutSeconds", 1.0);
                commands.Clear(); commands.Add(source); commands.Add(timeout);
                Set(task, "Status", "RUNNING"); Set(task, "HadCommandFailure", false);
                advance.Invoke(queue, new object[] { started.AddSeconds(4) });
                advance.Invoke(queue, new object[] { started.AddSeconds(6) });
                Check(Error(timeout) == "timeout" && !(bool)Value(timeout, "OwnsPlayerOrders"),
                    "归还通道的批量在施工未完成时仍按原超时终止");
            }
            finally { queueType.GetMethod("Dispose")!.Invoke(queue, null); ((IDisposable)log).Dispose(); }
        }
        finally { executorType.GetMethod("Dispose")!.Invoke(executor, null); dataField.SetValue(null, oldData); }
    }
}
