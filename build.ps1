# 一键构建 RandomPicker 插件，并打包成 ClassIsland 的 .cipx 插件包。
# 本机 dotnet SDK 在自定义目录，且 C:\Program Files\dotnet 只有运行时没有 SDK，
# 所以必须把 SDK 目录放到 PATH 最前面，否则 `dotnet` 会命中无 SDK 的那个宿主。
$ErrorActionPreference = "Stop"

$SdkDir = "D:\Environments\dotnet-sdk-8.0.426-win-x64"
$GitDir = "C:\Program Files\Git\cmd"
$env:Path = "$SdkDir;$GitDir;$env:Path"

Set-Location $PSScriptRoot

$Configuration = "Release"
$Tfm          = "net8.0"
$ProjectName  = "ClassIsland.RandomPicker"
$AssemblyName = "$ProjectName.dll"

# ---------------------------------------------------------------- 1. 编译

Write-Host "==> 编译 $ProjectName ($Configuration)" -ForegroundColor Cyan
dotnet build "$ProjectName.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE），已中止打包。" }

$OutDir = Join-Path $PSScriptRoot "bin/$Configuration/$Tfm"
if (-not (Test-Path $OutDir)) { throw "找不到输出目录：$OutDir" }

# ---------------------------------------------------------------- 2. 收集打包内容

# .cipx 就是一个普通 zip，宿主安装时整个解压到 Plugins/<id>/ 下，
# 并且用 pkg.GetEntry("manifest.yml") 取清单 —— 所以所有文件必须在 zip 根目录，不能有外层文件夹。
#
# 打包策略：只放插件自己的东西。宿主已经加载的那些程序集（ClassIsland.Core / Shared 等）
# 一律不带，插件包里再塞一份会重复初始化。这和 csproj 里 Private=false 的用意一致。
# 除插件 dll / 清单 / 图标 / 说明之外还需要什么，写在 pack-include.txt 里（ONNX Runtime 那几样）。

$Include = New-Object System.Collections.Generic.List[string]
$Include.Add($AssemblyName)
$Include.Add("manifest.yml")

# 清单里引用的两个文件，缺了只是难看，不至于装不上，所以允许缺席。
foreach ($opt in @("icon.png", "README.md")) {
    if (Test-Path (Join-Path $OutDir $opt)) { $Include.Add($opt) }
    else { Write-Warning "输出目录里没有 $opt，跳过（manifest.yml 里引用了它）。" }
}

$PackListPath = Join-Path $PSScriptRoot "pack-include.txt"
if (Test-Path $PackListPath) {
    foreach ($line in (Get-Content $PackListPath -Encoding UTF8)) {
        $t = $line.Trim()
        if ($t -eq "" -or $t.StartsWith("#")) { continue }
        if (-not $Include.Contains($t)) { $Include.Add($t) }
    }
}

# 校验必备文件都在
$Paths   = New-Object System.Collections.Generic.List[string]
$Missing = New-Object System.Collections.Generic.List[string]
foreach ($name in $Include) {
    $p = Join-Path $OutDir $name
    if (Test-Path $p) { $Paths.Add($p) } else { $Missing.Add($name) }
}
if ($Missing.Count -gt 0) { throw "输出目录缺少以下文件，无法打包：$($Missing -join ', ')" }

# ---------------------------------------------------------------- 3. 打 zip

$CipxDir  = Join-Path $PSScriptRoot "cipx"
$CipxPath = Join-Path $CipxDir "$ProjectName.cipx"

if (Test-Path $CipxDir)  { Remove-Item $CipxDir  -Recurse -Force }
New-Item -ItemType Directory -Path $CipxDir | Out-Null

Write-Host "==> 打包 $CipxPath" -ForegroundColor Cyan
# Compress-Archive 只接受 .zip 后缀，所以先压成 zip 再改名。
$ZipPath = Join-Path $CipxDir "$ProjectName.zip"
Compress-Archive -LiteralPath $Paths -DestinationPath $ZipPath -CompressionLevel Optimal
Move-Item -Path $ZipPath -Destination $CipxPath -Force

# ---------------------------------------------------------------- 4. 自检

# 再打开一次，确认清单真的在 zip 根目录 —— 装不上的绝大多数原因就是它被套了一层文件夹。
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($CipxPath)
try {
    $entries = $zip.Entries | ForEach-Object { $_.FullName }
    if ($entries -notcontains "manifest.yml") {
        throw "打包结果里没有根目录的 manifest.yml，宿主将无法识别这个插件。"
    }
    Write-Host "    包含 $(@($entries).Count) 个条目：" -ForegroundColor DarkGray
    foreach ($e in $entries) { Write-Host "      - $e" -ForegroundColor DarkGray }
}
finally { $zip.Dispose() }

# ---------------------------------------------------------------- 5. 校验和

# 和官方 PluginSdk 的 generate-md5.ps1 保持一致：往 checksums.md 里写一段带 JSON 的 HTML 注释，
# 插件市场用它校验下载到的包。
$hash = (Get-FileHash $CipxPath -Algorithm MD5).Hash
$json = ConvertTo-Json @{(Split-Path $CipxPath -Leaf) = $hash} -Compress
Set-Content -Path (Join-Path $CipxDir "checksums.md") `
            -Value "`n<!-- CLASSISLAND_PKG_MD5 $json -->" -Encoding UTF8

# ---------------------------------------------------------------- 6. 收尾

$sizeMb = [math]::Round((Get-Item $CipxPath).Length / 1MB, 2)
Write-Host ""
Write-Host "完成：$CipxPath ($sizeMb MB)  MD5=$hash" -ForegroundColor Green
Write-Host "拖进 ClassIsland 窗口，或放进 应用数据/Cache/PluginPackages/ 即可安装。" -ForegroundColor DarkGray
