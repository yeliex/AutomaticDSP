using System;
using System.Collections.Generic;
using UnityEngine;

namespace AutomaticDSP.Tasks
{
    // 只为已指定的落点避障；碰撞数据来自当前行星，移动仍由原生 Fly 执行。
    internal static class LocalNavigation
    {
        internal static bool Intersects(ColliderData collider, Vector3 start, Vector3 end, float clearance)
        {
            var segment = end - start;
            var t = segment.sqrMagnitude > 0 ? Mathf.Clamp01(Vector3.Dot(collider.pos - start, segment) / segment.sqrMagnitude) : 0;
            var bound = collider.ext.magnitude + collider.radius + clearance * 1.733f;
            if ((start + segment * t - collider.pos).sqrMagnitude > bound * bound) return false;
            if (collider.shape == EColliderShape.Capsule)
            {
                // ext 是世界坐标半轴；包围球只用于粗筛，否则塔旁空地会被误判为实体内部。
                var axisStart = collider.pos - collider.ext;
                var axisEnd = collider.pos + collider.ext;
                var distance = Math.Min(Math.Min(Maths.DistancePointLine(start, axisStart, axisEnd),
                    Maths.DistancePointLine(end, axisStart, axisEnd)),
                    Math.Min(Maths.DistancePointLine(axisStart, start, end), Maths.DistancePointLine(axisEnd, start, end)));
                var axis = axisEnd - axisStart;
                var offset = start - axisStart;
                double a = segment.sqrMagnitude, b = Vector3.Dot(segment, axis), c = axis.sqrMagnitude;
                double d = Vector3.Dot(segment, offset), e = Vector3.Dot(axis, offset);
                var denominator = a * c - b * b;
                // 两条有限线段的最近点在端点或内部公垂线上；平行及零长度由端点检查覆盖。
                if (denominator > 0.000001 * a * c)
                {
                    var alongSegment = (b * e - c * d) / denominator;
                    var alongAxis = (a * e - b * d) / denominator;
                    if (alongSegment >= 0 && alongSegment <= 1 && alongAxis >= 0 && alongAxis <= 1)
                        distance = Math.Min(distance, (offset + segment * (float)alongSegment - axis * (float)alongAxis).magnitude);
                }
                return distance <= collider.radius + clearance;
            }
            if (collider.shape != EColliderShape.Box)
            {
                var radius = collider.radius + collider.ext.magnitude + clearance;
                return (start + segment * t - collider.pos).sqrMagnitude <= radius * radius;
            }
            var inverse = new Quaternion(-collider.q.x, -collider.q.y, -collider.q.z, collider.q.w);
            var origin = inverse * (start - collider.pos);
            var direction = inverse * (end - start);
            var lower = 0f;
            var upper = 1f;
            for (var axis = 0; axis < 3; axis++)
            {
                var extent = Math.Abs(collider.ext[axis]) + clearance;
                if (Math.Abs(direction[axis]) < 0.00001f)
                {
                    if (Math.Abs(origin[axis]) > extent) return false;
                    continue;
                }
                var a = (-extent - origin[axis]) / direction[axis];
                var b = (extent - origin[axis]) / direction[axis];
                lower = Math.Max(lower, Math.Min(a, b));
                upper = Math.Min(upper, Math.Max(a, b));
                if (lower > upper) return false;
            }
            return true;
        }

        internal static List<ColliderData> ReadColliders(PlanetData planet, Vector3 player, Vector3 destination)
        {
            var result = new List<ColliderData>();
            if (planet.physics?.colChunks == null) return result;
            foreach (var chunk in planet.physics.colChunks)
            {
                if (chunk?.colliderPool == null) continue;
                for (var i = 1; i < chunk.cursor; i++)
                {
                    var collider = chunk.colliderPool[i];
                    if (collider.idType == 0 || collider.usage != EColliderUsage.Physics) continue;
                    var range = 120 + collider.ext.magnitude + collider.radius;
                    if ((collider.pos - player).sqrMagnitude <= range * range ||
                        (collider.pos - destination).sqrMagnitude <= range * range) result.Add(collider);
                }
            }
            return result;
        }

        private static bool Clear(List<ColliderData> colliders, Vector3 start, Vector3 end, float clearance, bool escaping = false)
        {
            foreach (var collider in colliders)
                if (Intersects(collider, start, end, clearance))
                {
                    // 已贴近建筑时允许离开安全余量，但航段仍不得进入实际碰撞体。
                    if (escaping && Intersects(collider, start, start, clearance) &&
                        !Intersects(collider, start, end, 0)) continue;
                    return false;
                }
            return true;
        }

        internal static bool TryLandingPoint(List<ColliderData> colliders, Vector3 requested, Vector3 player, float radius,
            out Vector3 landing)
        {
            landing = requested.normalized * radius;
            if (CanLand(colliders, landing, radius)) return true;
            var up = landing.normalized;
            var forward = Vector3.ProjectOnPlane(player - landing, up).normalized;
            if (forward.sqrMagnitude < 0.01f)
                forward = Vector3.Cross(up, Math.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var right = Vector3.Cross(up, forward);
            // 从靠近机甲的一侧开始搜索，优先停在建筑旁，而不是扩大原目标的到达容差。
            for (var distance = 3f; distance <= 36f; distance += 3f)
                for (var sample = 0; sample < 24; sample++)
                {
                    var angle = (sample % 2 == 0 ? sample / 2 : -(sample + 1) / 2) * Mathf.PI / 12;
                    var point = (requested + distance * (forward * Mathf.Cos(angle) + right * Mathf.Sin(angle))).normalized * radius;
                    if (!CanLand(colliders, point, radius)) continue;
                    landing = point;
                    return true;
                }
            return false;
        }

        internal static bool CanLand(List<ColliderData> colliders, Vector3 point, float radius) =>
            // 到达容差允许停在点的附近，不是机甲体积，不能叠加为碰撞膨胀量。
            Clear(colliders, point.normalized * radius, point.normalized * (radius + 50), 1.5f);

        internal static bool CanReachWaypoint(List<ColliderData> colliders, Vector3 player, Vector3 waypoint, float radius)
        {
            var start = player.normalized * (radius + 15);
            return Clear(colliders, start, LocalGoal(start, waypoint), 1.5f, true);
        }

        private static Vector3 LocalGoal(Vector3 start, Vector3 destination)
        {
            var goal = destination.normalized * start.magnitude;
            // 只检查前方短航段；跨行星表面的长弦会穿过地面，不能代表贴地飞行路线。
            if (Vector3.Distance(start, goal) <= 48) return goal;
            var up = start.normalized;
            var forward = Vector3.ProjectOnPlane(goal - start, up);
            // 对跖点没有唯一最短方向；使用稳定切向，避免零航段被当作可通行路线。
            if (forward.sqrMagnitude < 0.001f)
                forward = Vector3.Cross(up, Math.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right);
            return (start + forward.normalized * 48).normalized * start.magnitude;
        }

        internal static bool TryWaypoint(List<ColliderData> colliders, Vector3 player, Vector3 landing, float radius, out Vector3 waypoint)
        {
            var up = player.normalized;
            var start = up * (radius + 15);
            var goal = landing.normalized * (radius + 15);
            var partial = Vector3.Distance(start, goal) > 48;
            var localGoal = LocalGoal(start, landing);
            var forward = Vector3.ProjectOnPlane(localGoal - start, up).normalized;
            var right = Vector3.Cross(up, forward);
            waypoint = partial ? localGoal.normalized * radius : landing;
            if (Clear(colliders, start, localGoal, 1.5f, true)) return true;
            // 局部滚动搜索，只规划近地避障；长距离航行仍使用原来的球面导航。
            const int width = 41, center = 20;
            const float step = 3;
            var points = new Vector3[width * width];
            var occupancy = new sbyte[points.Length];
            var costs = new float[points.Length];
            var parents = new int[points.Length];
            var closed = new bool[points.Length];
            var estimates = new float[points.Length];
            var open = new SortedSet<Tuple<float, int>>();
            var goalIndex = -1;
            var startIndex = center + center * width;
            // 网格航段位于约 15 米高度，最长可见弦的下沉及安全余量仍高于 8 米。
            // 地面矮建筑保留给落地检查，不参与空中网格的逐点碰撞检查。
            var nearby = colliders.FindAll(c => c.pos.magnitude + c.ext.magnitude + c.radius >= radius + 8 &&
                (c.pos - start).magnitude <= 100 + c.ext.magnitude + c.radius);
            for (var y = 0; y < width; y++)
                for (var x = 0; x < width; x++)
                {
                    var index = x + y * width;
                    points[index] = (start + right * ((x - center) * step) + forward * ((y - center) * step)).normalized * (radius + 15);
                    costs[index] = float.PositiveInfinity;
                    parents[index] = -1;
                }
            bool IsClear(int index)
            {
                if (occupancy[index] == 0)
                    occupancy[index] = Clear(nearby, points[index], points[index], 1.5f) ? (sbyte)1 : (sbyte)-1;
                return occupancy[index] > 0;
            }
            var closest = float.PositiveInfinity;
            for (var i = 0; i < points.Length; i++)
            {
                var distance = Vector3.Distance(points[i], localGoal);
                // 滚动航段的中间点可以受占用，选择其旁边的空节点继续推进；真正落点仍须可直达。
                if (distance > (partial ? 24 : 6) || distance >= closest || !IsClear(i) ||
                    (!partial && !Clear(nearby, points[i], localGoal, 1.5f))) continue;
                closest = distance;
                goalIndex = i;
            }
            if (goalIndex < 0) return false;
            costs[startIndex] = 0;
            for (var i = 0; i < points.Length; i++) estimates[i] = Vector3.Distance(points[i], points[goalIndex]);
            open.Add(Tuple.Create(estimates[startIndex], startIndex));
            for (var iteration = 0; iteration < points.Length; iteration++)
            {
                if (open.Count == 0) return false;
                var entry = open.Min;
                open.Remove(entry);
                var current = entry.Item2;
                if (current == goalIndex)
                {
                    // 使用路径上的前方点，禁止把拐点平滑成穿过建筑的直线。
                    if (Clear(nearby, start, localGoal, 1.5f, true)) { waypoint = localGoal.normalized * radius; return true; }
                    var next = current;
                    while (parents[next] >= 0 && !Clear(nearby, start, points[next], 1.5f, true)) next = parents[next];
                    waypoint = points[next].normalized * radius;
                    return true;
                }
                closed[current] = true;
                var cx = current % width;
                var cy = current / width;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var x = cx + dx;
                        var y = cy + dy;
                        if ((dx == 0 && dy == 0) || x < 0 || y < 0 || x >= width || y >= width) continue;
                        var next = x + y * width;
                        if (closed[next] || !IsClear(next) || !Clear(nearby, points[current], points[next], 1.5f, current == startIndex)) continue;
                        var cost = costs[current] + Vector3.Distance(points[current], points[next]);
                        if (cost >= costs[next]) continue;
                        open.Remove(Tuple.Create(costs[next] + estimates[next], next));
                        costs[next] = cost;
                        parents[next] = current;
                        open.Add(Tuple.Create(cost + estimates[next], next));
                    }
            }
            return false;
        }
    }
}
