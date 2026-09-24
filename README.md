# AutomaticDSP

简体中文 | [English](README.en.md)

AutomaticDSP 是一个用于《戴森球计划》的自动化控制 Mod，目标是让外部 AI Agent 能够通过本地接口查询游戏状态，并顺序提交游戏内任务。

当前仓库包含方案文档、BepInEx 工程骨架和 M1 只读状态观测实现：

- 需求文档：`docs/requirements.md`
- 技术方案：`docs/technical-design.md`
- 开发计划：`docs/development-plan.md`
- API 文档：`docs/api.md`
- 查询语法：`docs/query-syntax.md`
- AI Agent 开局流程：`docs/ai-agent-getting-started.md`
- AI Agent skill：[AutomaticDSP](skills/automatic-dsp/SKILL.md)（游戏机制、规划提示与接口参考分开维护）
- 开局闭环验证：`docs/verification-startline.md`
- BepInEx 工程骨架：`src/AutomaticDSP`

## 安装 AI Agent skill

Skill 安装在运行 Agent 的机器上，Mod 安装在运行游戏的 Windows 主机上；两者可以是同一台机器。

以 Codex 为例，将仓库中的整个 `skills/automatic-dsp` 目录复制到 `$CODEX_HOME/skills/automatic-dsp`（未设置 `CODEX_HOME` 时为 `~/.codex/skills/automatic-dsp`）。保留目录内的 `SKILL.md`、`references/`、`scripts/` 等文件，安装后在下一轮对话中使用 `$automatic-dsp`。其他 Agent 按其 skill 目录约定安装。

也可以在已打开本仓库的 Codex 中发送：

```text
请把当前仓库的 skills/automatic-dsp 安装到我的 Codex 用户 skill 目录。
优先使用 CODEX_HOME，未设置时使用 ~/.codex；复制完整 skill 目录。
若已有同名 skill，先比较差异并保留本地修改。
完成后确认 automatic-dsp 的安装路径，并说明如何调用。
```

## Mod 安装与初始化 AI prompt

将下面的 prompt 发给能访问游戏主机文件系统、终端和游戏界面的 Agent，并填写方括号中的信息。构建需要游戏与 BepInEx 程序集，以及支持 .NET Framework 4.7.2 目标的构建环境。

```text
请帮我安装并初始化 AutomaticDSP，让 AI Agent 可以查询和操作《戴森球计划》。

项目：当前 AutomaticDSP 仓库；如尚未获取，从 https://github.com/yeliex/AutomaticDSP 获取。
游戏主机：[本机 Windows / 远程 Windows 主机及已配置的访问方式]
游戏安装目录：[自动查找 / 指定绝对路径]
对局：[继续当前对局 / 加载指定存档名 / 新建游戏及种子、星数、资源倍率、和平或战斗、是否跳过序幕]
Agent 与游戏是否同机：[是 / 否，填写 Agent 可访问的游戏主机地址]

先阅读 README、src/AutomaticDSP/AutomaticDSP.csproj，以及 skill 中的
references/mod-usage.md、references/guides/initialization.md 和 references/interface/game.md，确认实际安装与接口要求。

1. 检查游戏目录、BepInEx 和 .NET 构建环境。缺少 BepInEx 时，从官方发布或可信的 DSP 发行包
   安装与游戏兼容的版本，保留现有 Mod 和配置。
2. 使用实际游戏目录构建：
   dotnet build ./src/AutomaticDSP/AutomaticDSP.csproj -p:DSPGameDir="实际游戏目录"
3. 在游戏退出后，将 src/AutomaticDSP/bin/Debug/net472 中的 Mod DLL、运行依赖及原生依赖子目录
   部署到游戏目录的 BepInEx/plugins/AutomaticDSP，保留依赖目录结构。
4. 启动游戏并检查 BepInEx 日志，确认 AutomaticDSP 加载成功。
   按 Mod 使用说明确认连接地址；远程访问时配置所需的监听和访问范围。
5. 从 Agent 所在机器请求 GET /game，确认连通。按上面指定的对局要求继续、加载或新建游戏；
   对局要求不明确时先询问。等待 ready 为 true，并核对存档和运行状态。
6. 按查询文档读取一次游戏、玩家和背包状态；确认需要推进任务时游戏时间正常前进。

完成后给出部署位置、连接地址、当前对局和初始化验证结果；有失败时报告具体原因及尚未完成的步骤。
```

## Mod 使用说明

安装与构建、默认连接地址、配置项、远程访问及运行数据统一见 [Mod 使用说明](skills/automatic-dsp/references/mod-usage.md)。具体请求参数和示例按需查阅其中链接的三份接口文档。
