using System;
using System.Diagnostics;
using UnityEngine;

namespace AutomaticDSP.Tasks
{
    // 只控制调用方指定的目的地；所有运动与耗能仍由原生 Walk/Fly/Sail 动作执行。
    internal sealed class FlightNavigation
    {
        internal readonly PlanetData Target;
        internal readonly Vector3 Position;
        internal readonly VectorLF3? UniversalPosition;
        internal readonly double Tolerance;
        internal string Phase = "departing";
        internal double Distance;
        internal bool Arrived;
        internal readonly bool UseWarp;
        internal bool WarpUsed;
        internal string WarpStatus;
        private long nextWarpTick;
        private Vector3 departureDirection;
        internal double? ArrivalDistance;
        internal int GuidanceUpdates;
        internal int GuidanceIntervalTicks;
        private long nextGuidanceTick;
        private EMovementState plannedMovement;
        private PlanetData plannedPlanet;
        private bool plannedWarp;
        private VectorLF3? previousPosition;
        private long previousTick;
        private bool previousInSpace;
        private VectorLF3 observedVelocity;
        private double guidanceClearance;
        private double wantedSpeed;
        internal Vector3 LandingPosition;
        private Vector3 localWaypoint;
        private long nextLocalRouteTick;
        private bool landing;
        internal int LocalRouteChecks;
        internal int LocalRouteSearches;
        internal double MaxLocalRouteMilliseconds;

        internal FlightNavigation(PlanetData target, Vector3 position, VectorLF3? universalPosition, double tolerance, bool useWarp)
        {
            Target = target;
            Position = position;
            LandingPosition = target == null ? position : position.normalized * target.realRadius;
            localWaypoint = LandingPosition;
            UniversalPosition = universalPosition;
            Tolerance = tolerance;
            UseWarp = useWarp;
            WarpStatus = useWarp ? "pending" : "disabled";
        }

        internal void Update(FlightInput input, long tick)
        {
            var player = input.Player;
            var controller = player.controller;
            var local = GameMain.localPlanet;
            var onTarget = Target != null && player.planetId == Target.id;
            var gas = Target != null && Target.type == EPlanetType.Gas;
            if (onTarget && !gas && !landing && local?.physics != null && tick >= nextLocalRouteTick)
            {
                var routeStarted = Stopwatch.GetTimestamp();
                LocalRouteChecks++;
                var colliders = LocalNavigation.ReadColliders(local, player.position, LandingPosition);
                var previousLanding = LandingPosition;
                // 已选停靠点仍安全时保持它，避免搜索方向随机甲移动而旋转。
                if (!LocalNavigation.CanLand(colliders, LandingPosition, Target.realRadius) &&
                    !LocalNavigation.TryLandingPoint(colliders, Position, player.position, Target.realRadius,
                        out LandingPosition))
                { input.Error = "landing_area_blocked"; return; }
                var surfacePoint = player.position.normalized * Target.realRadius;
                var remaining = Vector3.Distance(surfacePoint, LandingPosition);
                if (player.movementState == EMovementState.Fly && remaining <= Tolerance &&
                    Vector3.ProjectOnPlane(controller.velocity, surfacePoint.normalized).magnitude <= Math.Min(2, Tolerance * 0.5) &&
                    LocalNavigation.CanLand(colliders, surfacePoint, Target.realRadius))
                {
                    // 接受容差内的安全落地柱；下降期间不再追逐中心或切换路线。
                    LandingPosition = surfacePoint;
                    localWaypoint = surfacePoint;
                    landing = true;
                }
                else if (remaining > Tolerance &&
                    (player.movementState == EMovementState.Fly || player.movementState == EMovementState.Walk) &&
                    ((previousLanding - LandingPosition).sqrMagnitude > 0.01f ||
                        Vector3.Distance(player.position.normalized * Target.realRadius, localWaypoint) <= 3 ||
                        !LocalNavigation.CanReachWaypoint(colliders, player.position, localWaypoint, Target.realRadius)))
                {
                    LocalRouteSearches++;
                    if (!LocalNavigation.TryWaypoint(colliders, player.position, LandingPosition, Target.realRadius, out localWaypoint))
                    { input.Error = "local_route_blocked"; return; }
                }
                MaxLocalRouteMilliseconds = Math.Max(MaxLocalRouteMilliseconds,
                    (Stopwatch.GetTimestamp() - routeStarted) * 1000.0 / Stopwatch.Frequency);
                nextLocalRouteTick = tick + 30;
                nextGuidanceTick = 0;
            }
            var targetWorld = UniversalPosition ?? (Target.uPosition + Maths.QRotateLF(Target.runtimeRotation, LandingPosition));
            Distance = onTarget ? Vector3.Distance(player.position.normalized * Target.realRadius,
                LandingPosition.normalized * Target.realRadius) : (targetWorld - player.uPosition).magnitude;
            var inSpace = local == null && player.movementState == EMovementState.Sail;
            observedVelocity = previousPosition.HasValue ? (player.uPosition - previousPosition.Value) * (60.0 / Math.Max(1, tick - previousTick)) : player.uVelocity;
            if (Target == null && inSpace && !ArrivalDistance.HasValue)
            {
                var closest = Distance;
                if (previousInSpace && previousPosition.HasValue)
                {
                    // 高速下两个采样点都可能在容差外，仍须识别这一 tick 实际穿过的到达范围。
                    var segment = player.uPosition - previousPosition.Value;
                    var fraction = segment.sqrMagnitude > 0 ? Math.Max(0, Math.Min(1,
                        VectorLF3.Dot(targetWorld - previousPosition.Value, segment) / segment.sqrMagnitude)) : 0;
                    closest = (targetWorld - previousPosition.Value - segment * fraction).magnitude;
                }
                if (closest <= Tolerance) ArrivalDistance = closest;
            }
            previousPosition = player.uPosition;
            previousTick = tick;
            previousInSpace = inSpace;
            Arrived = !player.warpCommand && !player.warping && (Target == null
                ? ArrivalDistance.HasValue
                : Distance <= Tolerance && onTarget && (gas
                    ? player.movementState == EMovementState.Fly && controller.velocity.magnitude <= 5 &&
                        player.position.magnitude - Target.realRadius <= 30
                    : IsSurfaceArrival(player.movementState, controller.actionWalk.isGrounded,
                        player.position.magnitude - Target.realRadius, controller.velocity.magnitude)));
            input.WarpToggle = false;
            if (Arrived)
            {
                Phase = "arrived";
                if (WarpUsed) WarpStatus = "completed";
                input.Mode = "idle";
                return;
            }
            if (!UseWarp && (player.warping || player.warpCommand)) { input.Error = "exit_warp_required"; return; }
            if (ArrivalDistance.HasValue)
            {
                // 到达后只等待原生曲速退出，不因退出惯性又掉头追逐目标。
                input.Mode = "sail";
                input.Direction = player.uRotation.Forward();
                input.Thrust = -1;
                input.Boost = false;
                input.WarpToggle = player.warpCommand;
                WarpUsed = true;
                WarpStatus = "exiting";
                Phase = "exitingWarp";
                return;
            }

            var warping = player.warpCommand || player.warping;
            if (tick >= nextGuidanceTick || GuidanceUpdates == 0 || plannedMovement != player.movementState ||
                plannedPlanet != local || plannedWarp != warping)
            {
                guidanceClearance = Math.Max(0, (targetWorld - player.uPosition).magnitude - Tolerance);
                Plan(input, targetWorld, local, onTarget, gas);
                GuidanceUpdates++;
                plannedMovement = player.movementState;
                plannedPlanet = local;
                plannedWarp = warping;
                // 以航速上限覆盖两次重算间的加速；接近关键位置只使用剩余时间的 1/5。
                var speedBound = Math.Max(observedVelocity.magnitude, warping ? player.mecha.maxWarpSpeed : player.mecha.maxSailSpeed);
                GuidanceIntervalTicks = (int)Math.Max(1, Math.Min(60, Math.Floor(guidanceClearance / Math.Max(1, speedBound) * 12)));
                if (VectorLF3.Dot(observedVelocity.normalized, input.Direction) < 0.98)
                    GuidanceIntervalTicks = Math.Min(GuidanceIntervalTicks, 6);
                // 近地控制不使用星际航速估算周期；帧间复用输入，避免误算成每 tick 都重新制导。
                if (onTarget && player.movementState != EMovementState.Sail) GuidanceIntervalTicks = 6;
                nextGuidanceTick = tick + GuidanceIntervalTicks;
            }
            if (player.movementState == EMovementState.Sail && input.Error == null)
            {
                UpdateSailInput(input, local);
                if (UseWarp) UpdateWarp(input, input.Direction, local, tick);
            }
        }

        internal void YieldToPlayer()
        {
            Phase = "manualOverride";
            nextGuidanceTick = 0;
            nextLocalRouteTick = 0;
            previousPosition = null;
            previousInSpace = false;
            departureDirection = Vector3.zero;
            landing = false;
            localWaypoint = LandingPosition;
        }

        private void Plan(FlightInput input, VectorLF3 targetWorld, PlanetData local, bool onTarget, bool gas)
        {
            var player = input.Player;
            input.Thrust = 0;
            input.Lift = 0;
            input.Boost = false;

            // 备用燃料通过已有原生补充流程进入燃烧室，不修改自动补充设置。
            var fuelPresent = false;
            foreach (var grid in player.mecha.reactorStorage.grids)
                if (grid.count > 0) { fuelPresent = true; break; }
            if (!fuelPresent && player.mecha.reactorEnergy < 10000)
                player.mecha.AutoReplenishFuelAll();

            // 原生漂浮也通过跳跃输入起飞，落地途中经过水面不能中断导航。
            if (player.movementState == EMovementState.Walk || player.movementState == EMovementState.Drift)
            {
                // 原生在离地约 3 米时已切换 Walk；下降尚未接地不能触发再次起飞。
                if (landing && onTarget && Distance <= Tolerance)
                {
                    input.Mode = "idle";
                    Phase = "landing";
                    return;
                }
                if (landing) { landing = false; nextLocalRouteTick = 0; }
                input.Mode = "takeOff";
                Phase = "takingOff";
                return;
            }
            if (player.movementState == EMovementState.Fly)
            {
                guidanceClearance = Math.Min(guidanceClearance, Math.Abs(player.position.magnitude - local.realRadius - 50));
                var horizontalSpeed = Vector3.ProjectOnPlane(player.controller.velocity, player.position.normalized).magnitude;
                if (onTarget && (landing || gas && Distance <= Tolerance && horizontalSpeed <= Math.Min(2, Tolerance * 0.5)))
                {
                    input.Mode = gas ? "fly" : "land";
                    input.Lift = gas && player.position.magnitude - Target.realRadius > 25 ? -1 : 0;
                    Phase = gas ? "hovering" : "landing";
                    return;
                }
                input.Mode = "fly";
                input.Direction = onTarget ? (Distance <= Tolerance ? LandingPosition : localWaypoint) - player.position :
                    (Vector3)Maths.QInvRotateLF(local.runtimeRotation, targetWorld - player.uPosition);
                if (onTarget)
                {
                    // 原生 Fly 已以约 0.75 秒的响应平滑速度；反馈的方向和幅度须来自同一个速度目标。
                    var offset = Vector3.ProjectOnPlane(input.Direction, player.position.normalized);
                    SetLocalFlightInput(input, offset,
                        Vector3.ProjectOnPlane(player.controller.velocity, player.position.normalized), player.mecha.walkSpeed * 2.5f);
                }
                var tangent = Vector3.ProjectOnPlane(input.Direction, player.position.normalized);
                if (tangent.sqrMagnitude < 0.001f)
                    input.Direction = Vector3.Cross(player.position.normalized,
                        Math.Abs(player.position.normalized.y) < 0.9f ? Vector3.up : Vector3.right);
                if (!onTarget)
                {
                    // 原生进入 Sail 还要求水平速度 >12.5m/s；升空时追逐目标投影会反复掉头、卡在 50m。
                    if (departureDirection.sqrMagnitude < 0.001f)
                        departureDirection = Vector3.ProjectOnPlane(input.Direction, player.position.normalized).normalized;
                    departureDirection = Vector3.ProjectOnPlane(departureDirection, player.position.normalized).normalized;
                    input.Direction = departureDirection;
                }
                if (!onTarget) input.Thrust = 1;
                input.Lift = onTarget ? (gas && player.position.magnitude - Target.realRadius > 25 ? -1 : 0) : 1;
                Phase = onTarget ? "approachingPosition" : "ascending";
                return;
            }
            if (player.movementState != EMovementState.Sail)
            {
                input.Error = "invalid_movement_state";
                return;
            }

            input.Mode = "sail";
            departureDirection = Vector3.zero;
            VectorLF3 direction;
            if (local != null) guidanceClearance = 0;
            if (local != null && local != Target)
            {
                direction = player.uPosition - local.uPosition;
                wantedSpeed = 180;
                Phase = "leavingPlanet";
            }
            else if (Target != null && local == Target && !gas)
            {
                // 优先直达可见落点；背面落点先原生撞入地表，再补齐近地航段。
                var landingDelta = targetWorld - player.uPosition;
                var landingNormal = (targetWorld - Target.uPosition).normalized;
                direction = VectorLF3.Dot(landingDelta.normalized, -landingNormal) > 0.25
                    ? landingDelta : Target.uPosition - player.uPosition;
                wantedSpeed = player.mecha.maxSailSpeed;
                Phase = "directDescent";
            }
            else if (Target != null && local == Target)
            {
                var radial = player.uPosition - Target.uPosition;
                var up = radial.normalized;
                var point = Maths.QRotateLF(Target.runtimeRotation, LandingPosition.normalized);
                var tangent = point - up * VectorLF3.Dot(point, up);
                var arc = Math.Acos(Math.Max(-1, Math.Min(1, VectorLF3.Dot(up, point)))) * Target.realRadius;
                var altitude = radial.magnitude - Target.realRadius;
                // 气态星低于 48m 才原生切换 Fly；提前下降，避免 Sail 围着小落点范围盘旋。
                var landing = arc < (gas ? 300 : 30);
                var vertical = Math.Max(-50, Math.Min(50, (landing ? (gas ? 20 : 0) : 70) - altitude));
                direction = tangent.normalized * (landing ? Math.Min(40, arc) : 100) + up * vertical;
                if (direction.sqrMagnitude < 0.001) direction = -up;
                // 接近时逐步减速，避免高速转弯半径大于下降区而一直绕点航行。
                wantedSpeed = landing ? 45 : Math.Min(120, Math.Max(45, arc));
                Phase = landing ? "descending" : "approachingPosition";
            }
            else
            {
                var delta = (Target == null ? targetWorld : Target.uPosition) - player.uPosition;
                var targetVelocity = Target == null ? VectorLF3.zero : (Target.uPositionNext - Target.uPosition) * 60;
                var relativeVelocity = player.uVelocity - targetVelocity;
                var surfaceDistance = delta.magnitude - (Target == null ? 0 : Target.realRadius);
                var lead = Math.Min(30, Math.Max(0, surfaceDistance) / Math.Max(100, relativeVelocity.magnitude));
                direction = delta + targetVelocity * lead;
                if (Target != null && !gas)
                {
                    var landingDelta = targetWorld - player.uPosition;
                    var landingNormal = (targetWorld - Target.uPosition).normalized;
                    // 在进入捕获范围前修正入射点；地平线附近保留径向余量，避免高速擦过。
                    if (VectorLF3.Dot(landingDelta.normalized, -landingNormal) > 0.25)
                        direction = landingDelta + targetVelocity * lead;
                }
                if (Target == null && !player.warpCommand && !player.warping)
                {
                    // 机头对准目标不等于速度已对准；提前抵消横向漂移，避免擦过后反复追点。
                    var lateral = relativeVelocity - delta.normalized * VectorLF3.Dot(relativeVelocity, delta.normalized);
                    direction -= lateral * Math.Min(3.5, delta.magnitude / Math.Max(100, relativeVelocity.magnitude));
                }
                // 原生 W 的普通推进目标至少 100m/s；到达范围即结束，不再反复刹停再加速。
                wantedSpeed = Target != null && !gas ? player.mecha.maxSailSpeed : Math.Min(player.mecha.maxSailSpeed, Target == null
                    ? Math.Max(100, (surfaceDistance - Tolerance * 0.75) / 3.5)
                    : Math.Max(100, (surfaceDistance - 700) / 3.5));
                Phase = "cruising";
                if (Target != null) guidanceClearance = Math.Max(0, surfaceDistance - 1200);
                var star = GameMain.localStar;
                if (star != null)
                {
                    guidanceClearance = Math.Min(guidanceClearance, Math.Max(0, (star.uPosition - player.uPosition).magnitude - star.viewRadius - 800));
                    AvoidBody(player.uPosition, star.uPosition, star.viewRadius + 800, ref direction);
                    foreach (var planet in star.planets)
                    {
                        guidanceClearance = Math.Min(guidanceClearance, Math.Max(0, (planet.uPosition - player.uPosition).magnitude - planet.realRadius - 1200));
                        if (planet != Target)
                            // 原生在地表外 900–1000m 切换 localPlanet；避让须覆盖该区域，避免反复进入其他星球。
                            AvoidBody(player.uPosition, planet.uPosition, planet.realRadius + 1200, ref direction);
                    }
                }
            }
            if (Target != null && !gas && (local == null || local == Target) &&
                (local == Target || (Target.uPosition - player.uPosition).magnitude <= Target.realRadius + 1200) &&
                !player.warping && !player.warpCommand)
            {
                var landingDelta = targetWorld - player.uPosition;
                var landingNormal = (targetWorld - Target.uPosition).normalized;
                var surfaceVelocity = Target.GetUniversalVelocityAtLocalPoint(GameMain.gameTime, LandingPosition);
                var approachVelocity = player.uVelocity - surfaceVelocity;
                // 固态星对准近侧落点时保留航速，交给原生地表碰撞落地；偏航或背面落点仍走修正流程。
                if (VectorLF3.Dot(landingDelta.normalized, -landingNormal) > 0.5 &&
                    (local == Target || VectorLF3.Dot(direction.normalized, landingDelta.normalized) > 0.98) &&
                    VectorLF3.Dot(approachVelocity.normalized, landingDelta.normalized) > 0.98)
                {
                    var lead = Math.Min(3, landingDelta.magnitude / Math.Max(100, approachVelocity.magnitude));
                    direction = landingDelta + surfaceVelocity * lead;
                    wantedSpeed = player.mecha.maxSailSpeed;
                    Phase = "directDescent";
                }
            }
            input.Direction = ((Vector3)direction).normalized;
        }

        internal static void SetLocalFlightInput(FlightInput input, Vector3 offset, Vector3 velocity, float speedLimit)
        {
            var desiredVelocity = Vector3.ClampMagnitude(offset * 0.8f - velocity * 0.6f, speedLimit);
            input.Direction = desiredVelocity;
            input.Thrust = desiredVelocity.magnitude / Math.Max(1, speedLimit);
        }

        internal static bool IsSurfaceArrival(EMovementState state, bool grounded, float altitude, float speed) =>
            state == EMovementState.Walk && grounded ||
            // 原生水面移动使用 Drift，稳定漂浮即可到达，不要求接触海床。
            state == EMovementState.Drift && altitude <= 3 && speed <= 2;

        private void UpdateSailInput(FlightInput input, PlanetData local)
        {
            var player = input.Player;
            // 加速及转向采用原生 Sail 的速度参考系，不能用移动目标的相对速度判断是否对准。
            var controlVelocity = player.uVelocity;
            if (local != null)
            {
                var altitude = (player.uPosition - local.uPosition).magnitude - local.realRadius;
                controlVelocity -= local.GetUniversalVelocityAtLocalPoint(GameMain.gameTime, player.position) *
                    Math.Max(0, Math.Min(1, (600 - altitude) / 450));
            }
            var speed = controlVelocity.magnitude;
            var alignment = VectorLF3.Dot(controlVelocity.normalized, input.Direction);
            var speedLimit = wantedSpeed;
            if (alignment < 0.7)
                speedLimit = Math.Min(speedLimit, Math.Max(120, player.mecha.maxSailSpeed * 0.3));
            // S 键制动按当前速度衰减，提前限速；不直接写入速度或抵消惯性。
            input.Thrust = speed > speedLimit * 1.08 ? -1 :
                speedLimit < 100 && speed >= speedLimit ? 0 : 1;
            // 固态星可用原生碰撞收速，持续请求最大原生加速，不随速度抬高全程制动储备。
            // 太空点与气态星仍需主动收速，保留对应制动余量。
            var brakingReserve = 10000.0;
            if (Target == null || Target.type == EPlanetType.Gas)
                brakingReserve += player.mecha.thrustPowerPerAcc * player.mecha.reactorPowerConsRatio * 3 * (1.5 * speed + 50);
            input.Boost = input.Thrust > 0 && speed < speedLimit - 1 && alignment > 0.95 &&
                player.mecha.coreEnergy > brakingReserve;
        }

        private void UpdateWarp(FlightInput input, VectorLF3 direction, PlanetData local, long tick = 0)
        {
            var player = input.Player;
            var mecha = player.mecha;
            var distance = Target == null ? Distance : (Target.uPosition - player.uPosition).magnitude - Target.realRadius;
            var alignment = VectorLF3.Dot(player.uRotation.Forward(), direction.normalized);
            // 只预留原生退出曲速尾程的耗能，不提前扣留 30% 核心能量。
            var reserve = 10000 + mecha.warpKeepingPowerPerSpeed * mecha.maxWarpSpeed * 0.3 * mecha.reactorPowerConsRatio;
            if (player.warpCommand || player.warping)
            {
                WarpUsed = true;
                nextWarpTick = tick + 60;
                // 参考运输船的原生退出距离；使用机甲实际曲速，计入天体衰减和原生速度控制。
                var exitDistance = Math.Max(Tolerance,
                    player.controller.actionSail.currentWarpSpeed * 0.0449 + 5000 + mecha.maxSailSpeed * 0.25);
                var delta = (Target == null ? UniversalPosition.Value : Target.uPosition) - player.uPosition;
                var velocity = observedVelocity - (Target == null ? VectorLF3.zero : (Target.uPositionNext - Target.uPosition) * 60);
                // 与沿实际运动方向到最近点的路程比较；直接用退出时长乘当前曲速会过早退出。
                var alongToClosest = velocity.sqrMagnitude > 1 ? VectorLF3.Dot(delta, velocity.normalized) : double.PositiveInfinity;
                var exit = local != null || distance <= exitDistance || alongToClosest <= exitDistance ||
                    alignment < 0.98 || mecha.coreEnergy <= reserve;
                input.WarpToggle = player.warpCommand && exit;
                input.Boost = false;
                // 翘曲期间 S 会降低原生 warpSpeedControl，不能沿用普通航速限速。
                input.Thrust = 1;
                if (exit || !player.warpCommand)
                {
                    input.Direction = player.uRotation.Forward();
                    input.Thrust = Target == null ? -1 : 1;
                }
                WarpStatus = !player.warpCommand || exit ? "exiting" : "cruising";
                Phase = WarpStatus == "exiting" ? "exitingWarp" : "warping";
                return;
            }
            if (tick < nextWarpTick) { WarpStatus = WarpUsed ? "recharging" : "native_rejected"; return; }
            if (local != null) { WarpStatus = "not_in_space"; return; }
            // 1 AU = 40000 米；导航从 0.5 AU 起允许启动，与退出距离分开以避免末段反复切换。
            if (distance < 20000 || distance <= Tolerance)
            { WarpStatus = WarpUsed ? "approaching" : "distance_too_short"; return; }
            // 满曲速的退出范围可能超过 0.5 AU；启动前避开该范围，并覆盖原生重启冷却的一秒航程。
            var attainableWarpSpeed = mecha.maxWarpSpeed *
                (Math.Pow(1001, player.controller.actionSail.targetAtten) - 1) / 1000;
            var startClearance = Math.Max(Tolerance,
                attainableWarpSpeed * 0.0449 + 5000 + mecha.maxSailSpeed * 0.25);
            if (distance <= startClearance + mecha.maxSailSpeed)
            { WarpStatus = WarpUsed ? "approaching" : "distance_too_short"; return; }
            // 近程交给普通航行；恢复曲速按剩余航程蓄能，长程仍分段，避免刚够启动就消耗翘曲器。
            if (WarpUsed && mecha.coreEnergy < Math.Min(mecha.coreEnergyCap * 0.9,
                EstimateWarpEnergy(mecha, distance, Target != null) * 1.1 + reserve))
            {
                // 远程等待曲速时先恢复储能，避免将反应堆输出消耗在收益很小的普通加速上。
                input.Boost = false;
                WarpStatus = "recharging";
                return;
            }
            if (mecha.thrusterLevel < 3) { WarpStatus = "tech_locked"; return; }
            if (mecha.coreEnergy <= mecha.warpStartPowerPerSpeed * mecha.maxWarpSpeed)
            { WarpStatus = "insufficient_energy"; return; }
            if (!mecha.HasWarper() && !(mecha.autoReplenishWarper && player.package.GetItemCount(1210) > 0))
            { WarpStatus = "missing_warper"; return; }
            if (alignment < 0.999 || VectorLF3.Dot(player.uVelocity.normalized, direction.normalized) < 0.98)
            { WarpStatus = "aligning"; return; }
            input.WarpToggle = true;
            input.Boost = false;
            nextWarpTick = tick + 60;
            WarpStatus = "starting";
            Phase = "startingWarp";
        }

        internal static double EstimateWarpEnergy(Mecha mecha, double distance, bool targetPlanet)
        {
            // 原生启动耗能按 (1-state)^3、维持耗能按 state 积分；不抵扣尚未产生的反应堆充电。
            var rampSeconds = 1 / (0.0055655558 * 60);
            var energy = mecha.maxWarpSpeed * (mecha.warpStartPowerPerSpeed * (rampSeconds / 4 + 1.0 / 60) +
                mecha.warpKeepingPowerPerSpeed * (rampSeconds / 2 + 1.0 / 60));
            // 按原生衰减曲线估算目标附近航段，而非将整段视为最低曲速。
            // 近端采样给耗能留余量，平方分段加密行星附近；绕行和其他天体仍可能要求再次充能。
            var nearDistance = targetPlanet ? Math.Min(distance, 2480000) : 0;
            var equivalentDistance = Math.Max(0, distance - nearDistance);
            var previous = 0.0;
            for (var i = 1; i <= 64 && nearDistance > 0; i++)
            {
                var next = nearDistance * i * i / (64.0 * 64);
                var planetAttenuation = 0.68 * Math.Pow(Math.Max(0, Math.Min(1, 1 - (previous - 3000) / 160000)), 2.5);
                var starAttenuation = 0.36 * Math.Pow(Math.Max(0, Math.Min(1, 1 - (previous - 80000) / 2400000)), 2);
                var attenuation = 1 - Math.Max(planetAttenuation, starAttenuation);
                equivalentDistance += (next - previous) * attenuation * 1000 / (Math.Pow(1001, attenuation) - 1);
                previous = next;
            }
            energy += mecha.warpKeepingPowerPerSpeed * equivalentDistance;
            return Math.Max(mecha.warpStartPowerPerSpeed * mecha.maxWarpSpeed,
                energy * mecha.reactorPowerConsRatio);
        }

        private static void AvoidBody(VectorLF3 position, VectorLF3 center, double clearance, ref VectorLF3 direction)
        {
            var delta = center - position;
            var length = direction.magnitude;
            var along = VectorLF3.Dot(delta, direction.normalized);
            if (along <= 0 || along >= length) return;
            var away = direction.normalized * along - delta;
            if (away.magnitude >= clearance) return;
            if (away.sqrMagnitude < 0.001)
                away = VectorLF3.Cross(direction.normalized,
                    Math.Abs(direction.normalized.y) < 0.9 ? Vector3.up : Vector3.right);
            direction = delta + away.normalized * (clearance * 1.5);
        }
    }
}
