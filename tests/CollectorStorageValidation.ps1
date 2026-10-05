param(
    [string]$BaseUrl = 'http://127.0.0.1:39270',
    [Parameter(Mandatory)][int]$PlanetId,
    [Parameter(Mandatory)][int]$EntityId,
    [int]$StorageIndex = 0
)
$ErrorActionPreference = 'Stop'

# 在已交接的对局中短暂修改采集槽配置；不移动机甲、不建造、不改物品、不操作存档。
function Observe {
    $query = "{ metadata { gameTick localPlanetId } factories(where:{planetId:$PlanetId},limit:1) { transport { stationPool(where:{id_gt:0,entityId:$EntityId},limit:1) { id entityId isCollector isVeinCollector storage { itemId count max inc localLogic remoteLogic } } } } }"
    $response = Invoke-RestMethod "$BaseUrl/game/state" -Method Post -ContentType 'application/json; charset=utf-8' -Body (@{query=$query}|ConvertTo-Json -Compress) -TimeoutSec 30
    if ($response.errors) { throw ($response.errors | ConvertTo-Json -Compress) }
    $response.data
}
function Execute($command) {
    $response = Invoke-RestMethod "$BaseUrl/tasks" -Method Post -ContentType 'application/json; charset=utf-8' -Body (@{commands=@($command)}|ConvertTo-Json -Depth 8 -Compress) -TimeoutSec 30
    $id = $response.task.id
    for ($i=0; $i -lt 100; $i++) {
        $response = Invoke-RestMethod "$BaseUrl/tasks/$id" -TimeoutSec 10
        if ($response.task.status -in @('SUCCEEDED','FAILED','CANCELLED')) { return $response.task.commands[0] }
        Start-Sleep -Milliseconds 100
    }
    throw "任务仍未完成：$id；检查状态后再操作，不重发。"
}
function Config($slot) { "$($slot.itemId):$($slot.max):$($slot.localLogic):$($slot.remoteLogic)" }
function Check([bool]$ok, [string]$message) {
    if (!$ok) { throw $message }
    Write-Output "通过：$message"
}
$baseline = Observe
$station = $baseline.factories[0].transport.stationPool[0]
if (!$station -or !($station.isCollector -or $station.isVeinCollector) -or $StorageIndex -lt 0 -or $StorageIndex -ge $station.storage.Count) {
    throw '必须指定有效采集器及其原生货槽。'
}
$slot = $station.storage[$StorageIndex]
$original = @{type='setStationStorage';planetId=$PlanetId;entityId=$EntityId;storageIndex=$StorageIndex;itemId=$slot.itemId;max=$slot.max;localLogic=$slot.localLogic;remoteLogic=$slot.remoteLogic}
$logic = if ($station.isCollector) { 'remoteLogic' } else { 'localLogic' }
try {
    foreach ($mode in @('None','Supply')) {
        $command = $original.Clone(); $command[$logic] = $mode
        $result = Execute $command
        $state = Observe; $actual = $state.factories[0].transport.stationPool[0].storage[$StorageIndex]
        Check ($result.status -eq 'SUCCEEDED' -and $actual.$logic -eq $mode -and $actual.itemId -eq $slot.itemId -and $actual.max -eq $slot.max) "$logic=$mode 原生读回一致，tick=$($state.metadata.gameTick)"
    }
    $command = $original.Clone(); $command.max = 0
    $result = Execute $command
    $actual = (Observe).factories[0].transport.stationPool[0].storage[$StorageIndex]
    Check ($result.status -eq 'SUCCEEDED' -and $actual.max -eq 0 -and $actual.itemId -eq $slot.itemId) '容量设为零保留采集物品'
    foreach ($change in @(@{key=$logic;value='Demand'},@{key='itemId';value=0},@{key='itemId';value=$(if($slot.itemId -eq 1001){1002}else{1001})},@{key='max';value=-1},@{key='max';value=2147483647})) {
        $before = (Observe).factories[0].transport.stationPool[0].storage[$StorageIndex]
        $command = $original.Clone(); $command[$change.key] = $change.value
        $result = Execute $command
        $after = (Observe).factories[0].transport.stationPool[0].storage[$StorageIndex]
        Check ($result.status -eq 'FAILED' -and $result.errorCode -eq 'invalid_command' -and (Config $before) -eq (Config $after)) "拒绝 $($change.key)=$($change.value)，未改变采集槽配置"
    }
}
finally {
    $restored = Execute $original
    $state = Observe; $actual = $state.factories[0].transport.stationPool[0].storage[$StorageIndex]
    Check ($restored.status -eq 'SUCCEEDED' -and (Config $slot) -eq (Config $actual)) "恢复原配置，tick=$($state.metadata.gameTick)"
}
