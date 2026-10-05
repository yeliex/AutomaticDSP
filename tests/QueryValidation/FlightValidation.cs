#nullable enable
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

static class FlightValidation
{
    public static void Run(Assembly mod)
    {
        var game = Assembly.Load("Assembly-CSharp");
        var unity = Assembly.Load("UnityEngine.CoreModule");
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object Empty(Type type) => RuntimeHelpers.GetUninitializedObject(type);
        object Native(string name) => Empty(game.GetType(name, true)!);
        void Set(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, fields);
            if (field != null) field.SetValue(target, value);
            else target.GetType().GetProperty(name, fields)!.SetValue(target, value);
        }
        object? Get(object target, string name) => target.GetType().GetField(name, fields)!.GetValue(target);
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("通过：" + name); }
        var inputType = mod.GetType("AutomaticDSP.Tasks.FlightInput", true)!;
        var navType = mod.GetType("AutomaticDSP.Tasks.FlightNavigation", true)!;
        var input = Empty(inputType); var nav = Empty(navType);
        var player = Native("Player"); var mecha = Native("Mecha");
        Set(player, "mecha", mecha); Set(input, "Player", player);
        var vector = Activator.CreateInstance(unity.GetType("UnityEngine.Vector3", true)!, 1f, 0f, 0f)!;
        var velocity = Activator.CreateInstance(game.GetType("VectorLF3", true)!, 1000d, 0d, 0d)!;
        Set(player, "uVelocity", velocity); Set(input, "Direction", vector);
        Set(nav, "wantedSpeed", 2000d); Set(mecha, "maxSailSpeed", 2000f);
        Set(mecha, "coreEnergyCap", 1000000000d); Set(mecha, "coreEnergy", 250000000d);
        Set(mecha, "thrustPowerPerAcc", 10000d); Set(mecha, "energyConsumptionCoef", 1d);
        void Sail() => navType.GetMethod("UpdateSailInput", fields)!.Invoke(nav, new object?[]{input, null});
        Sail(); Check((bool)Get(input, "Boost")!, "核心低于容量30%但能制动时仍优先加速");
        Set(mecha, "coreEnergy", 10000d); Sail(); Check(!(bool)Get(input, "Boost")!, "不足原生制动储备时不继续加速");
        Set(mecha, "coreEnergy", 250000000d); Set(nav, "wantedSpeed", 1000d); Sail();
        Check(!(bool)Get(input, "Boost")!, "达到航速上限时停止加速");
        var controller = Native("PlayerController"); Set(input, "controller", controller);
        var actions = Array.CreateInstance(game.GetType("PlayerAction", true)!, 4);
        var names = new[]{"actionWalk", "actionFly", "actionSail", "actionDrift"};
        for (var i=0;i<names.Length;i++)
        {
            var action = Empty(controller.GetType().GetField(names[i], fields)!.FieldType);
            Set(controller, names[i], action); actions.SetValue(action, i);
        }
        Set(controller, "actions", actions);
        void Attach(object owner) => inputType.GetMethod("EnsureAttached", fields)!.Invoke(owner, null);
        Set(input,"Navigation",nav);
        Attach(input); Check(actions.GetValue(2)!.GetType().Name == "InputAction", "导航包装原生动作输入");
        Check(actions.GetValue(3)!.GetType().Name == "InputAction", "导航包装原生漂浮动作以恢复起飞");
        for(var i=0;i<names.Length;i++) actions.SetValue(Get(controller,names[i]),i);
        Attach(input); Check(actions.GetValue(2)!.GetType().Name == "InputAction", "原生动作数组被重建后恢复导航输入");
        var competing = Empty(inputType); Set(competing,"controller",controller); Attach(competing);
        Check((string?)Get(competing,"Error") == "flight_input_conflict", "第二个飞行输入不能覆盖现有导航");
        inputType.GetMethod("Dispose")!.Invoke(input,null);
        Check((bool)inputType.GetProperty("InputReleased",fields)!.GetValue(input)!, "导航释放时报告输入已释放");
        // 未初始化的 Unity 控制器被原生空对象判定视为已销毁，动作还原另需实机验收。
        Set(nav,"Distance",5000d); Set(nav,"Tolerance",20d); Set(mecha,"thrusterLevel",3);
        Set(mecha,"coreEnergy",0d);
        var direction=Activator.CreateInstance(game.GetType("VectorLF3",true)!,0d,0d,1d)!;
        void Warp(long tick = 0) => navType.GetMethod("UpdateWarp",fields)!.Invoke(nav,new object?[]{input,direction,null,tick});
        Warp();
        Check((string?)Get(nav,"WarpStatus")=="insufficient_energy","短于旧距离门槛的曲速请求进入原生能量校验");
        Set(mecha,"maxWarpSpeed",1000000f); Set(mecha,"warpStartPowerPerSpeed",100d); Set(mecha,"coreEnergy",100000001d);
        Set(player,"uRotation",Activator.CreateInstance(unity.GetType("UnityEngine.Quaternion",true)!,0f,0f,0f,1f)!);
        Set(player,"uVelocity",direction);
        var storage=Native("StorageComponent"); var gridType=storage.GetType().GetField("grids",fields)!.FieldType.GetElementType()!;
        var grid=Activator.CreateInstance(gridType)!; Set(grid,"itemId",1210); Set(grid,"count",1);
        var grids=Array.CreateInstance(gridType,1); grids.SetValue(grid,0); Set(storage,"grids",grids); Set(mecha,"warpStorage",storage);
        Set(mecha,"reactorStorage",storage);
        Set(player,"movementState",Enum.Parse(game.GetType("EMovementState",true)!,"Drift"));
        navType.GetMethod("Plan",fields)!.Invoke(nav,new object?[]{input,direction,null,false,false});
        Check((string?)Get(nav,"Phase")=="takingOff" && (string?)Get(input,"Mode")=="takeOff",
            "漂浮状态通过原生起飞输入继续导航");
        Set(nav,"landing",true); Set(nav,"Distance",1d);
        Set(player,"movementState",Enum.Parse(game.GetType("EMovementState",true)!,"Walk"));
        navType.GetMethod("Plan",fields)!.Invoke(nav,new object?[]{input,direction,null,true,false});
        Check((string?)Get(input,"Mode")=="idle" && (string?)Get(nav,"Phase")=="landing",
            "下降途中切换 Walk 后继续等待接地，不反复起飞");
        Set(nav,"landing",false); Set(nav,"Distance",5000d);
        Warp();
        Check((bool)Get(input,"WarpToggle")! && (string?)Get(nav,"WarpStatus")=="starting","短距离且略高于原生启动耗能即可请求曲速");
        Set(nav,"WarpUsed",true); Set(nav,"Distance",100000000d);
        Set(input,"WarpToggle",false); Set(mecha,"coreEnergy",200000000d);
        Warp(60);
        Check(!(bool)Get(input,"WarpToggle")! && (string?)Get(nav,"WarpStatus")=="recharging",
            "低能退出后即使足够原生启动耗能也等待充分充能");
        Set(mecha,"coreEnergy",900000000d); Set(nav,"nextWarpTick",120L);
        Warp(60);
        Check(!(bool)Get(input,"WarpToggle")!,"曲速完全退出后等待原生状态稳定");
        Warp(120);
        Check((bool)Get(input,"WarpToggle")! && (string?)Get(nav,"WarpStatus")=="starting",
            "同一导航充分充能后再次请求原生曲速");
        Set(input,"WarpToggle",false); Set(nav,"Distance",5000d); Warp(180);
        Check(!(bool)Get(input,"WarpToggle")! && (string?)Get(nav,"WarpStatus")=="approaching",
            "退出曲速后的近程不反复消耗翘曲器");
        Set(nav,"nextGuidanceTick",999L);
        Set(nav,"LandingPosition",vector);
        Set(nav,"localWaypoint",Activator.CreateInstance(unity.GetType("UnityEngine.Vector3",true)!, -1f,0f,0f)!);
        navType.GetMethod("YieldToPlayer",fields)!.Invoke(nav,null);
        Check((string?)Get(nav,"Phase")=="manualOverride" && (long)Get(nav,"nextGuidanceTick")! == 0,
            "临时人工接管保留目的地并使下一次导航重新规划");
        Check(Equals(Get(nav,"localWaypoint"),Get(nav,"LandingPosition")), "人工移动后不返回已过时的绕行点");
        var dataField = game.GetType("GameMain", true)!.GetField("data")!;
        var oldData = dataField.GetValue(null);
        var commandType = mod.GetType("AutomaticDSP.Tasks.CommandState", true)!;
        var executorType = mod.GetType("AutomaticDSP.Tasks.TaskCommandExecutor", true)!;
        var finisherType = mod.GetType("AutomaticDSP.Tasks.TaskCommandFinisher", true)!;
        var parameters = finisherType.GetMethod("Invoke")!.GetParameters()
            .Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var finisher = Expression.Lambda(finisherType, Expression.Block(
            Expression.Assign(Expression.Property(parameters[0], "ErrorCode"), parameters[2]),
            Expression.Empty()), parameters).Compile();
        var executor = Activator.CreateInstance(executorType, finisher)!;
        void Replenish(string expected)
        {
            var command = Activator.CreateInstance(commandType, "warper", "autoReplenishMechaWarper",
                Activator.CreateInstance(mod.GetType("AutomaticDSP.Tasks.TaskCommandRequest", true)!))!;
            executorType.GetMethod("ExecuteAutoReplenishMechaWarper", fields)!
                .Invoke(executor, new[] {command, (object)DateTimeOffset.UtcNow});
            Check((string?)commandType.GetProperty("ErrorCode")!.GetValue(command) == expected,
                "翘曲器补充前置校验：" + expected);
        }
        try
        {
            dataField.SetValue(null, null); Replenish("game_not_ready");
            var data = Native("GameData"); Set(data, "mainPlayer", player); dataField.SetValue(null, data);
            var target = Native("PlanetData");
            var distant = Activator.CreateInstance(game.GetType("VectorLF3", true)!, 100000000d, 0d, 0d)!;
            Set(target, "uPosition", distant); Set(target, "uPositionNext", distant);
            Set(target, "runtimeRotation", Activator.CreateInstance(unity.GetType("UnityEngine.Quaternion", true)!, 0f, 0f, 0f, 1f)!);
            Set(nav, "Target", target); Set(nav, "Position", vector);
            Set(player, "movementState", Enum.Parse(game.GetType("EMovementState", true)!, "Sail"));
            Set(player, "uVelocity", velocity);
            navType.GetMethod("Plan", fields)!.Invoke(nav, new object?[]{input, distant, null, false, false});
            Check((string?)Get(nav, "Phase") == "cruising", "远处对准近侧落点仍保持巡航阶段");
            Set(player, "mecha", null!); Replenish("game_not_ready");
            Set(player, "mecha", mecha); Set(mecha, "thrusterLevel", 2);
            Replenish("tech_locked");
        }
        finally { dataField.SetValue(null, oldData); }
    }
}
