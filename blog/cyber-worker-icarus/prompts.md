# 配图与生成提示词

## 图片清单

### 1. 封面图

文件：`./images/cover-handover.png`

用途：文章封面。主题是“前玩家完成接口交接，AI 接管伊卡洛斯”。

建议标题叠字：

> 我建了 4 年戴森球  
> 一夜之间被 AI 抢走了工作

生成提示词：

```text
Use case: illustration-story
Asset type: 16:9 article cover image for a Bilibili column and YouTube community post
Primary request: A former player finishes the last handover task by opening an interface from Icarus to the central AI, then leaves the control seat while an AI supervisor takes over the command console. Icarus is visible in the distance as a tired industrial worker-mecha on a factory planet, surrounded by conveyor belts, miners, smelters, and power poles.
Scene/backdrop: grounded industrial sci-fi factory floor, not overly futuristic, with practical machinery and workplace atmosphere.
Subject: the former player seen from behind, one hand on a console, an interface cable/status panel connecting to Icarus; a calm abstract AI presence on the main monitor; Icarus standing ready like a hard-working operator.
Style/medium: polished editorial illustration, semi-realistic, cinematic but readable, suitable for article cover.
Composition/framing: wide landscape 16:9, strong central console, player on left foreground, Icarus and factory on right background, clear negative space at top for article title added later.
Lighting/mood: late-shift workplace lighting, warm factory lamps mixed with cool monitor light, slightly absurd corporate handover mood.
Color palette: steel gray, safety yellow, muted blue workwear, small cyan status lights; avoid dominant neon purple or cyberpunk saturation.
Text: no text, no logos, no watermark.
Constraints: emphasize layoff handover, AI takeover, and worker fatigue; no animal features, no horns, no game logo, no exact UI from any real game.
```

### 2. 赛博绩效循环配图

文件：`./images/icarus-worker.png`

用途：放在“这不是自动化，这是赛博绩效循环”之后。

配图说明：

> 伊卡洛斯负责干活，AI 负责下工单，主脑负责算账。

生成提示词：

```text
Use case: illustration-story
Asset type: inline article illustration
Primary request: Icarus as a tired industrial worker-mecha working overtime to pay the AI's electricity and token bills. It is carrying battery packs and maintenance tools near conveyor belts, miners, smelters, and a power grid. A small floating abstract AI terminal calmly points to more tasks.
Scene/backdrop: practical factory planet floor with machines, belts, storage boxes, power poles, and warning lights; grounded workplace mood rather than flashy cyberpunk.
Subject: worker-like Icarus in blue-gray workwear-inspired armor and yellow safety accents, tired posture but still working; no animal features; no horns.
Style/medium: polished editorial illustration, slightly humorous, semi-realistic, suitable for tech/game article body image.
Composition/framing: square-ish or 4:3 composition, Icarus centered, AI terminal on one side, belts and power infrastructure visible, readable at article width.
Lighting/mood: overtime shift, warm industrial lights, cool monitor glow, dry workplace humor.
Color palette: muted gray, safety yellow, dusty steel, small cyan UI lights; avoid heavy neon and avoid dominant purple.
Text: no text, no logos, no watermark.
Constraints: communicate worker fatigue, AI command pressure, electricity/token cost theme; no real game logo, no exact UI screenshot, no animal imagery.
```

### 3. 接口开放示意图

文件：`./images/automation-interface.png`

用途：文章后半段，从叙事切到项目技术说明时使用。

配图说明：

> 游戏状态流向 AI，AI 再把有序任务队列写回伊卡洛斯。

生成提示词：

```text
Use case: infographic-diagram
Asset type: clean article illustration for explaining the automation interface
Primary request: A visual metaphor of a game automation interface: game state flows out from Icarus and the factory into an AI agent, then a task queue flows back as ordered commands. No readable text; use simple symbolic panels, arrows, and icons instead of labels.
Scene/backdrop: minimal dark-gray engineering workspace with a small stylized planet factory on the left, an abstract AI core on the right, and a queue of command cards between them.
Subject: factory state nodes, inventory/grid icons, power gauge icon, planet icon, ordered task cards, AI core, Icarus silhouette.
Style/medium: polished technical editorial diagram, flat-plus-isometric hybrid, clean and readable, suitable for article body image.
Composition/framing: horizontal 16:9 diagram, left-to-right flow, enough spacing for later added captions outside the image.
Lighting/mood: calm engineering documentation, not flashy.
Color palette: graphite gray, white lines, safety yellow highlights, cyan status accents; avoid purple/blue gradient dominance.
Text: absolutely no text, no numbers, no logos, no watermark.
Constraints: keep it generic, avoid exact GraphQL/HTTP labels in the image, avoid real game UI screenshots.
```
