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
        Set(nav, "wantedSpeed", 45d);
        Set(player, "uVelocity", Activator.CreateInstance(game.GetType("VectorLF3", true)!, 48d, 0d, 0d)!);
        Sail(); Check((float)Get(input, "Thrust")! == 0, "低于原生推进下限的限速区间松开推进，避免反复加速到100");
        Set(player, "uVelocity", velocity);
        var controller = Native("PlayerController"); Set(input, "controller", controller);
        var actions = Array.CreateInstance(game.GetType("PlayerAction", true)!, 4);
        var names = new[]{"actionWalk", "actionFly", "actionSail", "actionDrift"};
        for (var i=0;i<names.Length;i++)
        {
            var action = Empty(controller.GetType().GetField(names[i], fields)!.FieldType);
            Set(controller, names[i], action); actions.SetValue(action, i);
        }
        Set(controller, "actions", actions);
        Set(player,"controller",controller);
        var sailAction = Get(controller,"actionSail")!;
        Set(sailAction,"player",player); Set(sailAction,"maxWarpSpeed",1000000f); Set(sailAction,"warpSpeedControl",1d);
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
        Check((string?)Get(nav,"WarpStatus")=="distance_too_short","导航在0.5 AU内不请求曲速");
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
        Set(input,"WarpToggle",false); Set(nav,"Distance",19999d);
        Warp();
        Check(!(bool)Get(input,"WarpToggle")! && (string?)Get(nav,"WarpStatus")=="distance_too_short",
            "电量和翘曲器充足时，低于0.5 AU仍不启动");
        Set(nav,"Distance",20000d); Warp();
        Check(!(bool)Get(input,"WarpToggle")!, "退出距离大于0.5 AU时不会刚达最短启动距离就启动");
        Set(nav,"Distance",52400d); Warp();
        Check(!(bool)Get(input,"WarpToggle")!, "启动还须留出退出范围之外的一秒普通航程");
        Set(nav,"Distance",52401d); Warp();
        Check((bool)Get(input,"WarpToggle")!, "超出动态退出范围和启动余量后允许翘曲");
        Set(nav,"nextWarpTick",0L); Set(input,"WarpToggle",false);
        Set(mecha,"maxWarpSpeed",100000f); Set(nav,"Distance",20000d); Warp();
        Check((bool)Get(input,"WarpToggle")! && (string?)Get(nav,"WarpStatus")=="starting","达到0.5 AU后检查原生条件并允许启动");
        Set(nav,"WarpUsed",true); Set(nav,"Distance",100000000d);
        Set(mecha,"warpKeepingPowerPerSpeed",10d);
        Set(input,"WarpToggle",false); Set(input,"Boost",true); Set(mecha,"coreEnergy",200000000d);
        Warp(60);
        Check(!(bool)Get(input,"WarpToggle")! && (string?)Get(nav,"WarpStatus")=="recharging",
            "低能退出后即使足够原生启动耗能也等待充分充能");
        Check(!(bool)Get(input,"Boost")!, "远程回充优先恢复曲速电量，不消耗输出继续普通加速");
        Set(mecha,"coreEnergy",900000000d); Set(nav,"nextWarpTick",120L);
        Warp(60);
        Check(!(bool)Get(input,"WarpToggle")!,"曲速完全退出后等待原生状态稳定");
        Warp(120);
        Check((bool)Get(input,"WarpToggle")! && (string?)Get(nav,"WarpStatus")=="starting",
            "同一导航充分充能后再次请求原生曲速");
        Set(nav,"Distance",50000d); Set(mecha,"coreEnergy",200000000d); Set(input,"WarpToggle",false);
        Warp(180);
        Check((bool)Get(input,"WarpToggle")! && (string?)Get(nav,"WarpStatus")=="starting",
            "剩余短航程在20%电量即可恢复曲速，无须等待90%");
        Set(input,"WarpToggle",false); Set(nav,"Distance",5000d); Warp(240);
        Check(!(bool)Get(input,"WarpToggle")! && (string?)Get(nav,"WarpStatus")=="approaching",
            "退出曲速后的近程不反复消耗翘曲器");
        Set(nav,"Distance",100000000d); Set(nav,"UniversalPosition",Activator.CreateInstance(game.GetType("VectorLF3",true)!,0d,0d,100000000d)!);
        Set(player,"warpCommand",true); Set(player,"warpState",0.5f); Set(input,"Thrust",-1f);
        Warp(300);
        Check((float)Get(input,"Thrust")! == 1 && !(bool)Get(input,"WarpToggle")!,
            "翘曲巡航不继承普通航速制动，保持原生速度倍率");
        Set(player,"warpState",0f); Set(nav,"Distance",5501d); Set(input,"WarpToggle",false);
        Warp(301);
        Check(!(bool)Get(input,"WarpToggle")!, "参考运输船，低曲速时在5000米加普通航速余量之外保持曲速");
        Set(nav,"Distance",5500d); Warp(302);
        Check((bool)Get(input,"WarpToggle")!, "达到运输船公式的低速退出距离时退出曲速");
        Set(nav,"nextWarpTick",360L);
        Set(player,"warpCommand",false); Set(player,"warpState",0f);
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
            Set(nav,"wantedSpeed",2000d); Set(input,"Direction",vector);
            Set(player,"uVelocity",velocity); Set(mecha,"coreEnergy",1000000d);
            Sail();
            Check((bool)Get(input,"Boost")!, "固态星巡航低于全程制动储备时仍持续请求最大原生加速");
            Set(mecha,"coreEnergy",250000000d);
            Set(player, "movementState", Enum.Parse(game.GetType("EMovementState", true)!, "Sail"));
            Set(player, "uVelocity", velocity);
            navType.GetMethod("Plan", fields)!.Invoke(nav, new object?[]{input, distant, null, false, false});
            Check((string?)Get(nav, "Phase") == "cruising", "远处对准近侧落点仍保持巡航阶段");
            var near = Activator.CreateInstance(game.GetType("VectorLF3", true)!, 5000d, 0d, 0d)!;
            Set(target,"uPosition",near); Set(target,"uPositionNext",near);
            navType.GetMethod("Plan", fields)!.Invoke(nav, new object?[]{input, near, null, false, false});
            Check((double)Get(nav,"wantedSpeed")! == 2000d, "接近固态星时保持最高普通航速，不按距离提前减速");
            var nearLanding = Activator.CreateInstance(game.GetType("VectorLF3", true)!, 4800d, 100d, 0d)!;
            navType.GetMethod("Plan", fields)!.Invoke(nav, new object?[]{input, nearLanding, null, false, false});
            Check((float)Get(Get(input,"Direction")!,"y")! > 0,
                "进入捕获范围前已经朝可见目标落点调整角度");
            var planetType = Get(target,"type")!;
            var gasCenter = Activator.CreateInstance(game.GetType("VectorLF3",true)!,
                -57040112.518600635d,-5908351.469411579d,-31043038.753827706d)!;
            Set(target,"type",Enum.Parse(game.GetType("EPlanetType",true)!,"Gas"));
            Set(target,"radius",800f); Set(target,"scale",1f);
            Set(target,"uPosition",gasCenter);
            Set(target,"uPositionNext",Activator.CreateInstance(game.GetType("VectorLF3",true)!,
                -57040112.10660282d,-5908351.595946369d,-31043040.63214383d)!);
            Set(player,"uPosition",Activator.CreateInstance(game.GetType("VectorLF3",true)!,
                -57040496.1537631d,-5908228.564001216d,-31041209.847013604d)!);
            Set(player,"uVelocity",Activator.CreateInstance(game.GetType("VectorLF3",true)!,24.0225d,-7.574d,-112.603d)!);
            navType.GetMethod("Plan",fields)!.Invoke(nav,new object?[]{input,gasCenter,null,false,true});
            var gasSpeed = (double)Get(nav,"wantedSpeed")!;
            var gasDirection = Get(input,"Direction")!;
            // 现场已几乎追平公转；新速度目标仍须产生明确向内的相对运动。
            var closure = new[]{"x","y","z"}.Select(axis =>
                ((float)Get(gasDirection,axis)! * gasSpeed - (double)Get(Get(player,"uVelocity")!,axis)!) *
                ((double)Get(gasCenter,axis)! - (double)Get(Get(player,"uPosition")!,axis)!)).Sum();
            Check(closure > 180000d, "气态星现场停滞状态仍请求向星心进近，预计闭合速度超过96米每秒");
            Sail(); Check((float)Get(input,"Thrust")! == 1,
                "气态星公转速度超过相对限速时不在捕获范围外持续制动");
            Set(player,"uVelocity",Activator.CreateInstance(game.GetType("VectorLF3",true)!,
                (double)(float)Get(gasDirection,"x")! * 600,
                (double)(float)Get(gasDirection,"y")! * 600,
                (double)(float)Get(gasDirection,"z")! * 600)!);
            Sail(); Check((float)Get(input,"Thrust")! == -1 && !(bool)Get(input,"Boost")!,
                "气态星过快进近仍请求原生制动，不取消减速保障");
            Set(target,"uPosition",near); Set(target,"uPositionNext",near);
            Set(player,"uPosition",Activator.CreateInstance(game.GetType("VectorLF3",true)!,0d,0d,0d)!);
            Set(player,"uVelocity",Activator.CreateInstance(game.GetType("VectorLF3",true)!,100d,0d,0d)!);
            navType.GetMethod("Plan",fields)!.Invoke(nav,new object?[]{input,near,null,false,true});
            Check(Math.Abs((double)Get(nav,"wantedSpeed")! - 1000d) < 0.001,
                "静止气态星保留按距地表计算的原有限速");
            Set(target,"uPositionNext",Activator.CreateInstance(game.GetType("VectorLF3",true)!,
                5000d,4000d / 60,0d)!);
            navType.GetMethod("Plan",fields)!.Invoke(nav,new object?[]{input,near,null,false,true});
            Check((double)Get(nav,"wantedSpeed")! == 2000d,
                "气态星公转补偿后的请求航速仍受机甲原生上限约束");
            Set(player,"warpCommand",true);
            navType.GetMethod("Plan",fields)!.Invoke(nav,new object?[]{input,near,null,false,true});
            Check(Math.Abs((double)Get(nav,"wantedSpeed")! - 1000d) < 0.001,
                "气态星普通进近速度转换不介入曲速阶段");
            Set(player,"warpCommand",false);
            Set(target,"type",planetType); Set(target,"radius",0f); Set(target,"scale",0f);
            Set(target,"uPosition",Activator.CreateInstance(game.GetType("VectorLF3",true)!,0d,0d,20000000d)!);
            Set(mecha,"maxWarpSpeed",600000f); Set(mecha,"warpStartPowerPerSpeed",400d);
            Set(mecha,"warpKeepingPowerPerSpeed",80d); Set(mecha,"coreEnergyCap",3200000000d);
            Set(mecha,"coreEnergy",2500000000d); Set(player,"uVelocity",direction); Set(input,"WarpToggle",false);
            Warp(360);
            Check((bool)Get(input,"WarpToggle")!, "真实机甲耗能参数下剩余两千万米在低于90%电量时可恢复翘曲");
            Set(sailAction,"maxWarpSpeed",600000f);
            Set(target,"uPosition",Activator.CreateInstance(game.GetType("VectorLF3",true)!,0d,0d,32441d)!);
            Set(target,"uPositionNext",Get(target,"uPosition")!);
            Set(player,"warpCommand",true); Set(player,"warpState",1f);
            Set(input,"WarpToggle",false);
            Warp(419);
            Check(!(bool)Get(input,"WarpToggle")!, "满曲速600000米每秒时在32440米退出阈值之外继续翘曲");
            Set(target,"uPosition",Activator.CreateInstance(game.GetType("VectorLF3",true)!,0d,0d,32440d)!);
            Set(target,"uPositionNext",Get(target,"uPosition")!);
            Warp(420);
            Check((bool)Get(input,"WarpToggle")!, "抵近目标按原生退出尾程结束翘曲");
            Set(player,"warpCommand",false); Set(player,"warpState",0f); Set(input,"WarpToggle",false);
            Warp(480);
            Check(!(bool)Get(input,"WarpToggle")! && (string?)Get(nav,"WarpStatus")=="approaching",
                "抵近退出后仍在当前动态启动范围内时不重启翘曲");
            Set(player,"uPosition",Activator.CreateInstance(game.GetType("VectorLF3",true)!,0d,0d,72440d)!);
            direction=Activator.CreateInstance(game.GetType("VectorLF3",true)!,0d,0d,-1d)!;
            Set(player,"uVelocity",direction);
            Warp(540);
            Check(!(bool)Get(input,"WarpToggle")! && (string?)Get(nav,"WarpStatus")=="aligning",
                "越过目标后已满足距离条件，仍先等待重新对准目标");
            Set(player,"uRotation",Activator.CreateInstance(unity.GetType("UnityEngine.Quaternion",true)!,0f,1f,0f,0f)!);
            Warp(541);
            Check((bool)Get(input,"WarpToggle")! && (string?)Get(nav,"WarpStatus")=="starting",
                "越过目标后重新满足当前距离、航向及能量条件即可恢复翘曲");
            Set(player, "mecha", null!); Replenish("game_not_ready");
            Set(player, "mecha", mecha); Set(mecha, "thrusterLevel", 2);
            Replenish("tech_locked");
        }
        finally { dataField.SetValue(null, oldData); }
    }
}
