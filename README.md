# AutomaticDSP

AutomaticDSP 是一个用于《戴森球计划》的自动化控制 Mod，目标是让外部 AI Agent 能够通过本地接口查询游戏状态，并顺序提交游戏内任务。

当前仓库包含方案文档、BepInEx 工程骨架和 M1 只读状态观测实现：

- 需求文档：`docs/requirements.md`
- 技术方案：`docs/technical-design.md`
- 开发计划：`docs/development-plan.md`
- BepInEx 工程骨架：`src/AutomaticDSP`

M1 只读状态观测接口默认监听：

```text
http://127.0.0.1:39270/
```

配置项默认值：

- `HTTP.Host = 127.0.0.1`
- `HTTP.Port = 39270`

需要从其他机器访问时，可以在 BepInEx 配置中把 `HTTP.Host` 改为 `0.0.0.0`。Windows 的 `HttpListener` 可能还需要为 `http://*:39270/` 添加 URLACL。

可用端点：

- `GET /health`
- `GET /state`
- `GET /tasks`
- `GET /history`

运行时数据输出到：

- `BepInEx/cache/AutomaticDSP/data/history.sqlite`
- `BepInEx/cache/AutomaticDSP/snapshots/latest.json`

`latest.json` 只会在真实对局载入后写入，并使用格式化 JSON 便于调试。停留在主菜单、菜单演示或加载界面时，Mod 会清理旧快照和早期 dump，避免把菜单里的默认游戏对象误当成可观测状态。

默认游戏目录：

```text
C:\Program Files (x86)\Steam\steamapps\common\Dyson Sphere Program
```

构建前需要先在游戏目录安装 BepInEx。安装后可在仓库根目录运行：

```powershell
dotnet build .\src\AutomaticDSP\AutomaticDSP.csproj
```

如果游戏安装在其他目录，可以覆盖 MSBuild 属性：

```powershell
dotnet build .\src\AutomaticDSP\AutomaticDSP.csproj -p:DSPGameDir="D:\SteamLibrary\steamapps\common\Dyson Sphere Program"
```

运行时需要将 `bin\Debug\net472` 下的 Mod DLL 与 NuGet 依赖一起放入 BepInEx 插件目录。
