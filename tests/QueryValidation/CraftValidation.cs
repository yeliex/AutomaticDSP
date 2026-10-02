#nullable enable
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

static class CraftValidation
{
    public static void Run(Assembly mod)
    {
        var game = Assembly.Load("Assembly-CSharp");
        object Empty(string name) => RuntimeHelpers.GetUninitializedObject(game.GetType(name, true)!);
        void Set(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) field.SetValue(target, value);
            else target.GetType().GetProperty(name)!.SetValue(target, value);
        }
        var gameMain = game.GetType("GameMain", true)!;
        var dataField = gameMain.GetField("data")!;
        var oldData = dataField.GetValue(null);
        var commandType = mod.GetType("AutomaticDSP.Tasks.CommandState", true)!;
        var requestType = mod.GetType("AutomaticDSP.Tasks.TaskCommandRequest", true)!;
        var executorType = mod.GetType("AutomaticDSP.Tasks.TaskCommandExecutor", true)!;
        var finisherType = mod.GetType("AutomaticDSP.Tasks.TaskCommandFinisher", true)!;
        var parameters = finisherType.GetMethod("Invoke")!.GetParameters()
            .Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var finisher = Expression.Lambda(finisherType, Expression.Block(
            Expression.Assign(Expression.Property(parameters[0], "Status"), parameters[1]),
            Expression.Assign(Expression.Property(parameters[0], "ErrorCode"), parameters[2]),
            Expression.Empty()), parameters).Compile();
        var executor = Activator.CreateInstance(executorType, finisher)!;
        object Command() => Activator.CreateInstance(commandType, "craft", "craftInventory", Activator.CreateInstance(requestType))!;
        void Execute(object command) => executorType.GetMethod("ExecuteCraftInventoryLocked", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(executor, new[] { command, (object)DateTimeOffset.UtcNow });
        void Error(object command, string expected)
        {
            if ((string?)commandType.GetProperty("ErrorCode")!.GetValue(command) != expected)
                throw new Exception($"手搓错误校验失败：应为 {expected}");
        }
        try
        {
            dataField.SetValue(null, null);
            var unavailable = Command(); Execute(unavailable); Error(unavailable, "game_not_ready");
            var data = Empty("GameData"); var player = Empty("Player");
            Set(data, "mainPlayer", player); dataField.SetValue(null, data);
            var noForge = Command(); Execute(noForge); Error(noForge, "game_not_ready");
            var mecha = Empty("Mecha"); var forge = Empty("MechaForge");
            Set(player, "mecha", mecha); Set(mecha, "forge", forge);
            if (gameMain.GetProperty("localPlanet")!.GetValue(null) != null)
                throw new Exception("太空夹具必须没有本地行星");
            var invalid = Command(); Execute(invalid); Error(invalid, "invalid_command");
            var task = Empty("ForgeTask"); Set(task, "count", 2);
            var tasks = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(task.GetType()))!;
            tasks.Add(task); Set(forge, "tasks", tasks);
            var waiting = Command();
            commandType.GetProperty("CraftTargets")!.SetValue(waiting, new Dictionary<int, int>());
            commandType.GetProperty("NativeForgeTask")!.SetValue(waiting, task);
            Execute(waiting);
            if ((string?)commandType.GetProperty("Phase")!.GetValue(waiting) != "crafting" ||
                commandType.GetProperty("ErrorCode")!.GetValue(waiting) != null)
                throw new Exception("太空中的原生制造跟踪被拒绝");
            tasks.Clear(); Execute(waiting); Error(waiting, "craft_interrupted");
            Console.WriteLine("通过：手搓缺玩家／制造器拒绝，太空请求进入参数校验、原生任务持续跟踪及移除检测；不模拟原生物资消耗或制造推进。");
        }
        finally { dataField.SetValue(null, oldData); }
    }
}
