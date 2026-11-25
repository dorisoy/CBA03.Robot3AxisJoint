using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HostComputer.Core;
using System.Globalization;

namespace HostComputer.App.ViewModels;

public partial class AxisViewModel : ObservableObject
{
    public int Node { get; }
    public string Title => $"J{Node} → ID{Node}  /  台架电机";
    public IBrush Accent { get; }
    public AxisState State { get; private set; }
    private readonly MainViewModel owner;
    [ObservableProperty] private string draft = "0";
    [ObservableProperty] private double pending;
    [ObservableProperty] private double? actual;
    [ObservableProperty] private double? target;
    [ObservableProperty] private double? visionPreview;
    [ObservableProperty] private string visionText = "相机解算 J角：—（无新鲜追踪）";
    [ObservableProperty] private string positionText = "—";
    [ObservableProperty] private string status = "未连接";
    [ObservableProperty] private string detail = "等待连接电机；连接后须显式使能才能下发角度";
    [ObservableProperty] private string telemetry = "— / 等待实机遥测";
    [ObservableProperty] private string lowText = Protocol.JointMinimumDeg.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string highText = Protocol.JointMaximumDeg.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string rpmText = "100";
    [ObservableProperty] private string ampsText = "0.5";
    [ObservableProperty] private string accelerationText = Protocol.FastAcceleration.ToString(CultureInfo.InvariantCulture);
    private bool responseDirty, syncingResponse;
    partial void OnRpmTextChanged(string value) => ResponseEdited();
    partial void OnAccelerationTextChanged(string value) => ResponseEdited();
    private void ResponseEdited()
    {
        if (!syncingResponse) responseDirty = true;
        RefreshResponse();
    }
    public bool TryGetResponse(out double rpm, out double acceleration)
    {
        var rpmValid = double.TryParse(RpmText, NumberStyles.Float, CultureInfo.CurrentCulture, out rpm);
        var accelerationValid = double.TryParse(AccelerationText, NumberStyles.Float, CultureInfo.CurrentCulture, out acceleration);
        return rpmValid && accelerationValid && Protocol.ValidResponse(rpm, acceleration);
    }
    public string ResponseDraftIssue => TryGetResponse(out _, out _) ? "" :
        "速度须为 1～32767 的整数 RPM，加减速度须为 (0, 360]°/s²；未钳位或应用";
    public bool CanEditResponse => !owner.IsAxisBusy(Node) && !State.Enabled && State.Gate != true && State.Phase is not ("停止待确认" or "停机未确认");
    public string ResponseApplyBlockReason => FirstReason(ResponseDraftIssue,
        !CanEditResponse ? "须停止本轴；暂停保持仍使能，不能调整响应" : "",
        CurrentCommonReason, State.Gate != false ? "等待本轴门极关闭" : "",
        State.HasDriveFault ? "驱动故障须先处理" : "",
        !State.Reference.HasValue ? "尚无参考：点击启动位置环时应用所选响应" : "");
    public bool CanApplyResponse => ResponseApplyBlockReason.Length == 0;
    public double SliderStep => owner.StepSize;
    public string ResponseHint
    {
        get
        {
            var valid = TryGetResponse(out var rpm, out var acceleration);
            var draftText = valid ? $"所选 {rpm:G} RPM = {rpm * 6:G}°/s · {acceleration:G}°/s²" : ResponseDraftIssue;
            var current = owner.StateFreshNow && State.Connected && State.Valid
                ? $"后端已生效 {State.Rpm:G} RPM · {State.Acceleration:G}°/s²"
                : "后端生效值未知（未连接或反馈过期）";
            var matching = valid && rpm == State.Rpm && acceleration == State.Acceleration;
            return draftText + "\n" + current + (valid && !matching ? "；草稿尚未生效" : "") + "\n" +
                (CanEditResponse ? "启动时应用本轴所选值；修改本身不发令，不改电流或行程。" :
                    "运行/保持中不能调整；须停止后修改并显式应用或重新启动。");
        }
    }
    private void RefreshResponse()
    {
        OnPropertyChanged(nameof(CanEditResponse)); OnPropertyChanged(nameof(CanApplyResponse));
        OnPropertyChanged(nameof(ResponseDraftIssue)); OnPropertyChanged(nameof(ResponseApplyBlockReason));
        OnPropertyChanged(nameof(ResponseHint)); OnPropertyChanged(nameof(SliderStep));
        OnPropertyChanged(nameof(CanStartPosition)); OnPropertyChanged(nameof(StartPositionBlockReason));
        OnPropertyChanged(nameof(VisionStartHint));
        StartPositionCommand?.NotifyCanExecuteChanged(); ApplyResponseCommand?.NotifyCanExecuteChanged();
    }
    [ObservableProperty] private bool reverse;
    [ObservableProperty] private bool visionSelected;
    [ObservableProperty] private bool directionConfirmed;
    [ObservableProperty] private bool rangeConfirmed;
    [ObservableProperty] private bool homeConfirmed;
    [ObservableProperty] private bool referenceConfirmed;
    [ObservableProperty] private bool unownedStopConfirmed;
    partial void OnUnownedStopConfirmedChanged(bool value) => owner.RefreshCommands();

    public bool MappingConfirmed => DirectionConfirmed && RangeConfirmed && HomeConfirmed && ReferenceConfirmed;
    partial void OnVisionSelectedChanged(bool value) { owner.MappingChanged(this); owner.RefreshCommands(); }
    partial void OnDirectionConfirmedChanged(bool value) { owner.MappingChanged(this); owner.RefreshCommands(); }
    partial void OnRangeConfirmedChanged(bool value) { owner.MappingChanged(this); owner.RefreshCommands(); }
    partial void OnHomeConfirmedChanged(bool value) { owner.MappingChanged(this); owner.RefreshCommands(); }
    partial void OnReferenceConfirmedChanged(bool value) { owner.MappingChanged(this); owner.RefreshCommands(); }
    public void ResetMapping()
    {
        DirectionConfirmed = RangeConfirmed = HomeConfirmed = ReferenceConfirmed = false;
        UnownedStopConfirmed = false;
    }

    public bool CanConfirmMapping => owner.CanAuthorize && !State.Enabled && !owner.IsAxisBusy(Node) && State.Connected;
    private string CommonReason(bool fresh, bool authorized) => !State.Connected ? "本轴未连接" :
        !fresh || !State.Valid ? "反馈过期或未知，等待新鲜反馈" :
        owner.CurrentState?.Fault is { Length: > 0 } fault ? "故障锁存：" + fault :
        owner.RecoveryPending ? "急停清除核验中，不允许其他控制" :
        owner.UnownedStopPending ? "人工停止核验中，不允许其他控制" :
        !authorized ? "会话授权已失效，须停止、断开重连" :
        owner.IsAxisBusy(Node) ? "本轴或结构操作尚未完成" : "";


    private string HardwareBlockReason => string.Join("；", State.Issues
        .Concat(State.Fault != 0 ? new[] { $"驱动 Fault=0x{State.Fault:X8}" + (State.Fault == 4 ? "（CAN 急停锁存）" : "") } : [])
        .Concat(State.Error != 0 ? new[] { $"协议 Error=0x{State.Error:X2}" } : [])
        .Concat(State.HeartbeatFaultFlags != 0 ? new[] { $"心跳故障分类待核验=0x{State.HeartbeatFaultFlags:X2}（不是 Error 错误码）" } : []));


    private string GateBlockReason => State.Gate == false ? "" :
        State.Gate == true ? "门极已开启，不自动接管外部使能；请核对控制来源" : "门极状态未知";


    private string MotionBlockReason => State.Stationary ? "" :
        State.StationaryReason.Length > 0 ? State.StationaryReason : "等待后端连续静止判定";

    private static string FirstReason(params string[] reasons) => reasons.FirstOrDefault(r => r.Length > 0) ?? "";


    private string PrepareReason(string common) => FirstReason(common,
        !owner.IsPositionSource ? "请选择位置控制或AI视觉随动后准备" : "",
        State.Enabled ? "须先停止本轴输出" : "",
        State.Gate != false || State.HasDriveFault || State.Phase is "停止待确认" or "停机未确认"
            ? "本轴须无故障、门极关闭且停止已确认" : "",
        MotionBlockReason,
        string.Join("；", State.PrepareIssues));


    public string EstopClearBlockReason => owner.AbnormalRecoveryBlockReason(this);
    public bool CanClearEstop => IsAbnormal && EstopClearBlockReason.Length == 0;
    public string EstopRecoveryText => owner.CurrentState?.EstopRecovery is { Stage.Length: > 0 } r && r.Node == Node
        ? $"{(r.Pending ? "清除进度" : "上次恢复结果（非当前使能状态）")}：{r.Reason}" + (!owner.StateFreshNow || !State.Valid ? "（历史信息，当前反馈未知）" : "")
        : "仅处理单独 CAN 急停锁存；其他故障不清除。清除后不使能、不恢复旧目标，须重新记录参考。";
    public bool CanPrepare => PrepareBlockReason.Length == 0;
    private string UnownedStopCommonReason => FirstReason(
        !State.Connected || !owner.Connected ? "本轴未连接" : "",
        !owner.StateFreshNow || !State.Valid ? "反馈过期或未知，禁止提交人工停止" : "",
        State.Enabled || State.Gate != true ? "须有新鲜的未接管门极开启反馈" : "",
        State.Fault != 0 || State.Error is not (0 or 9) || State.HeartbeatFaultFlags != 0 ? "其他驱动故障须另行排查" : "",
        !owner.IsManual ? "请切回手动控制" : "",
        owner.Busy || owner.StartingPositionNode != 0 ? "上一操作尚未完成" : "",
        owner.Active || owner.StreamRunning ? "须先停止本会话输出和控制流" : "",
        owner.UnownedStopPending || owner.RecoveryPending ? "停止或急停清除核验中" : "",
        State.UnownedStopBlockReason);
    public string UnownedStopBlockReason => FirstReason(UnownedStopCommonReason,
        !UnownedStopConfirmed ? "请确认其他控制器已停发、机械支撑就绪；停止后无保持力矩" : "");
    public bool CanAcknowledgeUnownedStop => UnownedStopCommonReason.Length == 0;
    public bool CanStopUnowned => UnownedStopBlockReason.Length == 0;
    public bool ShowUnownedStop => State.Connected && !State.Enabled && (State.Gate == true ||
        owner.CurrentState?.UnownedStop is { Stage.Length: > 0 } r && r.Node == Node);
    public string UnownedStopText => owner.CurrentState?.UnownedStop is { Stage.Length: > 0 } r && r.Node == Node
        ? $"{(r.Pending ? "停止进度" : "上次停止结果（非当前状态）")}：{r.Reason}" +
            (!owner.StateFreshNow || !State.Valid ? "（当前反馈未知）" : "")
        : "门极已开启，本会话未接管。仅发送本轴 STOP；不清故障、不自动启动，停止后无保持力矩。";
    public IAsyncRelayCommand StopUnownedCommand { get; }
    public bool IsIdle => owner.StateFreshNow && State.Connected && State.Valid &&
        !State.HasDriveFault && !State.Enabled && State.Gate == false && State.MotorMode == 2 &&
        State.Phase is not ("停止待确认" or "停机未确认" or "急停清除待确认" or "急停清除未确认");
    public string StartButtonText => State.Holding ? "继续" : "启动";
    public string MotorStateText => !State.Connected ? "未连接" : !owner.StateFreshNow || !State.Valid ? "状态未知" :
        State.Fault == 4 ? "急停（0x4）" : State.HasDriveFault ? "故障（非急停，不自动清除）" :
        IsIdle ? "空闲" : State.Enabled && State.Gate == true && State.MotorMode == 5 ?
        State.Holding ? "运行 · 停止转动并保持" : "运行" :
        State.Gate == true && !State.Enabled ? "运行 · 本会话未接管" : "状态转换中";
    public string StartPositionBlockReason => State.Holding ? FirstReason(CurrentCommonReason,
        HardwareBlockReason, !owner.IsPositionSource ? "请选择位置控制或AI视觉随动" : "",
        State.Gate != true || State.MotorMode != 5 || State.Phase != "位置控制" ? "等待保持门极与运行模式确认" : "",
        !State.Stationary || !State.Complete ? "等待减速至零及连续静止到位" : "",
        owner.IsVision ? owner.VisionStartBlockReason : "", owner.Busy ? "另一个结构操作尚未完成" : "") :
        FirstReason(CurrentCommonReason, !IsIdle ? "仅电机空闲状态可以启动；等待新鲜门极关闭及空闲模式反馈" : "",
        PrepareBlockReason, ResponseDraftIssue,
        owner.IsVision ? owner.VisionStartBlockReason : "",
        State.Enabled ? "本轴已启动或正在启动" : "",
        owner.Busy ? "另一个结构操作尚未完成" : "",
        owner.StreamRunning && owner.IsVision && owner.CurrentState?.Stream.State != "跟随" ? "失锁或归位中不能新增轴" : "");
    public bool CanStartPosition => StartPositionBlockReason.Length == 0;
    public string VisionStartHint => State.Holding ? $"ID{Node} 停止转动并保持力矩；" +
        (CanStartPosition ? "点击本轴“继续”恢复新控制，不执行旧目标" : StartPositionBlockReason) : owner.StartingPositionNode == Node ? $"ID{Node} 启动中：{owner.PositionStartStatus}" :
        State.Enabled && owner.StateFreshNow && State.Valid && !IsAbnormal ? $"ID{Node} 运行中；视觉暂缺保持，稳定新帧自动继续，启动不重复提交" :
        $"ID{Node}：" + (CanStartPosition ? $"已就绪 · 点击本轴“启动”跟随 J{Node}" : "启动受阻：" + MainViewModel.ShortText(StartPositionBlockReason, 48));
    public string PositionControlHint => State.Holding ? State.HoldState + "；点击“继续”恢复新控制，不重发START、不执行旧目标" : owner.StartingPositionNode == Node ? owner.PositionStartStatus :
        State.Enabled ? (owner.IsVision ? $"AI视觉控制 J{Node}→ID{Node}；按捕获基准下发相对转角，日常停止转动后仍有保持力矩" :
            CanExecute ? SliderControlHint + "；日常停止仅减速保持" : ExecuteBlockReason) :
        ShowUnownedStop && State.Gate == true ? "须先在下方人工确认停止未接管电机，再启动位置环" :
        CanStartPosition ? owner.IsVision ? $"点击“启动”授权本轴 J{Node}→ID{Node}：核验行程与归位，捕获当前 J 角基准、记录电机参考、使能并跟随；其他轴不启动" :
            "点击“启动位置环”：自动准备、记录当前位置并启动；不会执行旧目标" : StartPositionBlockReason;
    private string ReferenceReason(string common) => FirstReason(common, HardwareBlockReason,
        State.Enabled ? "本轴已使能，请先停止本轴再记录参考" : "", GateBlockReason, MotionBlockReason);
    private string EnableReason(string common) => FirstReason(common, HardwareBlockReason,
        State.Reference.HasValue ? CommandRangeBlockReason : "",
        State.Enabled ? "本轴已使能或正在使能" : "", GateBlockReason,
        !State.Reference.HasValue ? "请先记录软件参考点（不机械置零）" : "",
        MotionBlockReason,
        !owner.CanEnableForSource(this) ? "视觉控制须选择本轴、逐轴确认并捕获对齐" : "");
    private string ExecuteReason(string common) => FirstReason(common, HardwareBlockReason,
        !owner.IsManual ? "当前是视觉控制，请先暂停并切回手动位置控制" : "",
        !State.Enabled ? "请先记录参考并使能本轴" : "",
        State.Holding ? "本轴停止转动并保持中；点击“继续”恢复新控制" : "",
        State.Phase != "位置控制" ? "等待门极与位置控制反馈确认" : "",
        CommandRangeBlockReason,
        !ValidDraft ? $"目标须为 [{State.CommandLow:G}, {State.CommandHigh:G}]° 内的有限角度；预留端点裕量，未钳位或下发" : "",
        owner.StreamRunning && !owner.RealTimeControl ? "请先暂停实时流" : "");
    public (string Prepare, string Reference, string Enable, string Execute) BlockReasons(bool fresh, bool authorized)
    {
        var common = CommonReason(fresh, authorized);
        return (PrepareReason(common), ReferenceReason(common), EnableReason(common), ExecuteReason(common));
    }
    private string CurrentCommonReason => CommonReason(owner.StateFreshNow, owner.CanAuthorize);
    public string PrepareBlockReason => PrepareReason(CurrentCommonReason);
    public string ReferenceBlockReason => ReferenceReason(CurrentCommonReason);
    public string EnableBlockReason => EnableReason(CurrentCommonReason);
    public string ExecuteBlockReason => ExecuteReason(CurrentCommonReason);
    public bool CanReference => ReferenceBlockReason.Length == 0;
    public string Readiness => !State.Reference.HasValue ?
        (CanReference ? "① 可记录当前位置为软件参考（不机械置零）" : "记录参考受阻：" + ReferenceBlockReason) :
        !State.Enabled ? (CanEnable ? "② 可使能本轴；使能后保持当前位置" : "使能受阻：" + EnableBlockReason) :
        !owner.IsManual ? "已使能 · 由视觉控制；保持仍有力矩" :
        CanExecute ? "③ 可下发目标角度，电机将实际转动；保持仍有力矩" : "下发受阻：" + ExecuteBlockReason;
    private bool HasCurrentTransmission => owner.StateFreshNow && State.Valid && State.Enabled && State.TxAgeMs is >= 0 and <= 200;
    public string TransmissionStatus => State.TxCount == 0 ? "尚未下发位置帧（编辑角度不等于发送）" :
        $"{(!HasCurrentTransmission ? "历史 " : "")}TX 0x01 · {State.TxCount} 帧 · 绝对 {State.TxAbsolute:0.000}°\n" +
        $"最近发送 {State.TxAgeMs:0}ms 前 / revision {State.TxRevision} · " +
        (!HasCurrentTransmission ? "不代表当前运动/到位" :
            State.TxRevision != owner.CurrentState?.Revision ? "当前意图待发送确认" :
            State.Complete ? "反馈已到位，仍有保持力矩" : "等待反馈到位");
    public string ControlDiagnostics =>
        $"逐轴令牌 {State.ControlToken} / 准备阶段 {State.PreparationStage} / 局部取消 {State.LocalCancelReason}\n" +
        $"位置准备：{(CanPrepare ? "可操作；仅写 RAM，不使能" : PrepareBlockReason)}\n" +
        $"记录参考：{(CanReference ? "可操作" : ReferenceBlockReason)}\n" +
        $"使能本轴：{(CanEnable ? "可操作" : EnableBlockReason)}\n" +
        $"下发角度：{(CanExecute ? "可操作" : ExecuteBlockReason)}\n" +
        RangeHint + "\n" +
        $"停止入口：{StopMotorHint}\n" +
        $"停止帧：{State.StopTxCount} 次 / {State.StopTxKind} / revision {State.StopTxRevision} / 年龄 {AgeText(State.StopTxAgeMs)}；写入不等于门极关闭\n" +
        $"最近停止发送错误：{(State.StopTxError.Length > 0 ? State.StopTxError : "无已报告错误")}\n" +
        $"停止反馈：{(owner.StopFeedbackText(State) is { Length: > 0 } feedback ? feedback : "尚无本会话停止确认记录")}\n" +
        $"急停清除：{(CanClearEstop ? "可操作；不使能" : EstopClearBlockReason)}\n" +
        $"停止未接管电机：{(CanStopUnowned ? "可操作；仅 STOP" : UnownedStopBlockReason)}\n" +
        $"参数校验：{(State.Issues.Length == 0 ? "无已报告异常" : string.Join("；", State.Issues))}\n" +
        $"最近反馈{(!owner.StateFreshNow || !State.Valid ? "（过期/未知，不作有效状态）" : "")}：" +
        $"Fault=0x{State.Fault:X8} / Error=0x{State.Error:X2}\n" +
        $"故障字查询年龄 {AgeText(State.FaultAgeMs)} / MIT 状态错误码查询年龄 {AgeText(State.ErrorAgeMs)}\n" +
        $"0x48 心跳分类：{(State.HeartbeatFlags.HasValue ? $"0x{State.HeartbeatFlags:X2}" : "未收到")} / 接收年龄 {AgeText(State.HeartbeatAgeMs)} / 待核验 0x{State.HeartbeatFaultFlags:X2}（不代替状态查询）\n" +
        $"Gate={(State.Gate.HasValue ? State.Gate.Value ? "开" : "关" : "未知")} / 原始速度 {State.Velocity:0.00}°/s / 连续静止 {State.StationaryMs:0}ms\n" +
        $"后端静止：{State.StationaryReason}\n" +
        $"静止采样源：{State.StationarySource} / 分辨率 {State.StationaryResolutionDeg:0.000000}° / 整数采样年龄 {AgeText(State.EncoderCountAgeMs)}\n" +
        $"原始位置 float32 分辨率：{(State.PositionResolutionDeg is { } resolution ? $"{resolution:0.000000}°/LSB" : "未知")}（软件参考点不提高反馈精度）\n" +
        $"判定窗 {State.StationaryWindowMs:0}ms / {State.StationarySamples} 样本 / 跨度 {State.StationarySpan:0.0000}°\n" +
        $"路程速度 {State.StationaryPathVelocity:0.0000}°/s / 漂移 {State.StationaryDriftVelocity:0.0000}°/s\n" +
        $"位置观测窗 {State.PositionWindowMs:0}ms / 跨度 {State.PositionSpan:0.0000}° / 净均速 {State.PositionWindowVelocity:0.0000}°/s（仅诊断）\n" +
        TransmissionStatus;
    private static string AgeText(double? age) => age.HasValue ? $"{age:0}ms" : "未知";
    public IAsyncRelayCommand StartPositionCommand { get; }
    internal static string MotorSafetyIssue(AxisState state, bool fresh)
    {
        if (state.Phase == "停机未确认") return $"ID{state.Node} 停机未确认；检查硬件急停与机械支撑";
        if (state.Connected && state.HasDriveFault) return $"ID{state.Node} 驱动故障或心跳故障待核验";
        if (state.Connected && state.Issues.Length > 0 && state.Enabled)
            return $"ID{state.Node} 参数安全校验异常：" + string.Join("；", state.Issues);
        if (!state.Enabled) return "";
        if (!state.Connected || !fresh || !state.Valid) return $"ID{state.Node} 受控轴失联或反馈过期";
        if (state.Phase == "位置控制" && state.Gate != true) return $"ID{state.Node} 位置控制门极异常退出";
        return "";
    }
    public string AbnormalReason => owner.AxisProtectionReason(Node) is { Length: > 0 } protection ? protection :
        State.Connected && owner.CurrentState?.Fault is { Length: > 0 } fault ? "故障锁存：" + fault :
        MotorSafetyIssue(State, owner.StateFreshNow);
    public bool IsAbnormal => AbnormalReason.Length > 0;
    public string StopButtonText => IsAbnormal ? "异常" : "停止";
    public string StopMotorHint => IsAbnormal ? MainViewModel.ShortText(AbnormalReason) + "\n" +
        owner.CurrentAxisFeedback(State) + "\n" +
        (!owner.StateFreshNow || !State.Connected || !State.Valid ?
            "先核验硬件急停与支撑；顶部断开／取消→连接电机只读重连，不自动运动。" :
            !State.Enabled && State.Gate == true ? "本轴未接管，须先专用人工确认停止；不自动接管。" :
            EstopClearBlockReason.Length > 0 ? "恢复受阻：" + MainViewModel.ShortText(EstopClearBlockReason) :
            "点击异常核验并恢复合格锁存；不自动启动或恢复旧目标。") + "\n完整首因与请求结果见诊断详情。" : !State.Connected ?
        "本轴未连接；未知停机状态须检查硬件急停与支撑。" + (owner.Connected ? owner.ConnectionRecoveryHint : "") :
        !owner.StateFreshNow || !State.Valid ? "当前反馈未知，不能确认保持；真实失联仍走安全保护" :
        !State.Enabled && State.Gate == true ? "本轴未接管；日常停止转动不接管外部电机，须在专用人工入口核对" :
        !State.Enabled && State.Gate == false ? "本轴未启动；可取消本轴在途启动，不发送停止帧" :
        "仅停止本轴转动并保持力矩，其他轴继续；不发 STOP/ESTOP，等待实测静止。点击启动恢复控制。";
    public IAsyncRelayCommand StopMotorCommand { get; }
    public IAsyncRelayCommand ClearEstopCommand { get; }
    public IAsyncRelayCommand PrepareCommand { get; }
    public IAsyncRelayCommand ReferenceCommand { get; }
    public IAsyncRelayCommand EnableCommand { get; }
    public IAsyncRelayCommand ExecuteCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand ApplyResponseCommand { get; }
    public IRelayCommand PlusCommand { get; }
    public IRelayCommand MinusCommand { get; }
    public IRelayCommand SelectCommand { get; }
    private string CommandRangeBlockReason => State.HasCommandRange ? "" :
        State.CommandRangeIssue.Length > 0 ? State.CommandRangeIssue : "后端可下发行程无效，禁止下发";
    public double CommandMinimum => State.HasCommandRange ? State.CommandLow!.Value : 0;
    public double CommandMaximum => State.HasCommandRange ? State.CommandHigh!.Value : 1;
    public double DisplayHardLow => State.HardLow ?? State.Low;
    public double DisplayHardHigh => State.HardHigh ?? State.High;
    public double DisplayHardMid => DisplayHardLow + (DisplayHardHigh - DisplayHardLow) / 2;
    public double SliderTickFrequency
    {
        get
        {
            var span = DisplayHardHigh - DisplayHardLow;
            if (!double.IsFinite(span) || span <= 0) return 1;
            // 采用 1/2/5 系列的真实角度细刻度；默认 ±135° 每 5° 一格，较小行程自动细分。
            var unit = Math.Pow(10, Math.Floor(Math.Log10(span / 60)));
            return new[] { unit, unit * 2, unit * 5, unit * 10 }.First(step => span / step <= 60);
        }
    }
    public string SliderControlHint => !CanEditTarget ? "滑条不可用：" + FirstReason(CurrentCommonReason,
        !owner.IsManual ? "当前由视觉控制" : "", CommandRangeBlockReason) :
        !State.Enabled ? "尚未启动：滑条仅修改待下发值；点击本轴启动不会执行旧目标" :
        !owner.RealTimeControl ? "实时控制关闭：滑条仅修改待下发值；开启后须重新拖动，不执行旧目标" :
        !CanExecute ? "滑条下发受阻：" + ExecuteBlockReason : "实时控制已开启：拖动滑条或方向键即下发本轴最新目标";
    public string RangeHint => State.HasCommandRange
        ? $"可下发 [{State.CommandLow:G}, {State.CommandHigh:G}]°；两端各预留 {State.CommandMarginDeg:G}°\n" +
          $"有效保护 [{State.HardLow:G}, {State.HardHigh:G}]°（软件配置与固件交集）；实测越界仍停止。裕量未经实体跟随验证。"
        : "可下发行程未就绪：" + CommandRangeBlockReason;
    public bool CanEditTarget => !State.Holding && owner.IsManual && owner.CanAuthorize && !owner.IsAxisBusy(Node) && State.Connected && State.Valid && State.HasCommandRange;
    public bool CanConfigure => owner.CanAuthorize && !owner.Busy && State.Connected && State.Valid && !State.Enabled && State.Gate == false;
    public bool CanEnable => EnableBlockReason.Length == 0 && Protocol.CanEnable(owner.CurrentState, State, owner.Confirmed);
    public bool CanExecute => ExecuteBlockReason.Length == 0;
    public bool ValidDraft => double.TryParse(Draft, NumberStyles.Float, CultureInfo.CurrentCulture, out var n) && State.HasCommandRange && Protocol.ValidTarget(n, CommandMinimum, CommandMaximum);
    public double DraftValue => double.Parse(Draft, CultureInfo.CurrentCulture);
    public AxisViewModel(int node, MainViewModel owner)
    {
        this.owner = owner; Node = node;
        Accent = Brush.Parse(new[] { "#5AC8FA", "#BB9AF7", "#F3BD69" }[node - 1]);
        State = new AxisState { Node = node };
        StartPositionCommand = new AsyncRelayCommand(() => owner.StartPositionAsync(this), () => CanStartPosition);
        StopMotorCommand = new AsyncRelayCommand(() => IsAbnormal ? owner.RecoverAxisAsync(this) : owner.HoldAxisAsync(this),
            () => owner.Connected && (State.Connected || State.Enabled || owner.StartingPositionNode == Node ||
                State.Phase is "停止待确认" or "停机未确认"));
        StopUnownedCommand = new AsyncRelayCommand(async () =>
        {
            if (!CanStopUnowned) return;
            await owner.IntentAsync("stop_unowned", new
            {
                nodes = new[] { Node },
                confirmed = owner.Confirmed,
                safetyAcknowledged = UnownedStopConfirmed
            }, true);
        }, () => CanStopUnowned);
        ClearEstopCommand = new AsyncRelayCommand(() => owner.RecoverAxisAsync(this), () => CanClearEstop);
        PrepareCommand = new AsyncRelayCommand(async () =>
        {
            ResetMapping();
            await owner.IntentAsync("prepare_position", new { nodes = new[] { Node }, confirmed = owner.Confirmed });
        }, () => CanPrepare);
        ReferenceCommand = new AsyncRelayCommand(async () => { ResetMapping(); await owner.IntentAsync("reference", new { nodes = new[] { Node } }); }, () => CanReference);
        EnableCommand = new AsyncRelayCommand(() => owner.IntentAsync("enable", new { nodes = new[] { Node }, confirmed = owner.Confirmed }), () => CanEnable);
        ExecuteCommand = new AsyncRelayCommand(() => owner.ExecuteAsync([this]), () => CanExecute);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanConfigure);
        ApplyResponseCommand = new AsyncRelayCommand(async () =>
        {
            if (!CanApplyResponse || !TryGetResponse(out var rpm, out var acceleration))
            { owner.Message = "响应未应用：" + ResponseApplyBlockReason; return; }
            ResetMapping();
            await owner.IntentAsync("settings", new { nodes = new[] { Node }, rpm, acceleration });
        }, () => CanApplyResponse);
        PlusCommand = new RelayCommand(() => Step(1), () => CanEditTarget);
        MinusCommand = new RelayCommand(() => Step(-1), () => CanEditTarget);
        SelectCommand = new RelayCommand(() => owner.SelectedNodeIndex = Node - 1);
    }
    partial void OnPendingChanged(double value)
    {
        if (double.IsFinite(value)) Draft = value.ToString("G", CultureInfo.CurrentCulture);
    }
    partial void OnDraftChanged(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var n) && double.IsFinite(n) && pending != n)
        {
            pending = n;
            OnPropertyChanged(nameof(Pending));
        }
        // 草稿仅影响本轴下发条件；硬件诊断和其他轴仍由 UI tick 刷新，避免编辑时同步重建三轴卡片。
        OnPropertyChanged(nameof(ValidDraft));
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(ExecuteBlockReason));
        OnPropertyChanged(nameof(Readiness));
        OnPropertyChanged(nameof(PositionControlHint));
        OnPropertyChanged(nameof(SliderControlHint));
        ExecuteCommand?.NotifyCanExecuteChanged();
        owner.ExecuteAllCommand?.NotifyCanExecuteChanged();
    }
    public void SubmitUserSliderValue(double value)
    {
        if (!CanEditTarget) return;
        if (!Protocol.ValidTarget(value, CommandMinimum, CommandMaximum))
        { owner.Message = "滑条目标未提交；" + RangeHint; return; }
        Pending = value;
        // 同值的真实输入也应替换非法草稿；绑定刷新仍不会走此入口。
        Draft = value.ToString("G", CultureInfo.CurrentCulture);
        owner.QueueManualTarget(this);
    }
    public Task CancelUserDragAsync() => owner.CancelDragAsync();
    private void Step(int sign)
    {
        if (!Protocol.ValidStep(owner.StepSize))
        { owner.Message = "步进须为 0.1～10° 的有限值；未下发。"; return; }
        if (!CanEditTarget || !ValidDraft) { owner.Message = "步进未提交；" + RangeHint; return; }
        var value = DraftValue + sign * owner.StepSize;
        if (!Protocol.ValidTarget(value, CommandMinimum, CommandMaximum))
        { owner.Message = "步进超出可下发行程；未钳位或下发。" + RangeHint; return; }
        Pending = value;
        owner.QueueManualTarget(this);
    }
    private async Task ApplyAsync()
    {
        try
        {
            var low = double.Parse(LowText); var high = double.Parse(HighText);
            var rpm = double.Parse(RpmText); var amps = double.Parse(AmpsText); var acceleration = double.Parse(AccelerationText);
            if (!new[] { low, high, rpm, amps, acceleration }.All(double.IsFinite)) throw new FormatException("输入必须为有限数字");
            if (!Protocol.ValidResponse(rpm, acceleration)) throw new FormatException(ResponseDraftIssue);
            if (!Protocol.ValidJointRange(low, high))
                throw new FormatException($"相对保护范围须在 {Protocol.JointMinimumDeg:G}～{Protocol.JointMaximumDeg:G}° 内且下限小于上限；未钳位或提交");
            ResetMapping();
            await owner.IntentAsync("settings", new { nodes = new[] { Node }, low, high, rpm, amps, acceleration, direction = Reverse ? -1 : 1 });
        }
        catch (Exception ex) { owner.Message = "运行设置无效：" + ex.Message; }
    }


    public void UpdateVision(VisionSample? sample, double? modelReference, bool aligned)
    {
        VisionPreview = null;
        if (sample is not { IsSafe: true, Valid: true })
        { VisionText = "相机解算 J角：—（无新鲜追踪）"; return; }
        var angle = sample.Angles[Node - 1];
        VisionText = $"相机解算 J{Node}：{angle:0.00}° · 采集 {sample.CaptureSeq}\n";
        if (!aligned || !modelReference.HasValue || !State.Reference.HasValue || !owner.StateFreshNow || !State.Valid)
        { VisionText += "仅预览；待选择、确认并捕获对齐"; return; }
        var relative = angle - modelReference.Value;
        var absolute = State.Reference.Value + State.Direction * relative;
        var inRange = State.HasCommandRange && Protocol.ValidTarget(relative, CommandMinimum, CommandMaximum);
        if (inRange) VisionPreview = relative;
        VisionText += $"映射预览 {relative:0.00}° / 绝对 {absolute:0.00}°\n" +
            (inRange ? "绿色标记仅预览；白线为后端目标" : "超出可下发行程或行程未知，禁止下发；不钳位伪装同步");
    }


    public void Update(AxisState state, bool fresh, bool refreshCommands = true)
    {
        State = state;
        if (fresh && state.Connected && state.Valid && Protocol.ValidResponse(state.Rpm, state.Acceleration) &&
            (!responseDirty || TryGetResponse(out var rpm, out var acceleration) && rpm == state.Rpm && acceleration == state.Acceleration))
        {
            syncingResponse = true;
            RpmText = state.Rpm.ToString("G", CultureInfo.CurrentCulture);
            AccelerationText = state.Acceleration.ToString("G", CultureInfo.CurrentCulture);
            syncingResponse = false;
            responseDirty = false;
        }

        // 
        if (!fresh || !state.Connected || !state.Valid || state.Enabled || state.Gate != true ||
            state.Fault != 0 || state.Error is not (0 or 9) || state.HeartbeatFaultFlags != 0 ||
            state.UnownedStopBlockReason.Length > 0)
            UnownedStopConfirmed = false;
        OnPropertyChanged(nameof(State));
        Actual = fresh && state.Connected && state.Valid && state.Reference.HasValue ? state.Position : null;
        Target = fresh && state.Connected && state.Valid ? state.Target : null;

        PositionText = Actual.HasValue ? $"{Actual:0.00}°" : state.Reference.HasValue ? "—" : "0°";

        var phase = state.Phase == "只读" ? (state.Gate == true
                    ? "门极开启 · 本会话未接管" : "已连接 · 待使能（未接管）") : state.Phase;
        if (state.Holding) phase = state.HoldState;
        Status = !state.Connected ? phase : MotorStateText + " / " + (!fresh || !state.Valid ? phase + " / 反馈过期或未知" : phase);
        var error = state.FollowingErrorDeg.HasValue ? $"{state.FollowingErrorDeg:0.000}° / 超过3°持续 {state.FollowingDurationMs:0}ms" : "—";
        Detail = $"绝对 {state.Absolute:0.00}°  ·  采样对齐跟随误差（仅诊断，不自动停机） {error}\n速度 {state.Velocity:0.00}°/s  /  {state.Velocity / 6:0.000} RPM\n帧上限 {state.Rpm:0} RPM = {state.Rpm * 6:0}°/s  ·  {state.Amps:0.0} A\n加减速度 {state.Acceleration:G}°/s²\n参考 {(state.Reference.HasValue ? state.Reference.Value.ToString("0.00") + "°" : "未记录")}  ·  年龄 {state.AgeMs:0} ms";
        if (!fresh || !state.Valid)
            Detail = $"绝对位置 / 速度 / 误差：—（过期）\n帧上限 {state.Rpm:0} RPM · {state.Amps:0.0} A\n参考 {(state.Reference.HasValue ? state.Reference.Value.ToString("0.00") + "°" : "未记录")}\n反馈年龄 {state.AgeMs:0} ms";
        if (!state.Connected)
            Detail = (state.ConnectionError.Length > 0 ? state.ConnectionError : "尚未连接本节点") +
                (state.Phase == "停机未确认" ? "\n无法确认停机，请检查硬件急停与支撑" : "\n本轴控制已禁用") +
                "\n增减节点后请停止并断开重连";
        else if (!state.AgeMs.HasValue)
            Detail = "正在等待位置、状态和门极反馈\n反馈有效后才允许记录参考与使能";
        string SampleText(string key, string unit) => fresh && state.Connected && state.Samples.TryGetValue(key, out var s) && s.Valid ? $"{s.Value:0.00}{unit}" : "—";
        Telemetry = $"Iq {SampleText("current", "A")}  ·  母线 {SampleText("voltage", "V")}\n电机 {SampleText("motorTemp", "°C")}  ·  MOS {SampleText("driverTemp", "°C")}\n母线电流（估算）{SampleText("busCurrentEstimate", "A")}\n力矩（估算）{SampleText("torqueEstimate", "N·m")}\n功率（估算）{SampleText("powerEstimate", "W")}";
        if (refreshCommands) Refresh();
    }


    internal void RefreshPositionStatus()
    {
        OnPropertyChanged(nameof(PositionControlHint));
        OnPropertyChanged(nameof(VisionStartHint));
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(MotorStateText)); OnPropertyChanged(nameof(StartButtonText));
        RefreshResponse();
        OnPropertyChanged(nameof(CommandMinimum)); OnPropertyChanged(nameof(CommandMaximum));
        OnPropertyChanged(nameof(DisplayHardLow)); OnPropertyChanged(nameof(DisplayHardHigh));
        OnPropertyChanged(nameof(DisplayHardMid)); OnPropertyChanged(nameof(SliderTickFrequency));
        OnPropertyChanged(nameof(SliderControlHint));
        OnPropertyChanged(nameof(RangeHint)); OnPropertyChanged(nameof(ValidDraft));
        OnPropertyChanged(nameof(CanPrepare)); OnPropertyChanged(nameof(PrepareBlockReason));
        OnPropertyChanged(nameof(CanStartPosition)); OnPropertyChanged(nameof(StartPositionBlockReason));
        OnPropertyChanged(nameof(PositionControlHint)); StartPositionCommand.NotifyCanExecuteChanged();
        StopMotorCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(StopMotorHint));
        OnPropertyChanged(nameof(AbnormalReason)); OnPropertyChanged(nameof(IsAbnormal)); OnPropertyChanged(nameof(StopButtonText));
        OnPropertyChanged(nameof(CanStopUnowned)); OnPropertyChanged(nameof(CanAcknowledgeUnownedStop));
        OnPropertyChanged(nameof(UnownedStopBlockReason)); OnPropertyChanged(nameof(ShowUnownedStop));
        OnPropertyChanged(nameof(UnownedStopText)); StopUnownedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanClearEstop)); OnPropertyChanged(nameof(EstopClearBlockReason));
        OnPropertyChanged(nameof(EstopRecoveryText)); ClearEstopCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TransmissionStatus)); PrepareCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanReference)); OnPropertyChanged(nameof(Readiness)); OnPropertyChanged(nameof(CanConfirmMapping));
        OnPropertyChanged(nameof(ReferenceBlockReason)); OnPropertyChanged(nameof(EnableBlockReason));
        OnPropertyChanged(nameof(ExecuteBlockReason)); OnPropertyChanged(nameof(ControlDiagnostics));
        OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(CanEnable)); OnPropertyChanged(nameof(CanExecute)); OnPropertyChanged(nameof(CanEditTarget));
        PlusCommand.NotifyCanExecuteChanged(); MinusCommand.NotifyCanExecuteChanged();
        ReferenceCommand.NotifyCanExecuteChanged(); EnableCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged();
    }
}
