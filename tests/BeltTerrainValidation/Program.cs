using AutomaticDSP.Tasks;
using UnityEngine;

var passed = 0;
Vector3 Point(float radius, float angle = 0) => new Vector3(
    radius * MathF.Sin(angle), radius * MathF.Cos(angle), 0);
void Check(string name, Vector3[] points, Func<Vector3, float> terrain, bool accepted,
    Action<AutomaticDSP.Serialization.JsonObject> verify = null)
{
    var original = points.ToArray();
    var failure = BeltTerrainClearance.Check(points, 200.2f, terrain);
    if ((failure == null) != accepted) throw new Exception(name);
    if (points.Where((p, i) => !p.x.Equals(original[i].x) || !p.y.Equals(original[i].y) ||
        !p.z.Equals(original[i].z)).Any()) throw new Exception("校验修改了坐标：" + name);
    if (failure != null) verify?.Invoke(failure);
    passed++;
}

Check("已发现的 radius 200 穿地", new[] { Point(200), Point(200, 0.006f) }, _ => 200.2f, false,
    f => {
        if ((float)f["radius"] != 200 || (float)f["modifiedHeight"] != 200.2f ||
            (string)f["reason"] != "below_surface" || (int)f["pathIndex"] != 0)
            throw new Exception("失败结果未保留实际高度");
    });
Check("原生地面带与短球面弦", new[] { Point(200.2f), Point(200.2f, 0.006f) }, _ => 200.2f, true);
Check("重铺 radius 200.3", new[] { Point(200.3f), Point(200.3f, 0.006f) }, _ => 200.2f, true);
Check("水面带不贴海底", new[] { Point(200.2f), Point(200.2f, 0.006f) }, _ => 198, true);
Check("水下带拒绝", new[] { Point(199), Point(199, 0.006f) }, _ => 198, false);
Check("隆起位于端点之间", new[] { Point(201), Point(201, 0.02f) }, p => p.x > 1.4f && p.x < 1.8f ? 202 : 200.2f,
    false, f => {
        if ((int)f["pathIndex"] != 1 || (float)f["segmentFraction"] >= 1)
            throw new Exception("漏检段内地形");
    });
Check("长弦中点下沉", new[] { Point(200.2f, -0.05f), Point(200.2f, 0.05f) }, _ => 200.2f, false);
Check("高架", new[] { Point(205), Point(205, 0.02f) }, _ => 202, true);
Check("坡道", new[] { Point(200.2f), Point(201.53333f, 0.006f), Point(202.86666f, 0.012f) }, _ => 200.2f, true);
Check("已有起点穿地", new[] { Point(200), Point(200.3f, 0.006f), Point(200.3f, 0.012f) }, _ => 200.2f, false);
Check("已有终点穿地", new[] { Point(200.3f), Point(200.3f, 0.006f), Point(200, 0.012f) }, _ => 200.2f, false);
Check("实际接头段经过隆起", new[] { Point(201, -0.02f), Point(201), Point(201, 0.006f) },
    p => p.x < -1 && p.x > -2 ? 202 : 200.2f, false);
Check("厘米内浮点误差", new[] { Point(200.195f), Point(200.195f, 0.002f) }, _ => 200.2f, true);
Check("超出容差", new[] { Point(200.18f), Point(200.18f, 0.002f) }, _ => 200.2f, false);
var modifiedHeight = 200.2f;
Func<Vector3, float> modified = _ => modifiedHeight;
Check("地形修改前", new[] { Point(200.3f), Point(200.3f, 0.006f) }, modified, true);
modifiedHeight = 200.5f;
Check("地形修改后不使用旧高度", new[] { Point(200.3f), Point(200.3f, 0.006f) }, modified, false);
Check("原生调整后的预览重新检查", new[] { Point(201), Point(200, 0.006f) }, _ => 200.2f, false);
Check("零坐标", new[] { Vector3.zero, Point(200.3f) }, _ => 200.2f, false);
Check("非有限坐标", new[] { new Vector3(float.NaN, 0, 0), Point(200.3f) }, _ => 200.2f, false);
Check("异常长段有界拒绝", new[] { Point(200.3f), Point(100000) }, _ => 200.2f, false);
Console.WriteLine($"通过 {passed} 项传送带地表离线回归；使用真实 Unity Vector3 和生产校验代码，不操作游戏。地形函数为夹具，不证明原生碰撞或运输网格。 ");
