#nullable enable
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

static class StationRemoteValidation
{
    public static void Run(Assembly mod)
    {
        var game = Assembly.Load("Assembly-CSharp");
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object Native(string name) => RuntimeHelpers.GetUninitializedObject(game.GetType(name, true)!);
        void Set(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, fields);
            if (field != null) field.SetValue(target, value);
            else target.GetType().GetProperty(name, fields)!.SetValue(target, value);
        }
        var dataField = game.GetType("GameMain", true)!.GetField("data")!;
        var oldData = dataField.GetValue(null);
        var commandType = mod.GetType("AutomaticDSP.Tasks.CommandState", true)!;
        var requestType = mod.GetType("AutomaticDSP.Tasks.TaskCommandRequest", true)!;
        var executorType = mod.GetType("AutomaticDSP.Tasks.TaskCommandExecutor", true)!;
        var finisherType = mod.GetType("AutomaticDSP.Tasks.TaskCommandFinisher", true)!;
        var parameters = finisherType.GetMethod("Invoke")!.GetParameters()
            .Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var finisher = Expression.Lambda(finisherType, Expression.Block(
            Expression.Assign(Expression.Property(parameters[0], "ErrorCode"), parameters[2]),
            Expression.Empty()), parameters).Compile();
        var executor = Activator.CreateInstance(executorType, finisher)!;
        var json = Assembly.Load("Newtonsoft.Json");
        var tokenType = json.GetType("Newtonsoft.Json.Linq.JToken", true)!;
        var parse = tokenType.GetMethod("Parse", new[] { typeof(string) })!;
        void Check(string planet, string expected, string type = "setstationstorage")
        {
            var request = Activator.CreateInstance(requestType)!;
            var arguments = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), tokenType))!;
            arguments.Add("planetId", parse.Invoke(null, new object[] { planet }));
            arguments.Add("entityId", parse.Invoke(null, new object[] { "1" }));
            requestType.GetProperty("ExtensionData")!.SetValue(request, arguments);
            var command = Activator.CreateInstance(commandType, "station", type, request)!;
            executorType.GetMethod("ExecuteLogisticsSettingLocked", fields)!.Invoke(executor,
                new object?[] { null, command, DateTimeOffset.UtcNow });
            var actual = (string?)commandType.GetProperty("ErrorCode")!.GetValue(command);
            if (actual != expected) throw new Exception($"远程货槽校验应为 {expected}，实际 {actual}");
            Console.WriteLine("通过：远程货槽入口 " + expected);
        }
        try
        {
            dataField.SetValue(null, null); Check("102", "game_not_ready");
            var data = Native("GameData"); var player = Native("Player");
            Set(data, "mainPlayer", player); dataField.SetValue(null, data);
            Check("-1", "invalid_command"); Check("true", "invalid_command");
            Check("102", "factory_unavailable");
            var galaxy = Native("GalaxyData"); var star = Native("StarData"); var planet = Native("PlanetData");
            var stars = Array.CreateInstance(star.GetType(), 1); stars.SetValue(star, 0);
            var planets = Array.CreateInstance(planet.GetType(), 2); planets.SetValue(planet, 1);
            Set(galaxy, "stars", stars); Set(galaxy, "starCount", 1); Set(star, "planets", planets);
            Set(star, "planetCount", 2); Set(planet, "id", 102); Set(data, "galaxy", galaxy);
            var factory = Native("PlanetFactory"); Set(planet, "factory", factory);
            var entities = Array.CreateInstance(game.GetType("EntityData", true)!, 2);
            Set(factory, "entityPool", entities); Set(factory, "entityCursor", 2);
            Check("102", "target_not_found");
            var entity = Activator.CreateInstance(entities.GetType().GetElementType()!)!;
            Set(entity, "id", 1); entities.SetValue(entity, 1);
            // 玩家在太空且没有本地行星，远程目标仍进入运输站类型校验。
            Check("102", "invalid_station");
            Check("102", "invalid_dispenser", "setdispenser");
            Check("102", "invalid_dispenser", "setdispensersetting");
            Check("102", "invalid_miner", "setveincollectorspeed");
            Check("-1", "invalid_command", "setdispensersetting");
            Check("true", "invalid_command", "setveincollectorspeed");
        }
        finally { dataField.SetValue(null, oldData); }
    }
}
