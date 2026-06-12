# AutomaticDSP

AutomaticDSP 是一个用于《戴森球计划》的自动化控制 Mod，目标是让外部 AI Agent 能够通过 GraphQL 查询游戏状态，并顺序提交游戏内任务。

当前仓库处于方案和工程骨架阶段：

- 需求文档：`docs/requirements.md`
- 技术方案：`docs/technical-design.md`
- BepInEx 工程骨架：`src/AutomaticDSP`

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
