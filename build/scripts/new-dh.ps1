# build/scripts/new-dh.ps1
param(
  [string]$SolutionName = "DH",
  [string]$Root = "."
)

$ErrorActionPreference = "Stop"
$src   = Join-Path $Root "src"
$tests = Join-Path $Root "tests"

# 1) 解决方案
dotnet new sln -n $SolutionName | Out-Null
New-Item -ItemType Directory -Force -Path $src,$tests | Out-Null

# 2) 检查/安装 Avalonia 模板
$hasAva = (dotnet new list | Select-String -Pattern "Avalonia\.App")
if (-not $hasAva) {
  Write-Host "Installing Avalonia.Templates ..." 
  dotnet new install Avalonia.Templates | Out-Null
}

# 3) 创建项目
$projects = @(
  @{name="DH.Contracts";     type="classlib"},
  @{name="DH.Driver";        type="classlib"},
  @{name="DH.Datamanage";    type="classlib"},
  @{name="DH.Configmanage";  type="classlib"},
  @{name="DH.Algorithms";    type="classlib"},
  @{name="DH.Display";       type="classlib"}
)

foreach ($p in $projects) {
  $dir = Join-Path $src $p.name
  dotnet new $($p.type) -n $($p.name) -o $dir --framework net8.0 | Out-Null
}

# Avalonia 前端
dotnet new avalonia.app -n "DH.Client.App" -o (Join-Path $src "DH.Client.App") --framework net8.0 | Out-Null

# 4) 加入到解决方案
Get-ChildItem $src -Directory | ForEach-Object {
  $csproj = Get-ChildItem $_.FullName -Filter "*.csproj" | Select-Object -First 1
  if ($csproj) { dotnet sln "$SolutionName.sln" add $csproj.FullName | Out-Null }
}

# 5) 项目引用
function Add-Ref([string]$from,[string]$to){
  $fromProj = Join-Path (Join-Path $src $from) "$from.csproj"
  $toProj   = Join-Path (Join-Path $src $to)   "$to.csproj"

  if (-not (Test-Path $fromProj)) { throw "Not found: $fromProj" }
  if (-not (Test-Path $toProj))   { throw "Not found: $toProj" }

  Write-Host "  + $from -> $to"
  dotnet add $fromProj reference $toProj | Out-Null
}

# 所有业务都引用 Contracts
@("DH.Driver","DH.Datamanage","DH.Configmanage","DH.Algorithms","DH.Display","DH.Client.App") |
  ForEach-Object { Add-Ref $_ "DH.Contracts" }

# Display 依赖 Algorithms + Datamanage
Add-Ref "DH.Display" "DH.Algorithms"
Add-Ref "DH.Display" "DH.Datamanage"

# Algorithms 依赖 Datamanage
Add-Ref "DH.Algorithms" "DH.Datamanage"

# Datamanage 依赖 Driver
Add-Ref "DH.Datamanage" "DH.Driver"

# Client.App 依赖 Display + Configmanage
Add-Ref "DH.Client.App" "DH.Display"
Add-Ref "DH.Client.App" "DH.Configmanage"

Write-Host "✅ Solution scaffolded at $Root"
Write-Host "   1) dotnet build"
Write-Host "   2) dotnet run --project src/DH.Client.App"
