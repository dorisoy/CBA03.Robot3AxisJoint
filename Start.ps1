param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$python = if ($env:CBA03_PYTHON) { $env:CBA03_PYTHON } else { Join-Path $root '.venv\Scripts\python.exe' }
if (!(Test-Path $python)) { throw '未找到独立 Python 环境。请显式运行 PrepareRuntime.ps1，正常启动不会自动安装。' }
& $python -X utf8 -c 'import serial; assert serial.__version__ == "3.5"'
if ($LASTEXITCODE -ne 0) { throw 'pyserial 3.5 未准备好，请运行 PrepareRuntime.ps1。' }
$exe = Join-Path $root 'HostComputer.App.exe'
# 源码入口只使用对应构建；发布包须从包内 Start.cmd 启动，避免旧桌面混用新 bridge。
if (!(Test-Path $exe)) { $exe = Join-Path $root 'artifacts\build\bin\HostComputer.App\release_win-x64\HostComputer.App.exe' }
if (!(Test-Path $exe)) { $exe = Join-Path $root 'artifacts\build\bin\HostComputer.App\release\HostComputer.App.exe' }
if (!(Test-Path $exe)) { $exe = Join-Path $root 'src\HostComputer.App\bin\Release\net10.0\HostComputer.App.exe' }
if (!(Test-Path $exe)) { throw '缺少对应源码构建，请显式运行 Build.ps1 -Task Build。发布包请用包内 Start.cmd；启动不自动构建。' }
$env:CBA03_HOST_ROOT = $root
$env:CBA03_PYTHON = $python
if ($CheckOnly) { Write-Host "启动检查通过：$exe；没有打开设备。"; return }
& $exe
