param(
    [string]$BaseUrl = 'http://127.0.0.1:39270',
    [Parameter(Mandatory)][int]$AssemblerId,
    [Parameter(Mandatory)][int]$MatrixLabId,
    [Parameter(Mandatory)][int]$ResearchLabId
)

# 在独立测试档执行；三个实体须在建造范围内，制造配方须支持增产。
$ErrorActionPreference = 'Stop'
function Observe([string]$query) {
    (Invoke-RestMethod "$BaseUrl/game/state" -Method Post -ContentType 'application/json; charset=utf-8' -Body (@{query=$query}|ConvertTo-Json -Compress) -TimeoutSec 30).data
}
function Execute($command) {
    $r = Invoke-RestMethod "$BaseUrl/tasks" -Method Post -ContentType 'application/json; charset=utf-8' -Body (@{commands=@($command)}|ConvertTo-Json -Depth 8 -Compress) -TimeoutSec 30
    $id = $r.task.id
    for ($i=0; $i -lt 100; $i++) {
        $r = Invoke-RestMethod "$BaseUrl/tasks/$id" -TimeoutSec 10
        if ($r.task.status -in @('SUCCEEDED','FAILED','CANCELLED')) { return $r.task.commands[0] }
        Start-Sleep -Milliseconds 100
    }
    throw "任务仍未完成：$id；检查状态后再操作，不重发。"
}
function Check([bool]$ok, [string]$message) {
    if (!$ok) { throw $message }
    Write-Output "通过：$message"
}
function Modes {
    Observe "{ factory { factorySystem { assemblerPool(where:{entityId:$AssemblerId,id_gt:0}){id entityId forceAccMode} labPool(where:{id_gt:0},limit:256){id entityId forceAccMode matrixMode} } } }"
}
$before = Modes
$assembler = $before.factory.factorySystem.assemblerPool[0]
$lab = $before.factory.factorySystem.labPool | Where-Object entityId -eq $MatrixLabId
$research = $before.factory.factorySystem.labPool | Where-Object entityId -eq $ResearchLabId
Check ($null -ne $assembler -and $lab.matrixMode -and $null -ne $research -and !$research.matrixMode) '目标组件及研究站模式符合测试条件'
foreach ($mode in @('speed','extra')) {
    $a = Execute @{type='setProliferatorMode';entityId=$AssemblerId;mode=$mode}
    $l = Execute @{type='setProliferatorMode';entityId=$MatrixLabId;mode=$mode}
    $state = Modes
    $actualLab = $state.factory.factorySystem.labPool | Where-Object entityId -eq $MatrixLabId
    Check ($a.status -eq 'SUCCEEDED' -and $l.status -eq 'SUCCEEDED' -and
        $state.factory.factorySystem.assemblerPool[0].forceAccMode -eq ($mode -eq 'speed') -and
        $actualLab.forceAccMode -eq ($mode -eq 'speed')) "制造组件与矩阵研究站的 $mode 模式可由原生查询读回"
    # 已确认实际堆叠关系时，应另查关联研究站；全池同值不作为堆叠同步证明。
    $again = Execute @{type='setProliferatorMode';entityId=$AssemblerId;mode=$mode}
    Check ($again.status -eq 'SUCCEEDED' -and $again.result.forceAccMode -eq ($mode -eq 'speed')) '重复设置保持指定模式'
}
$snapshot = Modes | ConvertTo-Json -Depth 12 -Compress
foreach ($case in @(
    @{command=@{type='setProliferatorMode';entityId=$ResearchLabId;mode='extra'};error='invalid_recipe'},
    @{command=@{type='setProliferatorMode';entityId=$AssemblerId;mode='invalid'};error='invalid_command'},
    @{command=@{type='setProliferatorMode';entityId=$AssemblerId;mode=$true};error='invalid_command'},
    @{command=@{type='setProliferatorMode';entityId=$AssemblerId;mode='extra';planetId=-1};error='out_of_range'},
    @{command=@{type='setProliferatorMode';entityId=2147483647;mode='extra'};error='target_not_found'},
    @{command=@{type='setRayReceiverMode';entityId=$AssemblerId;mode='power'};error='invalid_target'},
    @{command=@{type='setEjectorOrbit';entityId=$AssemblerId;orbitId=0};error='invalid_target'}
)) {
    $r = Execute $case.command
    Check ($r.status -eq 'FAILED' -and $r.errorCode -eq $case.error) "$($case.command.type) 拒绝非法操作：$($case.error)"
}
Check ((Modes | ConvertTo-Json -Depth 12 -Compress) -eq $snapshot) '失败命令未改变制造或研究站增产模式'
foreach ($target in @(@{entity=$AssemblerId;speed=$assembler.forceAccMode},@{entity=$MatrixLabId;speed=$lab.forceAccMode})) {
    $mode = if($target.speed){'speed'}else{'extra'}
    $r = Execute @{type='setProliferatorMode';entityId=$target.entity;mode=$mode}
    Check ($r.status -eq 'SUCCEEDED') "恢复实体 $($target.entity) 的初始模式"
}
