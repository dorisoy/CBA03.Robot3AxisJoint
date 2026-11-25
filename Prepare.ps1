param([string]$Python = 'C:\Python312\python.exe')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$venv = Join-Path $root '.venv\Scripts\python.exe'
if (!(Test-Path $venv)) {
    if (!(Test-Path $Python)) { throw '请通过 -Python 指定已安装的 Python 3.12 路径。' }
    & $Python -X utf8 -m venv (Join-Path $root '.venv')
    if ($LASTEXITCODE -ne 0) { throw '创建独立 Python 环境失败' }
}
& $venv -X utf8 -m pip install --disable-pip-version-check -r (Join-Path $root 'bridge\requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'pyserial 安装失败' }
& dotnet restore (Join-Path $root 'HostComputer.slnx') -r win-x64 --artifacts-path (Join-Path $root 'artifacts\build') --nologo
if ($LASTEXITCODE -ne 0) { throw 'NuGet restore 失败' }
Write-Host '准备完成。未打开串口，未连接电机。'
