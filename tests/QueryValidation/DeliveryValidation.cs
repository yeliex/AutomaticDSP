using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

static class DeliveryValidation
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
        object Get(object target, string name) => target.GetType().GetField(name, flags)?.GetValue(target)
            ?? target.GetType().GetProperty(name, flags)!.GetValue(target)!;
        var gameMain = game.GetType("GameMain")!;
        var dataField = gameMain.GetField("data")!;
        var itemsField = game.GetType("LDB")!.GetField("_items", BindingFlags.NonPublic | BindingFlags.Static)!;
        var stacksField = game.GetType("StorageComponent")!.GetField("itemStackCount")!;
        var oldData = dataField.GetValue(null); var oldItems = itemsField.GetValue(null); var oldStacks = stacksField.GetValue(null);
        var commandType = mod.GetType("AutomaticDSP.Tasks.CommandState")!;
        var requestType = mod.GetType("AutomaticDSP.Tasks.TaskCommandRequest")!;
        var executorType = mod.GetType("AutomaticDSP.Tasks.TaskCommandExecutor")!;
        var finisherType = mod.GetType("AutomaticDSP.Tasks.TaskCommandFinisher")!;
        var parameters = finisherType.GetMethod("Invoke")!.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var finisher = Expression.Lambda(finisherType, Expression.Block(
            Expression.Assign(Expression.Property(parameters[0], "Status"), parameters[1]),
            Expression.Assign(Expression.Property(parameters[0], "ErrorCode"), parameters[2]),
            Expression.Assign(Expression.Property(parameters[0], "Result"), parameters[5]), Expression.Empty()), parameters).Compile();
        var executor = Activator.CreateInstance(executorType, finisher)!;
        var tokenType = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.Linq.JToken")!;
        var parse = tokenType.GetMethod("Parse", new[] { typeof(string) })!;
        object Check(string type, string json, string error = null)
        {
            var request = Activator.CreateInstance(requestType)!;
            var args = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), tokenType))!;
            foreach (var pair in System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject())
                args.Add(pair.Name, parse.Invoke(null, new object[] { pair.Value.GetRawText() }));
            requestType.GetProperty("ExtensionData")!.SetValue(request, args);
            var command = Activator.CreateInstance(commandType, "delivery", type, request)!;
            executorType.GetMethod("ExecuteDeliveryCommand", flags)!.Invoke(executor, new[] { command, (object)DateTimeOffset.UtcNow });
            if (!Equals(commandType.GetProperty("ErrorCode")!.GetValue(command), error) ||
                !Equals(commandType.GetProperty("Status")!.GetValue(command), error == null ? "SUCCEEDED" : "FAILED"))
                throw new Exception($"随身库存命令结果错误：{type} {json}，实际错误 {commandType.GetProperty("ErrorCode")!.GetValue(command)}");
            return command;
        }
        try
        {
            foreach (var type in new[] { "transferInventoryItem", "setDeliverySlot", "setDeliveryEnabled" })
            {
                dataField.SetValue(null, null); Check(type, "{}", "game_not_ready");
                dataField.SetValue(null, Native("GameData")); Check(type, "{}", "game_not_ready");
            }
            var data = Native("GameData"); var player = Native("Player");
            Set(data, "mainPlayer", player); Set(data, "history", Native("GameHistoryData")); dataField.SetValue(null, data);
            Check("transferInventoryItem", "{}", "game_not_ready");
            var package = Native("StorageComponent"); var delivery = Native("DeliveryPackage");
            Set(player, "package", package);
            Check("setDeliveryEnabled", "{}", "game_not_ready");
            Set(player, "deliveryPackage", delivery);
            Set(player, "movementState", Enum.Parse(game.GetType("EMovementState")!, "Sail"));
            if (gameMain.GetProperty("localPlanet")!.GetValue(null) != null) throw new Exception("夹具必须在太空");
            var stacks = new int[12000]; stacks[1210] = 100; stacksField.SetValue(null, stacks);
            var item = Native("ItemProto"); Set(item, "ID", 1210); Set(item, "UnlockKey", -1);
            var items = Native("ItemProtoSet");
            // 原型夹具绕过 Unity 资源加载，所有库存操作仍调用真实游戏方法。
            Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.Object")!.GetField("m_CachedPtr", flags)!.SetValue(items, new IntPtr(1));
            var itemArray = Array.CreateInstance(item.GetType(), 1); itemArray.SetValue(item, 0); Set(items, "dataArray", itemArray);
            items.GetType().BaseType!.GetField("dataIndices", flags)!.SetValue(items, new Dictionary<int, int> { [1210] = 0 });
            itemsField.SetValue(null, items);
            var packageGrids = Array.CreateInstance(game.GetType("StorageComponent+GRID")!, 1);
            var deliveryGrids = Array.CreateInstance(game.GetType("DeliveryPackage+GRID")!, 100);
            Set(package, "size", 1); Set(package, "grids", packageGrids);
            Set(delivery, "grids", deliveryGrids); Set(delivery, "rowCount", 1); Set(delivery, "colCount", 1);
            Check("setDeliveryEnabled", "{\"enabled\":true}", "tech_locked");
            Set(delivery, "unlocked", true);
            Check("setDeliveryEnabled", "{\"enabled\":false}");
            Check("setDeliverySlot", "{\"index\":0,\"itemId\":1210}", "invalid_command");
            Check("setDeliverySlot", "{\"index\":4,\"itemId\":1210,\"requireCount\":50}");
            if ((int)Get(deliveryGrids.GetValue(4)!, "itemId") != 1210 || (bool)Get(delivery, "enable")) throw new Exception("太空物流配置失败");
            void Grid(Array grids, int index, int count, int inc)
            {
                var grid = grids.GetValue(index)!;
                Set(grid, "itemId", 1210); Set(grid, "count", count); Set(grid, "inc", inc); Set(grid, "stackSize", 100);
                if (grids == deliveryGrids) Set(grid, "stackSizeMultiplier", 1);
                grids.SetValue(grid, index);
            }
            (int count, int inc) Totals() => (
                (int)Get(packageGrids.GetValue(0)!, "count") + (int)Get(deliveryGrids.GetValue(4)!, "count") + (int)Get(player, "inhandItemCount"),
                (int)Get(packageGrids.GetValue(0)!, "inc") + (int)Get(deliveryGrids.GetValue(4)!, "inc") + (int)Get(player, "inhandItemInc"));
            void Transfer(string from, string to, int count, int moved, string error = null)
            {
                var before = Totals();
                var command = Check("transferInventoryItem", $"{{\"from\":\"{from}\",\"to\":\"{to}\",\"itemId\":1210,\"count\":{count}}}", error);
                var result = commandType.GetProperty("Result")!.GetValue(command) as IDictionary;
                if (Totals() != before || (result != null && Convert.ToInt32(result["movedCount"]) != moved)) throw new Exception("转移数量或增产点不守恒");
            }
            Grid(packageGrids, 0, 0, 0); Grid(deliveryGrids, 4, 100, 237);
            Transfer("delivery", "package", 20, 20);
            Transfer("package", "hand", 10, 10); Transfer("hand", "delivery", 10, 10);
            Transfer("delivery", "hand", 10, 10); Transfer("hand", "package", 10, 10);
            Transfer("package", "delivery", 10, 10);
            Grid(packageGrids, 0, 95, 201); Grid(deliveryGrids, 4, 100, 237);
            Transfer("delivery", "package", 20, 5);
            Transfer("delivery", "package", 20, 0, "no_item_transferred");
            Transfer("package", "delivery", 20, 5);
            Transfer("package", "delivery", 20, 0, "no_item_transferred");
            Set(player, "inhandItemId", 1001); Set(player, "inhandItemCount", 1); Set(player, "inhandItemInc", 2);
            Transfer("package", "hand", 10, 0, "hand_occupied");
            Set(delivery, "unlocked", false); Transfer("delivery", "package", 20, 0, "tech_locked");
            Set(delivery, "unlocked", true);
            Check("setDeliverySlot", "{\"index\":4,\"itemId\":0}", "storage_not_empty");
            var readyArgs = new object[] { null, null, null };
            if ((bool)executorType.GetMethod("TryGetPlayer", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, readyArgs)!)
                throw new Exception("行星命令的通用就绪检查被放宽");
            Console.WriteLine("通过：太空背包六向转移、部分接收／满包回退、数量与增产点守恒、物流配置、缺玩家／库存、科技／活动槽／手持／非空槽拒绝，行星命令仍受限；不连接运行中的游戏。");
        }
        finally { dataField.SetValue(null, oldData); itemsField.SetValue(null, oldItems); stacksField.SetValue(null, oldStacks); }
    }
}
