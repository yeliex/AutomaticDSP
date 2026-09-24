# AutomaticDSP 安装、配置与使用

Mod 部署在运行游戏的 Windows 主机上，skill 安装在运行 Agent 的机器上。两者可以是同一台机器。

## 安装与构建

准备《戴森球计划》、与游戏兼容的 BepInEx，以及支持 .NET Framework 4.7.2 目标的 .NET 构建环境。先在游戏目录安装 BepInEx，构建会引用该目录中的游戏程序集和 `BepInEx/core/BepInEx.dll`。

获取 AutomaticDSP 仓库，在仓库根目录执行：

```powershell
dotnet build ./src/AutomaticDSP/AutomaticDSP.csproj -p:DSPGameDir="D:\SteamLibrary\steamapps\common\Dyson Sphere Program"
```

`DSPGameDir` 使用实际游戏安装目录；省略时默认 `C:\Program Files (x86)\Steam\steamapps\common\Dyson Sphere Program`。

退出游戏后，将 `src/AutomaticDSP/bin/Debug/net472` 中的 Mod DLL、运行依赖和原生依赖子目录部署到游戏目录下的 `BepInEx/plugins/AutomaticDSP`，保留依赖目录结构。通过正常游戏平台启动游戏，检查 BepInEx 日志中的 Mod 加载和 HTTP 服务启动结果。

## 配置

首次加载后，配置文件通常位于游戏目录的 `BepInEx/config/dev.yeliex.automaticdsp.cfg`。修改配置后重启游戏使其生效。

| 配置节 | 配置项 | 默认值 | 用途 |
| --- | --- | --- | --- |
| `HTTP` | `Enabled` | `true` | 启用 HTTP 服务 |
| `HTTP` | `Host` | `127.0.0.1` | 监听地址 |
| `HTTP` | `Port` | `39270` | 监听端口 |
| `State` | `QueryIntervalTicks` | `60` | 游戏状态查询批次的 tick 间隔 |

默认接口地址为 `http://127.0.0.1:39270`。同机 Agent 使用此地址；远程 Agent 使用可访问的游戏主机地址。需要监听其他网卡时，可将 `HTTP.Host` 设为 `0.0.0.0`，并按实际网络配置防火墙。Windows `HttpListener` 可能需要为 `http://*:39270/` 配置 URLACL。`0.0.0.0` 是监听配置，客户端使用主机实际地址连接。

## 连接与调用

将实际服务地址记为 `baseUrl`，端点路径追加在其后。接口文档中的 `Host: <游戏主机:端口>` 使用该地址的主机与端口；示例中的 ID、坐标和存档名替换为当前数据。

POST 请求使用 JSON，`Content-Type: application/json; charset=utf-8`；注明无请求体的端点可直接发送空请求。

先从 Agent 所在机器检查连通性：

```powershell
$baseUrl = "http://127.0.0.1:39270" # 远程或自定义配置时替换
Invoke-RestMethod -Uri "$baseUrl/game"
```

成功响应后，根据 `status` 和用户要求进入对局，流程见 [游戏初始化](guides/initialization.md)。服务可连接、对局可查询、游戏时间在前进分别确认。

| 需要 | 接口文档 |
| --- | --- |
| 对局状态、新建、存档与加载 | [游戏管理](interface/game.md) |
| 世界状态、物品、配方与科技查询 | [游戏状态](interface/game-state.md) |
| 提交操作、查询进度、取消与命令历史 | [游戏控制](interface/game-control.md) |

连接失败时检查游戏进程、Mod 加载日志、HTTP 开关及监听配置；远程连接还需核对主机地址、URLACL 和防火墙。查询超时则结合对局状态和查询批次是否推进排查。

## 运行数据

命令历史保存于 `BepInEx/cache/AutomaticDSP/data/history.sqlite`。状态查询结果和任务队列保存在内存中；重启后用命令历史与当前游戏状态恢复上下文。
