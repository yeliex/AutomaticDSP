using AutomaticDSP.Serialization;
using UnityEngine;

namespace AutomaticDSP.State
{
    internal sealed partial class GameStateQueryService
    {
        private static object CaptureSurface(PlanetData planet, Vector3 position)
        {
            if (planet.data == null) return null;
            // 仅查询指定方向的原生地表；建筑占地采样和选址仍属于外部 Agent。
            return new JsonObject
            {
                ["height"] = planet.data.QueryHeight(position),
                ["modifiedHeight"] = planet.data.QueryModifiedHeight(position),
                ["realRadius"] = planet.realRadius,
                ["waterHeight"] = planet.waterHeight,
                ["waterItemId"] = planet.waterItemId
            };
        }
    }
}
