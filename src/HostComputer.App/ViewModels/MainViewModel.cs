using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HostComputer.Core;
using Markdig;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;

namespace HostComputer.App.ViewModels;

public sealed record ParameterRow(string Name, string Label, string Reference, string Online, string Unit, string Difference);

/// <summary>
/// 打通“相机追踪角度 → 三关节映射 → 电机控制”
/// </summary>
public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IBridgeClient bridge;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
    private sealed record LeaseRequest(BridgeState State, long Revision, long Started, Task<JsonElement?> Reply);
    private LeaseRequest? pendingLease;
    private long leaseSubmitted, leaseCompleted;
    private readonly Queue<JsonElement> uiLeaseHistory = new();
    private JsonElement? firstUiProtection;
    private long nextCurveAt, nextProtectionDetailsAt;
    private const string StaleBridgeMessage = "桥接状态过期，停止续约；实机停机状态须检查。";
    private bool preparingPosition;
    private bool portsPending, disposed;
    private long nextPortsAt;
    private bool updatingPorts;
    private bool authorizationValid = true;
    private string motorProtectionReason = "", motorProtectionSummary = "", motorStopResult = "";
    private readonly HashSet<int> motorProtectionNodes = [];
    private long intentRevision;
    private long acknowledgedTargetRevision = -1;
    private bool targetLeaseHandoffPending;
    private long recoveryRevision = -1;
    private string lastRecoveryNotice = "";
    private string session = "", axisSession = "";
    private long lastLog;
    private double lastTime;
    private long parameterRefreshAt;
    private readonly HashSet<(string Session, int Node)> parameterRequests = [];
    private long lastUiTickStamp;
    private double uiTickIntervalMs, uiTickDurationMs, maxUiTickDurationMs;
    private JsonElement? firstLeaseFailure;
    private readonly Lazy<JsonElement[]> referenceRows = new(() =>
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(BridgeClient.FindRoot(), "profiles", "screenshot-reference.json")));
        return doc.RootElement.GetProperty("parameters").EnumerateArray().Select(p => p.Clone()).ToArray();
    });
    private readonly List<string> logLines = [];
    private JsonElement? parameterSnapshot;
    public BridgeState? CurrentState { get; private set; }
    public AxisViewModel[] Axes { get; }
    public CurveBuffer Curve { get; } = new();
    public event Action? CurveChanged;
    public ObservableCollection<ParameterRow> Parameters { get; } = [];
    public ObservableCollection<PortInfo> Ports { get; } = [];
    [ObservableProperty] private PortInfo? selectedPort;
    [ObservableProperty] private string portEnumerationStatus = "尚未枚举；枚举不会打开串口";
    public string PortStatus => Connected
        ? $"{Port} · CAN 1 Mbit/s · " + (Fresh ? "电机已连接；点击本轴“启动位置环”，就绪后拖动滑条" : "会话反馈过期，连接状态待核实；禁止新运动指令")
        : PortEnumerationStatus;
    partial void OnPortEnumerationStatusChanged(string value) => OnPropertyChanged(nameof(PortStatus));
    partial void OnConnectedChanged(bool value)
    {
        if (!value) PortEnumerationStatus = "当前无电机会话；等待刷新端口，未自动连接";
        OnPropertyChanged(nameof(PortStatus));
    }
    partial void OnSelectedPortChanged(PortInfo? value)
    {
        if (!updatingPorts) Port = value?.Device ?? "";
    }
    public string[] NodeNames { get; } = ["ID1", "ID2", "ID3"];
    public string[] ParameterGroups { get; } = ["电机参数", "驱动参数", "全部参数"];
    public string[] CurveModes { get; } = ["位置 / °", "速度 / °/s", "Iq / A"];
    public double[] Steps { get; } = [.1, .5, 1, 5, 10];
    public string HelpText { get; } = Markdown.ToPlainText($"""
        # CBA03 台架位置控制
        1. 固定空载台架为默认前提，无需重复确认；不代表自动检测负载。仅真机模式；启动只枚举端口，不打开串口。支持 CANable/SLCAN、Classic 1M、CyberBeast、ID1/2/3 任意组合、主站127。关闭电机精灵等其他主站；缺席节点不影响其他节点连接。
        2. 连接电机（不自动使能）→ 点击本轴“启动位置环” → 拖动滑条或使用滑条方向键 → 停止转动并保持力矩。再次启动恢复控制，不执行旧目标。角度控制仅提供滑条，不提供目标输入框、±或下发按钮。启动串联 RAM 参数写读回、连续静止、记录当前位置为相对0°及使能反馈确认；不写 Flash、不机械置零、不执行旧目标。原分步操作保留在“高级 / 分步操作”中。
        3. “启动位置环”确认就绪后自动打开滑条实时控制；目标变化即唤醒提交，在途只保留最新值，仅真实拖动或滑条按键事件发新目标，打开本身不发目标。未启动或实时关闭时滑条仅修改待下发值；再次开启后须重新拖动，不执行旧目标。电位器式游标标注仪表完整保护范围及上下长短刻度，默认每5°一格，不吸附或量化鼠标角度。三轴相对保护范围 {Protocol.JointMinimumDeg:G}～{Protocol.JointMaximumDeg:G}°，可下发端点各预留0.1°；选择保护端点会拒绝，不自动钳位或下发。速度自动取连接时只读固件 config.v_max 内最大整数 RPM（不超过32767），加减速度固定 {Protocol.FastAcceleration:G}°/s²，限流默认0.5A；不提供速度、加减速度输入或应用按钮。速度上限未知或不足1 RPM时禁止启动，不改固件速度限制。启动时只核验并应用本轴响应，不改行程、方向或电流；运行/保持中不自动更新响应，不写 Flash。提高响应不保证无延迟、无超调，实际效果仍受电流、负载和驱动闭环限制，须验证制动与停止。顶部步进只影响滑条方向键，不改变鼠标角度映射、刻度或底层CAN调度。与在线限值求交集，不自动加流；不得超过已核验的机械行程。
        4. 日常停止仅减速保持，仍显示运行，继续位置帧和UI租约，不发STOP/ESTOP。保持后“继续”仅恢复控制，不重发START。只有空闲状态可启动。视觉采集暂缺超过300ms减速保持，同身份新采集稳定后自动继续，不锁存。仅单独急停Fault=0x4可人工清除，新反馈确认空闲后才可启动。真实故障、失联、越限、通信超时、断开和退出仍安全停机；软件急停不能替代硬件急停与机械支撑。未接管外部电机不自动接管，须通过专用人工确认STOP核验关闭输出。
        5. 故障后禁止自动续跑。点击本轴“异常”即可请求恢复，无需勾选或二次确认。新反馈确认所有在线轴门极关闭后，本地锁存通过停止、断开和只读重连恢复；仅单独 CAN 急停锁存 Fault=4 / Error=9 可逐轴清除（已核对固件 1.0.5）。其他故障、失联或停机未确认会拒绝并显示原因。发送前后均核验新反馈，不使能、不恢复旧目标、不置零、不写 Flash；成功后须显式重新启动。
        6. 位置、速度及遥测来自实机；未连接或过期不显示有效读数。母线电流/功率、Iq×KT×gr 力矩均为估算。三轴逐帧发令，不是硬件同步插补，Windows 不是硬实时。
        7. 0～67V 是截图保护设置，不是供电建议；截图已标定不等于实机已标定。图示相间 R/L 不直接换算到固件单相参数。
        8. 首次实机验收待单独授权：只读核对已连接节点，逐轴 1° 验证方向、单位、反馈和停止。三轴全部就绪才开放三轴联动。
        9. 增减节点须先停止并断开重连；失联节点本会话不自动恢复。运行中已授权轴掉线仍触发组停机。
        10. 视觉随动：相机观测经网页解算为 J1/J2/J3，固定对应 ID1/ID2/ID3。AI 模式点击本轴“启动”即显式授权该映射：复用 RAM 准备、静止参考、响应核验，捕获当前 J 角基准后仅使能本轴并跟随；其他轴不会启动。自动订阅只预览，不自动运动；切窗保留新采集发布，原采集超过300ms减速保持等待，同流稳定新帧自动继续。未接管且新鲜反馈确认门极关闭的故障轴仅阻断本轴；受控、启动中或输出未核验轴故障仍保护。多轴分步确认入口保留。
        """);
    [ObservableProperty] private string port = "";
    [ObservableProperty] private bool connected;
    [ObservableProperty] private bool active;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool fresh;
    // 用户已确认固定空载台架；仅免去重复勾选，不代表使能或清故障授权。
    public bool Confirmed => true;
    [ObservableProperty] private bool curvePaused;
    [ObservableProperty] private int curveMode;
    [ObservableProperty] private int selectedNodeIndex;
    [ObservableProperty] private int parameterGroup;
    [ObservableProperty] private double stepSize = 1;
    [ObservableProperty] private string message = "请选择 USB2CAN 串口 → 连接电机 → 启动位置环 → 拖动滑条控制角度。单台电机即可，不需要相机。";
    public string Banner => "真实硬件 · 单主站127，请关闭电机精灵 · 软件急停不等于硬件急停";
    [ObservableProperty] private string timing = "周期 —  /  UI 租约 50ms";
    [ObservableProperty] private string logs = "";
    public bool CanChangeConnection => !Connected && !Busy && !portsPending;
    internal static string ShortText(string text, int limit = 76) => text.Length <= limit ? text : text[..limit] + "…";
    public string ProtectionFirstCause
    {
        get
        {
            if (motorProtectionSummary.Length > 0) return motorProtectionSummary;
            if (firstUiProtection is { } first)
            {
                if (first.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    return ShortText(reason.GetString() ?? "首因未知");
                if (first.TryGetProperty("fault", out var fault) && fault.ValueKind == JsonValueKind.String)
                    return ShortText("后端首报：" + fault.GetString());
            }
            return "首因未知；尚无本会话保护证据";
        }
    }
    internal string CurrentAxisFeedback(AxisState axis)
    {
        if (!StateFreshNow || CurrentState?.SessionId != session || !axis.Connected || !axis.Valid ||
            !ReferenceEquals(axis, CurrentState.Axes.FirstOrDefault(a => a.Node == axis.Node)))
            return $"ID{axis.Node} 当前反馈未知；停机未确认";
        if (axis.Phase is "停止待确认" or "停机未确认") return $"ID{axis.Node} 本次停机未确认";
        return axis.Gate == false ? $"ID{axis.Node} 门极关闭 · 无保持力矩；不代表机械静止" :
            axis.Gate == true ? $"ID{axis.Node} 门极开启 · {(axis.Holding ? "减速保持" : "输出开启")}；不代表机械静止" :
            $"ID{axis.Node} 门极未知；停机未确认";
    }
    public string ProtectionCurrentFeedback => string.Join("\n", Axes.Select(a => CurrentAxisFeedback(a.State)));
    public string ProtectionNextStep => !StateFreshNow || CurrentState?.Axes.Any(a => a.Phase is "停止待确认" or "停机未确认" || a.Connected && !a.Valid) == true
        ? "先核验硬件急停、机械支撑与实际停止；反馈未知时用顶部断开／取消→连接电机只读重连，不自动运动。"
        : CanRestoreConnection ? "检查首因后恢复连接（不启动）；合格急停须人工处理，再显式启动。" :
        "先停止并核验新门极反馈；点击本轴异常查看恢复条件，不自动启动或恢复旧目标。";
    [ObservableProperty] private bool protectionDetailsExpanded;
    partial void OnProtectionDetailsExpandedChanged(bool value) { if (value) OnPropertyChanged(nameof(ProtectionDetails)); }
    public string ProtectionDetails => JsonSerializer.Serialize(new
    {
        firstUiProtection, firstLeaseFailure, firstPositionStartFailure, firstStreamSampleFailure,
        visionChannel = new { vision.DesiredConnected, vision.Connected, vision.ChannelInterruption, vision.FirstChannelBreak },
        originalReason = motorProtectionReason, requestResult = motorStopResult,
        currentFeedback = ProtectionCurrentFeedback,
        historicalFirstRequestTimeout = bridge.FirstTimeout,
        recentTransport = SessionTransport(), recentUiTicks = uiLeaseHistory.ToArray()
    }, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true });
    private BridgeTransportEvent[] SessionTransport() => bridge.RecentTransport.Where(e => e.SessionId == session).ToArray();
    private string ProtectionMessage => "异常锁存：" + ProtectionFirstCause + "；停机状态须核验，见当前反馈／诊断详情";
    public bool StateFreshNow => bridge.Running && CurrentState?.Mode == "hardware" && Protocol.IsFresh(CurrentState, DateTimeOffset.UtcNow);
    public bool RecoveryPending => CurrentState?.EstopRecovery.Pending == true || recoveryRevision >= 0 && (CurrentState?.Revision ?? -1) < recoveryRevision;
    public bool CanAuthorize => StateFreshNow && Connected && authorizationValid && CurrentState?.Fault.Length == 0 && !RecoveryPending && !UnownedStopPending;
    public AxisViewModel SelectedAxis => Axes[Math.Clamp(SelectedNodeIndex, 0, 2)];
    public bool SelectedNodeConnected => Connected && CurrentState?.Axes.Any(a => a.Node == SelectedNodeIndex + 1 && a.Connected) == true;
    public string ConnectionSummary
    {
        get
        {
            if (!Connected) return "未连接 · 支持 ID1/2/3 任意组合";
            if (!Fresh) return "桥接状态过期 · 节点在线状态未知";
            var online = CurrentState!.Axes.Where(a => a.Connected).Select(a => $"ID{a.Node}").ToArray();
            var missing = CurrentState.Axes.Where(a => !a.Connected).Select(a => $"ID{a.Node}").ToArray();
            return $"已连接 {online.Length}/3：{(online.Length > 0 ? string.Join("、", online) : "无")}" +
                (missing.Length > 0 ? $"  ·  未连接：{string.Join("、", missing)}" : "");
        }
    }
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand DisconnectCommand { get; }
    public IAsyncRelayCommand EnableAllCommand { get; }
    public IAsyncRelayCommand ExecuteAllCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IAsyncRelayCommand EstopCommand { get; }
    public IAsyncRelayCommand RefreshPortsCommand { get; }
    public IAsyncRelayCommand RefreshParametersCommand { get; }

    public MainViewModel(IBridgeClient bridge, IVisionClient? vision = null)
    {
        this.bridge = bridge;
        this.vision = vision ?? new VisionClient();
        Axes = [new(1, this), new(2, this), new(3, this)];
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => CanChangeConnection);
        DisconnectCommand = new AsyncRelayCommand(() => IntentAsync("disconnect", null, true), () => Connected || Busy);
        EnableAllCommand = new AsyncRelayCommand(() => IntentAsync("enable", new { confirmed = Confirmed }), () => Axes.All(a => a.CanEnable));
        ExecuteAllCommand = new AsyncRelayCommand(() => ExecuteAsync(Axes), () => Axes.All(a => a.CanExecute));
        PauseCommand = new AsyncRelayCommand(HoldAllAsync, () => CanAuthorize && Active && !Busy);
        StopCommand = new AsyncRelayCommand(HoldAllAsync, () => Connected);
        EstopCommand = new AsyncRelayCommand(() => IntentAsync("estop", null, true), () => Connected);
        RefreshPortsCommand = new AsyncRelayCommand(RefreshPortsAsync, () => CanChangeConnection);
        RefreshParametersCommand = new AsyncRelayCommand(LoadParametersAsync, () => SelectedNodeConnected && Fresh && !Busy);
        timer.Tick += (_, _) => UiTick();
        InitializeControl();
        RefreshParameterRows();
    }

    /// <summary>
    /// 开始 UI Dispatcher 定时器和桥接客户端。连接异常时不会抛出异常；连接成功后会刷新参数快照。
    /// </summary>
    public void Start()
    {
        try { bridge.Start(); timer.Start(); _ = RefreshPortsAsync(); }
        catch (Exception ex) { Message = ex.Message; }
    }

    partial void OnBusyChanged(bool value) => RefreshCommands();
    partial void OnCurveModeChanged(int value) => CurveChanged?.Invoke();
    partial void OnStepSizeChanged(double value) { foreach (var axis in Axes) axis.Refresh(); }
    partial void OnSelectedNodeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedAxis));
        parameterSnapshot = null;
        RefreshParameterRows();
        RefreshCommands();
        if (SelectedNodeConnected) _ = LoadParametersAsync();
    }
    partial void OnParameterGroupChanged(int value) => RefreshParameterRows();
    public void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanChangeConnection));
        OnPropertyChanged(nameof(CanAuthorize));
        OnPropertyChanged(nameof(RecoveryPending));
        OnPropertyChanged(nameof(ProtectionFirstCause)); OnPropertyChanged(nameof(ProtectionCurrentFeedback));
        OnPropertyChanged(nameof(ProtectionNextStep));
        // 完整时序仅在展开诊断时刷新，避免每 50ms 序列化长段文本。
        if (ProtectionDetailsExpanded && Environment.TickCount64 >= nextProtectionDetailsAt)
        { nextProtectionDetailsAt = Environment.TickCount64 + 200; OnPropertyChanged(nameof(ProtectionDetails)); }
        OnPropertyChanged(nameof(ConnectionSummary));
        OnPropertyChanged(nameof(PortStatus));
        OnPropertyChanged(nameof(SelectedNodeConnected));
        ConnectCommand?.NotifyCanExecuteChanged(); DisconnectCommand?.NotifyCanExecuteChanged();
        EnableAllCommand?.NotifyCanExecuteChanged(); ExecuteAllCommand?.NotifyCanExecuteChanged();
        PauseCommand?.NotifyCanExecuteChanged(); StopCommand?.NotifyCanExecuteChanged(); EstopCommand?.NotifyCanExecuteChanged();
        RefreshPortsCommand?.NotifyCanExecuteChanged(); RefreshParametersCommand?.NotifyCanExecuteChanged();
        foreach (var a in Axes) a.Refresh();
        RefreshControlCommands();
    }

    /// <summary>
    /// Ui Dispatcher 每 50ms 调用一次，更新状态、曲线和日志，并在新鲜状态下续约意图。   
    /// </summary>
    public void UiTick()
    {
        var started = Stopwatch.GetTimestamp();
        uiTickIntervalMs = lastUiTickStamp == 0 ? 0 : Stopwatch.GetElapsedTime(lastUiTickStamp, started).TotalMilliseconds;
        lastUiTickStamp = started;
        try { UpdateUi(); }
        finally
        {
            uiTickDurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            maxUiTickDurationMs = Math.Max(maxUiTickDurationMs, uiTickDurationMs);
        }
    }

    private void UpdateUi()
    {
        if (!disposed && bridge.Running && bridge.Latest?.Connected != true && !Connected && !Busy && Environment.TickCount64 >= nextPortsAt)
            _ = RefreshPortsAsync();
        var state = bridge.Latest;
        // 只读连接事务等待自身应答确认新会话；提前到达的快照不抢占事务，也不续租。
        if (connectionTransitionEpoch == positionStartEpoch && state is not null && state.SessionId != session) return;
        Fresh = bridge.Running && state?.Mode == "hardware" && Protocol.IsFresh(state, DateTimeOffset.UtcNow);
        if (state == null)
        {
            Message = bridge.Error.Length > 0 ? bridge.Error : Message;
            ObserveMotorSafety(null);
            foreach (var axis in Axes) axis.Update(axis.State, false);
            ControlTick();
            RefreshCommands();
            return;
        }
        CurrentState = state;
        if (state.SessionId != session)
        {
            ++globalCancellationEpoch;
            ++positionStartEpoch;
            if (StartingPositionNode != 0 || preparingPosition || structuralNode != 0)
            { StartingPositionNode = structuralNode = 0; preparingPosition = Busy = false; }
            if (abnormalRecoveryNode != 0) { ++abnormalRecoveryToken; abnormalRecoveryNode = 0; Busy = false; }
            ClearLocalStream(); ClearAlignment();
            foreach (var axis in Axes) axis.ResetMapping();
            ResetLeaseSession();
            stoppingAxes.Clear(); stopFeedbackRevisions.Clear(); holdFeedbackRevisions.Clear(); axisOperationEpoch.Clear(); structuralNode = 0;
            session = state.SessionId; intentRevision = state.Revision;
            unownedStopRevision = -1;
            Curve.Clear(); parameterSnapshot = null;
            authorizationValid = true;
            RefreshParameterRows();
        }
        intentRevision = Math.Max(intentRevision, state.Revision);
        ObserveLocalStops(state);
        Connected = state.Connected;
        Active = state.Active;
        CompletePendingLease();
        if (state.Revision >= recoveryRevision) recoveryRevision = -1;
        if (state.Revision >= unownedStopRevision) unownedStopRevision = -1;
        ObserveMotorSafety(state);
        // 瞬断后已收到新预览，也须在本次续约之前撤销旧视觉流。
        HandleVisionInterruption();
        // 先续约，再处理曲线、日志、参数表及界面通知；续约仍只来自活着的 UI。
        var leaseDecision = disposed ? "窗口已关闭" : !StateFreshNow ? "UI 快照过期" : !Connected ? "未连接" :
            !authorizationValid ? "授权已撤销" : state.Fault.Length > 0 ? "故障锁存" :
            UiLeaseRevision(state) is null ? "等待意图版本反馈" : pendingLease is not null ? "等待租约应答" :
            "UI 提交续约";
        if (leaseDecision == "UI 提交续约") _ = LeaseAsync(state);
        uiLeaseHistory.Enqueue(JsonSerializer.SerializeToElement(new
        {
            observedAt = DateTimeOffset.UtcNow,
            state.SessionId,
            state.Revision,
            intentRevision,
            state.Active,
            fresh = StateFreshNow,
            leaseDecision,
            leaseSubmitted,
            leaseCompleted,
            pendingRevision = pendingLease?.Revision,
            replyCompleted = pendingLease?.Reply.IsCompleted,
            acknowledgedStreamRevision,
            streamLeaseHandoffPending,
            acknowledgedTargetRevision,
            targetLeaseHandoffPending,
            state.LeaseAgeMs,
            state.LeaseCount,
            state.LastLeaseRevision,
            stateAgeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.Time * 1000,
            uiTickIntervalMs,
            previousUiTickDurationMs = uiTickDurationMs
        }, Protocol.Json));
        while (uiLeaseHistory.Count > 16) uiLeaseHistory.Dequeue();
        if (firstUiProtection is null && state.Fault.Length > 0)
            firstUiProtection = JsonSerializer.SerializeToElement(new
            {
                observedAt = DateTimeOffset.UtcNow,
                state.SessionId,
                state.Revision,
                state.Fault,
                backendProtection = state.FirstProtection,
                recentUiTicks = uiLeaseHistory.ToArray(),
                recentTransport = SessionTransport(),
                note = "UI 观测时序；不将历史最大耗时认定为故障瞬间根因"
            }, Protocol.Json);
        Timing = $"周期 {state.PeriodMs:0.0}ms  /  UI 租约 50ms  /  revision {state.Revision}";
        if (Fresh && state.EstopRecovery is { Stage.Length: > 0 } recovery &&
            $"{state.SessionId}/{recovery.Revision}/{recovery.Stage}" is var notice && notice != lastRecoveryNotice)
        {
            lastRecoveryNotice = notice;
            Message = $"ID{recovery.Node} 急停清除：{recovery.Reason}";
        }
        if (Fresh && state.UnownedStop is { Stage.Length: > 0 } stopped &&
            $"{state.SessionId}/{stopped.Revision}/{stopped.Stage}" is var stopNotice && stopNotice != lastUnownedStopNotice)
        {
            lastUnownedStopNotice = stopNotice;
            Message = $"ID{stopped.Node} 人工停止：{stopped.Reason}";
        }
        if (state.Fault.Length > 0) Message = "故障锁存：" + ShortText(state.Fault);
        if (!Fresh && !Busy) Message = state.Mode != "hardware" ? "桥接协议不兼容：仅支持真机；请重启最新版上位机。运动授权已禁用。" : StaleBridgeMessage;
        if (Fresh && Message == StaleBridgeMessage)
            Message = !authorizationValid ? "桥接状态已恢复，但会话授权仍失效；请停止、断开并重新确认连接。" :
                Connected ? "桥接状态已恢复；运动须按本轴就绪提示显式操作。" : "桥接状态已恢复；当前未连接电机。";
        if (motorProtectionReason.Length > 0) Message =
            (Fresh && motorProtectionReason == "受控会话失联或 UI 反馈过期" ? "桥接状态已恢复，但会话授权仍失效；" : "") + ProtectionMessage;
        for (var i = 0; i < Math.Min(3, state.Axes.Length); i++) Axes[i].Update(state.Axes[i], Fresh, refreshCommands: false);
        axisSession = session;
        ControlTick();
        if (state.Time != lastTime)
        {
            lastTime = state.Time;
            foreach (var a in state.Axes)
            {
                var iq = a.Samples.TryGetValue("current", out var sample) && sample.Valid ? sample.Value : null;
                Curve.Add(new(state.Time, state.Mode, a.Node, Fresh ? a.Target : null, Fresh ? a.Position : null, Fresh ? a.Velocity : null, Fresh ? iq : null));
            }
            // 仅展示按 10Hz 重绘；安全消费、续约、20Hz 控制及原始曲线采样不降频。
            if (!CurvePaused && Environment.TickCount64 >= nextCurveAt)
            { nextCurveAt = Environment.TickCount64 + 100; CurveChanged?.Invoke(); }
            var previousLog = lastLog;
            foreach (var l in state.Logs.Where(l => l.Id > lastLog))
            {
                logLines.Add($"{DateTimeOffset.FromUnixTimeMilliseconds((long)(l.Time * 1000)).ToLocalTime():HH:mm:ss.fff}  {(l.Node > 0 ? "ID" + l.Node : "系统")}  {l.Text}");
                lastLog = l.Id;
            }
            if (logLines.Count > 200) logLines.RemoveRange(0, logLines.Count - 200);
            if (lastLog != previousLog) Logs = string.Join('\n', logLines.AsEnumerable().Reverse());
        }
        if (parameterSnapshot.HasValue && Environment.TickCount64 - parameterRefreshAt >= 200)
        {
            parameterRefreshAt = Environment.TickCount64;
            RefreshParameterRows();
        }
        RefreshCommands();
    }

    private long ConfirmedControlRevision(BridgeState state) => Math.Max(state.Revision, acknowledgedStreamRevision);

    private long? UiLeaseRevision(BridgeState state) => state.SessionId != session ? null :
        state.Revision == intentRevision ? intentRevision :
        state.Revision < intentRevision && acknowledgedStreamRevision == intentRevision && (StreamRunning || StartingPositionNode != 0 || preparingPosition) ? intentRevision :
        state.Revision < intentRevision && Active && acknowledgedTargetRevision == intentRevision ? intentRevision :
        state.Revision < intentRevision && Active && (structuralNode != 0 || StreamRunning && acknowledgedStreamRevision >= 0)
            ? ConfirmedControlRevision(state) : null;

    /// <summary>
    /// Attempts to renew the lease for the specified bridge session asynchronously.
    /// </summary>
    /// <remarks>If the lease operation fails due to a timeout or I/O error, the authorization state is
    /// invalidated and command availability is refreshed.</remarks>
    /// <param name="state">The current bridge session state containing the session identifier and revision to be used for the lease
    /// operation.</param>
    /// <returns>A task that represents the asynchronous lease operation.</returns>
    private async Task LeaseAsync(BridgeState state)
    {
        // 开流/目标应答的 UI 续约交接同样不能越过连接中断屏障。
        HandleVisionInterruption();
        var revision = UiLeaseRevision(state);
        if (disposed || !StateFreshNow || !Protocol.IsFresh(state, DateTimeOffset.UtcNow) || !Connected || !authorizationValid || state.Fault.Length > 0 ||
            state.SessionId != session || revision is null || pendingLease is not null ||
            bridge.Latest is not { } latest || latest.SessionId != session || !latest.Connected || latest.Fault.Length > 0 ||
            !Protocol.IsFresh(latest, DateTimeOffset.UtcNow) ||
            latest.Axes.Any(a => a.Enabled && AxisViewModel.MotorSafetyIssue(a, true).Length > 0)) return;
        var started = Stopwatch.GetTimestamp();
        Task<JsonElement?> reply;
        try { reply = bridge.SendAsync("lease", state.SessionId, revision.Value, timeoutMs: 150); }
        catch (Exception ex) { reply = Task.FromException<JsonElement?>(ex); }
        var request = new LeaseRequest(state, revision.Value, started, reply);
        pendingLease = request;
        if (revision == acknowledgedStreamRevision) streamLeaseHandoffPending = false;
        if (revision == acknowledgedTargetRevision) targetLeaseHandoffPending = false;
        ++leaseSubmitted;
        try { await reply; }
        catch (Exception) { /* 由 UI 收割结果，旧会话失败也必须被观察。 */ }
        finally { CompleteLease(request); }
    }

    private void CompletePendingLease()
    {
        if (pendingLease is { Reply.IsCompleted: true } request) CompleteLease(request);
    }

    private void CompleteLease(LeaseRequest request)
    {
        // 异步续接和下次 UI tick 均可收割；代次隔离且只执行一次。
        // 后台任务完成不自动发新租约，也不在后台修改 UI 或授权。
        // 旧会话或已关闭窗口的异步结果不能污染当前授权、诊断和待应答标记。
        if (disposed || request.State.SessionId != session || !ReferenceEquals(pendingLease, request)) return;
        pendingLease = null;
        var state = request.State;
        try { request.Reply.GetAwaiter().GetResult(); ++leaseCompleted; }
        catch (Exception ex)
        {
            // 意图抢占后的旧 revision 拒绝不是当前意图失败；真正通信超时仍须锁存。
            if (ex is InvalidOperationException && request.Revision != intentRevision) return;
            firstLeaseFailure ??= JsonSerializer.SerializeToElement(new
            {
                observedAt = DateTimeOffset.UtcNow,
                state.SessionId,
                revision = request.Revision,
                snapshotRevision = state.Revision,
                elapsedUntilUiObservedMs = Stopwatch.GetElapsedTime(request.Started).TotalMilliseconds,
                uiTickIntervalMs,
                uiTickDurationMs,
                maxUiTickDurationMs,
                sinceUiTickMs = lastUiTickStamp == 0 ? (double?)null : Stopwatch.GetElapsedTime(lastUiTickStamp).TotalMilliseconds,
                error = ex.Message,
                currentRequestTimeout = (ex as BridgeTimeoutException)?.Diagnostic,
                historicalFirstRequestTimeout = bridge.FirstTimeout,
                recentTransport = SessionTransport()
            }, Protocol.Json);
            Message = "续约失败：" + ShortText(ex.Message);
            if (ex is TimeoutException or IOException)
            {
                if (HasMotorExposure) _ = ForceMotorStopAsync("UI 续约通信异常：" + ex.Message,
                    ex is BridgeTimeoutException timeout ? $"UI 续约超时 · {timeout.Diagnostic.Stage}（{timeout.Diagnostic.LimitMs}ms）" :
                    "UI 续约通信异常；详细首因见诊断");
                // 有运动暴露时由同步进入的停机流程先冻结首因，再撤权；无暴露仍结束授权。
                authorizationValid = false;
            }
            RefreshCommands();
        }
        finally
        {
            if (streamLeaseHandoffPending)
                Dispatcher.UIThread.Post(RenewOpenedStreamLease, DispatcherPriority.Input);
            if (targetLeaseHandoffPending)
                Dispatcher.UIThread.Post(RenewAcceptedTargetLease, DispatcherPriority.Input);
        }
    }

    private void RenewAcceptedTargetLease()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (disposed || !targetLeaseHandoffPending || acknowledgedTargetRevision != intentRevision || !Active || StreamRunning) return;
        CompletePendingLease();
        // 只有 UI 消费成功目标应答后才可交接；后台快照不替代已消费快照，也不周期续租。
        if (!targetLeaseHandoffPending || bridge.Latest is not { } state || state.SessionId != session ||
            !state.Connected || state.Fault.Length > 0 || !Protocol.IsFresh(state, DateTimeOffset.UtcNow) ||
            state.Axes.Any(a => a.Enabled && AxisViewModel.MotorSafetyIssue(a, true).Length > 0)) return;
        _ = LeaseAsync(state);
    }

    private void ResetLeaseSession()
    {
        pendingLease = null;
        acknowledgedTargetRevision = -1;
        targetLeaseHandoffPending = false;
        firstLeaseFailure = null;
        firstPositionStartFailure = null;
        firstStreamSampleFailure = null;
        firstUiProtection = null;
        uiLeaseHistory.Clear();
        leaseSubmitted = leaseCompleted = 0;
        nextCurveAt = 0;
        recoveryRevision = -1;
        lastRecoveryNotice = "";
        motorProtectionReason = motorProtectionSummary = motorStopResult = "";
        motorProtectionNodes.Clear();
    }

    private bool HasMotorExposure => Active || StartingPositionNode != 0 || StreamRunning ||
        CurrentState?.Axes.Any(a => a.Enabled) == true || axisSession == session && Axes.Any(a => a.State.Enabled);
    internal string AxisProtectionReason(int node) => motorProtectionNodes.Contains(node) ? motorProtectionReason : "";
    internal string MotorStopResult => string.Join("；", new[] { motorStopResult }
        .Concat(CurrentState?.Axes.Select(StopFeedbackText) ?? []).Where(s => s.Length > 0));
    internal string StopFeedbackText(AxisState axis)
    {
        var state = CurrentState;
        if (state is null || state.SessionId != session ||
            !ReferenceEquals(axis, state.Axes.FirstOrDefault(a => a.Node == axis.Node))) return "";
        var history = axis.StopConfirmedAt is { } at && double.IsFinite(at) && at > 0 &&
            at <= state.Time + .1 && axis.StopConfirmedTxCount > 0;
        if (!history && axis.StopTxCount == 0 && axis.StopTxError.Length == 0) return "";
        var current = StateFreshNow && state.Connected && axis.Connected && axis.Valid;
        var sameStop = history && axis.StopConfirmedTxCount == axis.StopTxCount && axis.StopTxError.Length == 0;
        if (current && sameStop && !axis.Enabled && axis.Gate == false && axis.Phase == "停止输出已确认")
            return $"ID{axis.Node} 新鲜反馈已确认门极关闭（停止第{axis.StopTxCount}次）；无保持力矩，不代表机械静止，不恢复授权";
        var previous = history ? $"曾确认门极关闭（历史第{axis.StopConfirmedTxCount}次）；" : "尚无门极关闭确认；";
        var feedback = !current ? "当前反馈未知，不能确认已停机" : axis.Gate == true ? "当前门极开启，不能视作已停机" :
            !sameStop || axis.Phase is "停止待确认" or "停机未确认" ? "本次停止未确认，旧确认不能替代" :
            "当前门极／停止状态未核验，不能视作已停机";
        return $"ID{axis.Node} {previous}{feedback}" +
            (axis.StopTxError.Length > 0 ? "；本次停止发送失败：" + axis.StopTxError : "");
    }
    private bool IsClosedUnownedObserver(AxisState axis, bool fresh) =>
        fresh && axis.Connected && axis.Valid && !axis.Enabled && axis.Gate == false &&
        axis.Phase is not ("停止待确认" or "停机未确认") && axis.Node != StartingPositionNode &&
        !streamNodes.Contains(axis.Node) &&
        !(axisSession == session && Axes.Any(a => a.Node == axis.Node && a.State.Enabled));

    private void ObserveMotorSafety(BridgeState? state)
    {
        // 必须先撤权和取消旧目标，再允许本 tick 的续约或位置发送。
        var running = Active || state?.Active == true || StreamRunning || axisSession == session && Axes.Any(a => a.State.Enabled) ||
            state?.Axes.Any(a => a.Enabled) == true;
        if (!running || motorProtectionReason.Length > 0) return;
        var reason = !bridge.Running || state is null || !Fresh ? "受控会话失联或 UI 反馈过期" :
            !state.Connected ? "受控会话已断开，停机须核验" :
            state.Fault.Length > 0 ? "本地保护：" + state.Fault :
            !authorizationValid ? "会话运动授权已失效" :
            // 只读观察轴的残留故障不升级受控组停机；本轴异常显示及启动阻断仍保留。
            // 必须有新鲜门极关闭证据；当前/此前受控、流成员及启动中轴不可排除。
            state.Axes.Select(a => IsClosedUnownedObserver(a, Fresh) ? "" : AxisViewModel.MotorSafetyIssue(a, Fresh))
                .FirstOrDefault(r => r.Length > 0) ?? "";
        if (reason.Length == 0 && axisSession == session && Axes.Any(a => a.State.Enabled &&
            state?.Axes.Any(s => s.Node == a.Node && s.Connected) != true))
            reason = "此前受控轴失联，禁止继续发送";
        if (reason.Length > 0) _ = ForceMotorStopAsync(reason);
    }
    private async Task ForceMotorStopAsync(string reason, string? summary = null)
    {
        // 每会话自动提交一次；人工停止仍可重试，不自动清故障或恢复。
        if (disposed || session.Length == 0 || motorProtectionReason.Length > 0) return;
        // 在撤权、清流及发送急停之前冻结 UI 首因，避免随后 Fault=0x4 覆盖原触发证据。
        var evidence = bridge.Latest?.SessionId == session ? bridge.Latest : CurrentState;
        firstUiProtection ??= JsonSerializer.SerializeToElement(new
        {
            observedAt = DateTimeOffset.UtcNow,
            sessionId = session,
            revision = evidence?.Revision,
            reason,
            fault = evidence?.Fault,
            backendProtection = evidence?.FirstProtection,
            source = streamSource,
            generation = streamGeneration,
            streamNodes,
            startingPositionNode = StartingPositionNode,
            authorizationValidBeforeStop = authorizationValid,
            axes = evidence?.Axes.Select(a => new
            {
                a.Node, a.Connected, a.Valid, a.Enabled, a.Gate, a.Phase,
                a.Fault, a.Error, a.HeartbeatFaultFlags, a.AgeMs, a.FaultAgeMs, a.ErrorAgeMs,
                excludedIdleUnowned = IsClosedUnownedObserver(a, Protocol.IsFresh(evidence, DateTimeOffset.UtcNow))
            }).ToArray(),
            recentUiTicks = uiLeaseHistory.ToArray(),
            recentTransport = SessionTransport(),
            note = "自动安全停机提交前的本会话证据；后续急停反馈不覆盖首因，不证明现场原始CAN时序"
        }, Protocol.Json);
        motorProtectionReason = reason;
        motorProtectionSummary = summary ?? ProtectionFirstCause;
        foreach (var axis in Axes.Where(a => axisSession == session && (a.State.Connected || a.State.Enabled) || a.Node == StartingPositionNode))
            motorProtectionNodes.Add(axis.Node);
        foreach (var axis in CurrentState?.Axes.Where(a => a.Connected || a.Enabled) ?? [])
            motorProtectionNodes.Add(axis.Node);
        authorizationValid = false;
        ++globalCancellationEpoch;
        ++positionStartEpoch;
        if (StartingPositionNode != 0 || preparingPosition || structuralNode != 0)
        {
            PositionStartStatus = "启动或继续因异常取消；不会自动继续";
            StartingPositionNode = structuralNode = 0; preparingPosition = Busy = false;
        }
        holdFeedbackRevisions.Clear();
        ClearLocalStream(); ClearAlignment();
        recoveryRevision = unownedStopRevision = -1;
        foreach (var axis in Axes) axis.ResetMapping();
        var requestedSession = session;
        var revision = ++intentRevision;
        motorStopResult = "强制停止请求已提交；等待新门极反馈，尚未确认停机";
        Message = ProtectionMessage;
        try
        {
            await bridge.SendAsync("safety_stop", requestedSession, revision, new { reason = reason[..Math.Min(reason.Length, 512)] }, timeoutMs: 300);
            if (disposed || requestedSession != session || revision != intentRevision) return;
            motorStopResult = "强制停止请求已接受；须核验新门极反馈，不等于已停机";
        }
        catch (Exception ex)
        {
            if (disposed || requestedSession != session || revision != intentRevision) return;
            motorStopResult = "强制停止未完成：" + ex.Message + "；停机未确认，检查硬件急停与机械支撑";
        }
        if (requestedSession != session) return;
        Message = ProtectionMessage;
        RefreshCommands();
    }

    /// <summary>
    /// 连接异常时不会抛出异常；连接成功后会刷新参数快照。
    /// </summary>
    /// <returns></returns>
    private async Task ConnectAsync()
    {
        if (!CanChangeConnection) return;
        if (string.IsNullOrWhiteSpace(Port)) { Message = "请先选择 USB2CAN 串口；未打开任何设备。"; return; }
        Busy = true;
        try
        {
            if (!bridge.Running) bridge.Start();
            await OpenMotorSessionAsync();
        }
        catch (Exception ex) { Message = "连接失败：" + ex.Message; }
        finally { Busy = false; }
    }

    private async Task OpenMotorSessionAsync()
    {
        var epoch = positionStartEpoch;
        Message = "正在逐台识别 ID1/2/3 并只读获取参数；缺席节点跳过，不会启动电机。";
        connectionTransitionEpoch = epoch;
        JsonElement? result;
        try { result = await bridge.SendAsync("connect", session, 0, new { mode = "hardware", port = Port }, 120000); }
        finally { if (connectionTransitionEpoch == epoch) connectionTransitionEpoch = -1; }
        if (disposed || epoch != positionStartEpoch) throw new OperationCanceledException("连接已取消");
        var s = result?.Deserialize<BridgeState>(Protocol.Json);
        if (s is not { Mode: "hardware", Connected: true })
            throw new IOException("未收到有效真机会话；请重启最新版上位机");
        ClearLocalStream(); ClearAlignment();
        foreach (var axis in Axes) { axis.ResetMapping(); axis.Draft = "0"; }
        if (session != s.SessionId) ResetLeaseSession();
        session = s.SessionId; intentRevision = s.Revision; Connected = true;
        authorizationValid = true; Curve.Clear(); parameterSnapshot = null;
        CurrentState = s;
        Fresh = StateFreshNow; Active = s.Active;
        foreach (var axis in Axes)
            axis.Update(s.Axes.First(a => a.Node == axis.Node), Fresh, refreshCommands: false);
        axisSession = session;
        if (!SelectedNodeConnected && s.Axes.FirstOrDefault(a => a.Connected) is { } first)
            SelectedNodeIndex = first.Node - 1;
        var nodes = s.Axes.Where(a => a.Connected).Select(a => $"ID{a.Node}");
        Message = $"电机已连接：{string.Join("、", nodes)}。固定空载模式：点击本轴“启动位置环” → 拖动滑条；连接本身不启动。";
        await LoadParametersAsync();
    }

    /// <summary>
    /// Sends an intent action to the remote session asynchronously, optionally including additional data and urgency.
    /// </summary>
    /// <remarks>If <paramref name="urgent"/> is <see langword="false"/>, the method will not proceed if the
    /// session is busy or authorization is invalid. When <paramref name="urgent"/> is <see langword="true"/>, the
    /// action is sent regardless of session state. The method updates the session message to reflect the result or any
    /// errors encountered.</remarks>
    /// <param name="action">The name of the action to perform. This determines the operation requested on the remote session.</param>
    /// <param name="data">An optional object containing additional data to accompany the action. May be null if no extra data is required.</param>
    /// <param name="urgent">Indicates whether the intent should bypass busy and authorization checks. Set to <see langword="true"/> to force
    /// the action even if the session is busy or not authorized; otherwise, <see langword="false"/>.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task IntentAsync(string action, object? data, bool urgent = false)
    {
        if (action is "stop" or "stop_unowned" or "estop" or "disconnect" or "pause" or "hold")
        {
            ++globalCancellationEpoch;
            ++positionStartEpoch;
            if (abnormalRecoveryNode != 0)
            {
                ++abnormalRecoveryToken;
                abnormalRecoveryNode = 0;
                Busy = false;
            }
            if (StartingPositionNode != 0 || preparingPosition || structuralNode != 0)
            {
                PositionStartStatus = "启动或继续已取消；不会自动继续";
                StartingPositionNode = structuralNode = 0; preparingPosition = Busy = false;
            }
            ClearLocalStream();
            recoveryRevision = unownedStopRevision = -1;
            foreach (var axis in Axes) axis.UnownedStopConfirmed = false;
            if (action is not ("pause" or "hold")) { ClearAlignment(); holdFeedbackRevisions.Clear(); }
            if (action == "hold") foreach (var axis in Axes) axis.Draft = "0";
        }
        if (action is "reference" or "settings" or "prepare_position")
        {
            var scope = JsonSerializer.SerializeToElement(data, Protocol.Json);
            if (scope.ValueKind == JsonValueKind.Object && scope.TryGetProperty("nodes", out var ns))
                foreach (var n in ns.EnumerateArray()) MappingChanged(Axes[n.GetInt32() - 1]);
        }
        if (Busy && !urgent) return;
        if (!urgent && !CanAuthorize) { Message = "会话授权无效；请停止、断开并重新确认连接。"; return; }
        if (!urgent)
        {
            var scope = JsonSerializer.SerializeToElement(data, Protocol.Json);
            structuralNode = scope.ValueKind == JsonValueKind.Object && scope.TryGetProperty("nodes", out var ns) && ns.GetArrayLength() == 1
                ? ns[0].GetInt32() : 0;
            Busy = true;
        }
        if (action == "prepare_position") preparingPosition = true;
        var requestedSession = session;
        var requestedEpoch = positionStartEpoch;
        var requestedNode = action == "hold" ? 0 : structuralNode;
        var requestedAxisEpoch = requestedNode == 0 ? 0 : AxisEpoch(requestedNode);
        var revision = ++intentRevision;
        if (action == "hold") foreach (var axis in Axes) holdFeedbackRevisions[axis.Node] = revision;
        if (action == "stop_unowned")
        {
            unownedStopRevision = revision;
            foreach (var axis in Axes) { axis.ResetMapping(); axis.Draft = "0"; }
        }

        // 如果是清除急停，则增加全局取消代次，更新恢复修订号，并清除本地流和对齐信息，同时重置每个轴的映射和草稿值。
        if (action == "clear_estop")
        {
            ++globalCancellationEpoch;
            recoveryRevision = revision;
            ClearLocalStream(); ClearAlignment();
            foreach (var axis in Axes)
            {
                axis.ResetMapping(); axis.Draft = "0";
            }
        }
        try
        {
            var reply = await bridge.SendAsync(action, session, revision, data, timeoutMs: action == "prepare_position" ? 5000 : 1000);
            if (requestedSession != session || requestedEpoch != positionStartEpoch ||
                requestedNode != 0 && requestedAxisEpoch != AxisEpoch(requestedNode)) return;
            if (action == "hold") AcceptHoldReply(reply, revision);
            else AcceptStructureReply(reply, revision);
            if (action == "target" && revision == intentRevision)
            {
                acknowledgedTargetRevision = revision;
                targetLeaseHandoffPending = true;
                RenewAcceptedTargetLease();
            }
            if (action == "prepare_position") await LoadParametersAsync();
            if (requestedSession != session || revision != intentRevision) return;
            Message = action switch
            {
                "stop" when reply is { ValueKind: JsonValueKind.Object } result &&
                    result.TryGetProperty("message", out var stopMessage) && stopMessage.ValueKind == JsonValueKind.String
                    => stopMessage.GetString()!,
                "stop" => "停止请求已接受；发送与门极关闭须看新鲜反馈，未接管轴仍须人工确认。",
                "hold" => "全部停止转动已接受：减速保持力矩，不关闭电机、不发送停止帧；等待实测静止，点击本轴“继续”恢复新控制，不执行旧目标。", 
                "stop_unowned" when StateFreshNow && CurrentState?.UnownedStop is { Stage.Length: > 0 } stopped && stopped.Revision == revision
                    => $"ID{stopped.Node} 人工停止：{stopped.Reason}",
                "stop_unowned" => "人工停止请求已接受，等待新门极/故障/状态反馈；不等于停机已确认，不会自动启动。",
                "clear_estop" when StateFreshNow && CurrentState?.EstopRecovery is { Stage.Length: > 0 } recovery && recovery.Revision == revision
                    => $"ID{recovery.Node} 急停清除：{recovery.Reason}",
                "clear_estop" => "急停清除请求已接受，等待发送前核对及发送后反馈；尚未确认清除成功，不会自动使能。",
                "prepare_position" => "位置准备读回成功：直接输入 + 速度限制，仅 RAM、未写 Flash、未使能。等待静止后重新记录参考，再使能并下发角度。",
                "reference" => "软件参考已记录（未机械置零）。固定空载模式：点击使能本轴。",
                "enable" => "使能请求已接受，等待门极与位置控制反馈；就绪后可下发目标角度。",
                "target" => "目标已交给轨迹调度器；查看本轴 CAN TX 0x01 确认实际写入转换器，是否到位以新鲜反馈为准。",
                _ => $"已接受：{action}。实际状态以新鲜反馈为准。"
            };
        }
        catch (Exception ex)
        {
            if (requestedSession != session || requestedEpoch != positionStartEpoch ||
                requestedNode != 0 && requestedAxisEpoch != AxisEpoch(requestedNode)) return;
            Message = "操作未完成：" + ex.Message;
            if (action == "clear_estop") recoveryRevision = -1;
            if (action == "stop_unowned") unownedStopRevision = -1;
            if (action == "hold") holdFeedbackRevisions.Clear();
            if (ex is TimeoutException or IOException)
            {
                var stopping = ForceMotorStopAsync("操作通信异常：" + ex.Message);
                authorizationValid = false;
                await stopping;
            }
            // 拒绝后以已确认状态为准；不重试使能或运动。
            if (motorProtectionReason.Length == 0 && requestedSession == session && bridge.Latest is { } s && s.SessionId == session)
                intentRevision = ConfirmedControlRevision(s);
        }
        finally
        {
            if (requestedSession == session && requestedEpoch == positionStartEpoch &&
                (requestedNode == 0 || requestedAxisEpoch == AxisEpoch(requestedNode)))
            {
                if (action == "prepare_position") preparingPosition = false;
                if (!urgent) { Busy = false; structuralNode = 0; }
                RefreshCommands();
            }
            else if (requestedSession == session && motorProtectionReason.Length > 0) Busy = false;
        }
    }
    public async Task ExecuteAsync(AxisViewModel[] axes)
    {
        if (axes.Length == 0) { Message = "未选择电机"; return; }
        if (axes.Any(a => !a.CanExecute))
        {
            Message = "未下发：" + string.Join("；", axes.Where(a => !a.CanExecute).Select(a => $"ID{a.Node}：{a.ExecuteBlockReason}"));
            return;
        }
        if (RealTimeControl && axes.Length == 1) { QueueManualTarget(axes[0]); return; }
        if (StreamRunning) { Message = "请先暂停实时流再提交离散目标"; return; }
        await IntentAsync("target", new { nodes = axes.Select(a => a.Node).ToArray(), targets = axes.ToDictionary(a => a.Node.ToString(), a => a.DraftValue) });
    }
    private async Task RefreshPortsAsync()
    {
        if (disposed || portsPending || Connected || Busy || !bridge.Running) return;
        portsPending = true;
        RefreshCommands();
        try
        {
            var result = await bridge.SendAsync("ports", session, intentRevision);
            if (disposed || Connected || Busy) return;
            var list = result?.Deserialize<PortInfo[]>(Protocol.Json) ?? [];
            var previous = Port;
            updatingPorts = true;
            Ports.Clear();
            foreach (var p in list) Ports.Add(p);
            SelectedPort = list.FirstOrDefault(p => p.Device == previous);
            if (SelectedPort is null && list.Count(p => p.Candidate) == 1)
                SelectedPort = list.Single(p => p.Candidate);
            Port = SelectedPort?.Device ?? "";
            var count = list.Count(p => p.Candidate);
            PortEnumerationStatus = $"{list.Length} 个串口 / {count} 个 USB2CAN 候选 · " +
                (count > 1 && SelectedPort is null ? "请选择设备；" : "") +
                "仅枚举，未打开；点击连接电机后核实协议与在线节点";
        }
        catch (Exception ex) { PortEnumerationStatus = "端口枚举失败：" + ex.Message; }
        finally
        {
            updatingPorts = false;
            portsPending = false;
            nextPortsAt = Environment.TickCount64 + 2000;
            RefreshCommands();
        }
    }

    /// <summary>
    /// 载人参数快照，刷新参数表格。连接异常时不会抛出异常；连接成功后会刷新参数快照。
    /// </summary>
    /// <returns></returns>
    public async Task LoadParametersAsync()
    {
        var node = SelectedNodeIndex + 1;
        var requestedSession = session;
        if (!SelectedNodeConnected) { parameterSnapshot = null; RefreshParameterRows(); return; }
        var key = (requestedSession, node);
        if (!parameterRequests.Add(key)) return;
        try
        {
            // 参数页重复选择只保留同会话同轴的一次只读请求，不能挤占 UI 租约。
            var result = await bridge.SendAsync("parameters", session, intentRevision, new { node });
            if (SelectedNodeIndex + 1 != node || requestedSession != session || !SelectedNodeConnected) return;
            parameterSnapshot = result;
            RefreshParameterRows();
        }
        catch (Exception ex)
        {
            if (requestedSession == session && SelectedNodeIndex + 1 == node)
                Message = "读取参数快照失败：" + ex.Message;
        }
        finally { parameterRequests.Remove(key); }
    }

    /// <summary>
    /// 刷新参数行集合以反映当前配置文件和参数快照的状态。此方法会清空现有参数列表，并根据最新的参考配置和快照数据重新填充参数行。适用于参数分组筛选和在线/离线状态下的参数同步。
    /// </summary>
    /// <remarks>如果参数快照不可用或参考配置文件读取失败，参数列表将被清空并显示错误信息。方法会根据当前连接状态和参数分组动态调整参数来源和显示内容。</remarks>
    private void RefreshParameterRows()
    {
        try
        {
            var next = new List<ParameterRow>();
            var rows = referenceRows.Value;
            var rowNames = rows.Select(r => r.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
            var units = new Dictionary<string, string>(StringComparer.Ordinal);
            if (parameterSnapshot?.TryGetProperty("descriptor", out var descriptors) == true)
                foreach (var ep in descriptors.EnumerateArray())
                    if (ep.TryGetProperty("unit", out var unit)) units[ep.GetProperty("name").GetString()!] = unit.GetString() ?? "固件原值";
            var values = parameterSnapshot?.GetProperty("values");
            var source = !parameterSnapshot.HasValue ? "未读取" : !SelectedNodeConnected ? "历史缓存（未连接）" : parameterSnapshot.Value.GetProperty("source").GetString() == "hardware" ? "实机连接缓存" : "来源不兼容";
            string Reading(string name, string value)
            {
                if (parameterSnapshot?.TryGetProperty("readings", out var readings) == true && readings.TryGetProperty(name, out var r))
                {
                    var expired = name.StartsWith("telemetry.", StringComparison.Ordinal) &&
                        (!r.TryGetProperty("acquiredAt", out var at) || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - at.GetDouble() > .2);
                    if (!SelectedNodeConnected || !StateFreshNow || !r.GetProperty("valid").GetBoolean() || expired) return "过期 / 未知";
                }
                return parameterSnapshot?.GetProperty("source").GetString() == "hardware" ? value : "—";
            }
            string Acquisition(string name)
            {
                if (parameterSnapshot?.TryGetProperty("readings", out var readings) == true && readings.TryGetProperty(name, out var r) &&
                    r.TryGetProperty("acquiredAt", out var at) && at.GetDouble() > 0)
                    return " / 采集 " + DateTimeOffset.FromUnixTimeMilliseconds((long)(at.GetDouble() * 1000)).ToLocalTime().ToString("HH:mm:ss.fff");
                return "";
            }
            foreach (var row in rows.Where(r => ParameterGroup == 2 || r.GetProperty("group").GetString() == ParameterGroups[ParameterGroup]))
            {
                var name = row.GetProperty("name").GetString()!;
                var baseline = row.GetProperty("value").ToString();
                var online = values.HasValue && values.Value.TryGetProperty(name, out var value) ? value.ToString() : "—";
                next.Add(new(name, row.GetProperty("label").GetString()!, baseline, Reading(name, online), row.GetProperty("unit").GetString()!, $"{source} / {(online == "—" ? "无对应读回值" : online == baseline ? "一致" : "有差异")}" + Acquisition(name)));
            }
            if (ParameterGroup == 2 && values.HasValue)
                foreach (var p in values.Value.EnumerateObject().Where(p => !rowNames.Contains(p.Name)))
                    next.Add(new(p.Name, p.Name, "—", Reading(p.Name, p.Value.ToString()), units.GetValueOrDefault(p.Name, "固件原值"), source + " / 只读" + Acquisition(p.Name)));
            for (var i = 0; i < next.Count; i++)
                if (i >= Parameters.Count) Parameters.Add(next[i]);
                else if (Parameters[i] != next[i]) Parameters[i] = next[i];
            while (Parameters.Count > next.Count) Parameters.RemoveAt(Parameters.Count - 1);
        }
        catch (Exception ex) { Message = "参考配置读取失败：" + ex.Message; }
    }
    public string ExportParameters()
    {
        var captured = DateTimeOffset.Now;
        var state = CurrentState;
        var latest = bridge.Latest;
        var running = bridge.Running;
        var freshAtCapture = running && state?.Mode == "hardware" && Protocol.IsFresh(state, captured);
        var axis = state?.Axes.FirstOrDefault(a => a.Node == SelectedNodeIndex + 1);
        var coherent = ReferenceEquals(axis, SelectedAxis.State);
        var sameSession = state is not null && latest?.SessionId == state.SessionId;
        double? Age(BridgeState? s) => s is null ? null : captured.ToUnixTimeMilliseconds() - s.Time * 1000;
        var block = !freshAtCapture ? "反馈过期或未知，等待新鲜反馈" :
            !coherent ? "界面轴状态尚未同步，禁止使用混合快照" : "";
        string Reason(string reason) => block.Length > 0 ? block : reason;
        var reasons = SelectedAxis.BlockReasons(freshAtCapture, freshAtCapture && Connected && authorizationValid && state?.Fault.Length == 0);
        return JsonSerializer.Serialize(new
        {
            capturedAt = captured,
            mode = state?.Mode,
            node = SelectedNodeIndex + 1,
            connected = freshAtCapture && state?.Connected == true && axis?.Connected == true,
            snapshot = parameterSnapshot,
            diagnostics = new
            {
                stateTime = state?.Time,
                fresh = freshAtCapture,
                stateSource = "UI 已消费快照；导出不续租、不发令，后台最新快照单列",
                stateAgeMs = Age(state),
                axisUsable = freshAtCapture && coherent && axis?.Valid == true,
                sessionId = state?.SessionId,
                revision = state?.Revision,
                sessionFault = state?.Fault,
                firstProtection = state?.FirstProtection,
                protectionFirstCause = ProtectionFirstCause,
                protectionCurrentFeedback = ProtectionCurrentFeedback,
                protectionNextStep = ProtectionNextStep,
                health = new { visionCaptureAgeMs = VisionCaptureAgeMs, desktopSnapshotAgeMs = DesktopSnapshotAgeMs,
                    backendAcceptedLeaseAgeMs = BackendAcceptedLeaseAgeMs, controlFlow = CurrentControlFlowState },
                confirmed = Confirmed,
                benchMode = "fixed_unloaded",
                confirmationSource = "用户已确认固定空载台架；非自动设备验证",
                startingPositionNode = StartingPositionNode,
                positionStartStatus = PositionStartStatus,
                firstPositionStartFailure,
                firstStreamSampleFailure,
                stopMotorAvailable = SelectedAxis.StopMotorCommand.CanExecute(null),
                stopMotorHint = SelectedAxis.StopMotorHint,
                stopButtonText = SelectedAxis.StopButtonText,
                abnormalReason = SelectedAxis.AbnormalReason,
                motorProtectionReason,
                motorStopResult = MotorStopResult,
                motorStopRequestResult = motorStopResult,
                motorProtectionNodes = motorProtectionNodes.Order().ToArray(),
                connectionRecoveryHint = ConnectionRecoveryHint,
                canRestoreConnection = CanRestoreConnection,
                startPositionBlockReason = Reason(SelectedAxis.StartPositionBlockReason),
                response = new
                {
                    rpmDraft = SelectedAxis.RpmText,
                    accelerationDraft = SelectedAxis.AccelerationText,
                    draftIssue = SelectedAxis.ResponseDraftIssue,
                    applyBlockReason = Reason(SelectedAxis.ResponseApplyBlockReason),
                    stepDeg = double.IsFinite(StepSize) ? (double?)StepSize : null
                },
                controlSource = IsManual ? "manual" : "vision",
                axis,
                axes = state?.Axes,
                axisStartBlocks = Axes.Select(a => new
                {
                    node = a.Node,
                    coherent = ReferenceEquals(state?.Axes.FirstOrDefault(s => s.Node == a.Node), a.State),
                    startPositionBlockReason = !freshAtCapture ? "反馈过期或未知，等待新鲜反馈" :
                        !ReferenceEquals(state?.Axes.FirstOrDefault(s => s.Node == a.Node), a.State)
                            ? "界面轴状态尚未同步，禁止使用混合快照" : a.StartPositionBlockReason
                }).ToArray(),
                realTimeControl = RealTimeControl,
                realTimeBlockReason = Reason(RealTimeBlockReason),
                estopRecovery = state?.EstopRecovery,
                recoveryPending = RecoveryPending,
                unownedStop = state?.UnownedStop,
                unownedStopPending = UnownedStopPending,
                unownedStopBlockReason = Reason(SelectedAxis.UnownedStopBlockReason),
                estopClearBlockReason = Reason(SelectedAxis.EstopClearBlockReason),
                prepareBlockReason = Reason(reasons.Prepare),
                referenceBlockReason = Reason(reasons.Reference),
                enableBlockReason = Reason(reasons.Enable),
                executeBlockReason = Reason(reasons.Execute),
                transport = new
                {
                    running,
                    latestStateTime = latest?.Time,
                    latestStateAgeMs = Age(latest),
                    latestFresh = running && latest?.Mode == "hardware" && Protocol.IsFresh(latest, captured),
                    latestSessionId = latest?.SessionId,
                    latestRevision = latest?.Revision,
                    sameSession,
                    authorizationValid,
                    intentRevision,
                    leasePending = pendingLease is not null,
                    preparingPosition,
                    acknowledgedStreamRevision,
                    streamLeaseHandoffPending,
                    acknowledgedTargetRevision,
                    targetLeaseHandoffPending,
                    abnormalRecoveryNode,
                    connectionTransitionEpoch,
                    uiConsumptionLagMs = sameSession ? Math.Max(0, (latest!.Time - state!.Time) * 1000) : (double?)null,
                    uiTickIntervalMs,
                    uiTickDurationMs,
                    maxUiTickDurationMs,
                    firstRequestTimeout = bridge.FirstTimeout,
                    recentTransport = SessionTransport(),
                    firstLeaseFailure,
                    firstUiProtection,
                    visionChannel = new { vision.DesiredConnected, vision.Connected, vision.ChannelInterruption, vision.FirstChannelBreak },
                    leaseSubmitted,
                    leaseCompleted,
                    recentUiTicks = uiLeaseHistory.ToArray(),
                    leaseReplyCompleted = pendingLease?.Reply.IsCompleted,
                    backendLeaseAgeMs = state?.LeaseAgeMs,
                    backendLeaseCount = state?.LeaseCount,
                    backendLastLeaseRevision = state?.LastLeaseRevision,
                    sinceUiTickMs = lastUiTickStamp == 0 ? (double?)null : Stopwatch.GetElapsedTime(lastUiTickStamp).TotalMilliseconds
                }
            },
            units = "描述符固件单位；axis 是 stateTime 时的历史快照，只有 axisUsable=true 才是有效运动反馈；角度 °、速度 °/s",
            logs = logLines.ToArray()
        }, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true });
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); ClearLocalStream();
        vision.Changed -= WakeControlTargets;
        await bridge.DisposeAsync();
        await vision.DisposeAsync();
    }
}
