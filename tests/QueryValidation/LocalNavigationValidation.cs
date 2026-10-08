using System.Reflection;

static class LocalNavigationValidation
{
    public static void Run(Assembly mod)
    {
        const BindingFlags methods = BindingFlags.Static | BindingFlags.NonPublic;
        var unity = Assembly.Load("UnityEngine.CoreModule");
        var game = Assembly.Load("Assembly-CSharp");
        var vectorType = unity.GetType("UnityEngine.Vector3", true)!;
        var colliderType = game.GetType("ColliderData", true)!;
        var navigation = mod.GetType("AutomaticDSP.Tasks.LocalNavigation", true)!;
        object Vector(float x, float y, float z) => Activator.CreateInstance(vectorType, x, y, z)!;
        float Component(object v, string axis) => (float)vectorType.GetField(axis)!.GetValue(v)!;
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("通过：" + name); }
        object Box(float x, float y, float z, float ex, float ey, float ez)
        {
            var collider = Activator.CreateInstance(colliderType)!;
            colliderType.GetField("idType")!.SetValue(collider, 0x03000001);
            colliderType.GetField("pos")!.SetValue(collider, Vector(x, y, z));
            colliderType.GetField("ext")!.SetValue(collider, Vector(ex, ey, ez));
            colliderType.GetField("q")!.SetValue(collider,
                Activator.CreateInstance(unity.GetType("UnityEngine.Quaternion", true)!, 0f, 0f, 0f, 1f));
            return collider;
        }
        bool Hit(object box, object a, object b, float padding = 0) =>
            (bool)navigation.GetMethod("Intersects", methods)!.Invoke(null, new[]{box, a, b, (object)padding})!;
        object Capsule(float x, float y, float z, float ex, float ey, float ez, float radius)
        {
            var collider = Box(x, y, z, ex, ey, ez);
            colliderType.GetField("idType")!.SetValue(collider, 0x02000001);
            colliderType.GetField("radius")!.SetValue(collider, radius);
            return collider;
        }
        var capsule = Capsule(0, 220, 0, 0, -11, 0, 4.2f);
        Check(!Hit(capsule, Vector(13.6f,220,0), Vector(13.6f,220,0), 1.5f), "胶囊旁安全起点不因包围球被判在实体内部");
        Check(Hit(capsule, Vector(-20,220,0), Vector(20,220,0)), "航段中部穿过胶囊轴线仍被阻挡");
        Check(Hit(capsule, Vector(0,240,0), Vector(0,233,0)), "平行航段进入胶囊端帽仍被阻挡");
        Check(!Hit(capsule, Vector(6,200,0), Vector(6,240,0), 1.5f), "平行航段在胶囊安全余量之外保持可达");
        Check(Hit(capsule, Vector(5,220,0), Vector(5,220,0), 1.5f), "胶囊仍保留机甲安全余量");
        Check(Hit(Capsule(0,220,0,0,0,0,4.2f), Vector(-10,220,0), Vector(10,220,0)), "零长度胶囊轴按球体检查");
        var tower = Box(0, 220, 0, 4, 20, 4);
        Check(Hit(tower, Vector(-12, 215, 0), Vector(12, 215, 0)), "航段穿过物流塔碰撞体时识别阻挡");
        Check(!Hit(tower, Vector(-12, 215, 8), Vector(12, 215, 8)), "旁侧空航段不误判阻挡");
        Check(Hit(Box(0, 220, 0, -4, 20, -4), Vector(-12, 215, 0), Vector(12, 215, 0)), "负碰撞尺寸仍按实际范围避障");
        var listType = typeof(List<>).MakeGenericType(colliderType);
        var colliders = Activator.CreateInstance(listType)!;
        var observed = Activator.CreateInstance(listType)!;
        listType.GetMethod("Add")!.Invoke(observed, new[]{Capsule(146.423874f,34.34897f,-148.275543f,
            7.48758554f,1.756481f,-7.582273f,4.2f)});
        foreach (var observedStart in new[]{Vector(144.546631f,40.1335945f,-132.851639f),Vector(180.838867f,49.36261f,-165.363724f)})
        {
            var observedRoute = new object?[]{observed,observedStart,Vector(93.73853f,34.0581474f,-173.3584f),200f,null};
            Check((bool)navigation.GetMethod("TryWaypoint",methods)!.Invoke(null,observedRoute)!,
                "现场斜向胶囊旁的地面和升空起点均可生成路线");
        }
        listType.GetMethod("Add")!.Invoke(colliders, new[]{tower});
        var requested = Vector(0, 200, 0);
        var player = Vector(-20, 199, 0);
        var args = new object?[]{colliders, requested, player, 200f, null};
        Check((bool)navigation.GetMethod("TryLandingPoint", methods)!.Invoke(null, args)!, "建筑中心目标找到附近停靠点");
        var landing = args[4]!;
        Check(Math.Abs(Component(landing, "x")) > 5 && !Hit(tower, landing, Vector(Component(landing,"x") * 1.25f,
            Component(landing,"y") * 1.25f, Component(landing,"z") * 1.25f), 1.5f), "停靠点只留机甲碰撞余量，不叠加到达容差");
        args = new object?[]{Activator.CreateInstance(listType), requested, player, 200f, null};
        Check((bool)navigation.GetMethod("TryLandingPoint", methods)!.Invoke(null,args)! && Component(args[4]!,"x") == 0,
            "空地目标保持原落点");
        var routeArgs = new object?[]{colliders, Vector(-20,199,0), Vector(20,199,0), 200f, null};
        Check((bool)navigation.GetMethod("TryWaypoint",methods)!.Invoke(null,routeArgs)!, "挡路物流塔能够生成局部绕行路线");
        var waypoint = routeArgs[4]!;
        var startFly = Vector(-21.5f, 213.925f, 0);
        var waypointFly = Vector(Component(waypoint,"x") * 1.075f, Component(waypoint,"y") * 1.075f, Component(waypoint,"z") * 1.075f);
        Check(Math.Abs(Component(waypoint,"z")) > 1 && !Hit(tower, startFly, waypointFly, 1.5f), "绕行第一段不穿越物流塔");
        var routePosition = player;
        var destination = Vector(20,199,0);
        var routeComplete = false;
        for (var i = 0; i < 30; i++)
        {
            routeArgs = new object?[]{colliders,routePosition,destination,200f,null};
            Check((bool)navigation.GetMethod("TryWaypoint",methods)!.Invoke(null,routeArgs)!, "滚动绕行保持可达：" + i);
            routePosition = routeArgs[4]!;
            if (Math.Abs(Component(routePosition,"x") - 20) < 1 && Math.Abs(Component(routePosition,"z")) < 1)
            { routeComplete = true; break; }
        }
        Check(routeComplete, "连续绕行判断能够抵达物流塔另一侧");
        var lowBuilding = Activator.CreateInstance(listType)!;
        listType.GetMethod("Add")!.Invoke(lowBuilding,new[]{Box(0,209,0,2,1,2)});
        Check((bool)navigation.GetMethod("CanReachWaypoint",methods)!.Invoke(null,
            new object[]{lowBuilding,Vector(-40,196,0),Vector(40,196,0),200f})!,
            "长距离贴地飞行不以穿入地面的长弦误判矮建筑阻挡");
        routeArgs = new object?[]{colliders,Vector(4.8f,200,0),Vector(20,199,0),200f,null};
        Check((bool)navigation.GetMethod("TryWaypoint",methods)!.Invoke(null,routeArgs)!, "贴近建筑时可以退出安全余量而不误报无路可走");
        var middleBlocked = Activator.CreateInstance(listType)!;
        listType.GetMethod("Add")!.Invoke(middleBlocked,new[]{Box(45,210,0,10,30,10)});
        routeArgs = new object?[]{middleBlocked,Vector(0,200,0),Vector(80,183.3f,0),200f,null};
        Check((bool)navigation.GetMethod("TryWaypoint",methods)!.Invoke(null,routeArgs)!, "长航段中间点被建筑占用时仍可选择旁侧路径");
        foreach (var opposite in new[]{Vector(1,-200,0),Vector(0,-200,0),Vector(0.001f,-200,0)})
        {
            var oppositeStart = Vector(0,200,0);
            var empty = Activator.CreateInstance(listType)!;
            routeArgs = new object?[]{empty,oppositeStart,opposite,200f,null};
            Check((bool)navigation.GetMethod("TryWaypoint",methods)!.Invoke(null,routeArgs)!, "近对跖与精确对跖目标可生成前进航段");
            var nextPoint = routeArgs[4]!;
            var nx = Component(nextPoint,"x");
            var nz = Component(nextPoint,"z");
            Check(nx * nx + nz * nz > 100 && Component(nextPoint,"y") > 190,
                "对跖目标返回当前位置前方短航段，不将远处微小投影当作到达偏差");
            var obstacle = Box(nx * 0.54f,214, nz * 0.54f,4,20,4);
            var oppositeBlocked = Activator.CreateInstance(listType)!;
            listType.GetMethod("Add")!.Invoke(oppositeBlocked,new[]{obstacle});
            Check(!(bool)navigation.GetMethod("CanReachWaypoint",methods)!.Invoke(null,
                new object[]{oppositeBlocked,oppositeStart,opposite,200f})!, "对跖方向的前方碰撞仍被检查");
            foreach (var altitude in new[]{200f,250f})
            {
                routeArgs = new object?[]{oppositeBlocked,Vector(0,altitude,0),opposite,200f,null};
                Check((bool)navigation.GetMethod("TryWaypoint",methods)!.Invoke(null,routeArgs)!,
                    "对面目标受局部阻挡时，地面及升空起点均能绕行");
                var detour = routeArgs[4]!;
                Check(Component(detour,"y") > 180 && !Hit(obstacle,Vector(0,215,0),
                    Vector(Component(detour,"x") * 1.075f,Component(detour,"y") * 1.075f,Component(detour,"z") * 1.075f),1.5f),
                    "对跖绕行航段保持本地且不穿过障碍");
            }
        }
        routeArgs = new object?[]{Activator.CreateInstance(listType),Vector(200,0,0),Vector(-200,0,0),200f,null};
        Check((bool)navigation.GetMethod("TryWaypoint",methods)!.Invoke(null,routeArgs)! &&
            Math.Abs(Component(routeArgs[4]!,"z")) > 10, "非极点的精确对跖方向也能稳定推进");
        var blocked = Activator.CreateInstance(listType)!;
        listType.GetMethod("Add")!.Invoke(blocked, new[]{Box(0,220,0,100,30,100)});
        args = new object?[]{blocked,requested,player,200f,null};
        Check(!(bool)navigation.GetMethod("TryLandingPoint", methods)!.Invoke(null,args)!, "无可用停靠空地时明确失败而非持续卡住");
        var inputType = mod.GetType("AutomaticDSP.Tasks.FlightInput",true)!;
        var input = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(inputType);
        var feedback = mod.GetType("AutomaticDSP.Tasks.FlightNavigation",true)!.GetMethod("SetLocalFlightInput",methods)!;
        object? InputField(string name) => inputType.GetField(name,BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(input);
        feedback.Invoke(null,new[]{input,Vector(10,0,0),Vector(13.3f,0,0),(object)30f});
        Check((float)InputField("Thrust")! < 0.002f, "反馈接近零时推进强度也接近零，不再以大推力反复反向");
        feedback.Invoke(null,new[]{input,Vector(100,0,0),Vector(0,0,0),(object)30f});
        Check(Math.Abs((float)InputField("Thrust")! - 1) < 0.0001f, "远处仍可使用原生飞行速度上限");
        var positionError = 50f;
        var speed = 0f;
        var lowestError = positionError;
        var desired = 0f;
        for(var i=0;i<1800;i++)
        {
            if (i % 6 == 0)
            {
                feedback.Invoke(null,new[]{input,Vector(positionError,0,0),Vector(speed,0,0),(object)30f});
                desired = Component(InputField("Direction")!,"x");
            }
            // 同向一维时与原生 Slerp 的 0.022/帧响应等价，不模拟原生碰撞或着陆。
            speed += (desired - speed) * 0.022f;
            positionError -= speed / 60;
            lowestError = Math.Min(lowestError,positionError);
        }
        Check(lowestError >= -0.01f && Math.Abs(positionError) < 0.02f && Math.Abs(speed) < 0.02f,
            "每六 tick 更新输入时接近目标仍稳定收敛，不反复越过落点");
        var arrived = mod.GetType("AutomaticDSP.Tasks.FlightNavigation",true)!.GetMethod("IsSurfaceArrival",methods)!;
        bool AtSurface(string mode,bool grounded,float altitude,float speed) => (bool)arrived.Invoke(null,
            new object[]{Enum.Parse(game.GetType("EMovementState",true)!,mode),grounded,altitude,speed})!;
        Check(!AtSurface("Walk",false,2,1) && AtSurface("Walk",true,0,0), "Walk 下降阶段仍需等待实际接地");
        Check(AtSurface("Drift",false,1,0.1f) && !AtSurface("Drift",false,1,10) && !AtSurface("Fly",false,15,0),
            "只接受稳定水面漂浮，不把高速经过或空中飞行认作表面到达");
    }
}
