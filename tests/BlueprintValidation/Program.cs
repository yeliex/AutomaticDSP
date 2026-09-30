using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;

// 只执行真实程序集的有界解压与类型映射，不装载存档或调用原生建造。
var mod = Path.GetFullPath("src/AutomaticDSP/bin/Debug/net472");
var game = "C:/Program Files (x86)/Steam/steamapps/common/Dyson Sphere Program";
AssemblyLoadContext.Default.Resolving += (_, name) => {
    foreach (var dir in new[] {mod, game + "/DSPGAME_Data/Managed", game + "/BepInEx/core"}) {
        var path = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(mod, "AutomaticDSP.dll"));
var executor = assembly.GetType("AutomaticDSP.Tasks.TaskCommandExecutor", true)!;
var check = executor.GetMethod("CheckBlueprintCompression", BindingFlags.NonPublic | BindingFlags.Static)!;
var type = executor.GetMethod("TryDysonBlueprintType", BindingFlags.NonPublic | BindingFlags.Static)!;
int checks = 0;
void Assert(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("通过：" + name); }
bool Valid(string code) => (bool)check.Invoke(null, new object[] { code, null })!;
string Compressed(int size) {
    using var buffer = new MemoryStream();
    using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true)) {
        var block = new byte[8192];
        for (int offset = 0; offset < size; offset += block.Length) gzip.Write(block, 0, Math.Min(block.Length, size - offset));
    }
    return "DYBP:test,\"" + Convert.ToBase64String(buffer.ToArray()) + "\"signature";
}
Assert(!Valid("DYBP:garbage"), "拒绝缺少压缩体边界");
Assert(!Valid("DYBP:\"!\"signature"), "拒绝非法 Base64");
Assert(!Valid("DYBP:\"YWJjZGVm\"signature"), "拒绝非 gzip 数据");
Assert(!Valid(Compressed(3)), "拒绝不足以读取版本号的数据");
Assert(Valid(Compressed(4)), "允许完整版本号大小的压缩体；不冒充内容有效性");
Assert(Valid(Compressed(64 * 1024 * 1024)), "允许解压上限");
Assert(!Valid(Compressed(64 * 1024 * 1024 + 1)), "拒绝超过解压上限");
foreach (var value in new[] { "sphere", "layers", "layer", "swarm" })
    Assert((bool)type.Invoke(null, new object[] { value, null })!, "显式戴森蓝图类型 " + value);
Assert(!(bool)type.Invoke(null, new object[] { "factory", null })!, "工厂类型不能映射为戴森设计");
Console.WriteLine($"蓝图边界验证通过：{checks} 项");
