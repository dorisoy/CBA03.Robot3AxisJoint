# CBA03.Robot3AxisJoint 三轴位置台架上位机

面向自动跟随口腔灯研发的 Windows 桌面上位机，通过 USB2CAN / SLCAN 控制 CBA03 电机，支持逐轴手动位置控制，以及从 GeminiMaxRobot 网页接收 J1/J2/J3 目标的 AI 视觉随动。

本文依据本目录现行源码、启动脚本和已有离线验证记录编写。源码、已构建程序、发布包、正在运行的视觉服务以及电机已烧录固件是不同对象，不能仅凭目录名称认定它们版本一致。


## 目录

- [1. 项目定位与功能边界](#1-项目定位与功能边界)
- [2. 项目结构与数据链路](#2-项目结构与数据链路)
- [3. 环境与硬件要求](#3-环境与硬件要求)
- [4. 从源码准备和启动](#4-从源码准备和启动)
- [5. 使用 Windows 发布包](#5-使用-windows-发布包)
- [6. 连接与手动位置控制](#6-连接与手动位置控制)
- [7. 停止、保持、继续与异常恢复](#7-停止保持继续与异常恢复)
- [8. AI 视觉随动](#8-ai-视觉随动)
- [9. 运行设置、坐标与限位](#9-运行设置坐标与限位)
- [10. 协议、时序与安全机制](#10-协议时序与安全机制)
- [11. 诊断 JSON、曲线和日志](#11-诊断-json曲线和日志)
- [12. 常见问题排查](#12-常见问题排查)
- [13. 离线测试与构建](#13-离线测试与构建)
- [14. 发布与交付检查](#14-发布与交付检查)
- [15. 开发约束与已知限制](#15-开发约束与已知限制)
- [16. 原厂资料与许可证](#16-原厂资料与许可证)

## 1. 项目定位与功能边界

### 已实现

- 枚举串口及适配器候选信息，显式连接后识别 ID1、ID2、ID3；任意合法非空在线组合均可建立只读会话。
- 一键逐轴启动位置环：准备 RAM 参数、核验静止、记录软件参考、应用运行设置、使能并确认运行反馈。
- 电位器样式滑条控制相对角度，方向键按所选步进提交；实时流保留最新目标，不积压历史拖动轨迹。
- 逐轴停止转动与力矩保持、人工继续、全部保持，以及独立的软急停和故障保护。
- AI 视觉自动订阅预览、逐轴捕获基准与启动、失锁减速后原位保持，以及同一视觉来源稳定重锁后自动续跟随。
- 描述符与可读标量参数快照、运行反馈、曲线 CSV、会话诊断 JSON。
- Python 内存 CAN 测试、Avalonia Headless 桌面测试和三端合成契约验证。

### 当前不提供

- “速度控制”“力矩控制”虽然保留在来源列表中，但属于尚未实现的占位入口；目前可用的是“位置控制”和“AI视觉随动”。
- 不自动机械置零、标定、刷写固件、保存 Flash 或任意修改驱动参数。
- 不自动接管其他控制器已使能的电机，不因订阅、重连、解除急停或恢复数据而自动首次使能。
- 网页只发送目标，不接收或显示真实电机反馈；网页模型运动、发送计数均不能证明电机转动或到位。
- 软件不会消除机械碰撞、负载下坠、夹伤、供电或布线风险，也不保证 Windows 是硬实时系统。

## 2. 项目结构与数据链路

```text
HostComputer/
├── README.md
├── HostComputer.slnx                  # .NET 解决方案
├── Prepare.ps1                        # 源码环境：Python + NuGet
├── PrepareRuntime.ps1                 # 发布运行环境：仅 Python
├── Build.ps1                          # Build / Test / Publish / All
├── Start.ps1                          # 显式启动；支持 -CheckOnly
├── Start.cmd                          # 调用 PowerShell 7 启动
├── bridge/
│   ├── requirements.txt               # pyserial==3.5
│   ├── server.py                      # JSON Lines 服务、有界队列与优先级
│   ├── engine.py                      # 会话、反馈核验、运动调度与保护
│   ├── protocol.py                    # CyberBeast / SLCAN 编解码
│   ├── can_endpoint.py                # 描述符、SDO 与非阻塞 RAM 准备
│   ├── motion.py                      # 轨迹、运行限制和参数核验
│   └── control_stream.py              # 手动/视觉流、令牌及失锁状态机
├── profiles/
│   └── screenshot-reference.json      # 参数表截图参考；不是在线参数
├── src/
│   ├── HostComputer.Core/             # 状态模型、协议契约和曲线缓冲
│   └── HostComputer.App/
│       ├── BridgeClient.cs            # Python 子进程和 JSON Lines 客户端
│       ├── VisionClient.cs            # 视觉 WebSocket 客户端
│       ├── ViewModels/                # 控制编排、逐轴状态与界面门控
│       ├── Views/                     # 窗口、导出和退出处理
│       └── Controls/                  # 表盘、曲线和真实输入滑条
├── tests/
│   ├── HostComputer.Tests/            # xUnit v3 / Avalonia Headless
│   └── python/                        # unittest / 内存 CAN
└── artifacts/                         # 生成产物，不是源码依据
    ├── build/                         # 隔离的 bin / obj
    ├── verification/                  # JSON 验证记录与 TRX
    └── publish/win-x64/               # 显式 Publish 后生成的发布目录
```

源码运行还依赖同级原厂目录 `../robot_mc-CBA03_v4.2.89/tools/` 的 `mc_ep.py` 及其 `rmc` 包。发布时这些 Python 源码复制到包内 `vendor/`，不要求用户安装全局 SDK。

```text
手动滑条 ───────────────────────────────────────────────┐
                                                        ↓
网页 J1/J2/J3 → GeminiMaxRobot JointRelay → VisionClient → MainViewModel
                                                        ↓
                                              BridgeClient 子进程管道
                                                        ↓
                                       server.py → Engine / MotionStreams
                                                        ↓
                                             SLCAN → USB2CAN → CBA03

电机反馈 → Engine 状态快照 → BridgeClient → 桌面表盘、诊断与控制核验
```

桌面与 Python bridge 使用 UTF-8 标准输入/输出的 JSON Lines，不开放 TCP 电机控制桥。视觉客户端另外使用本机 WebSocket，不能将整个项目理解为完全没有网络功能。Python 的 Engine 执行路径负责 CAN 读写和运动调度；输入/输出线程只负责协议交付，视觉接收线程不直接下发电机指令，也不能替代 UI 续租。

## 3. 环境与硬件要求

### 软件环境

| 项目 | 要求或当前依赖 |
| --- | --- |
| 目标平台 | Windows x64；当前开发环境为 Windows 11 |
| PowerShell | PowerShell 7，命令为 `pwsh`；`Start.cmd` 不使用旧版 `powershell.exe` |
| .NET | 源码构建/测试需要 .NET 10 SDK，目标框架 `net10.0` |
| 桌面 UI | Avalonia 12.1.2，CommunityToolkit.Mvvm 8.4.2 |
| Python | 推荐 Python 3.12 x64，安装在本项目 `.venv` |
| CAN 串口依赖 | `pyserial==3.5`；启动入口会检查此版本 |
| 桌面测试 | xunit.v3 3.2.2、Avalonia.Headless.XUnit 12.1.2 |
| 视觉可选环境 | 独立的 GeminiMaxRobot Python 3.12 环境及 Node.js/npm；不与本项目 `.venv` 混用 |
| 磁盘 | `Build.ps1` 要求至少 1 GiB 可用空间；完整构建/发布应另留余量 |

框架依赖的源码构建需要对应 .NET 运行时，安装 SDK 通常已满足。Windows 发布包采用 .NET 自包含，但不内置 Python，也不是纯单文件 EXE。

### CAN 与电机

- 适配器必须兼容本项目 SLCAN 扩展帧接口；发现一个 COM 口不代表它就是可用 USB2CAN。
- 应用侧固定 CyberBeast / Classic CAN / 1 Mbit/s，29-bit 扩展 CAN ID，最多 8 字节载荷；不支持 CAN FD 或 11-bit 标准帧替代。
- 主站 ID 固定为 `127`，电机节点固定为 `1 / 2 / 3`，映射 `J1→ID1、J2→ID2、J3→ID3`。
- 串口打开参数为 115200；这是主机侧串口参数，不是 CAN 总线位速率。SLCAN 初始化使用 `C、S8、M0、A0、O`。
- 电机应完成厂家要求的标定，在线协议、减速比、电流/速度限制、位置限制、故障和门极状态必须通过后端核验。
- 在线 `config.can_timeout` 必须在 100～500ms；程序不自动改写此值。
- 核验 CAN_H/CAN_L、公共参考地、终端电阻、供电、节点 ID 唯一性及独立急停；由熟悉设备的现场人员完成。
- 同一适配器/总线只保留一个控制主站。使用本上位机前，停止并关闭电机精灵、rmc 控制脚本或其他主站，避免抢串口或改变驱动主站。

## 4. 从源码准备和启动

以下在 PowerShell 7 中执行。示例路径含中文，应保留引号；Python 路径按本机实际安装位置替换。

```powershell
$HostRoot = 'E:\Sinol\项目\AR眼镜\自动跟随口腔灯\Motor\CBA03\固件源码\HostComputer'
$Python312 = 'C:\Python312\python.exe'

pwsh --version
dotnet --info
& $Python312 --version

# 首次准备：创建项目 .venv、安装 pyserial，并恢复 NuGet。
& (Join-Path $HostRoot 'Prepare.ps1') -Python $Python312

# 显式构建 Debug 和 Release；不发布、不打开设备。
& (Join-Path $HostRoot 'Build.ps1') -Task Build

# 仅检查解释器、依赖与 EXE 路径，不启动应用或打开设备。
& (Join-Path $HostRoot 'Start.ps1') -CheckOnly

# 人工确认需要打开桌面后执行；此步骤不自动连接电机。
& (Join-Path $HostRoot 'Start.ps1')
```

也可在环境和构建已准备好后双击根目录 `Start.cmd`。程序窗口打开会启动自有 Python bridge 并枚举端口；枚举不打开串口，仍须人工点击“连接电机”。

### 启动入口的行为

[Start.ps1](Start.ps1) 不自动安装依赖、不自动构建，也不从旧发布目录寻找 EXE。它依次检查：

1. 当前入口目录中的 `HostComputer.App.exe`，用于完整发布包。
2. `artifacts/build/bin/HostComputer.App/release_win-x64/HostComputer.App.exe`。
3. `artifacts/build/bin/HostComputer.App/release/HostComputer.App.exe`。
4. `src/HostComputer.App/bin/Release/net10.0/HostComputer.App.exe`。

源码修改后应重新构建，再启动对应入口；路径正确不等于 EXE 已同步最新源码。`-CheckOnly` 也不验证电机、视觉服务、运行时完整性或实机安全。

### 环境变量

| 变量 | 用途 |
| --- | --- |
| `CBA03_PYTHON` | 指定 bridge 使用的 Python EXE；默认是入口目录 `.venv/Scripts/python.exe` |
| `CBA03_HOST_ROOT` | 指定包含 `bridge/server.py` 的完整根目录；启动脚本设为自己的目录 |

不建议手工双击构建目录中的 EXE 并让它猜测 bridge 路径。开发时优先使用 `Start.ps1`，发布时使用包内入口。

`Prepare.ps1` 和 `Build.ps1 -Task Test` 使用根目录 `.venv`，不会因设置 `CBA03_PYTHON` 而改用另一环境。已有 `.venv` 不会被准备脚本自动重建；解释器版本不符时应先人工检查环境，不要直接覆盖正在使用的目录。

## 5. 使用 Windows 发布包

完整发布目录位于 `artifacts/publish/win-x64/`。部署时带上整个目录，不能只复制 EXE，也不应复制开发机 `.venv` 作为可移植运行环境。

```powershell
$PackageRoot = 'D:\CBA03-HostComputer'  # 替换为实际发布包位置

# 首次部署：只准备包内 Python 环境，不要求 .NET SDK。
& (Join-Path $PackageRoot 'PrepareRuntime.ps1') -Python 'C:\Python312\python.exe'
& (Join-Path $PackageRoot 'Start.ps1') -CheckOnly

# 确认后启动；也可双击包内 Start.cmd。
& (Join-Path $PackageRoot 'Start.ps1')
```

核对包内 `bridge/、profiles/、vendor/、Start.cmd、Start.ps1、PrepareRuntime.ps1、delivery.json` 是否齐全。查看 `delivery.json` 的生成时间、平台、Python 打包状态与原厂工具哈希，不要把源码的新验证记录当作旧发布包的验收记录。

目前已有发布包早于最新源码回归。修改源码或通过测试不会自动更新发布包；只有显式运行 `Publish` 才生成/覆盖发布目录。本文编写不包含发布或现场服务重启。

## 6. 连接与手动位置控制

### 连接前

1. 确认处于适合本软件的空载台架条件，机械支撑、独立急停和可运动空间就绪。
2. 停止其他 CAN 控制器，选择正确 USB2CAN COM 口。
3. 首次操作只验证一轴，并按机械负责人批准的小行程操作；不要直接测试满行程或满速度。
4. 保持 Windows 正常调度，避免在运动时睡眠、长时间阻塞 UI 或断开供电/适配器。

### 只读连接

点击“刷新端口”，查看候选适配器信息，再点击“连接电机”。连接过程配置适配器、下载描述符并读取参数/反馈，不自动发送 START 或运动目标。

连接不要求三台同时在线。未在线轴禁用；只读连接也不代表在线轴可使能。必须分别核对：反馈有效、无驱动故障、门极关闭、参数合格和连续静止。失联节点会隔离，同一会话不自动接回；新增或恢复节点后需在实际停机确认后只读重连。

### 启动一轴

来源选择“位置控制”，查看目标轴圆形“启动”按钮的提示。允许启动时点击一次，程序按本轴依次执行：

1. 等待本会话、当前意图对应且 UI 已消费的新鲜轴快照。
2. 仅在 RAM 中准备位置路径，并读回确认。
3. 等待本次“准备完成”以及重新积累的新静止证据。
4. 将当前位置记录为本次软件相对 0°，不改变电机编码器零点。
5. 应用并核验当前后端速度/加减速度设置。
6. 建立有效 UI 租约，发送本轴 START，核验门极开启及位置运行模式。
7. 启动后保持当前位置；开启实时控制，等待新的真实滑条输入。

启动是分阶段事务，不是点一下后立即默认成功。准备、参数、参考、Gate 或模式核验失败会中止；不会自动重试、执行启动前草稿或恢复旧目标。其他已接管轴仍按原控制运行，但真实组保护仍优先。

### 提交角度

- 拖动轴卡片滑条，或聚焦滑条后使用方向键。顶部步进默认 1°，合法范围 0.1～10°。
- 在实时控制开启且本轴可下发时，真实输入提交最新相对目标；绑定刷新、重新使能或仅勾选实时控制不会执行旧草稿。
- 实时控制关闭时，滑条只修改待下发值；再开启后须重新输入。
- 普通松开滑条后电机完成当前目标并保持力矩；异常失捕/取消拖动会撤销实时流并减速，不补发旧输入。
- 表盘轴色读数为有效的相对实际位置，白色目标线为后端目标，绿色视觉标记只用于预览。无新鲜反馈时不能用历史数字认定当前位置。
- 调整步进不改变鼠标映射、表盘保护范围、刻度或 CAN 调度周期。

“三轴使能”是保留的高级入口，需要三轴各自满足参考和使能条件，不等价于自动完成三轴初始化。日常操作优先逐轴“启动”。

## 7. 停止、保持、继续与异常恢复

### 必须区分的操作

| 入口或事件 | 行为 | 是否保留力矩 |
| --- | --- | --- |
| 每轴“停止” | 取消本轴在途启动/继续，移除本轴目标流，连续减速保持；其他轴继续 | 已接管轴保留 |
| 顶部“全部停止”“暂停保持” | 受控轴停止转动并保持；桌面/bridge 继续运行 | 保留 |
| 保持后本轴“继续” | 核验新鲜保持/静止状态，恢复新控制；不重发 START、不执行旧草稿 | 保留 |
| “停止未接管电机（不启动）” | 经专用人工确认发送本轴 STOP，并核验新门极关闭 | 不保留 |
| “软急停” | CAN 急停及撤销控制授权；不是日常暂停 | 不应依赖保持力矩 |
| 真实故障、通信失联、越限或授权失效 | 安全停机/撤权，并检查停止反馈 | 不应依赖保持力矩 |
| “断开 / 取消”、退出程序、运行中断开视觉通道 | 结束相关控制并走停机处理；退出回收自有 bridge | 不应依赖保持力矩 |
| “仅暂停曲线显示” | 只暂停图表刷新，不暂停电机控制 | 控制不受此选项暂停 |

日常保持不发送 `STOP 0x63` 或 `ESTOP 0xC0`，但仍持续发送合法位置控制帧并依赖新鲜反馈、UI 租约和调度保护。保持不是断电，也不是可安全拆卸机械的状态；负载下仍可能有力矩、发热或运动风险。

### 保持后的人工继续

等待减速至零、连续静止与到位确认，本轴按钮显示“继续”。手动继续后须重新拖动滑条；AI 继续会捕获新的视觉基准。不会执行停止前未完成的目标，也不会机械置零。

用户明确点击停止后，不会因为新视觉帧到来而自动恢复该轴。自动续跟随仅适用于仍被授权的同一视觉流的暂时失锁或采集等待。

### 未接管的外部使能轴

若连接后看到“运行 · 本会话未接管”，软件不自动接管，也不让日常保持代替外部电机停机。确认其他控制器已停发、机械支撑就绪后，在轴卡专用区域勾选确认，再点击“停止未接管电机（不启动）”。

此入口只处理对应轴 STOP，不清故障、不自动启动。必须等新鲜门极关闭反馈后再进入启动流程；反馈过期或其他驱动故障会阻止提交。

### “异常”按钮和急停清除

异常时圆形停止按钮显示“异常”，点击是显式人工恢复请求，当前界面不再弹出重复双重确认。但后端核验没有取消：

- 只允许单独 CAN 急停 `Fault=0x4 / Error=9`；非急停故障、组合故障及其他未核验心跳故障不清除。
- 所有控制流与本会话输出已停止，在线轴具有新鲜门极关闭反馈，参数安全核验通过。
- 受保护失联轴必须先由人员确认实际停机并处理连接，不能用失联表示已停。
- 必要时先恢复只读新会话，再清除符合条件的急停，并核验新反馈回到无故障、Gate=false、空闲模式。

`Error=9` 单独出现不能证明是可清除急停；驱动超时也可能返回同类错误码。必须结合故障字、心跳分类、门极和当前查询结果判断。

恢复完成不自动使能，不恢复旧流/目标；须重新显式启动。对欠压、过压、过流、过温、位置故障，应先排查原因，不能循环点击解锁。

### 只读恢复连接

出现本地锁存、失联或授权失效时，先检查实际停止、机械支撑和硬件急停。有合格反馈时点击“恢复连接（不启动）”；全部节点失联或反馈未知时，按提示人工“断开 / 取消→连接电机”。

重连建立新会话并清理本地旧控制状态，不清除驱动故障、不自动运动。“停机未确认”始终按未知处理，历史停止成功记录不能代替当前反馈。

保护区分为简短首因、当前新鲜反馈、下一步人工操作三层。门极关闭只表示无保持力矩，不证明机械静止；本轴“异常”按钮提示只列简短原因、本轴反馈和操作条件，不拼接三轴停止历史。底部消息仅显示摘要，原始原因、请求结果与传输时序在“诊断详情”中展开查看；完整详情只在展开时限频刷新。

## 8. AI 视觉随动

### 三端组成与入口

视觉模式依赖本项目外的两个同工作区项目：

- [GeminiMaxRobot](../../../../GeminiMaxRobot/launch.py)：相机、追踪、视觉服务与 JointRelay。
- [Web交互演示](../../../../Web交互演示/package.json)：模型显示、校准、J角解算及目标发布。

默认本机网页 `http://127.0.0.1:5173/`，视觉服务 `http://127.0.0.1:5189/`；桌面使用 `ws://127.0.0.1:5189/ws/joints`。桌面没有远程地址设置入口，不能仅替换网页 URL 就作为跨机控制方案。

以下安装和启动服务不等于打开相机或启动电机；真正的相机连接在网页中由人员操作。

```powershell
# 接续源码启动示例中的 $HostRoot。
$VisionRoot = [System.IO.Path]::GetFullPath((Join-Path $HostRoot '..\..\..\..\GeminiMaxRobot'))

# 首次准备独立视觉环境及网页 npm 依赖。
& (Join-Path $VisionRoot '安装依赖.ps1')
# 需要嘴部功能时可改用：安装依赖.ps1 -WithMouthModel

# 仅检查依赖与 5173/5189 端口，不启动新服务、不打开相机。
& (Join-Path $VisionRoot '启动联调.ps1') -CheckOnly

# 人工确认后启动；保留运行终端。
& (Join-Path $VisionRoot '启动联调.ps1') -OpenBrowser
```

`-WithMouthModel` 会准备口部模型，可能需要网络。`-OpenBrowser` 只打开页面。启动器默认拒绝占用端口；`-ReuseExisting` 只复用经项目身份检查的既有服务，不会自动重启旧服务。依赖/能力检查通过也不证明其已加载最新源码。

### 推荐流程：先自动订阅预览，再显式启动

1. 三端同步使用预期源码/构建；如要重启既有现场服务，应先停止实机控制并人工确认。新版要求 `protocol=2`，不兼容旧配对协议，不静默回退。
2. 在上位机选择“AI视觉随动”，点击顶部“连接视觉通道”即自动订阅。网页加载即建立轻量订阅，启动顺序不限，无需输入配对码；无采集时显示等待。仅订阅不枚举设备、打开相机或使能电机。
3. 网页人工进入视觉跟随，选择人脸或嘴部，再连接相机、居中校准并“开始跟随”。只订阅现有视觉 J角，网页手动/动画不是实机控制来源。
4. 服务核验合法新鲜原始观测后确认唯一发布者及来源代次。空闲页不占来源；竞争页得到 `source_busy`，不能覆盖现有发布者。只观察桌面 J角预览、新鲜度和逐轴阻断原因。
5. 机械条件确认后点击目标轴“启动”。核验方向、行程、归位及参考，捕获本次 J角和电机软件参考，只使能本轴；其他轴须分别显式启动。
6. 日常每轴停止只移除本轴跟随并减速保持；人工继续使用新基准，不复跑旧目标。失锁或采集等待保持期间禁止新增轴，但仍可停止轴。

逻辑订阅没有五分钟有效期。临时断线按 0.5/1/2/4 秒、随后最多 5 秒无限重试；成功握手重置退避。桌面握手限时 3 秒、网页 10 秒，服务协议 ping 每 10 秒、20 秒超时。保活不续电机 lease；只读摘要超过 2 秒仅标记未知/过期，不关闭订阅，也不能授权。

暂停、切换模式和停止相机保留网页通道；暂停/校准/身份或归位改变先撤旧来源，再完整核验新 `source_begin`。发布者在暂停或异常时保留占有，网页真正退出才释放。桌面退出不撤网页发布资格，新桌面不补发缓存目标。重复订阅不重置来源或采集序号。

真正断线后自动恢复的仅是轻量订阅/角度预览。网页不重放相机 start，须人员重新连接采集并开始跟随；桌面不清故障、不重新使能、不恢复旧授权，运行仍须逐轴人工启动。连接与来源中断计数、首次冻结证据独立于采集暂挂，瞬断后同一 UI tick 收到新帧也必须先撤旧流。主动断开、退出和页面关闭取消连接意愿；BFCache 返回只重新订阅。浏览器休眠期间不能保证物理连接永不中断。

界面保留“高级 / 多轴分步视觉随动”入口，部分旧分步控件隐藏；推荐入口是顶部订阅和每轴圆形启动。订阅绿色不代表电机已经恢复。

### 四项独立状态

顶部视觉区分别显示原始采集年龄（≤300ms）、桌面已消费快照年龄（<150ms）、后端已接受 lease 年龄（<200ms）和当前控制流。后端 lease 年龄会加上快照之后的经过时间；没有接受记录或快照无效时显示未知，不能把“已发送 lease”当作“后端已接受”。无效未来时间也不能钳成零后显示新鲜。

控制流区分正在跟随、日常保持、采集等待保持、失锁保持、故障与未知。视觉新鲜只描述原始采集，不证明电机可运行；逐轴启动仍受原有门控约束。

### 视觉样本与映射

视觉样本内容保持 `schema=1`：`schema、unit、mode、visionSession、calibration、targetKind、seq、captureSeq、capturedAtMs、ageMs、angles、home、valid、faceCount、status`。新版信封另含 `protocol=2` 与 `publisher` 来源代次；订阅握手使用 `schema=2、protocol=2、role=web/desktop`，返回 `subscribed`。

- `schema=1、unit=deg、mode=face`；`targetKind` 可以是 `face` 或 `mouth`。
- 三个角必须有限，模型几何范围为 J1/J2：[-135°, 135°]，J3：[0°, 80°]；与三台电机相对 ±135°保护范围不是同一层约束。
- 有效跟随要求单人 tracking、网页已开始跟随且完成校准/重锁确认，原始采集年龄不超过 300ms；新锁定观测不等于已确认的有效目标。
- 发布序号、采集序号及原采集时间必须前进；重发旧目标、心跳或刷新网页显示不能刷新原始采集时钟。
- 身份锚点包含 publisher、视觉会话、校准和目标类型；归位姿态、电机参考变化也会使旧对齐失效。

映射关系：`相对目标 = 当前 J角 - 本次捕获 J角`；`绝对目标 = 软件参考 + direction × 相对目标`。实际下发仍须满足电机命令范围，不会通过钳位假装同步。

### 采集暂缺、失锁保持与同源重锁

- **原采集过期/暂缺超过 300ms**：保留订阅和本次流身份，减速进入“采集等待保持”；不会继续按旧帧跑，也不因这一原因写急停锁存。
- **有新鲜采集但目标失踪或重锁确认中**：网页发布 `valid=false`，上位机进入“失锁保持”，受控制动后原位保持力矩、流授权与原对齐基准。失踪多久都不自动归位，不生成视觉归位超时；保持仍发送合法位置帧并核验电机反馈和 UI lease。
- **自动重捕获**：自动 `lock_id` 变化不清除保存的校准与跟随意愿。网页对单人目标稳定至少 500ms 且至少 5 个独立新采集确认后才发布有效目标；确认期间保持模型角度及上一有效 IK 解，不用插值姿态覆盖 IK 种子。
- **同流恢复**：后端独立累计同身份、同校准、同参考的新鲜有效采集，稳定至少 500ms 且至少 5 帧后，沿原基准恢复仍授权的跟随；不重新对齐、不重发 START，不补跑积压目标。再次失锁或采集超过 300ms 会清空恢复累计，不能借用停顿前的确认时间提前续跟。
- **人工“重新锁定”**：仍显式清除网页校准并撤销旧来源，与自动重捕获不同；需要重新校准、开始跟随并由人员重新启动目标轴，不能继承已撤销的授权。
- **真实通道断开、来源撤销、身份/校准/归位或参考变化**：不属于采集暂缺，旧授权必须失效，按提示处理；订阅自动恢复仅预览，电机须重新人工启动。
- **电机故障、越限、反馈失联、门极异常、UI 租约或调度失效、人工停止**：仍按原规则保护和取消。后来出现新视觉帧不能清故障、首次使能或复活已撤销流。

网页不接收电机反馈，其“已提交/恢复”提示只描述目标通道。是否仍在跟随、已保持或须人工启动，以桌面当前反馈及保护状态为准。

## 9. 运行设置、坐标与限位

### 当前响应策略

当前 UI 已移除 RPM 和加减速度输入控件。连接时按在线只读 `config.v_max`（rad/s）选择不超过该值的最大整数 RPM，上限为 32767；加减速度固定 360°/s²。换算为 `1 RPM = 6°/s`。

源码模型中的 100 RPM 是未连接初始化值，不是在线固定限速；旧版本 1 RPM、100 RPM 或 90°/s² 的说明不能代替当前策略。上限未知、非法或不足 1 RPM 时阻止启动，不擅自提高固件限制。高速度上限也不等于适合带载机械，应先做现场风险评估。

### 临时运行设置

右侧“参数与运行设置”选择轴，展开“临时运行设置 / 未使能时调整”，可编辑相对保护下/上限、临时电流和反向，显式点击“应用全部本轴设置”。

- 只有本轴未使能、Gate=false、反馈/授权合格时可应用；日常保持仍处于使能状态，不能作为修改设置的条件。
- 此处修改后端运行限制，不改写固件绝对限位、速度上限、电流上限或 Flash；改动本身不自动启动。
- 初始临时电流为 0.5A；实际必须满足在线电流/力矩限制，按 0.1A 向下量化。不能将默认值视作对负载的安全验证。
- 反向取 `direction=-1`，否则为 `+1`；改变映射/参考后旧视觉对齐不可继续沿用。

### 软件参考与有效范围

默认三轴相对保护范围均为 `[-135°, +135°]`。记录参考不发送机械置零，只将当前多圈绝对位置作为本会话坐标原点。

有效保护范围取软件配置转换后的绝对区间与已启用固件绝对位置限制的交集；然后转换回相对范围。两端再各预留 0.1°命令裕量：完整 ±135°范围有效时，可下发范围为 `[-134.9°, +134.9°]`。

表盘和滑条展示保护范围，命令端点仍不可下发。非法目标、无有效交集、持续制动终点超限都被拒绝，不自动钳位。实测位置越过保护界仍安全停机；裕量不能保证消除机械惯性或过冲。

## 10. 协议、时序与安全机制

### CAN 编码要点

实现参见 [protocol.py](bridge/protocol.py) 和 [can_endpoint.py](bridge/can_endpoint.py)。

- 29-bit 扩展 ID 按 `priority / messageType / destination / source / sequence` 编码；2-bit sequence 用于响应匹配，不等于桌面 revision。
- 位置命令 `0x01` 的 8 字节数据为大端 `float32 输出轴绝对角度(deg) + int16 输出轴 RPM + int16 电机端电流限值(0.1A)`。
- RPM 必须是 1～32767 的正整数；固件中的 RPM=0 不表示停止，可能退回速度上限，不能用零速度帧替代保持流程。
- `START 0x62、STOP 0x63、CLEAR_ERRORS 0x65` 按普通节点寻址；`ESTOP 0xC0` 使用目的位掩码，ID1/2/3 分别为 2/4/8。
- SDO 固定头字段与参数值字节序不同：头字段按协议处理，标量值复用原厂小端类型。不可将整个 CAN/SDO 报文统一按同一字节序解释。
- 描述符必须完整组装，并通过长度、偏移、重复/冲突、CRC16 与 JSON/端点校验；缺片不能补零后继续。
- 普通参数访问只允许描述符可读标量，不调用 action/function。

唯一位置准备写白名单为 `config.input_mode=1` 和 `config.enable_vel_limit=true`，仅写 RAM，且必须读回。写 ACK 本身不证明参数已生效；不提供通用 write/call 或 Flash 保存入口。

### 不同时间阈值不能混用

| 机制 | 当前判据 | 含义 |
| --- | --- | --- |
| Python 运动 tick | 目标约 20ms | 调度目标，不是硬实时保证 |
| 活动运动调度保护 | 超过 50ms | 调度失效时安全保护 |
| 后端快照 / UI tick | 目标约 50ms | 快照发布和 UI 消费；不是采集新鲜度 |
| 桌面桥接快照新鲜度 | 年龄 <150ms，允许有限未来偏差 | 界面授权依据，后台收到不等于 UI 已消费 |
| 连续目标 / lease 桌面应答 | 150ms | 排队、管道写入、应答等待共用请求预算 |
| 桌面单次管道写入 | 最多 150ms；非停止类另取原请求剩余预算 | 排队后不刷新 deadline；停止类不因排队过期丢弃 |
| UI 租约 | 超过 200ms 未有效续约 | UI 控制授权失效，活动轴安全保护 |
| 电机位置/状态/门极 | 各 ≤200ms；故障字另要求 ≤1s | 轴有效反馈判据 |
| 静止证据 | 500ms 窗口至少 6 个新采样，再确认 200ms | 静止不是仅看一次速度接近零；采样间隔另须合格 |
| 视觉原采集 | ≤300ms | 不由心跳、发布时刻或旧帧重发刷新 |
| 视觉恢复稳定 | ≥500ms 且 ≥5 帧 | 只允许同身份、同参考、仍授权流恢复 |
| 在线驱动 CAN timeout | 合格范围 100～500ms | 厂商驱动保护，软件不自动改写 |
| 活动位置发令间隙 | ≤min(50ms, CAN timeout/2) | 持续控制/保持仍须合法运动帧 |
| 常规桌面请求 | 默认 1000ms | 个别动作另有预算，不等于电机采样期限 |
| 位置准备 / 阶段反馈等待 | 准备请求 5000ms；各 UI 核验阶段最多 3s | 不是整个启动流程仅有一个 3s 总预算 |

普通切窗/失去窗口焦点不主动暂停。若实际 UI 调度、管道、有效续租或电机反馈真的超时，仍会停机；不应为了避免误停把接收线程改成无限代续 UI 租约。

### Windows 管道写入与监督

- Windows 唯一独立写线程同步写完整 UTF-8 JSON Lines 并 flush，不逐帧等待 ThreadPool 异步写入。入队事件立即唤醒；空闲 `WaitOne(50)` 不是固定发送延迟。仍按 urgent→lease→normal 使用原有有界队列，不增加队列容量、CAN 线程或生产 ThreadPool 配额。
- 独立监督线程每 5ms 按单调时钟检查在途写入；非停止写预算为 `min(150ms, 原请求剩余预算)`，写前再次核验有效性。超限封闭写泵、结束通信授权，部分 JSON 不重试，旧目标不补发。
- Windows 通过 SafeHandle 管理自有写线程句柄，`CancelSynchronousIo` 的返回只表示取消请求结果。`ERROR_NOT_FOUND` 按开始/完成竞争处理，必须再次核验真实完成和写线程退出；取消异常不能抢先覆盖当次超时诊断。
- 取消后仍未退出则有界回收本实例自有 bridge，最多等待 800ms 后使用自有进程回收兜底。关闭窗口不无限等待写线程；结束 Python 进程不是机械停止确认，未知输出仍提示“停机未确认”。非 Windows 保留有界异步路径，本轮不新增跨平台验收承诺。

续租仍只由活着且已消费新鲜快照的 UI 发起；写线程和监督线程不自行无限续租。150/200/50/300ms 请求、授权、活动调度和原采集判据保持不变。

### 会话、版本与流隔离

- `sessionId`：只读重连后变化，旧会话请求/迟到响应不得污染新会话。
- `revision`：后端已接受的结构意图版本，不是原厂端点 CRC 或固件版本。
- `generation` 与流 `seq`：隔离旧流、重复/倒序样本。
- `axisTokens` / 轴 `controlToken`：隔离逐轴停止、准备、移除及再次启动后的旧成员目标。
- 桌面 `controlEpoch、axisOperationEpoch` 等局部取消标记：避免旧异步完成结果复活已停止控制。

结构请求应答可能先于状态广播。连续目标和续租必须使用已确认控制版本，旧广播不能将其降级，未确认的意图版本也不能当成已接受版本。诊断中保留 `acknowledgedStreamRevision` 与交接状态便于核对。

停止类请求、lease 与普通目标有独立有界队列；停止优先，目标只保留有限最新值。RAM 准备分步推进，不得饿死其他轴位置发送或正常反馈查询。控制失败后保留首保护原因，旧流拒绝不应覆盖先发生的安全事件。

### 故障、状态与跟随误差

- `telemetry.fault` 是故障位掩码；当前原厂源码为过压 0x1、过流 0x2、急停 0x4、位置 0x8、欠压 0x10、驱动过温 0x20、电机过温 0x40。
- 协议 `Error` 与 `HeartbeatFaultFlags` 是另外两类字段，不能相互替代，也不能用一次零心跳清除真实故障。
- 未接管观察轴的残留故障只显示在本轴并阻止本轴启动，不升级为其他正常轴的组级停机。排除必须同时满足：同会话新鲜快照、轴已连接且反馈有效、未使能、Gate=false、不在启动中或当前流内、上一份本会话已消费反馈中也未使能，且不是“停止待确认/停机未确认”。手动和视觉共用这一边界。
- 输出开启、门极未知、反馈无效、启动中、当前流成员或上一份反馈仍受控的故障轴不能按只读残留故障排除；受控轴故障和全局保护仍先撤权，再提交安全停机。故障轴不会被自动清错、接管或重新启动。
- 桌面启动核验的协议空闲模式为 2，位置运行反馈模式为 5；这是 CAN 响应模式，不是 `command.mode` 或固件内部 FSM 的编号。
- 主机“跟随误差超过 3°持续 200ms 自动停机”已取消；采样对齐误差和持续时间仍用于诊断，到位、越限、驱动故障及通信保护仍保留。本次失锁保持另取消视觉自动归位及其超时路径，不放宽真实通信或电机保护。
- 原厂 CAN timeout 清命令不等于本项目位置保持，也不等于已经确认门极关闭。QUERY/心跳不能替代驱动要求的控制命令刷新。

## 11. 诊断 JSON、曲线和日志

### 导出入口

右侧选择故障轴，点击“导出诊断 JSON”；曲线区域点击“导出 CSV”。系统保存对话框默认文件名为 `CBA03-yyyyMMdd-HHmmss.json/csv`，位置由用户选择，不自动保存到固定目录。

JSON 导出本身不续租、不发令，按导出时的 UI 已消费快照冻结诊断，并将后台最新状态单独列出。参数表“读取快照”读取后端参数缓存，不是每次强制扫描/重写全部驱动参数。

建议故障发生后尽快导出原会话 JSON，再决定是否重连。新会话清理部分首因和授权记录，重连后的健康 JSON 不能证明原故障原因。涉及视觉时同时保存网页诊断、视觉后端日志和采集序号对照。

### 重点字段

`capturedAt、node、snapshot、logs` 位于根对象；其余诊断项位于 `diagnostics` 下。表中 `axis.*` 和 `transport.*` 分别指 `diagnostics.axis.*`、`diagnostics.transport.*`；部分单元格省略共同前缀。

| JSON 字段 | 用途 |
| --- | --- |
| `capturedAt、node、snapshot` | 导出时间、所选轴、参数来源/读回缓存 |
| `diagnostics.fresh、axisUsable、stateAgeMs` | 当前 UI 快照是否有效，防止把历史轴值当新反馈 |
| `sessionId、revision、sessionFault、firstProtection` | 会话、接受版本、本地保护与后端首保护 |
| `firstPositionStartFailure` | 首次启动失败阶段、轴快照、意图版本与 UI 时序 |
| `firstStreamSampleFailure` | 首次连续目标失败及已确认/请求版本对照 |
| `axis、axes、axisStartBlocks` | 三轴反馈、参数问题、逐轴真实启动受阻原因 |
| `axis.txCount、txAbsolute、txRevision、txAgeMs` | 实际位置帧发送记录；不等价于运动或到位 |
| `axis.stopTx*、stopConfirmed*` | 停止发送及会话内历史门极关闭确认；缺失/过期不是已停 |
| `axis.stationary*、positionResolutionDeg、encoderCountAgeMs` | 静止窗口、整数证据和多圈 float32 精度 |
| `estopRecovery、unownedStop` | 上次恢复/人工停止进度和结果；不是当前使能状态 |
| `transport.latestFresh、latestRevision、uiConsumptionLagMs` | 区分后台接收与 UI 消费滞后 |
| `transport.authorizationValid、intentRevision、acknowledgedStreamRevision` | 授权和结构请求版本交接 |
| `transport.firstRequestTimeout` | 进程级历史首超时，区分发送排队、管道写入和等待应答；不能替代当前 lease 首因 |
| `transport.firstLeaseFailure.currentRequestTimeout、historicalFirstRequestTimeout` | 当次诊断异常证据与历史首超时分列；当次缺失时明确未知，不借用历史归因 |
| `transport.firstLeaseFailure、firstUiProtection、recentUiTicks` | 首次续约失败、UI 首保护与最近时序；自动安全停机在撤权、清流和发送急停前冻结首因，`authorizationValidBeforeStop` 保留撤权前状态 |
| `transport.recentTransport` | 当前会话最近至多16条传输时序；含队列、阶段、字节数、单调时戳和取消结果，不记录请求正文或来源凭据 |
| `health、protectionFirstCause、protectionCurrentFeedback、protectionNextStep` | 四项独立状态及分层保护显示，未知/过期不是健康或已停机 |
| `transport.firstUiProtection.reason、source、generation、axes` | 新增停机记录中的触发原因、当时来源/流及三轴故障、门极和新鲜度；`axes[].excludedIdleUnowned` 标明是否满足只读观察轴排除条件 |
| `logs` | 会话日志；导出保留日志数组，不是无限持久化日志服务 |

当次超时对象还包含 `queue、requestBytes、writerState、inflightAction、inflightAgeMs、lastWriteAgeMs、urgentDepth、leaseDepth、normalDepth、availableWorkers、originalDeadlineStamp、writeBudgetMs、cancellationResult`。队列阻塞者与本次请求分别记录；单调时戳仅用于本进程时序比较，取消返回不能当作写线程已经结束。`firstUiProtection.recentTransport` 是提交安全停机前的冻结记录，不会被后续急停或迟到应答替换。

`diagnostics.response` 中的 draft 字段仍保留用于内部状态对照，不代表当前界面存在响应输入框。`controlSource` 应结合实际选择与当前流判断，占位控制来源不能视作可运行模式。

后续急停产生的 Fault=0x4 不覆盖 `firstUiProtection`；它是本会话软件观察证据，不是原始 CAN 总线时序。仅凭停机后的截图或重连后的 JSON，不能认定急停位就是最初触发原因。

### 曲线

曲线来源于电机反馈，缓冲保留最近约 60 秒并受容量限制。CSV 列为：

```text
time_unix_s,mode,node,target_deg,position_deg,velocity_deg_s,iq_A
```

未知值为空，不补零伪造反馈。母线电流、力矩、功率等若标为“估算”，不可当作独立传感器实测。选择“仅暂停曲线显示”不会暂停控制，导出文件也不能代替原始 CAN 总线记录。

## 12. 常见问题排查

| 现象 | 优先检查 | 不应采取的捷径 |
| --- | --- | --- |
| `pwsh` 找不到 | 安装 PowerShell 7、检查 PATH，或由管理员提供正确执行入口 | 修改系统级策略绕过组织限制 |
| 缺少 Python / pyserial 版本不符 | 运行对应 Prepare 脚本，检查 `.venv` 或 `CBA03_PYTHON` | 让正常启动静默安装/升级全局依赖 |
| 缺少对应源码构建 | 显式 `Build.ps1 -Task Build`，再 `Start.ps1 -CheckOnly` | 直接使用旧 publish EXE 搭配新 bridge |
| COM 口存在但连接失败 | 端口占用、SLCAN 协议、波特率、CAN 接线、节点应答和描述符 | 把任意 COM 口当 USB2CAN，反复写 START |
| 只有一/两台在线 | 查看各轴连接错误；合法非空组合可只读/逐轴使用 | 伪造缺失轴在线或强行三轴使能 |
| 启动按钮不可用 | Tooltip/顶部逐轴原因及 JSON `startPositionBlockReason` | 仅凭一次速度为零绕过静止核验 |
| “门极已开、本会话未接管” | 停止外部控制器并使用专用人工停止入口 | 自动接管或用日常保持误代 STOP |
| RAM 准备或 SDO 超时 | 原会话诊断、描述符缺片/CRC、参数读回、总线稳定性 | 写 ACK 即认为成功，沿用可能串位的迟到应答 |
| 多圈精度不足/静止不合格 | `positionResolutionDeg`、整数采样、两类增量一致性、实际抖动 | 自动置零、多圈取模或放宽静止阈值 |
| 启动后滑条不发送 | 实时控制、源选择、Gate/模式、命令范围、新的真实输入 | 把草稿值、绑定通知或白线当 TX 成功 |
| 目标发送后未到位 | TX 与新鲜实际反馈、到位/静止、限流、负载和驱动状态 | 用发送计数或网页模型证明实机到位 |
| UI 租约/调度超时 | 原 JSON 的首因、排队/写入/应答等待、UI tick 和后台年龄 | 单纯提高 200ms 时限或让后台替 UI 无限续租 |
| 连续目标 revision 拒绝 | 会话、应答接受版本、旧广播、generation、axisTokens | 盲目把 revision 加大并重发旧目标 |
| 视觉连接没有配对码 | 新版已取消；查看已订阅/等待采集/重连中、三端 protocol=2 与服务身份 | 将订阅或端口可达当作数据新鲜/电机授权 |
| 视觉已订阅但启动受阻 | 单人 tracking、校准、原采集年龄、逐轴电机条件 | 把 WebSocket 心跳或旧 J角当作新采集 |
| “采集等待保持”或“失锁保持” | 等待同来源稳定重锁与新帧；确认当前流、lease 和电机反馈仍有效 | 自动归位、解锁急停、重复 START 或发送旧帧 |
| Fault=4 或 Error=9 | 区分故障字、错误码和心跳分类，核对门极与清除条件 | 将所有 Error=9 当可清除急停 |
| “停机未确认” | 实际运动、机械支撑、独立急停、停止 TX 错误及新 Gate 反馈 | 把串口关闭、程序退出或历史成功当当前已停 |
| 运行行为与 README 不符 | 实际 EXE/bridge 根目录、构建时间、三端加载版本和在线固件版本 | 按文件夹名称或旧发布验证记录判定已更新 |

多圈绝对位置仍以原始反馈为依据。整数编码器计数仅用于合格的连续静止证据，不提高 float32 绝对位置/命令精度；缺失、分辨率不足或增量不一致时仍闭锁。

## 13. 离线测试与构建

以下是开发验证，不连接实体串口/相机，不发送实机运动；必须使用已准备的环境。测试会生成或覆盖 `artifacts/verification/` 记录。

```powershell
& (Join-Path $HostRoot 'Build.ps1') -Task Build
& (Join-Path $HostRoot 'Build.ps1') -Task Test
& (Join-Path $HostRoot 'Start.ps1') -CheckOnly
```

`Build` 恢复解决方案并隔离构建 Debug/Release。正在运行的 EXE 锁住默认产物时，可显式指定 `-ArtifactsPath <独立构建目录> -VerificationPath <独立结果目录>`，不会关闭现场程序；独立目录构建不改变默认源码启动入口。`Test` 运行 Python 和 Release 桌面测试；它不是 Publish，也不保证执行完整 Debug 构建。`Build.ps1` 每个成功任务写入 `build-<Task>.json`，Python 运行器写入 `python-tests.json`，桌面写入 `desktop.trx`。

### 单独执行

```powershell
# 全部 Python 回归；显式指定独立结果目录，避免覆盖默认记录。
$Verification = Join-Path $HostRoot 'artifacts\verification\auto-subscribe'
& (Join-Path $HostRoot '.venv\Scripts\python.exe') -X utf8 (Join-Path $HostRoot 'tests\python\run_tests.py') --results-directory $Verification

# 桌面测试，产物仍隔离；TRX 使用不同名称避免覆盖全量记录。
$DesktopTests = Join-Path $HostRoot 'tests\HostComputer.Tests\HostComputer.Tests.csproj'
$BuildArtifacts = Join-Path $HostRoot 'artifacts\build'
$TestResults = Join-Path $HostRoot 'artifacts\verification'
dotnet test $DesktopTests -c Release -r win-x64 -p:SelfContained=false --artifacts-path $BuildArtifacts --results-directory $TestResults --logger 'trx;LogFileName=desktop-manual.trx'
```

Python 运行器的 `--results-directory` 仅指定记录位置，不改变测试范围；省略时仍写入默认 `artifacts/verification/python-tests.json`。

测试范围包括编解码、参数边界、内存 CAN 故障交错、逐轴启停、RAM 准备取消、静止/精度、限位、保持/急停恢复、UI 租约、目标版本交接、真实滑条输入与 Headless 布局。三端契约测试依赖同工作区网页和视觉源码；仅拷贝 HostComputer 时不要将缺少外部项目造成的失败解释为电机故障。

如修改跨项目协议，另行执行相应网页/视觉回归，并确认各自依赖已准备：

```powershell
$WebRoot = [System.IO.Path]::GetFullPath((Join-Path $HostRoot '..\..\..\..\Web交互演示'))
$VisionRoot = [System.IO.Path]::GetFullPath((Join-Path $HostRoot '..\..\..\..\GeminiMaxRobot'))
npm.cmd --prefix $WebRoot test
npm.cmd --prefix $WebRoot run build
& (Join-Path $VisionRoot '.venv\Scripts\python.exe') -X utf8 -m unittest discover -s (Join-Path $VisionRoot 'tests') -p 'test_*.py'
```

### 本次失踪保持与稳定重锁验证

本次独立记录位于 `artifacts/verification/loss-hold/`：[Python](artifacts/verification/loss-hold/python-tests.json) 237/237、[Release 桌面全量](artifacts/verification/loss-hold/desktop-final.trx) 484/484，网页 132/132、视觉 80/80，均通过；网页构建通过。桌面全量包含三端契约与新增 14 项失踪保持专项。

回归覆盖三轴单独/组合与正反向、非归位姿态长期保持、重锁确认期无效发布、人脸/嘴部 IK 种子保留、采集再次停顿重置稳定累计，以及保持中真实租约超时、电机故障、断链、身份变化和人工停止仍保护。恢复前后原流代次、逐轴令牌与对齐基准不变，不重发 START/STOP/急停或清错命令；这里的故障与运动均为合成数据/内存 CAN。

[Debug/Release win-x64 构建记录](artifacts/verification/loss-hold/build-Build.json) 对应独立产物 `artifacts/loss-hold-build`，双配置通过。`Start.ps1 -CheckOnly` 通过，但仅核验依赖与默认源码入口存在，不证明默认入口、旧发布包或现场程序已更新。本次桌面测试还原曾提示 NU1900（NuGet 漏洞数据服务索引不可达），测试仍通过；未关闭审计或修改依赖配置。

本次只完成源码和离线验证，未 Publish/All、重启现场服务/用户 EXE、打开实体相机/串口、清错、置零或试转。此前截图中的 UI 租约超时仍需原会话 JSON 与加载版本对照，不能将本次视觉保持增强认定为现场唯一根因的证明。三端同步加载新版后，先人工只订阅预览复验失踪/重捕获，再按现场授权方案验收电机。

### 此前自动订阅验证记录

此前无配对码自动订阅的最终结果集中于 [auto-subscribe 汇总](artifacts/verification/auto-subscribe/verification-summary.json)：[Python](artifacts/verification/auto-subscribe/python-tests.json) 235/235、[Release 桌面全量](artifacts/verification/auto-subscribe/desktop-final-4.trx) 470/470、网页 127/127、视觉 80/80，均退出 0；网页构建通过。桌面全量包含三端契约、三轴瞬断即重连撤权、迟到响应、真实滑条输入及既有保护回归。

[真实 WebSocket→内存 CAN](artifacts/verification/auto-subscribe/real-websocket-can.json) 覆盖桌面先启动、网页先启动两种顺序，仅使用测试自有随机端口和假相机；两场景的只订阅预览与重连 CAN 控制帧均为 0，故障仍保留。[三端合成契约](artifacts/verification/auto-subscribe/cross-project-contract.json) 覆盖两场景及 24 份桌面摘要，`success=true、hardwareAccess=false、webFeedback=false`。摘要不是电机授权或续租，订阅保活依靠独立 WebSocket ping/pong。

1500×1020 与 1120×780 的 Headless 布局和最终截图已核验：无配对码控件，等待采集、未知 lease、逐轴阻断及故障首因仍独立显示，恢复新鲜预览不清故障。截图使用合成数据，不是浏览器或实机验收。

[Debug/Release win-x64 构建记录](artifacts/verification/auto-subscribe/build-Build.json) 对应独立产物目录 `artifacts/verification/auto-subscribe/build`，双配置构建退出 0。`Start.ps1 -CheckOnly` 通过，但只核验依赖及默认 `artifacts/build` 源码入口存在，不启动应用，不证明该入口或现场程序已加载独立新版。

本轮未修改计划文件，未 Publish/All、提交 Git、重启用户 EXE 或现场 5173/5189 服务，也未打开实体相机/串口、清错、置零或试转。此前 233/445、lease-boost 的 235/464/22 为历史基线；本轮较早失败的 `desktop-final.trx`（464/470）、`desktop-final-3.trx`（467/470）仍保留，最终验收以 `desktop-final-4.trx` 为准。夹具修正没有放宽生产保护时限或改动 BridgeClient 写泵。

三端同步加载新版须另行确认，现场先只订阅预览。原归位超时和 UI 租约异常仍需原会话 JSON 及加载版本对照；取消配对码不代表这些故障已经解除。

测试数量随源码变化，仅作为该次记录；后续以执行退出码、JSON success、TRX 结果、生成时间与实际加载源码为准。历史 `build-Publish.json` 或 `release-smoke.json` 不能证明当前源码已发布或现场故障已消失。

## 14. 发布与交付检查

[Build.ps1](Build.ps1) 支持以下任务：

| 任务 | 行为 |
| --- | --- |
| `Build`（默认） | restore + Debug/Release 隔离构建 |
| `Test` | Python 回归 + Release 桌面测试，保存验证记录 |
| `Publish` | Windows x64 .NET 自包含发布，并复制 bridge、profiles、vendor、运行脚本 |
| `All` | 顺序执行 Build、Test、Publish |

只有明确要生成交付包时执行：

```powershell
# 应先完成本次 Build/Test。此命令会生成/覆盖发布目录，但不启动应用或电机。
& (Join-Path $HostRoot 'Build.ps1') -Task Publish
# 完整流程也可使用 -Task All；它包含发布，不是“只检查”。
```

发布脚本生成 `delivery.json`，记录平台、.NET 自包含状态、Python 未打包、原厂工具哈希和 `hardwareTested=false`。该字段表达未做实体验收，不表示程序没有硬件访问能力。

当前脚本不会自动复制本 README 或整个验证目录到发布包；源码根目录是本文的权威位置。交付需要附带文档和当次记录时，应在发布流程中显式安排，不把目录里遗留的旧文件算作本次产物。

交付检查：

1. 核对当前源码、发布 EXE、bridge 和 vendor 来源；发布时无其他程序占用交付目录。
2. 完整复制发布目录，在目标机显式准备 Python，执行包内 `Start.ps1 -CheckOnly`。
3. 对照本次 Python/TRX/构建记录，不把历史成功混入当前版本。
4. 开窗仅验证未连接状态；真实设备验收需另外授权和现场操作方案。
5. AI 模式同步部署对应网页/视觉版本；三端服务重启和浏览器刷新不会由 HostComputer 发布脚本完成。
6. 现场先只读、只订阅预览，再经机械确认按单轴小行程逐步验收，保存同会话诊断。

## 15. 开发约束与已知限制

### 维护入口

- UI 布局：[MainWindow.axaml](src/HostComputer.App/Views/MainWindow.axaml)。
- 主控制编排：[MainViewModel.cs](src/HostComputer.App/ViewModels/MainViewModel.cs)、[MainViewModel.Control.cs](src/HostComputer.App/ViewModels/MainViewModel.Control.cs)。
- 单轴门控/真实输入：[AxisViewModel.cs](src/HostComputer.App/ViewModels/AxisViewModel.cs)、[UserTargetSlider.cs](src/HostComputer.App/Controls/UserTargetSlider.cs)。
- 桥接进程与请求时序：[BridgeClient.cs](src/HostComputer.App/BridgeClient.cs)、[server.py](bridge/server.py)。
- 视觉客户端/状态契约：[VisionClient.cs](src/HostComputer.App/VisionClient.cs)、[Models.cs](src/HostComputer.Core/Models.cs)。
- 后端保护与轨迹：[engine.py](bridge/engine.py)、[motion.py](bridge/motion.py)、[control_stream.py](bridge/control_stream.py)。
- 回归：[DesktopTests.cs](tests/HostComputer.Tests/DesktopTests.cs)、[test_bridge.py](tests/python/test_bridge.py)、[test_safety.py](tests/python/test_safety.py)、[test_stream.py](tests/python/test_stream.py)。

### 修改时必须保留的边界

- `stdout` 只输出版本化 JSON Lines，日志写 `stderr`；报文、缓冲、请求和队列均有界，拒绝非有限数值。
- 真实输入与数据绑定分离；不要让属性刷新、迟到响应、重新连接或仅勾选实时控制产生目标。
- 接收线程只更新最新状态/发唤醒，控制提交和 UI 租约仍受 UI 已消费快照及最新故障核验约束。
- 区分已确认版本与待提交意图，不绕过 session/generation/轴令牌/局部取消屏障。
- 区分运动目标、规划值、实际 TX 和反馈到位；不要把未发送规划拿来制造跟随误差保护。
- 局部停止不得影响其他轴正常发送；真实全局保护、未知停机和超时升级仍优先。
- 保留原始采集时间及单调序号；预览/图像编码不能阻塞关键采集交付。
- 不把 Fault、Error、心跳分类或三套模式编号混用；不增加无门控的通用参数写/动作读取。
- 使用现行 xUnit v3/Headless 依赖及隔离 artifacts，避免旧 bin/obj 和发布包混用。

### 尚需独立验收的事项

- 真实三轴负载、机械安装、运动方向、行程裕量、限流和高速响应未由离线测试证明安全。
- Windows 卡顿、系统睡眠、适配器排队及真实 CAN 干扰仍可能触发保护；切窗正常不等于允许真实失联。
- 大多圈坐标精度、整数静止证据和实际固件能力需以当轴 JSON 与现场读回核对，不能自动置零规避。
- 用户已现场确认手动位置控制反复停止问题解决；这一结论只适用于手动场景，不代替 AI 视觉验收。
- 此前已离线复现并修复未接管关闭输出轴故障误升级组级锁存，以及 ThreadPool 压力下 Windows 管道交付阻塞的路径；本轮自动订阅保留这些回归与生产写泵。这些不是现场唯一归因：原会话桌面 JSON、网页诊断及实际加载版本仍需对照，后续 Fault=0x4 不能倒推最初原因。
- 同源失踪保持、稳定重锁自动续跟与再次采集停顿已具备软件回归；人工重新锁定/校准仍撤旧授权，旧服务/旧 EXE 混用或实机异常须保留当次证据复验。
- 速度/力矩来源、远程控制、通用参数编辑和固件升级不在当前已实现功能范围。
- 程序退出只回收自有 bridge 子进程，不负责终止外部电机精灵或视觉服务；结束进程也不是停机确认。

## 16. 原厂资料与许可证

- [原厂文档索引](../robot_mc-CBA03_v4.2.89/docs/README.md)
- [用户手册](../robot_mc-CBA03_v4.2.89/docs/USER.md)
- [开发文档](../robot_mc-CBA03_v4.2.89/docs/DEV.md)
- [参数目录](../robot_mc-CBA03_v4.2.89/docs/parameters.md)
- [rmc 工具说明](../robot_mc-CBA03_v4.2.89/tools/rmc/README.md)
- [OpenBootloader / blcan 使用说明](../robot_mc-CBA03_v4.2.89/tools/blcan_使用说明.md)
- [原厂 MIT License](../robot_mc-CBA03_v4.2.89/LICENSE)

这些资料可辅助理解驱动参数、CAN 超时、寻址及工具，但不直接描述本项目 session/revision/UI 租约。原厂端点 CRC、固件版本和 HostComputer 控制版本是不同概念。部分原厂文档存在故障位、模式、计数含义或版本示例差异，应以对应源码和实际固件读回交叉核对；文件夹名 `v4.2.89` 不保证实体固件版本。

`rmc` 可访问 CAN，`mc_ep.py` 是兼容入口；“读取动作端点”可能执行动作，不应把任意工具读取当成无副作用检查。`blcan` 面向 OpenBootloader 升级，固定 500 kbit/s，与本应用 1 Mbit/s 不同；刷写不是上位机通信问题的默认排查步骤。

发布包 `vendor/` 携带原厂 Python 源码及 `vendor/LICENSE`，再分发时应保留版权与许可。HostComputer 自身的使用/交付授权及其他第三方依赖许可应分别确认，不因引用原厂 MIT 代码就自动推定整个项目可按 MIT 发布。
