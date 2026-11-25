param([string]$Python = '', [string]$Published = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (!$Python) { $Python = Join-Path $root '.venv\Scripts\python.exe' }
if (!$Published) { $Published = Join-Path $root 'artifacts\publish\win-x64' }
$records = Join-Path $root 'artifacts\verification'
$exe = Join-Path $Published 'HostComputer.App.exe'
if (!(Test-Path $exe) -or !(Test-Path $Python)) { throw '请先显式准备 Python 并完成 Windows x64 发布。' }
$oldRoot, $oldPython = $env:CBA03_HOST_ROOT, $env:CBA03_PYTHON
$process = $null
$children = @()
try {
    $env:CBA03_PYTHON = $Python
    & (Join-Path $root 'Start.ps1') -CheckOnly
    & (Join-Path $Published 'Start.ps1') -CheckOnly
    $env:CBA03_HOST_ROOT = $Published
    & $Python -X utf8 -c 'import os,sys,pathlib; r=pathlib.Path(os.environ["CBA03_HOST_ROOT"]); sys.path.insert(0,str(r/"bridge")); import can_endpoint; assert can_endpoint.VENDOR.resolve()==(r/"vendor"/"mc_ep.py").resolve(); print("发布包 vendor 导入通过，未打开串口")'
    if ($LASTEXITCODE -ne 0) { throw '发布包 vendor 导入失败' }
    $adapterJson = & $Python -X utf8 (Join-Path $root 'tests\python\test_safety.py') --adapter-contract $Published
    if ($LASTEXITCODE -ne 0) { throw '发布包独立解释器位置链路验收失败' }
    $adapterCheck = ($adapterJson -join "`n") | ConvertFrom-Json
    if (!$adapterCheck.success -or $adapterCheck.hardwareAccess) { throw '发布包适配器验收记录无效' }
    $adapterJson | Set-Content (Join-Path $records 'published-adapter-contract.json') -Encoding utf8
    & dotnet test (Join-Path $root 'tests\HostComputer.Tests\HostComputer.Tests.csproj') -c Release -r win-x64 -p:SelfContained=false --no-build --no-restore --artifacts-path (Join-Path $root 'artifacts\build') --filter 'FullyQualifiedName~RealPythonProcessHardwareOnlyDisconnectedLifecycle' --logger 'trx;LogFileName=published-bridge.trx' --results-directory $records --nologo
    if ($LASTEXITCODE -ne 0) { throw '发布目录真机未连接子进程测试失败' }
    # 只启动本脚本创建的真机未连接窗口，不选择端口或连接硬件。
    $process = Start-Process -FilePath $exe -WorkingDirectory $Published -PassThru
    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
        if ($process.HasExited) { throw '发布程序在窗口出现前退出' }
    } while ($process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
    if ($process.MainWindowHandle -eq 0) { throw '发布程序没有在截止时间内建立窗口' }
    Start-Sleep -Seconds 2
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id)")
    if (!($children | Where-Object Name -Match '^python.*\.exe$')) { throw '未检测到本窗口创建的 Python 桥接子进程' }
    foreach ($child in @($children)) {
        $children += @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($child.ProcessId)")
    }
    if (!$process.CloseMainWindow()) { throw '窗口不接受正常关闭请求' }
    if (!$process.WaitForExit(10000)) { throw '正常退出超时，无法确认桥接已回收' }
    if ($process.ExitCode -ne 0) { throw "发布程序退出码异常：$($process.ExitCode)" }
    Start-Sleep -Milliseconds 500
    foreach ($child in $children) {
        $remaining = Get-CimInstance Win32_Process -Filter "ProcessId = $($child.ProcessId)"
        if ($remaining -and $remaining.CreationDate -eq $child.CreationDate) { throw '退出后仍有本窗口的桥接子进程存活' }
    }
    [ordered]@{
        createdAt = (Get-Date).ToString('o'); success = $true; executable = $exe
        executableSha256 = (Get-FileHash $exe).Hash; dotnetSelfContained = $true
        startChecks = @('源码入口', '发布入口'); publishedVendorImport = $true
        publishedBridgeHardwareOnlyDisconnectedLifecycle = $true; desktopWindowOpened = $true
        isolatedPublishedAdapterPositionChain = $adapterCheck
        desktopExitCode = $process.ExitCode; ownedPythonProcessesReclaimed = $children.Count
        pythonEnvironment = $Python; pythonBundled = $false; hardwareAccess = $false
        hardwareAcceptance = '待用户授权与实体 USB2CAN 接入；本验证没有打开串口'
        screenshots = 'Headless 测试数据，不是实体电机测量'
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $records 'release-smoke.json') -Encoding utf8
    $packageRecords = Join-Path $Published 'verification'
    New-Item -ItemType Directory -Path $packageRecords -Force | Out-Null
    foreach ($pattern in @('*.json', '*.trx', '*.png')) {
        Copy-Item (Join-Path $records $pattern) $packageRecords -Force
    }
    Write-Host "发布验证通过：真机未连接窗口、发布包 Python、正常退出与子进程回收。记录：$records"
} finally {
    if ($process -and !$process.HasExited) {
        $null = $process.CloseMainWindow()
        if (!$process.WaitForExit(3000)) { $process.Kill($true) }
    }
    if ($process) { $process.Dispose() }
    $env:CBA03_HOST_ROOT, $env:CBA03_PYTHON = $oldRoot, $oldPython
}
