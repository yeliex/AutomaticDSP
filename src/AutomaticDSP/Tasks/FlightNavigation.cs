using System;
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
        private bool warpAttempted;
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

        internal FlightNavigation(PlanetData target, Vector3 position, VectorLF3? universalPosition, double tolerance, bool useWarp)
        {
            Target = target;
            Position = position;
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
            var targetWorld = UniversalPosition ?? (Target.uPosition + Maths.QRotateLF(Target.runtimeRotation, Position));
            Distance = onTarget ? Vector3.Distance(player.position.normalized * Target.realRadius,
                Position.normalized * Target.realRadius) : (targetWorld - player.uPosition).magnitude;
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
                    : player.movementState == EMovementState.Walk && controller.actionWalk.isGrounded));
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
                plannedPlanet != local || plannedWarp != warping ||
                (onTarget && Distance <= Tolerance && input.Mode != "land" && Phase != "hovering"))
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
                nextGuidanceTick = tick + GuidanceIntervalTicks;
            }
            if (player.movementState == EMovementState.Sail && input.Error == null)
            {
                UpdateSailInput(input, local);
                if (UseWarp) UpdateWarp(input, input.Direction, local);
            }
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

            if (player.movementState == EMovementState.Walk)
            {
                input.Mode = "takeOff";
                Phase = "takingOff";
                return;
            }
            if (player.movementState == EMovementState.Fly)
            {
                guidanceClearance = Math.Min(guidanceClearance, Math.Abs(player.position.magnitude - local.realRadius - 50));
                if (onTarget && Distance <= Tolerance)
                {
                    input.Mode = gas ? "fly" : "land";
                    input.Lift = gas && player.position.magnitude - Target.realRadius > 25 ? -1 : 0;
                    Phase = gas ? "hovering" : "landing";
                    return;
                }
                input.Mode = "fly";
                input.Direction = onTarget ? Position - player.position :
                    (Vector3)Maths.QInvRotateLF(local.runtimeRotation, targetWorld - player.uPosition);
                if (onTarget)
                {
                    // 原生 Fly 平滑转向会保留横向速度；位置反馈加入阻尼，避免高移速机甲绕着落点盘旋。
                    var offset = Vector3.ProjectOnPlane(input.Direction, player.position.normalized);
                    input.Direction = offset - Vector3.ProjectOnPlane(player.controller.velocity, player.position.normalized);
                    input.Thrust = input.Direction.sqrMagnitude < 0.001f ? 0 :
                        Mathf.Clamp01(offset.magnitude / (player.mecha.walkSpeed * 2.5f));
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
            else if (Target != null && local == Target)
            {
                var radial = player.uPosition - Target.uPosition;
                var up = radial.normalized;
                var point = Maths.QRotateLF(Target.runtimeRotation, Position.normalized);
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
                if (Target == null && !player.warpCommand && !player.warping)
                {
                    // 机头对准目标不等于速度已对准；提前抵消横向漂移，避免擦过后反复追点。
                    var lateral = relativeVelocity - delta.normalized * VectorLF3.Dot(relativeVelocity, delta.normalized);
                    direction -= lateral * Math.Min(3.5, delta.magnitude / Math.Max(100, relativeVelocity.magnitude));
                }
                // 原生 W 的普通推进目标至少 100m/s；到达范围即结束，不再反复刹停再加速。
                wantedSpeed = Math.Min(player.mecha.maxSailSpeed, Target == null
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
            if (Target != null && !gas && (local == null || local == Target) && !player.warping && !player.warpCommand)
            {
                var landingDelta = targetWorld - player.uPosition;
                var landingNormal = (targetWorld - Target.uPosition).normalized;
                var surfaceVelocity = Target.GetUniversalVelocityAtLocalPoint(GameMain.gameTime, Position);
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
            input.Thrust = speed > speedLimit * 1.08 ? -1 : 1;
            // 制动之外还需绕行、悬停和落点调整，不能把核心剩余能量全部用于巡航加速。
            var brakingReserve = player.mecha.thrustPowerPerAcc * player.mecha.reactorPowerConsRatio * 3 * (1.5 * speed + 50);
            brakingReserve += player.mecha.coreEnergyCap * 0.3;
            input.Boost = input.Thrust > 0 && speed < speedLimit - 1 && alignment > 0.95 &&
                player.mecha.coreEnergy > brakingReserve;
        }

        private void UpdateWarp(FlightInput input, VectorLF3 direction, PlanetData local)
        {
            var player = input.Player;
            var mecha = player.mecha;
            var distance = Target == null ? Distance : (Target.uPosition - player.uPosition).magnitude - Target.realRadius;
            var alignment = VectorLF3.Dot(player.uRotation.Forward(), direction.normalized);
            var reserve = mecha.coreEnergyCap * 0.3 + mecha.warpKeepingPowerPerSpeed * mecha.maxWarpSpeed * 0.3 * mecha.reactorPowerConsRatio;
            if (player.warpCommand || player.warping)
            {
                WarpUsed = true;
                warpAttempted = true;
                // 不调用会遍历天体的 currentWarpSpeed；用无天体衰减的指数尾程作保守上界。
                // 原生每 tick 退出 0.06667667，额外覆盖一次输入延迟及离散积分误差。
                var state = Math.Min(1, player.warpState + 0.0055655558);
                var exponent = Math.Log(1001);
                var peak = mecha.maxWarpSpeed * (Math.Exp(exponent * state) - 1) / 1000;
                var exitSeconds = state / (0.06667667 * 60) + 1.0 / 60;
                var tail = mecha.maxWarpSpeed / 1000.0 * ((Math.Exp(exponent * state) - 1) / exponent - state) /
                    (0.06667667 * 60) + peak * (2.0 / 60);
                var exitDistance = Math.Max(6000, tail + player.uVelocity.magnitude * exitSeconds + mecha.maxSailSpeed * 3);
                var delta = (Target == null ? UniversalPosition.Value : Target.uPosition) - player.uPosition;
                var velocity = observedVelocity - (Target == null ? VectorLF3.zero : (Target.uPositionNext - Target.uPosition) * 60);
                // 与沿实际运动方向到最近点的路程比较；直接用退出时长乘当前曲速会过早退出。
                var alongToClosest = velocity.sqrMagnitude > 1 ? VectorLF3.Dot(delta, velocity.normalized) : double.PositiveInfinity;
                var exit = local != null || distance <= exitDistance || alongToClosest <= exitDistance ||
                    alignment < 0.98 || mecha.coreEnergy <= reserve;
                input.WarpToggle = player.warpCommand && exit;
                input.Boost = false;
                if (exit || !player.warpCommand)
                {
                    input.Direction = player.uRotation.Forward();
                    input.Thrust = -1;
                }
                WarpStatus = !player.warpCommand || exit ? "exiting" : "cruising";
                Phase = WarpStatus == "exiting" ? "exitingWarp" : "warping";
                return;
            }
            if (warpAttempted) { WarpStatus = WarpUsed ? "completed" : "native_rejected"; return; }
            if (local != null) { WarpStatus = "not_in_space"; return; }
            if (distance <= Math.Max(12000, mecha.maxWarpSpeed * 0.1 + mecha.maxSailSpeed * 6))
            { WarpStatus = "distance_too_short"; return; }
            if (mecha.thrusterLevel < 3) { WarpStatus = "tech_locked"; return; }
            if (mecha.coreEnergy <= mecha.warpStartPowerPerSpeed * mecha.maxWarpSpeed + reserve)
            { WarpStatus = "insufficient_energy"; return; }
            if (!mecha.HasWarper() && !(mecha.autoReplenishWarper && player.package.GetItemCount(1210) > 0))
            { WarpStatus = "missing_warper"; return; }
            if (alignment < 0.999 || VectorLF3.Dot(player.uVelocity.normalized, direction.normalized) < 0.98)
            { WarpStatus = "aligning"; return; }
            // 单次导航最多启动一次，原生近天体退出或低能退出后不反复消耗翘曲器。
            input.WarpToggle = true;
            input.Boost = false;
            warpAttempted = true;
            WarpStatus = "starting";
            Phase = "startingWarp";
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
