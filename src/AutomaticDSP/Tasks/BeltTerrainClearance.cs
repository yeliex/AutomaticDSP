using System;
using System.Collections.Generic;
using AutomaticDSP.Serialization;
using UnityEngine;

namespace AutomaticDSP.Tasks
{
    internal static class BeltTerrainClearance
    {
        // 比原生带段间距更密；沿空间弦采样，不能用归一化插值掩盖球面下沉。
        internal const float SampleSpacing = 0.1f;
        internal const float HeightTolerance = 0.01f;

        internal static JsonObject Check(IReadOnlyList<Vector3> positions, float groundRadius,
            Func<Vector3, float> queryModifiedHeight)
        {
            for (var i = 0; i < positions.Count; i++)
            {
                var start = i == 0 ? positions[i] : positions[i - 1];
                var end = positions[i];
                var length = (end - start).magnitude;
                // 限制异常坐标的采样工作量，且不把无法验证的路径当作通过。
                if (float.IsNaN(length) || float.IsInfinity(length) || length > 6553.6f)
                    return new JsonObject { ["reason"] = "invalid_geometry", ["pathIndex"] = i };
                var steps = Math.Max(1, (int)Math.Ceiling(length / SampleSpacing));
                for (var sample = i == 0 ? 0 : 1; sample <= steps; sample++)
                {
                    var fraction = (float)sample / steps;
                    var position = Vector3.Lerp(start, end, fraction);
                    var radius = position.magnitude;
                    if (float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0.01f)
                        return new JsonObject { ["reason"] = "invalid_geometry", ["pathIndex"] = i };
                    var height = queryModifiedHeight(position);
                    var minimum = Math.Max(groundRadius, height);
                    if (float.IsNaN(height) || float.IsInfinity(height) || radius + HeightTolerance < minimum)
                    {
                        return new JsonObject
                        {
                            ["reason"] = "below_surface",
                            ["pathIndex"] = i,
                            ["segmentFraction"] = fraction,
                            ["position"] = new JsonObject { ["x"] = position.x, ["y"] = position.y, ["z"] = position.z },
                            ["radius"] = radius,
                            ["modifiedHeight"] = height,
                            ["minimumRadius"] = minimum,
                            ["heightTolerance"] = HeightTolerance,
                            ["sampleSpacing"] = SampleSpacing
                        };
                    }
                }
            }
            return null;
        }
    }
}
