using System;
using AutomaticDSP.Serialization;
using AutomaticDSP.State;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static AutomaticDSP.Tasks.TaskStatusNames;

namespace AutomaticDSP.Tasks
{
    internal sealed partial class TaskCommandExecutor
    {
        internal static object FlightResult(FlightInput flight)
        {
            var player = GameMain.mainPlayer;
            var state = GameStateQueryService.CaptureFlightState(player) ?? new JsonObject();
            state["inputReleased"] = true;
            state["appliedTicks"] = flight?.AppliedTicks ?? 0;
            if (flight?.Navigation != null)
            {
                state["targetPlanetId"] = flight.Navigation.Target?.id;
                if (flight.Navigation.UniversalPosition.HasValue)
                {
                    var point = flight.Navigation.UniversalPosition.Value;
                    state["targetUPosition"] = new JsonObject { ["x"] = point.x, ["y"] = point.y, ["z"] = point.z };
                }
                state["distanceToTarget"] = flight.Navigation.Distance;
                state["arrivalDistance"] = flight.Navigation.ArrivalDistance;
                state["guidanceUpdates"] = flight.Navigation.GuidanceUpdates;
                state["guidanceIntervalTicks"] = flight.Navigation.GuidanceIntervalTicks;
                state["navigationPhase"] = flight.Navigation.Phase;
                state["useWarp"] = flight.Navigation.UseWarp;
                state["warpUsed"] = flight.Navigation.WarpUsed;
                state["warpStatus"] = flight.Navigation.WarpStatus;
            }
            return state;
        }

        private void ExecuteNavigateTo(CommandState command, DateTimeOffset now)
        {
            var player = GameMain.mainPlayer;
            if (player?.controller == null)
            {
                finishCommand(command, CommandFailed, "player_unavailable", "机甲控制器不可用。", now, null);
                return;
            }
            if (command.Flight == null)
            {
                var space = TryGetToken(command, "uPosition", out var universalToken);
                var hasPlanet = TryGetToken(command, "planetId", out _);
                var hasPosition = TryGetToken(command, "position", out _);
                var target = space ? null : GameMain.galaxy.PlanetById(GetInt(command, "planetId", player.planetId));
                var tolerance = GetDouble(command, "tolerance", space ? 20 : 3);
                var position = Vector3.zero;
                VectorLF3? universalPosition = null;
                string error = null;
                if (space)
                {
                    if (hasPlanet || hasPosition || !TryReadUniversalPosition(universalToken, out var point))
                        error = "invalid_command";
                    else universalPosition = point;
                }
                else if (target == null) error = "target_not_found";
                else if (hasPosition)
                {
                    if (!TryGetVector(command, "position", out position, out _) ||
                        float.IsNaN(position.sqrMagnitude) || float.IsInfinity(position.sqrMagnitude) || position.sqrMagnitude < 1)
                        error = "invalid_command";
                }
                else if (hasPlanet)
                {
                    // 仅指定星球时使用下达时面向机甲的一侧；具体施工落点仍由调用方显式给出。
                    position = ((Vector3)Maths.QInvRotateLF(target.runtimeRotation, player.uPosition - target.uPosition)).normalized * target.realRadius;
                }
                else error = "invalid_command";
                if (double.IsNaN(tolerance) || tolerance < 0.5 || tolerance > (space ? 100 : 10)) error = "invalid_command";
                if (error == null && player.mecha.thrusterLevel < (target != null && player.planetId == target.id ? 1 : 2)) error = "tech_locked";
                if (error == null && player.mecha.coreEnergy <= 0) error = "insufficient_energy";
                if (error != null)
                {
                    finishCommand(command, CommandFailed, error, "指定目的地的飞行不可执行。", now, FlightResult(null));
                    return;
                }
                if (TryGetToken(command, "useWarp", out var warpToken) && warpToken.Type != JTokenType.Boolean)
                {
                    finishCommand(command, CommandFailed, "invalid_command", "useWarp 必须是布尔值。", now, FlightResult(null));
                    return;
                }
                var navigation = new FlightNavigation(target, position, universalPosition, tolerance, GetBool(command, "useWarp", false));
                command.Flight = new FlightInput(player, "idle", Vector3.zero, 0, 0, false, int.MaxValue,
                    now.AddSeconds(command.Request.TimeoutSeconds ?? 900), navigation);
            }
            var flight = command.Flight;
            if (player != flight.Player || flight.Error != null)
                finishCommand(command, CommandFailed, flight.Error ?? "session_changed", "导航输入已中断。", now, null);
            else if (flight.Navigation.Arrived)
                finishCommand(command, CommandSucceeded, null, null, now, null);
            else command.Phase = flight.Navigation.Phase;
        }

        private static bool TryReadUniversalPosition(JToken token, out VectorLF3 position)
        {
            position = VectorLF3.zero;
            JToken x, y, z;
            if (token is JArray array && array.Count == 3)
            { x = array[0]; y = array[1]; z = array[2]; }
            else if (token is JObject obj && obj.TryGetValue("x", StringComparison.OrdinalIgnoreCase, out x) &&
                obj.TryGetValue("y", StringComparison.OrdinalIgnoreCase, out y) &&
                obj.TryGetValue("z", StringComparison.OrdinalIgnoreCase, out z)) { }
            else return false;
            if ((x.Type != JTokenType.Float && x.Type != JTokenType.Integer) ||
                (y.Type != JTokenType.Float && y.Type != JTokenType.Integer) ||
                (z.Type != JTokenType.Float && z.Type != JTokenType.Integer)) return false;
            position = new VectorLF3(x.Value<double>(), y.Value<double>(), z.Value<double>());
            return !double.IsNaN(position.sqrMagnitude) && !double.IsInfinity(position.sqrMagnitude);
        }

        private void ExecuteFlight(CommandState command, DateTimeOffset now)
        {
            var player = GameMain.mainPlayer;
            var error = (string)null;
            if (player?.controller == null) error = "player_unavailable";
            if (error == null && command.Flight == null)
            {
                var mode = command.NormalizedType == "flightinput" ? GetString(command, "mode", "") :
                    command.NormalizedType == "takeoff" ? "takeOff" : command.NormalizedType == "exitwarp" ? "exitWarp" : command.NormalizedType;
                var direction = Vector3.zero;
                var thrust = GetDouble(command, "thrust", 0);
                var lift = GetDouble(command, "lift", 0);
                var boost = GetBool(command, "boost", false);
                var duration = command.Request.DurationTicks ?? 600;
                if (command.NormalizedType == "flightinput" && !command.Request.DurationTicks.HasValue) duration = 0;
                if (duration < 1 || duration > 3600 || double.IsNaN(thrust) || double.IsNaN(lift) ||
                    Math.Abs(thrust) > 1 || Math.Abs(lift) > 1) error = "invalid_command";
                else if (mode != "takeOff" && mode != "land" && mode != "fly" && mode != "sail" && mode != "warp" && mode != "exitWarp") error = "invalid_command";
                else if (mode == "fly" || mode == "sail")
                {
                    if (!TryGetVector(command, "direction", out direction, out _) ||
                        float.IsNaN(direction.sqrMagnitude) || float.IsInfinity(direction.sqrMagnitude) || direction.sqrMagnitude < 0.000001f)
                        error = "invalid_direction";
                    else direction.Normalize();
                }
                var requiredLevel = mode == "warp" ? 3 : mode == "sail" ? 2 : mode == "takeOff" || mode == "fly" ? 1 : 0;
                if (error == null && player.mecha.thrusterLevel < requiredLevel) error = "tech_locked";
                if (error == null && mode != "exitWarp" && player.mecha.coreEnergy <= 0) error = "insufficient_energy";
                if (error == null && (mode == "takeOff" || mode == "land" || mode == "fly") && GameMain.localPlanet == null) error = "not_on_planet";
                if (error == null && mode == "land" && GameMain.localPlanet.type == EPlanetType.Gas) error = "cannot_land_on_gas";
                if (error == null && ((mode == "takeOff" && player.movementState != EMovementState.Walk) ||
                    ((mode == "land" || mode == "fly") && player.movementState != EMovementState.Fly) ||
                    ((mode == "sail" || mode == "warp" || mode == "exitWarp") && player.movementState != EMovementState.Sail)))
                    error = "invalid_movement_state";
                if (error == null && mode == "warp")
                {
                    if (GameMain.localPlanet != null) error = "not_in_space";
                    else if (player.warping || player.warpCommand) error = "already_warping";
                    else if (player.mecha.coreEnergy <= player.mecha.warpStartPowerPerSpeed * player.mecha.maxWarpSpeed) error = "insufficient_energy";
                    else if (!player.mecha.HasWarper() && !(player.mecha.autoReplenishWarper && player.package.GetItemCount(1210) > 0)) error = "missing_warper";
                }
                if (error == null && mode == "exitWarp" && !player.warpCommand && !player.warping) error = "not_warping";
                if (error == null)
                {
                    command.Flight = new FlightInput(player, mode, direction, (float)thrust, (float)lift, boost, (int)duration,
                        now.AddSeconds(command.Request.TimeoutSeconds ?? 120));
                    command.Phase = "applyingFlightInput";
                }
            }

            var flight = command.Flight;
            if (error == null && flight != null && player != flight.Player) error = "session_changed";
            if (error == null && flight != null) error = flight.Error;
            if (error != null)
            {
                finishCommand(command, CommandFailed, error, "原生飞行输入不可执行，或已被中断。", now, FlightResult(flight));
                return;
            }
            var succeeded = flight.Mode == "takeOff" ? player.movementState == EMovementState.Fly :
                flight.Mode == "land" ? player.movementState == EMovementState.Walk && player.controller.actionWalk.isGrounded :
                flight.Mode == "warp" ? player.warpCommand && player.warping :
                flight.Mode == "exitWarp" ? !player.warpCommand && !player.warping :
                flight.AppliedTicks >= flight.Duration ||
                    (flight.Mode == "fly" && player.movementState != EMovementState.Fly) ||
                    (flight.Mode == "sail" && player.movementState != EMovementState.Sail);
            if (succeeded)
                finishCommand(command, CommandSucceeded, null, null, now, null);
            else if (flight.AppliedTicks >= flight.Duration)
                finishCommand(command, CommandFailed, "native_state_not_reached", "输入结束后尚未达到要求的原生状态。", now, null);
        }
    }
}
