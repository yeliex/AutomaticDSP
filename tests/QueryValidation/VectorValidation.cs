using System.Globalization;
using System.Reflection;

static class VectorValidation
{
    public static void Run(Assembly mod)
    {
        var executor = mod.GetType("AutomaticDSP.Tasks.TaskCommandExecutor", true)!;
        var read = executor.GetMethod("TryReadVectorToken", BindingFlags.NonPublic | BindingFlags.Static)!;
        var token = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.Linq.JToken", true)!;
        var parse = token.GetMethod("Parse", new[] { typeof(string) })!;
        var count = 0;
        void Check(string json, bool valid)
        {
            var args = new object[] { parse.Invoke(null, new object[] { json })!, "points[1]", null!, null! };
            var actual = (bool)read.Invoke(null, args)!;
            if (actual != valid || (valid ? args[3] != null : !((string)args[3]).Contains("points[1]")))
                throw new Exception($"坐标校验结果错误：{json}");
            var vector = args[2];
            var expected = valid ? new[] { 1.25f, -2f, 300f } : new[] { 0f, 0f, 0f };
            foreach (var (name, index) in new[] { ("x", 0), ("y", 1), ("z", 2) })
                if ((float)vector.GetType().GetField(name)!.GetValue(vector)! != expected[index])
                    throw new Exception($"坐标值错误：{json}，{name}");
            count++;
        }
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check("[1.25,-2,3e2]", true);
            Check("{\"X\":1.25,\"y\":-2,\"Z\":300}", true);
            foreach (var value in new[] { "null", "true", "\"1\"", "\"bad\"", "{}", "[]", "NaN", "Infinity", "-Infinity", "1e100", "999999999999999999999999999999999999999999999999999999999999" })
            {
                for (var i = 0; i < 3; i++)
                {
                    var parts = new[] { "1", "2", "3" };
                    parts[i] = value;
                    Check($"[{string.Join(",", parts)}]", false);
                    Check($"{{\"x\":{parts[0]},\"y\":{parts[1]},\"z\":{parts[2]}}}", false);
                }
            }
            foreach (var json in new[] { "null", "{}", "[]", "[1,2]", "[1,2,3,4]", "{\"x\":1,\"y\":2}", "42" })
                Check(json, false);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
        Console.WriteLine($"通过：{count} 项真实坐标解析验证，非法输入不抛异常、保留路径名称，合法输入不受区域设置影响。");
    }
}
