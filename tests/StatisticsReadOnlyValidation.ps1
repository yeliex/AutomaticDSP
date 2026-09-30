param([string]$BaseUrl = 'http://127.0.0.1:39270')
$ErrorActionPreference = 'Stop'

function Read-State([string]$Query) {
    Invoke-RestMethod "$BaseUrl/game/state" -Method Post -ContentType 'application/json; charset=utf-8' -Body (@{query=$Query}|ConvertTo-Json -Compress) -TimeoutSec 30
}
function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -ne $Expected) { throw "$Message，实际=$Actual，期望=$Expected" }
}

# 所有调用均为只读查询，不打开面板，也不操作存档、任务或原生统计刷新队列。
$before = (Read-State '{ localPlanet { id factoryIndex star { id } } production { extraInfoCalculator { calculating lastCalcCostTime } } }').data
$context = $before.localPlanet
if ($null -eq $context -or $context.factoryIndex -lt 0) { throw '需要已加载且有工厂的当前行星。' }
$query = @'
{
  metadata { gameTick }
  local: statistics(timeLevel:0) {
    available gameTick astroFilter factoryCount
    productionExtraInfo { source freshness calculating lastCalculationMilliseconds }
    products(limit:2048) { itemId produced consumed productionPerMinute consumptionPerMinute referenceProductionPerMinute storage playerStorage }
    power { generationCapacityWatts consumptionDemandWatts chargeWatts dischargeWatts storedJoules consumptionJoulesTotal }
    research { hashes hashesPerSecond }
    dyson { products { itemId name } spheres { starId generationWatts } }
  }
  total: statistics(astroFilter:-1,timeLevel:5) {
    available trafficAvailable products(limit:2048) { itemId produced productionPerMinute imported exported }
  }
  star: statistics(astroFilter:STAR_ASTRO) { available astroFilter scope factoryCount }
  missing: statistics(astroFilter:999999) { available reason }
  source: production {
    factoryStatPool(offset:FACTORY_INDEX,limit:1) {
      productPool(limit:2048) { itemId total(limit:14) storageCount refProductSpeed }
      powerPool(limit:6) { energy(limit:600) cursor(limit:1) total(limit:7) }
      energyConsumption
    }
    extraInfoCalculator { calculating lastCalcCostTime }
  }
}
'@
$query = $query.Replace('STAR_ASTRO',[string]($context.star.id*100)).Replace('FACTORY_INDEX',[string]$context.factoryIndex)
$data = (Read-State $query).data
Assert-Equal $data.local.available $true '本地统计可用'
Assert-Equal $data.local.gameTick $data.metadata.gameTick '同一查询批次时间'
Assert-Equal $data.local.astroFilter $context.id '当前行星范围'
Assert-Equal $data.local.productionExtraInfo.source 'nativeCache' '附加统计必须读取原生缓存'
Assert-Equal $data.local.productionExtraInfo.freshness 'unknown' '不能伪造缓存新鲜度'
Assert-Equal $data.local.productionExtraInfo.lastCalculationMilliseconds $data.source.extraInfoCalculator.lastCalcCostTime '计算器信息'
if (!$before.production.extraInfoCalculator.calculating -and $before.production.extraInfoCalculator.lastCalcCostTime -eq 0) {
    Assert-Equal $data.source.extraInfoCalculator.lastCalcCostTime 0 '未打开面板的初始缓存不应被统计查询主动计算'
}
Assert-Equal $data.star.scope 'star' '恒星系范围'
Assert-Equal $data.total.trafficAvailable $false '全局没有进出口统计'
Assert-Equal $data.missing.available $false '无效天体应明确不可用'
Assert-Equal $data.missing.reason 'astro_not_found' '无效天体原因'
$native = $data.source.factoryStatPool[0]
$rows = @{}
foreach ($row in $native.productPool) { if ($null -ne $row -and $row.itemId -gt 0) { $rows[$row.itemId] = $row } }
foreach ($row in $data.local.products) {
    $raw = $rows[$row.itemId]
    Assert-Equal $row.produced $(if ($raw) {$raw.total[1]} else {0}) "物品 $($row.itemId) 产量"
    Assert-Equal $row.consumed $(if ($raw) {$raw.total[8]} else {0}) "物品 $($row.itemId) 消耗"
    Assert-Equal $row.productionPerMinute $row.produced '一分钟产速分母'
    Assert-Equal $row.storage ($(if ($raw) {$raw.storageCount} else {0}) + $row.playerStorage) '缓存库存聚合'
    Assert-Equal $row.referenceProductionPerMinute $(if ($raw) {$raw.refProductSpeed} else {0}) '参考产能缓存'
}
$powerNames = @('generationCapacityWatts','consumptionDemandWatts','chargeWatts','dischargeWatts')
for ($i=0; $i -lt $powerNames.Count; $i++) {
    $power=$native.powerPool[$i]; [long]$sum=0
    for ($j=1; $j -le 60; $j++) { $sum += $power.energy[($power.cursor[0]-$j+600)%600] }
    Assert-Equal $data.local.power.($powerNames[$i]) $sum $powerNames[$i]
}
Assert-Equal $data.local.power.consumptionJoulesTotal $native.energyConsumption '实际累计耗电'
Assert-Equal $data.local.research.hashes $native.powerPool[4].total[1] '研究量'
Assert-Equal $data.local.research.hashesPerSecond ($native.powerPool[4].total[1]/60.0) '研究速率'
foreach ($row in $data.total.products) {
    Assert-Equal $row.productionPerMinute $null '总计不提供速率'
    Assert-Equal $row.imported $null '全局进口不可用'
    Assert-Equal $row.exported $null '全局出口不可用'
}
Assert-Equal $data.local.dyson.products[1].name '结构点' '戴森虚拟项名称'
Write-Output "通过：同批次原生统计对照（$($data.local.products.Count) 种物品）、电力、研究、范围边界、缓存读取、戴森虚拟项及累计语义。tick=$($data.metadata.gameTick)"
