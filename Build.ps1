param([ValidateSet('Build','Test','Publish','All')][string]$Task = 'Build')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$solution = Join-Path $root 'HostComputer.slnx'
$testProject = Join-Path $root 'tests\HostComputer.Tests\HostComputer.Tests.csproj'
$python = Join-Path $root '.venv\Scripts\python.exe'
$records = Join-Path $root 'artifacts\verification'
$isolated = Join-Path $root 'artifacts\build'
$drive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($root))
if ($drive.AvailableFreeSpace -lt 1GB) { throw '构建前至少需要 1 GiB 可用空间；请先释放空间，脚本不会自动删除文件。' }
New-Item -ItemType Directory -Path $records -Force | Out-Null
function Check-Exit([string]$step) { if ($LASTEXITCODE -ne 0) { throw "$step 失败，退出码 $LASTEXITCODE" } }
if ($Task -in @('Build','All')) {
    & dotnet restore $solution -r win-x64 --artifacts-path $isolated --nologo
    Check-Exit 'restore'
    & dotnet build $testProject -c Debug -r win-x64 --self-contained false --no-restore --artifacts-path $isolated --nologo
    Check-Exit 'Debug build'
    & dotnet build $testProject -c Release -r win-x64 --self-contained false --no-restore --artifacts-path $isolated --nologo
    Check-Exit 'Release build'
}
if ($Task -in @('Test','All')) {
    if (!(Test-Path $python)) { throw '缺少独立 .venv，请先运行 Prepare.ps1' }
    & $python -X utf8 (Join-Path $root 'tests\python\run_tests.py')
    Check-Exit 'Python tests'
    & dotnet test $testProject -c Release -r win-x64 -p:SelfContained=false --artifacts-path $isolated --nologo --logger 'trx;LogFileName=desktop.trx' --results-directory $records
    Check-Exit '.NET tests'
}
if ($Task -in @('Publish','All')) {
    $output = Join-Path $root 'artifacts\publish\win-x64'
    & dotnet publish (Join-Path $root 'src\HostComputer.App\HostComputer.App.csproj') -c Release -r win-x64 --self-contained true --artifacts-path $isolated --nologo -o $output
    Check-Exit 'Windows x64 publish'
    foreach ($dir in @('bridge','profiles','vendor')) { New-Item -ItemType Directory -Path (Join-Path $output $dir) -Force | Out-Null }
    Copy-Item (Join-Path $root 'bridge\*.py') (Join-Path $output 'bridge') -Force
    Copy-Item (Join-Path $root 'bridge\requirements.txt') (Join-Path $output 'bridge') -Force
    Copy-Item (Join-Path $root 'profiles\*.json') (Join-Path $output 'profiles') -Force
    $vendor = Join-Path (Split-Path $root -Parent) 'robot_mc-CBA03_v4.2.89'
    Copy-Item (Join-Path $vendor 'tools\mc_ep.py') (Join-Path $output 'vendor\mc_ep.py') -Force
    # 当前 mc_ep 是 rmc 转发入口；随包带齐原厂 Python 源码，不依赖系统安装版本。
    $rmcSource = Join-Path $vendor 'tools\rmc\rmc'
    if (Test-Path $rmcSource) {
        foreach ($source in Get-ChildItem $rmcSource -Recurse -File -Filter '*.py') {
            $relative = [System.IO.Path]::GetRelativePath($rmcSource, $source.FullName)
            $destination = Join-Path $output (Join-Path 'vendor\rmc\rmc' $relative)
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item $source.FullName $destination -Force
        }
    }
    Copy-Item (Join-Path $vendor 'LICENSE') (Join-Path $output 'vendor\LICENSE') -Force
    Copy-Item (Join-Path $root 'Start.cmd') $output -Force
    Copy-Item (Join-Path $root 'Start.ps1') $output -Force
    Copy-Item (Join-Path $root 'PrepareRuntime.ps1') $output -Force
    $manifest = [ordered]@{
        createdAt = (Get-Date).ToString('o'); platform = 'win-x64'; dotnetSelfContained = $true
        pythonBundled = $false; pythonPreparation = '显式运行 PrepareRuntime.ps1 -Python <Python3.12路径>；普通启动不安装依赖'
        hardwareTested = $false; hardwareAcceptance = '待授权接入 ID1/2/3，未打开任何串口或驱动实体电机'
        sourceRoot = $root; vendorLicense = 'vendor/LICENSE'; vendorToolSha256 = (Get-FileHash (Join-Path $output 'vendor\mc_ep.py')).Hash
        entryPoint = 'Start.cmd'; executable = 'HostComputer.App.exe'
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'delivery.json') -Encoding utf8
    Write-Host "发布完成：$output（.NET 自包含；Python 需独立准备，不是纯单文件程序）"
}
[ordered]@{ createdAt = (Get-Date).ToString('o'); task = $Task; success = $true; runtime = 'win-x64'; hardwareAccess = $false } |
    ConvertTo-Json | Set-Content (Join-Path $records "build-$Task.json") -Encoding utf8
