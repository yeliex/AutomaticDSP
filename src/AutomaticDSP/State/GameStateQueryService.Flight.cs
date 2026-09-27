using AutomaticDSP.Serialization;

namespace AutomaticDSP.State
{
    internal sealed partial class GameStateQueryService
    {
        internal static JsonObject CaptureFlightState(Player player)
        {
            if (player == null) return null;
            var planet = GameMain.localPlanet;
            var mecha = player.mecha;
            return new JsonObject
            {
                ["movementState"] = player.movementState.ToString(),
                ["cursorLocked"] = UICursor.locked,
                ["disableLockCursor"] = UIRoot.instance.uiGame.disableLockCursor,
                ["planetId"] = player.planetId,
                ["localPlanetId"] = planet?.id,
                ["grounded"] = player.controller != null && player.controller.actionWalk.isGrounded,
                ["position"] = VectorOrNull(player.position),
                ["uPosition"] = VectorOrNull(player.uPosition),
                ["uVelocity"] = VectorOrNull(player.uVelocity),
                ["localVelocity"] = player.controller == null ? null : VectorOrNull(player.controller.velocity),
                ["reactorPowerGen"] = mecha.reactorPowerGen,
                ["radialAltitude"] = planet == null ? (object)null : (player.uPosition - planet.uPosition).magnitude - planet.realRadius,
                ["warpCommand"] = player.warpCommand,
                ["warpState"] = player.warpState,
                ["thrusterLevel"] = mecha.thrusterLevel,
                ["coreEnergy"] = mecha.coreEnergy,
                ["coreEnergyCap"] = mecha.coreEnergyCap,
                ["maxSailSpeed"] = mecha.maxSailSpeed,
                ["maxWarpSpeed"] = mecha.maxWarpSpeed,
                ["warpStartEnergy"] = mecha.warpStartPowerPerSpeed * mecha.maxWarpSpeed,
                ["warperCount"] = mecha.warpStorage.GetItemCount(1210),
                ["autoReplenishWarper"] = mecha.autoReplenishWarper
            };
        }
    }
}
