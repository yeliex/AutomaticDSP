param(
    [string]$BaseUrl = 'http://127.0.0.1:39270',
    [Parameter(Mandatory)][double[]]$Position,
    [Parameter(Mandatory)][int]$VegeId
)

# 实机测试会修改当前对局。先通过 /game/load 加载独立测试档；Position 应是范围内未改造的空地。
$ErrorActionPreference = 'Stop'
if ($Position.Count -ne 3) { throw 'Position 必须包含 x、y、z' }
function Observe([string]$query) {
    (Invoke-RestMethod "$BaseUrl/game/state" -Method Post -ContentType 'application/json; charset=utf-8' -Body (@{query=$query}|ConvertTo-Json -Compress) -TimeoutSec 30).data
}
function Execute($command) {
    $r = Invoke-RestMethod "$BaseUrl/tasks" -Method Post -ContentType 'application/json; charset=utf-8' -Body (@{commands=@($command)}|ConvertTo-Json -Depth 10 -Compress) -TimeoutSec 30
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
function Resources {
    Observe '{player {sandCount inhandItemId inhandItemCount vegetableCollection {playerVegeDict {key value}}} inventory {grids(where:{itemId:1131},limit:256){itemId count inc}}}' | ConvertTo-Json -Depth 12 -Compress
}

$g = Invoke-RestMethod "$BaseUrl/game" -TimeoutSec 10
Check ($g.ready -eq $true) '游戏已就绪'
$initial = Observe '{player {sandCount inhandItemId inhandItemCount} inventory {grids(where:{itemId:1131},limit:256){count}}}'
$beforeFoundation = ($initial.inventory.grids | Measure-Object count -Sum).Sum + $initial.player.inhandItemCount
$first = Execute @{type='reformTerrain';mode='flatten';position=$Position;brushSize=1}
Check ($first.status -eq 'SUCCEEDED' -and $first.result.foundationDelta -eq -1 -and $first.result.changedCells.Count -eq 1) '单格铺设实际扣除一地基并修改网格'
$repeat = Execute @{type='reformTerrain';mode='flatten';position=$Position;brushSize=1}
Check ($repeat.status -eq 'SUCCEEDED' -and $repeat.result.foundationDelta -eq 0 -and $repeat.result.sandDelta -eq 0 -and $repeat.result.changedCells.Count -eq 0) '重复铺设不重复扣料'
$paint = Execute @{type='reformTerrain';mode='flatten';position=$Position;brushSize=1;brushType=7;brushColor=2}
Check ($paint.status -eq 'SUCCEEDED' -and $paint.result.foundationDelta -eq 0 -and $paint.result.changedCells[0].after -eq 226) '无装饰地基与颜色使用原生网格编码'
$restore = Execute @{type='reformTerrain';mode='restore';position=$Position;brushSize=1}
Check ($restore.status -eq 'SUCCEEDED' -and $restore.result.foundationDelta -eq 1 -and $restore.result.changedCells[0].after -eq 0 -and [math]::Abs($restore.result.heightAfter-$first.result.heightBefore) -lt 0.01) '还原地形并返还一地基'
$repeatRestore = Execute @{type='reformTerrain';mode='restore';position=$Position;brushSize=1}
Check ($repeatRestore.status -eq 'SUCCEEDED' -and $repeatRestore.result.foundationDelta -eq 0 -and $repeatRestore.result.sandDelta -eq 0) '重复还原不重复退款'
$after = Observe '{player {sandCount inhandItemId inhandItemCount} inventory {grids(where:{itemId:1131},limit:256){count}}}'
$afterFoundation = ($after.inventory.grids | Measure-Object count -Sum).Sum + $after.player.inhandItemCount
Check ($beforeFoundation -eq $afterFoundation -and $after.player.sandCount -eq $initial.player.sandCount+$first.result.sandDelta+$paint.result.sandDelta+$restore.result.sandDelta) '查询确认实际地基与沙土结算'

$collect = Execute @{type='collectVegetation';vegeId=$VegeId}
Check ($collect.status -eq 'SUCCEEDED' -and $collect.result.collectionDelta -eq 1) '指定植被进入收藏'
$plant = Execute @{type='plantVegetation';protoId=$collect.result.protoId;position=$collect.result.position;rotation=90}
Check ($plant.status -eq 'SUCCEEDED' -and $plant.result.vegeId -gt 0 -and $plant.result.collectionDelta -eq -1) '消耗收藏并生成植被'
$id=$plant.result.vegeId
$world=Observe "{factory {vegePool(where:{id:$id}){id protoId pos}} player {vegetableCollection {playerVegeDict {key value}}}}"
Check ($world.factory.vegePool[0].protoId -eq $collect.result.protoId) '查询确认植被实际存在'

$unchanged=Resources
$far=@(-$Position[0],-$Position[1],-$Position[2])
$cases=@(
    @{command=@{type='plantVegetation';protoId=$collect.result.protoId;position=$plant.result.position};error='collision'},
    @{command=@{type='reformTerrain';position=$far};error='out_of_range'},
    @{command=@{type='plantVegetation';protoId=$collect.result.protoId;position=$far};error='out_of_range'},
    @{command=@{type='reformTerrain';position=$Position;brushSize=11};error='invalid_command'},
    @{command=@{type='reformTerrain';position=$Position;brushSize=1.5};error='invalid_command'},
    @{command=@{type='reformTerrain';position=$Position;brushColor=32};error='invalid_command'},
    @{command=@{type='reformTerrain';position=$Position;buryVeins='false'};error='invalid_command'},
    @{command=@{type='reformTerrain';position=@(0,0,0)};error='invalid_command'},
    @{command=@{type='collectVegetation';vegeId=2147483647};error='target_not_found'},
    @{command=@{type='plantVegetation';protoId=9999;position=$Position};error='invalid_command'}
)
$catalog=Observe '{veges(limit:2048){id:ID type:Type} player {vegetableCollection {playerVegeDict(limit:2048){key value}}}}'
Check ($catalog.veges.Count -gt 0 -and $catalog.veges[0].id -gt 0 -and $null -ne $catalog.veges[0].type) '原生植被目录可读'
$missing=$catalog.veges | Where-Object { $_.id -gt 0 -and $_.id -lt 9999 -and $_.type -ne 'VFX' -and $_.id -notin $catalog.player.vegetableCollection.playerVegeDict.key } | Select-Object -First 1
if($missing){ $cases+=@{command=@{type='plantVegetation';protoId=$missing.id;position=$Position};error='missing_vegetation'} }
foreach($case in $cases) {
    $r=Execute $case.command
    Check ($r.status -eq 'FAILED' -and $r.errorCode -eq $case.error) "$($case.command.type) 拒绝 $($case.error)"
}
Check ((Resources) -eq $unchanged) '失败命令不改变地基、沙土和植被收藏'
Write-Output '地形与植被实机回归通过；当前对局保留测试结果，保存与退出请使用 Mod HTTP 接口。'
