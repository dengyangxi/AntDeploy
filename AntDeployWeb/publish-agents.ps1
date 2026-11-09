# =============================================================================
#  publish-agents.ps1
#  编译并填充 AntDeployWeb/AgentService/{Windows,Linux} 的自包含 Agent 发布包。
#
#  用法（仓库任意位置均可，路径按脚本自身定位）：
#      pwsh -File AntDeployWeb/publish-agents.ps1
#      pwsh -File AntDeployWeb/publish-agents.ps1 -Configuration Debug
#
#  设计说明：
#   - 使用 --self-contained（不裁剪 PublishTrimmed=false），目标机免装 .NET 运行时；
#     不裁剪以避免 Newtonsoft.Json / TinyFox.Owin 反射依赖被 trim 破坏。
#   - 产物为“整目录上传”模式：Web 端安装时把 AgentService/{Windows|Linux} 整个目录
#     发到服务器，故单文件之外散落的原生库/pdb 一并保留即可。
#   - 发布后写入 agentsettings.json 模板（字段与 AntDeployAgent.Model.AgentConfig 一致）；
#     Agent 首次运行若无该文件，AgentConfigLoader 亦会依据旧 *.config/默认值自动生成。
# =============================================================================

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# --- 路径定位（脚本位于 <repoRoot>/AntDeployWeb/ 下）-------------------------
$webDir   = $PSScriptRoot
$repoRoot = Split-Path -Parent $webDir

$winProj   = Join-Path $repoRoot 'AntDeployAgentService\AntDeployAgentService.csproj'
$linuxProj = Join-Path $repoRoot 'AntDeployAgentLinuxService\AntDeployAgentLinuxService\AntDeployAgentLinuxService.csproj'

$outRoot  = Join-Path $webDir 'AgentService'
$winOut   = Join-Path $outRoot 'Windows'
$linuxOut = Join-Path $outRoot 'Linux'

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg" -ForegroundColor Green }

# --- 前置校验 ----------------------------------------------------------------
foreach ($p in @($winProj, $linuxProj)) {
    if (-not (Test-Path $p)) { throw "找不到工程文件：$p" }
}

# --- 清理并重建输出目录 ------------------------------------------------------
Write-Step "清理旧产物目录：$outRoot"
foreach ($d in @($winOut, $linuxOut)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

# --- 发布 Windows Agent (win-x64) -------------------------------------------
Write-Step "发布 Windows Agent：win-x64 / self-contained -> $winOut"
& dotnet publish $winProj -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -o $winOut
if ($LASTEXITCODE -ne 0) { throw "Windows Agent 发布失败（dotnet exit=$LASTEXITCODE）" }
Write-Ok "Windows Agent 发布完成"

# --- 发布 Linux Agent (linux-x64) -------------------------------------------
Write-Step "发布 Linux Agent：linux-x64 / self-contained -> $linuxOut"
& dotnet publish $linuxProj -c $Configuration -r linux-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -o $linuxOut
if ($LASTEXITCODE -ne 0) { throw "Linux Agent 发布失败（dotnet exit=$LASTEXITCODE）" }
Write-Ok "Linux Agent 发布完成"

# --- 写入 agentsettings.json 模板 -------------------------------------------
# 字段与 AntDeployAgent.Model.AgentConfig 一一对应；Port 沿用旧 *.config 的 8091。
$template = @'
{
  "Token": "AntDeploy@publish#",
  "Port": 8091,
  "DeployDir": "",
  "NeedBackUp": true,
  "BackUpLimit": 0,
  "MacWhiteList": "",
  "OldPublishLimit": 10,
  "ClearOldPublishFolderOverDays": 10
}
'@

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
foreach ($d in @($winOut, $linuxOut)) {
    $jsonPath = Join-Path $d 'agentsettings.json'
    [System.IO.File]::WriteAllText($jsonPath, $template, $utf8NoBom)
    Write-Ok "已写入模板：$jsonPath"
}

# --- 汇总 --------------------------------------------------------------------
Write-Step "完成。产物汇总："
foreach ($pair in @(@{ Name = 'Windows'; Dir = $winOut }, @{ Name = 'Linux'; Dir = $linuxOut })) {
    $stat = Get-ChildItem $pair.Dir -Recurse -File | Measure-Object -Property Length -Sum
    Write-Ok ("{0,-8} 文件数={1,-5} 总大小={2:N1} MB  路径={3}" -f `
        $pair.Name, $stat.Count, ($stat.Sum / 1MB), $pair.Dir)
}

Write-Host ""
Write-Host "提示：AgentService/** 为自包含大二进制，已在 .gitignore 忽略，不入 Git。" -ForegroundColor Yellow
