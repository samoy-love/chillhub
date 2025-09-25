#Requires -Version 7.0
param(
  [string]$BaseUrl = 'http://localhost:55777',            # адрес сервера по умолчанию
  [string]$FilePath,                                      # путь к тестовому файлу (если не задан — будет сгенерирован автоматически)
  [int]$SizeGB = 3,                                       # размер тестового файла (ГБ), если файл генерируется
  [string[]]$ChunkSizes = @('8388608','16777216','33554432','67108864'), # 8,16,32,64 MiB
  [int[]]$Concurrency = @(4,8,16,32,64),                  # потоки
  [string]$Cookie = ''                                    # Cookie для админ-сессии, если требуется
)

# Ensure UTF-8 output after parameters are bound
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Requires PowerShell 7+
$ErrorActionPreference = 'Stop'
Import-Module ThreadJob -ErrorAction SilentlyContinue | Out-Null

# ===== Утилиты форматирования =====
function Format-Duration([double]$seconds){
  if($seconds -lt 60){ return ('{0:N2} с' -f $seconds) }
  $mins = [math]::Floor($seconds/60); $secs = $seconds % 60
  if($mins -lt 60){ return ('{0} мин {1:N0} с' -f $mins, $secs) }
  $hours = [math]::Floor($mins/60); $mins = $mins % 60
  return ('{0} ч {1} мин {2:N0} с' -f $hours, $mins, $secs)
}
function Format-SizeMBGB([double]$bytes){
  $mb = $bytes/1MB; $gb = $bytes/1GB
  return ('{0:N1} МБ / {1:N2} ГБ' -f $mb, $gb)
}
function Format-SpeedKBMB([double]$bytesPerSec){
  $kb = $bytesPerSec/1KB; $mb = $bytesPerSec/1MB
  return ('{0:N1} КБ/с - {1:N2} МБ/с' -f $kb, $mb)
}

# Цветные логи
function Log-Info($msg){ Write-Host $msg -ForegroundColor Cyan }
function Log-Warn($msg){ Write-Host $msg -ForegroundColor Yellow }
function Log-Err($msg){ Write-Host $msg -ForegroundColor Red }
function Log-Ok($msg){ Write-Host $msg -ForegroundColor Green }

# Прогресс-бар в одной строке (обновляется на месте)
function Write-ProgressLine([double]$fraction, [string]$prefix, [string]$suffix){
  if($fraction -lt 0){ $fraction = 0 } elseif($fraction -gt 1){ $fraction = 1 }
  $width = [Math]::Min([Console]::WindowWidth, 100); if($width -lt 40){ $width = 40 }
  $barWidth = $width - ($prefix.Length + $suffix.Length + 10); if($barWidth -lt 10){ $barWidth = 10 }
  $filled = [math]::Floor($barWidth * $fraction)
  $empty = $barWidth - $filled
  $pct = [math]::Floor($fraction*100)
  $bar = '[' + ('#' * $filled) + ('-' * $empty) + ']'
  $line = ('{0} {1,3}% {2} {3}' -f $prefix, $pct, $bar, $suffix)
  Write-Host -NoNewline "`r$line"
}
function Finish-ProgressLine([string]$final){
  $width = [Console]::WindowWidth
  Write-Host -NoNewline ("`r" + (' ' * ($width-1)) + "`r")
  Write-Host $final
}

# Цвет по значению (градиент от красного к зелёному)
function Get-ColorForValue([double]$value, [double]$min, [double]$max, [switch]$LowerIsBetter){
  if($max -le $min){ return 'White' }
  $ratio = ($value - $min) / ($max - $min)
  if($LowerIsBetter){ $ratio = 1 - $ratio }
  if($ratio -le 0.15){ return 'Red' }
  elseif($ratio -le 0.35){ return 'DarkYellow' }
  elseif($ratio -le 0.65){ return 'Yellow' }
  elseif($ratio -le 0.85){ return 'DarkGreen' }
  else { return 'Green' }
}

# ===== Подготовка тестового файла =====
if(-not $FilePath){
  $tmpName = "bench-upload-${SizeGB}GB.bin"
  $FilePath = Join-Path $env:TEMP $tmpName
}
if(!(Test-Path -LiteralPath $FilePath)){
  $targetBytes = [int64]$SizeGB * 1GB
  Log-Info ("Генерация файла: {0} (размер {1:N2} ГБ)" -f $FilePath, $SizeGB)
  $swGen = [System.Diagnostics.Stopwatch]::StartNew()
  $fs = [System.IO.File]::Open($FilePath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
  try{ $fs.SetLength($targetBytes) } finally { $fs.Close() }
  $swGen.Stop()
  Log-Ok ("Файл создан за " + (Format-Duration $swGen.Elapsed.TotalSeconds))
} else {
  Log-Info ("Использую существующий файл: {0}" -f $FilePath)
}
$fi = Get-Item -LiteralPath $FilePath
$TotalSize = $fi.Length
Log-Info ("Размер файла: " + (Format-SizeMBGB $TotalSize))

# Simple HTTP helpers
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AutomaticDecompression = [System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
$handler.UseCookies = $true
$handler.CookieContainer = [System.Net.CookieContainer]::new()

# Build base URI for cookie scope
try{ $baseUri = [Uri]::new($BaseUrl) } catch { throw "Некорректный BaseUrl: $BaseUrl" }

# Attach cookies via CookieContainer
if($Cookie){
  foreach($part in ($Cookie -split ';')){
    $kv = $part.Trim()
    if([string]::IsNullOrWhiteSpace($kv)) { continue }
    $eq = $kv.IndexOf('=')
    if($eq -lt 1){ continue }
    $name = $kv.Substring(0,$eq).Trim()
    $val  = $kv.Substring($eq+1).Trim()
    if($name -and $val){
      try{ $handler.CookieContainer.Add($baseUri, [System.Net.Cookie]::new($name, $val, '/', $baseUri.Host)) } catch {}
    }
  }
}

$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromHours(6)

# Extract csrf and set header for state-changing requests
if($Cookie){
  try{
    $m = [System.Text.RegularExpressions.Regex]::Match($Cookie, 'csrf_token=([^;]+)')
    if($m.Success -and $m.Groups.Count -ge 2){
      $csrf = $m.Groups[1].Value
      if($csrf){ $client.DefaultRequestHeaders.Remove('X-CSRF-Token') | Out-Null; $client.DefaultRequestHeaders.Add('X-CSRF-Token', $csrf) }
    } else {
      Log-Warn 'В cookie не найден csrf_token; запросы POST/PUT могут получить 401'
    }
  } catch { Log-Warn ('Не удалось разобрать csrf_token: ' + $_.Exception.Message) }
}

function Invoke-JsonPost($url, $obj){
  $json = $obj | ConvertTo-Json -Depth 5
  $content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, 'application/json')
  $resp = $client.PostAsync($url, $content).GetAwaiter().GetResult()
  return $resp
}

function Invoke-PutBytes($url, [byte[]]$bytes){
  $content = New-Object System.Net.Http.ByteArrayContent($bytes)
  $resp = $client.PutAsync($url, $content).GetAwaiter().GetResult()
  return $resp
}

$results = @()

foreach($cs in $ChunkSizes){
  $chunkSize = [int64]$cs
  $totalChunks = [int][Math]::Ceiling($TotalSize / $chunkSize)
  foreach($par in $Concurrency){
    Log-Info ("=== Тест: чанк={0} байт, потоки={1} ===" -f $chunkSize, $par)
    # INIT
    $initBody = @{ kind='game'; gameId='benchmark'; version='bench-ps'; zipName=(Split-Path -Leaf $FilePath); totalSize=$TotalSize; chunkSize=[int]$chunkSize }
    $initResp = Invoke-JsonPost "$BaseUrl/admin/api/upload/init" $initBody
    if(-not $initResp.IsSuccessStatusCode){ Log-Err ("init HTTP $($initResp.StatusCode)"); continue }
    $initJson = ($initResp.Content.ReadAsStringAsync().Result | ConvertFrom-Json)
    $uploadId = $initJson.uploadId
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [long]$uploadedBytesCounter = 0
    [int]$completedChunks = 0

    # Параллельная заливка чанков через пул thread-job с троттлингом
    $errors = New-Object System.Collections.Concurrent.ConcurrentBag[string]
    $active = @()
    $nextIndex = 0
    function Start-ChunkJob([int]$idx){
      $script = {
        param($BaseUrl,$UploadId,$ChunkSize,$TotalSize,$FilePath,$Idx,$Cookie)
        try {
          # Локальный HTTP клиент с CookieContainer и CSRF
          Add-Type -AssemblyName System.Net.Http
          Add-Type -AssemblyName System
          $handler = [System.Net.Http.HttpClientHandler]::new()
          $handler.AutomaticDecompression = [System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
          $handler.UseCookies = $true
          $handler.CookieContainer = [System.Net.CookieContainer]::new()
          $baseUri = [Uri]::new($BaseUrl)
          if($Cookie){ foreach($part in ($Cookie -split ';')){ $kv=$part.Trim(); if([string]::IsNullOrWhiteSpace($kv)){continue}; $eq=$kv.IndexOf('='); if($eq -lt 1){continue}; $name=$kv.Substring(0,$eq).Trim(); $val=$kv.Substring($eq+1).Trim(); if($name -and $val){ try{ $handler.CookieContainer.Add($baseUri,[System.Net.Cookie]::new($name,$val,'/',$baseUri.Host)) }catch{} } } }
          $cli = [System.Net.Http.HttpClient]::new($handler)
          $cookieStr = [string]$Cookie
          $m = [System.Text.RegularExpressions.Regex]::Match($cookieStr, 'csrf_token=([^;]+)')
          if($m.Success -and $m.Groups.Count -ge 2){ $csrf=$m.Groups[1].Value; if($csrf){ $cli.DefaultRequestHeaders.Add('X-CSRF-Token', $csrf) } }
          # Подготовка чанка
          $start = [int64]$Idx * [int64]$ChunkSize
          $len = [int64][Math]::Min([int64]$ChunkSize, [int64]$TotalSize - $start)
          $fs = [System.IO.File]::Open($FilePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
          try{
            $buf = New-Object byte[] $len
            $fs.Seek($start, [System.IO.SeekOrigin]::Begin) | Out-Null
            $rd = $fs.Read($buf, 0, $len)
            if($rd -ne $len){ throw "short read $rd/$len at chunk $Idx" }
          } finally { $fs.Close() }
          $u = "$BaseUrl/admin/api/upload/chunk?uploadId=$([Uri]::EscapeDataString($UploadId))&index=$Idx"
          $content = New-Object System.Net.Http.ByteArrayContent($buf)
          $resp = $cli.PutAsync($u, $content).GetAwaiter().GetResult()
          if($resp.IsSuccessStatusCode -or ($resp.StatusCode -eq 409)){
            [pscustomobject]@{ Ok=$true; Index=$Idx; Bytes=$len; StatusCode=[int]$resp.StatusCode }
          } else {
            [pscustomobject]@{ Ok=$false; Index=$Idx; Bytes=0; StatusCode=[int]$resp.StatusCode; Error="http $($resp.StatusCode)" }
          }
        } catch {
          [pscustomobject]@{ Ok=$false; Index=$Idx; Bytes=0; StatusCode=0; Error=$_.Exception.Message }
        }
      }
      return Start-ThreadJob -ScriptBlock $script -ArgumentList $BaseUrl,$uploadId,$chunkSize,$TotalSize,$FilePath,$idx,$Cookie
    }

    while($nextIndex -lt $totalChunks -or $active.Count -gt 0){
      # Запускаем новые job до лимита параллельности
      while($nextIndex -lt $totalChunks -and $active.Count -lt $par){
        $active += (Start-ChunkJob -idx $nextIndex)
        $nextIndex++
      }
      # Ожидаем завершение хотя бы одного
      $done = @()
      try{ $done = @(Wait-Job -Job $active -Any -Timeout 1) } catch { $done = @() }
      if($done.Count -gt 0){
        foreach($dj in $done){
          $res = Receive-Job -Job $dj -Keep
          # Снимаем из активных
          $active = $active | Where-Object { $_.Id -ne $dj.Id }
          foreach($r in $res){
            if($r.Ok){
              $uploadedBytesCounter += [int64]$r.Bytes
              $completedChunks += 1
            } else {
              $errors.Add("chunk $($r.Index) error: $($r.Error)")
            }
          }
          try{ Remove-Job -Job $dj -Force -ErrorAction SilentlyContinue }catch{}
        }
      } else {
        Start-Sleep -Milliseconds 100
      }
      # Обновление прогресса
      $secs = [math]::Max(0.001, $sw.Elapsed.TotalSeconds)
      $frac = [double]$completedChunks / [math]::Max(1,$totalChunks)
      $speed = [double]$uploadedBytesCounter / $secs
      $errCount = [int]$errors.Count
      Write-ProgressLine -fraction $frac -prefix ('  → загрузка') -suffix (('{0}/{1} чанков · {2} · {3} · ошибок={4}' -f $completedChunks, $totalChunks, (Format-SizeMBGB $uploadedBytesCounter), (Format-SpeedKBMB $speed), $errCount))
    }
    # Финальный сдвиг строки прогресса перед суммари
    Write-Host -NoNewline "`r"

    $complete = $client.PostAsync("$BaseUrl/admin/api/upload/complete?uploadId=$([Uri]::EscapeDataString($uploadId))", $null).GetAwaiter().GetResult()
    $sw.Stop()
    $mb = [double]$TotalSize / 1048576.0
    $secs = [math]::Max(0.001, $sw.Elapsed.TotalSeconds)
    $bytesPerSec = [double]$TotalSize / $secs
    $mbps = $mb / $secs
    $resultLine = "Результат: " + (Format-SizeMBGB $TotalSize) + " за " + (Format-Duration $secs) + " → " + (Format-SpeedKBMB $bytesPerSec) + ", ошибки=" + $errors.Count
    if($complete.IsSuccessStatusCode -and ($errors.Count -eq 0)){
      Finish-ProgressLine ("  ✓ " + $resultLine)
    } else {
      Finish-ProgressLine ("  ✗ " + $resultLine)
    }

    $results += [pscustomobject]@{
      ChunkBytes = $chunkSize
      Parallel   = $par
      Seconds    = [math]::Round($secs, 2)
      MiB        = [math]::Round($mb, 1)
      MiBps      = [math]::Round($mbps, 2)
      Errors     = $errors.Count
      UploadId   = $uploadId
      CompleteOK = $complete.IsSuccessStatusCode
    }
  }
}

# Output CSV
$csv = $results | Sort-Object MiBps -Descending | ConvertTo-Csv -NoTypeInformation
$csv | Set-Content -Encoding UTF8 (Join-Path (Split-Path -Parent $PSCommandPath) 'bench-upload-results.csv')

# Summary: best combination and colored table
if($results.Count -gt 0){
  $okResults = $results | Where-Object { $_.Errors -eq 0 -and $_.CompleteOK }
  if(-not $okResults -or $okResults.Count -eq 0){ $okResults = $results }
  $best = $okResults | Sort-Object MiBps -Descending | Select-Object -First 1
  Log-Ok ("Лучшая комбинация: чанк={0} байт, потоки={1} → {2} МБ/с (время {3} | ошибок {4})" -f $best.ChunkBytes, $best.Parallel, $best.MiBps, (Format-Duration $best.Seconds), $best.Errors)

  $sorted = $results | Sort-Object -Property @{Expression='MiBps';Descending=$true}, @{Expression='Seconds';Descending=$false}
  $maxMiBps = ($results | Measure-Object -Property MiBps -Maximum).Maximum
  $minMiBps = ($results | Measure-Object -Property MiBps -Minimum).Minimum
  $minSecs  = ($results | Measure-Object -Property Seconds -Minimum).Minimum
  $maxSecs  = ($results | Measure-Object -Property Seconds -Maximum).Maximum

  Write-Host "\nСводная таблица (лучшее зелёным, худшее красным):"
  # Header
  Write-Host ("{0,12}  {1,8}  {2,10}  {3,10}  {4,8}  {5,10}" -f 'ChunkBytes','Threads','MiB/s','Seconds','Errors','OK') -ForegroundColor Gray

  foreach($row in $sorted){
    $mi = '{0,10:N2}' -f $row.MiBps
    $se = '{0,10:N2}' -f $row.Seconds
    $miColor = Get-ColorForValue -value $row.MiBps -min $minMiBps -max $maxMiBps
    $seColor = Get-ColorForValue -value $row.Seconds -min $minSecs -max $maxSecs -LowerIsBetter
    $okText = if($row.CompleteOK){ 'yes' } else { 'no' }
    $lineLeft = ("{0,12}  {1,8}  " -f $row.ChunkBytes, $row.Parallel)
    Write-Host -NoNewline $lineLeft
    Write-Host -NoNewline $mi -ForegroundColor $miColor
    Write-Host -NoNewline "  "
    Write-Host -NoNewline $se -ForegroundColor $seColor
    Write-Host -NoNewline ("  {0,8}  {1,10}" -f $row.Errors, $okText)
    Write-Host ""
  }
}
