param([string]$Python = 'C:\Python312\python.exe')
$ErrorActionPreference = 'Stop'
$venv = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
if (!(Test-Path $venv)) {
    if (!(Test-Path $Python)) { throw '请用 -Python 指定已安装的 Python 3.12 路径。' }
    & $Python -X utf8 -m venv (Join-Path $PSScriptRoot '.venv')
    if ($LASTEXITCODE -ne 0) { throw '创建运行环境失败' }
}
& $venv -X utf8 -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot 'bridge\requirements.txt')
if ($LASTEXITCODE -ne 0) { throw '安装 pyserial 失败' }
Write-Host 'Python 运行环境准备完成；未访问实体串口。'
