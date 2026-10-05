param(
    [string]$BaseUrl = 'http://127.0.0.1:39270',
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$PlanetId,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$ItemId,
    [ValidateRange(1,120)][int]$GameMinutes = 5,
    [ValidateRange(1,60)][int]$SampleSeconds = 10,
    [ValidateRange(60,14400)][int]$TimeoutSeconds = 900,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputPath) { throw '输出文件已存在，请换一个路径以保留原始观测。' }
# 累计原生进口的差值才是窗口交付量；货槽存量和预约订单不能替代进口。
$query = @"
{ metadata { gameTick } statistics(astroFilter:$PlanetId,timeLevel:5) {
  available trafficAvailable products(where:{itemId:$ItemId},limit:1) { itemId imported exported produced consumed }
} }
"@
$started = [DateTime]::UtcNow
$first = $null
$previous = $null
$samples = 0
$deliveryIntervals = 0
while ($true) {
    if (([DateTime]::UtcNow-$started).TotalSeconds -gt $TimeoutSeconds) { throw '观测超时；原始样本已保留，未宣称完成完整游戏时间窗口。' }
    $response = Invoke-RestMethod "$BaseUrl/game/state" -Method Post -ContentType 'application/json; charset=utf-8' -Body (@{query=$query}|ConvertTo-Json -Compress) -TimeoutSec 30
    $data = $response.data
    if (!$data.statistics.available -or !$data.statistics.trafficAvailable) { throw '目标行星的原生运输统计不可用。' }
    $row = @($data.statistics.products) | Select-Object -First 1
    $sample = [pscustomobject]@{
        observedAt = [DateTimeOffset]::Now.ToString('o')
        gameTick = [long]$data.metadata.gameTick
        imported = $(if ($row) {[long]$row.imported} else {0L})
        exported = $(if ($row) {[long]$row.exported} else {0L})
        produced = $(if ($row) {[long]$row.produced} else {0L})
        consumed = $(if ($row) {[long]$row.consumed} else {0L})
    }
    $sample | ConvertTo-Json -Compress | Add-Content -LiteralPath $OutputPath -Encoding utf8
    if (!$first) { $first = $sample }
    if ($previous) {
        if ($sample.gameTick -lt $previous.gameTick -or $sample.imported -lt $previous.imported) { throw '游戏时间或累计进口回退，可能发生读档；停止跨存档统计。' }
        if ($sample.imported -gt $previous.imported) { $deliveryIntervals++ }
    }
    $samples++
    $previous = $sample
    $elapsed = ($sample.gameTick-$first.gameTick)/3600.0
    if ($elapsed -ge $GameMinutes) { break }
    Start-Sleep -Seconds $SampleSeconds
}
[pscustomobject]@{
    planetId = $PlanetId; itemId = $ItemId; gameMinutes = $elapsed
    imported = $previous.imported-$first.imported
    importPerMinute = ($previous.imported-$first.imported)/$elapsed
    exported = $previous.exported-$first.exported
    produced = $previous.produced-$first.produced
    consumed = $previous.consumed-$first.consumed
    samples = $samples; deliveryIntervals = $deliveryIntervals
    firstTick = $first.gameTick; lastTick = $previous.gameTick
    evidencePath = [IO.Path]::GetFullPath($OutputPath)
    limitation = '指定行星与物品的窗口实测，不归因于单塔，不承诺未来吞吐；期间其他游戏操作可能影响结果。'
} | ConvertTo-Json -Compress
