using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

static class StationOptionsValidation
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
        var commandType = mod.GetType("AutomaticDSP.Tasks.CommandState", true)!;
        var requestType = mod.GetType("AutomaticDSP.Tasks.TaskCommandRequest", true)!;
        var executorType = mod.GetType("AutomaticDSP.Tasks.TaskCommandExecutor", true)!;
        var finisherType = mod.GetType("AutomaticDSP.Tasks.TaskCommandFinisher", true)!;
        var parameters = finisherType.GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var finisher = Expression.Lambda(finisherType, Expression.Block(
            Expression.Assign(Expression.Property(parameters[0], "Status"), parameters[1]),
            Expression.Assign(Expression.Property(parameters[0], "ErrorCode"), parameters[2]), Expression.Empty()), parameters).Compile();
        var executor = Activator.CreateInstance(executorType, finisher)!;
        var tokenType = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.Linq.JToken", true)!;
        var parse = tokenType.GetMethod("Parse", new[] { typeof(string) })!;
        var factory = Native("PlanetFactory");
        var planet = Native("PlanetData");
        Set(planet, "id", 102);
        Set(factory, "planet", planet);
        var entity = Activator.CreateInstance(game.GetType("EntityData", true)!)!;
        var station = Native("StationComponent");
        Set(station, "isStellar", true);
        object Command(string setting, string value, string type = "setstationsetting")
        {
            var request = Activator.CreateInstance(requestType)!;
            var args = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), tokenType))!;
            args.Add("setting", parse.Invoke(null, new object[] { "\"" + setting + "\"" }));
            args.Add("value", parse.Invoke(null, new object[] { value }));
            requestType.GetProperty("ExtensionData")!.SetValue(request, args);
            return Activator.CreateInstance(commandType, "option", type, request)!;
        }
        void Check(string setting, string value, string field, object expected, bool success)
        {
            var before = station.GetType().GetField(field)!.GetValue(station);
            var command = Command(setting, value);
            executorType.GetMethod("ExecuteStationOption", flags)!.Invoke(executor, new[] { command, Native("Player"), factory, entity, station, DateTimeOffset.UtcNow });
            var error = commandType.GetProperty("ErrorCode")!.GetValue(command);
            var actual = station.GetType().GetField(field)!.GetValue(station);
            if ((error == null) != success || !Equals(actual, success ? expected : before))
                throw new Exception($"物流设置失败：{setting}={value}，错误 {error}，字段 {actual}");
            Console.WriteLine($"通过：物流设置 {setting}={value}，成功={success}，失败不修改原值");
        }
        Check("shipRangeLightYears", "22", "tripRangeShips", 52800000d, true);
        Check("shipRangeLightYears", "10000", "tripRangeShips", 24000000000d, true);
        foreach (var value in new[] { "21", "61", "0", "\"20\"", "20.5", "999999999999" })
            Check("shipRangeLightYears", value, "tripRangeShips", 0d, false);
        Check("warpDistanceAU", "0.5", "warpEnableDist", 20000d, true);
        Check("warpDistanceAU", "60", "warpEnableDist", 2400000d, true);
        foreach (var value in new[] { "0.2", "3.5", "13", "22", "\"1\"" })
            Check("warpDistanceAU", value, "warpEnableDist", 0d, false);
        Check("deliveryShips", "1", "deliveryShips", 1, true);
        Check("deliveryShips", "100", "deliveryShips", 100, true);
        Check("deliveryShips", "50", "deliveryShips", 50, true);
        foreach (var value in new[] { "0", "5", "101", "true", "50.0" })
            Check("deliveryShips", value, "deliveryShips", 0, false);
        Check("warperNecessary", "true", "warperNecessary", true, true);
        Check("warperNecessary", "1", "warperNecessary", true, false);
        Check("includeOrbitCollector", "false", "includeOrbitCollector", false, true);
        Set(station, "isStellar", false);
        Check("shipRangeLightYears", "20", "tripRangeShips", 0d, false);
        Check("deliveryShips", "10", "deliveryShips", 0, false);
        Check("warperNecessary", "false", "warperNecessary", false, false);
        Check("deliveryDrones", "10", "deliveryDrones", 10, true);
        Check("unknown", "10", "deliveryDrones", 0, false);
        var uiRootType = game.GetType("UIRoot", true)!;
        var rootField = uiRootType.GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldRoot = rootField.GetValue(null);
        try
        {
            var uiRoot = Native("UIRoot"); var uiGame = Native("UIGame");
            var panel = Native("UIControlPanelWindow"); var inspector = Native("UIControlPanelStationInspector");
            var sliderType = Assembly.Load("UnityEngine.UI").GetType("UnityEngine.UI.Slider", true)!;
            var slider = RuntimeHelpers.GetUninitializedObject(sliderType);
            Set(slider, "m_MinValue", 0f); Set(slider, "m_MaxValue", 20f);
            Set(inspector, "maxMiningSpeedSlider", slider); Set(panel, "stationInspector", inspector);
            Set(uiGame, "controlPanelWindow", panel); Set(uiRoot, "uiGame", uiGame); rootField.SetValue(null, uiRoot);
            var transport = Native("PlanetTransport"); Set(factory, "transport", transport);
            var stations = Array.CreateInstance(station.GetType(), 2); stations.SetValue(station, 1); Set(transport, "stationPool", stations);
            Set(entity, "stationId", 1); Set(station, "isVeinCollector", true); Set(station, "minerId", 1);
            var system = Native("FactorySystem"); Set(factory, "factorySystem", system); Set(system, "minerCursor", 2);
            var minerType = game.GetType("MinerComponent", true)!;
            var miners = Array.CreateInstance(minerType, 2); var miner = Activator.CreateInstance(minerType)!;
            Set(miner, "id", 1); Set(miner, "speed", 10000); miners.SetValue(miner, 1); Set(system, "minerPool", miners);
            foreach (var value in new[] { "100", "150", "300", "99", "310", "105", "150.0", "\"150\"", "true", "999999999999" })
            {
                var command = Command("", value);
                var request = commandType.GetProperty("Request")!.GetValue(command)!;
                var args = (IDictionary)requestType.GetProperty("ExtensionData")!.GetValue(request)!;
                args.Add("speedPercent", parse.Invoke(null, new object[] { value }));
                var before = minerType.GetField("speed")!.GetValue(miners.GetValue(1));
                executorType.GetMethod("ExecuteVeinCollectorSpeed", flags)!.Invoke(executor, new[] { command, factory, entity, DateTimeOffset.UtcNow });
                var success = value == "100" || value == "150" || value == "300";
                var actual = minerType.GetField("speed")!.GetValue(miners.GetValue(1));
                if ((commandType.GetProperty("ErrorCode")!.GetValue(command) == null) != success ||
                    !Equals(actual, success ? int.Parse(value) * 100 : before)) throw new Exception("大矿机速度边界或状态保护失败：" + value);
                Console.WriteLine("通过：大矿机原生组件速度／失败不修改 " + value);
            }
        }
        finally { rootField.SetValue(null, oldRoot); }
        var dispenserTransport = Native("PlanetTransport"); Set(factory, "transport", dispenserTransport);
        var dispenser = Native("DispenserComponent");
        var dispensers = Array.CreateInstance(dispenser.GetType(), 2); dispensers.SetValue(dispenser, 1);
        Set(dispenserTransport, "dispenserPool", dispensers); Set(entity, "dispenserId", 1);
        foreach (var value in new[] { "1", "\"true\"", "[]", "null" })
        {
            var command = Command("courierAutoReplenish", value, "setdispensersetting");
            executorType.GetMethod("ExecuteDispenserSetting", flags)!.Invoke(executor,
                new[] { command, Native("Player"), factory, entity, DateTimeOffset.UtcNow });
            if (!Equals(commandType.GetProperty("ErrorCode")!.GetValue(command), "invalid_command") ||
                (bool)dispenser.GetType().GetField("courierAutoReplenish")!.GetValue(dispenser)!)
                throw new Exception("配送器错误类型修改了状态：" + value);
            Console.WriteLine("通过：配送器自动补充拒绝错误类型且不修改状态 " + value);
        }
    }
}
