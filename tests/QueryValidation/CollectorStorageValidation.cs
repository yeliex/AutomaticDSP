using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

static class CollectorStorageValidation
{
    public static void Run(Assembly mod)
    {
        var game = Assembly.Load("Assembly-CSharp");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object Native(string name) => RuntimeHelpers.GetUninitializedObject(game.GetType(name, true)!);
        void Set(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, flags);
            if (field != null) field.SetValue(target, value);
            else target.GetType().GetProperty(name, flags)!.SetValue(target, value);
        }
        object Get(object target, string name) => target.GetType().GetField(name, flags)!.GetValue(target)!;
        var dataField = game.GetType("GameMain")!.GetField("data")!;
        var ldb = game.GetType("LDB")!;
        var itemsField = ldb.GetField("_items", BindingFlags.NonPublic | BindingFlags.Static)!;
        var modelsField = ldb.GetField("_models", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldData = dataField.GetValue(null); var oldItems = itemsField.GetValue(null); var oldModels = modelsField.GetValue(null);
        object Protos(string name, params object[] values)
        {
            var set = Native(name + "Set");
            // 仅让 Unity 的空对象比较识别离线原型表，避免 LDB 尝试访问原生资源加载器。
            Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.Object")!
                .GetField("m_CachedPtr", flags)!.SetValue(set, new IntPtr(1));
            var array = Array.CreateInstance(game.GetType(name)!, values.Length);
            var indices = new Dictionary<int, int>();
            for (var i = 0; i < values.Length; i++) { array.SetValue(values[i], i); indices[(int)Get(values[i], "ID")] = i; }
            Set(set, "dataArray", array);
            set.GetType().BaseType!.GetField("dataIndices", flags)!.SetValue(set, indices);
            return set;
        }
        try
        {
            var data = Native("GameData"); var history = Native("GameHistoryData");
            Set(data, "history", history); dataField.SetValue(null, data);
            Set(history, "localStationExtraStorage", 1000); Set(history, "remoteStationExtraStorage", 9000);
            var desc = Native("PrefabDesc"); Set(desc, "stationMaxItemCount", 5000);
            var building = Native("ItemProto"); Set(building, "ID", 2316); Set(building, "prefabDesc", desc);
            var ore = Native("ItemProto"); Set(ore, "ID", 1001);
            var other = Native("ItemProto"); Set(other, "ID", 1002);
            itemsField.SetValue(null, Protos("ItemProto", building, ore, other));
            var model = Native("ModelProto"); Set(model, "ID", 1); Set(model, "prefabDesc", desc);
            modelsField.SetValue(null, Protos("ModelProto", model));
            var factory = Native("PlanetFactory"); var planet = Native("PlanetData"); Set(planet, "id", 102); Set(factory, "planet", planet);
            var transport = Native("PlanetTransport"); Set(factory, "transport", transport); Set(transport, "factory", factory);
            var entity = Native("EntityData"); Set(entity, "id", 1); Set(entity, "stationId", 1); Set(entity, "protoId", (short)2316); Set(entity, "modelIndex", (short)1);
            var entities = Array.CreateInstance(entity.GetType(), 2); entities.SetValue(entity, 1); Set(factory, "entityPool", entities);
            var station = Native("StationComponent"); Set(station, "id", 1); Set(station, "entityId", 1);
            var stations = Array.CreateInstance(station.GetType(), 2); stations.SetValue(station, 1); Set(transport, "stationPool", stations);
            var store = Native("StationStore"); Set(store, "itemId", 1001); Set(store, "count", 7000); Set(store, "inc", 14000); Set(store, "max", 5000);
            var stores = Array.CreateInstance(store.GetType(), 1); stores.SetValue(store, 0); Set(station, "storage", stores);
            var commandType = mod.GetType("AutomaticDSP.Tasks.CommandState")!; var requestType = mod.GetType("AutomaticDSP.Tasks.TaskCommandRequest")!;
            var executorType = mod.GetType("AutomaticDSP.Tasks.TaskCommandExecutor")!; var finisherType = mod.GetType("AutomaticDSP.Tasks.TaskCommandFinisher")!;
            var parameters = finisherType.GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var finisher = Expression.Lambda(finisherType, Expression.Block(Expression.Assign(Expression.Property(parameters[0], "Status"), parameters[1]),
                Expression.Assign(Expression.Property(parameters[0], "ErrorCode"), parameters[2]), Expression.Empty()), parameters).Compile();
            var executor = Activator.CreateInstance(executorType, finisher)!;
            var tokenType = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.Linq.JToken")!;
            var parse = tokenType.GetMethod("Parse", new[] { typeof(string) })!;
            void Check(string json, bool success, string type = "setstationstorage", string expectedError = null,
                int expectedCount = 7000, int expectedInc = 14000)
            {
                var request = Activator.CreateInstance(requestType)!;
                var args = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), tokenType))!;
                foreach (var pair in System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject())
                    args.Add(pair.Name, parse.Invoke(null, new object[] { pair.Value.GetRawText() }));
                requestType.GetProperty("ExtensionData")!.SetValue(request, args);
                var command = Activator.CreateInstance(commandType, "collector", type, request)!;
                var before = stores.GetValue(0)!;
                executorType.GetMethod("ExecuteStationSetting", flags)!.Invoke(executor, new[] { command, Native("Player"), factory, entity, DateTimeOffset.UtcNow });
                var after = stores.GetValue(0)!;
                if ((commandType.GetProperty("ErrorCode")!.GetValue(command) == null) != success ||
                    (!success && !before.Equals(after)) || (int)Get(after, "count") != expectedCount || (int)Get(after, "inc") != expectedInc ||
                    (expectedError != null && (string)commandType.GetProperty("ErrorCode")!.GetValue(command) != expectedError))
                    throw new Exception("采集槽校验或库存保护失败：" + json);
                if (success && (int)Get(after, "max") != 6000) throw new Exception("采集槽未使用本地容量加成");
                Console.WriteLine($"通过：采集槽 {type} {json}，成功={success}，库存保留");
            }
            foreach (var orbital in new[] { false, true })
            {
                Set(station, "isCollector", orbital); Set(station, "isVeinCollector", !orbital); Set(station, "isStellar", orbital);
                Check("{\"storageIndex\":0,\"itemId\":1001}", true);
                foreach (var json in new[] {
                    "{\"storageIndex\":0,\"itemId\":1001,\"max\":6001}",
                    "{\"storageIndex\":0,\"itemId\":1001,\"max\":-1}",
                    "{\"storageIndex\":1,\"itemId\":1001}",
                    "{\"storageIndex\":0,\"itemId\":0}",
                    "{\"storageIndex\":0,\"itemId\":1002}",
                    "{\"storageIndex\":0,\"itemId\":1001,\"localLogic\":\"Demand\"}",
                    "{\"storageIndex\":0,\"itemId\":1001,\"remoteLogic\":\"Demand\"}"
                }) Check(json, false);
                Check("{}", false, "setstationsetting");
                Check("{}", false, "setstationvehicles");
                Check("{}", false, "transferstationitem");
                Check("{\"itemId\":1001,\"count\":1,\"direction\":\"toStation\"}", false,
                    "transferstationitem", "no_item_transferred");
                Check("{\"itemId\":1002,\"count\":1}", false, "transferstationitem", "invalid_command");
                Check("{\"itemId\":1001,\"count\":4,\"inventory\":\"hand\"}", true,
                    "transferstationitem", expectedCount: 6996, expectedInc: 13992);
                stores.SetValue(store, 0);
            }
        }
        finally { dataField.SetValue(null, oldData); itemsField.SetValue(null, oldItems); modelsField.SetValue(null, oldModels); }
    }
}
