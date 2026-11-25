using Avalonia.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HostComputer.Core;
using System.Diagnostics;
using System.Text.Json;

namespace HostComputer.App.ViewModels;

/// <summary>
/// 控制流、视觉对齐、实时跟随等功能的实现。
/// </summary>
public partial class MainViewModel
{
    private readonly IVisionClient vision;
    private string streamGeneration = "", streamSource = "none", alignedIdentity = "";
    private int[] streamNodes = [], alignedNodes = [];
    private double[] alignedReferences = [];
    private long streamSequence, lastCapture = -1, controlEpoch, streamRevision, nextSummaryAt;
    private int controlWakeQueued;
    private bool streamRequestPending, streamOpening, streamLeaseHandoffPending, attachingStream;
    private long acknowledgedStreamRevision = -1;
    private JsonElement? firstStreamSampleFailure;
    private int abnormalRecoveryNode;
    private long abnormalRecoveryToken;
    private long connectionTransitionEpoch = -1;
    private readonly Dictionary<int, double> pendingManual = [];
    private readonly Dictionary<string, long> streamAxisTokens = [];
    private readonly Dictionary<int, long> axisOperationEpoch = [];
    private readonly HashSet<int> stoppingAxes = [];
    private readonly Dictionary<int, long> stopFeedbackRevisions = [];
    private readonly Dictionary<int, long> holdFeedbackRevisions = [];
    private int structuralNode;
    internal bool IsAxisBusy(int node) => stoppingAxes.Contains(node) || stopFeedbackRevisions.ContainsKey(node) || holdFeedbackRevisions.ContainsKey(node) || Busy && (structuralNode == 0 || structuralNode == node);
    private void ObserveLocalStops(BridgeState state)
    {
        if (!Fresh) return;
        foreach (var (node, revision) in holdFeedbackRevisions.ToArray())
            if (state.Revision >= revision && state.Axes.FirstOrDefault(a => a.Node == node) is { Valid: true } axis &&
                (axis is { Enabled: true, Gate: true, Holding: true, Complete: true, Stationary: true } ||
                 axis is { Enabled: false, Gate: false } && axis.PreparationStage is not ("核验必需参数" or "RAM 白名单写入" or "最终读回")))
                holdFeedbackRevisions.Remove(node);
        foreach (var (node, revision) in stopFeedbackRevisions.ToArray())
            if (state.Revision >= revision && state.Axes.FirstOrDefault(a => a.Node == node) is
                { Valid: true, Enabled: false, Gate: false } axis &&
                axis.Phase is not ("停止待确认" or "停机未确认") &&
                axis.PreparationStage is not ("核验必需参数" or "RAM 白名单写入" or "最终读回"))
                stopFeedbackRevisions.Remove(node);
    }
    private long AxisEpoch(int node) => axisOperationEpoch.GetValueOrDefault(node);

    public void MappingChanged(AxisViewModel axis)
    {
        alignedNodes = alignedNodes.Where(n => n != axis.Node).ToArray();
        if (alignedNodes.Length == 0) { alignedIdentity = ""; alignedReferences = []; }
        axis.UpdateVision(null, null, false);
        if (streamSource == "vision" && streamNodes.Contains(axis.Node)) _ = StopAxisAsync(axis);
    }

    private void RemoveLocalAxis(AxisViewModel axis, bool preserveMapping = false)
    {
        streamNodes = streamNodes.Where(n => n != axis.Node).ToArray();
        streamAxisTokens.Remove(axis.Node.ToString());
        pendingManual.Remove(axis.Node);
        if (!preserveMapping) { MappingChanged(axis); axis.ResetMapping(); }
        axis.Draft = "0";
        if (streamNodes.Length == 0 && StreamRunning) ClearLocalStream();
    }

    private void AcceptStructureReply(JsonElement? reply, long submitted)
    {
        var accepted = reply?.TryGetProperty("revision", out var r) == true ? r.GetInt64() : submitted;
        intentRevision = Math.Max(intentRevision, accepted);
        acknowledgedStreamRevision = Math.Max(acknowledgedStreamRevision, accepted);
        streamLeaseHandoffPending = StreamRunning && acknowledgedStreamRevision == intentRevision;
        CompletePendingLease();
        if (CurrentState is { } state) _ = LeaseAsync(state);
        WakeControlTargets();
    }

    public Task HoldAllAsync() => IntentAsync("hold", new { nodes = Axes.Select(a => a.Node).ToArray() }, true);

    private void AcceptHoldReply(JsonElement? reply, long revision)
    {
        if (reply?.TryGetProperty("skippedNodes", out var skipped) == true)
            foreach (var node in skipped.EnumerateArray()) holdFeedbackRevisions.Remove(node.GetInt32());
        AcceptStructureReply(reply, revision);
        if (Active && !StreamRunning)
        {
            acknowledgedTargetRevision = intentRevision;
            targetLeaseHandoffPending = true;
            RenewAcceptedTargetLease();
        }
    }

    public async Task HoldAxisAsync(AxisViewModel axis)
    {
        if (!Connected || stoppingAxes.Contains(axis.Node)) return;
        var requestedSession = session;
        var globalEpoch = globalCancellationEpoch;
        var epoch = axisOperationEpoch[axis.Node] = AxisEpoch(axis.Node) + 1;
        if (StartingPositionNode == axis.Node || Busy && structuralNode == axis.Node)
        {
            if (StartingPositionNode == axis.Node) ++positionStartEpoch;
            StartingPositionNode = structuralNode = 0;
            preparingPosition = Busy = false;
            PositionStartStatus = "启动或继续已取消；已启动轴减速保持，不自动继续";
        }
        stoppingAxes.Add(axis.Node);
        RemoveLocalAxis(axis, preserveMapping: true);
        RefreshCommands();
        try
        {
            var revision = ++intentRevision;
            holdFeedbackRevisions[axis.Node] = revision;
            var reply = await bridge.SendAsync("hold_axis", requestedSession, revision, new { nodes = new[] { axis.Node } });
            if (disposed || requestedSession != session || globalEpoch != globalCancellationEpoch || epoch != AxisEpoch(axis.Node)) return;
            if (holdFeedbackRevisions.ContainsKey(axis.Node)) holdFeedbackRevisions[axis.Node] = revision;
            AcceptHoldReply(reply, revision);
            Message = $"ID{axis.Node} 停止转动已接受：减速保持力矩，其他轴继续；等待实测静止，不发送停止帧。";
        }
        catch (Exception ex)
        {
            if (disposed || requestedSession != session || globalEpoch != globalCancellationEpoch || epoch != AxisEpoch(axis.Node)) return;
            Message = $"ID{axis.Node} 保持未确认：{ex.Message}";
            if (ex is TimeoutException or IOException) await ForceMotorStopAsync(Message);
            else if (bridge.Latest is { } state && state.SessionId == session)
            { intentRevision = ConfirmedControlRevision(state); holdFeedbackRevisions.Remove(axis.Node); }
        }
        finally
        {
            if (requestedSession == session && epoch == AxisEpoch(axis.Node)) stoppingAxes.Remove(axis.Node);
            RefreshCommands();
        }
    }

    private async Task ResumePositionAsync(AxisViewModel axis)
    {
        var requestedSession = session;
        var source = ControlSourceIndex;
        var epoch = AxisEpoch(axis.Node);
        var globalEpoch = globalCancellationEpoch;
        var visionStart = IsVision;
        var sample = visionStart ? vision.Latest?.Current : null;
        var channel = vision.ChannelInterruption;
        Busy = true; structuralNode = axis.Node;
        try
        {
            var revision = ++intentRevision;
            var reply = await bridge.SendAsync("resume_axis", requestedSession, revision,
                new { nodes = new[] { axis.Node }, source = visionStart ? "vision" : "manual", sample });
            HandleVisionInterruption();
            if (visionStart && channel != vision.ChannelInterruption)
                throw new IOException("视觉恢复期间连接或来源代次中断；旧恢复已取消");
            if (disposed || requestedSession != session || globalEpoch != globalCancellationEpoch || epoch != AxisEpoch(axis.Node)) return;
            AcceptHoldReply(reply, revision);
            var started = Stopwatch.GetTimestamp();
            var ready = false;
            while (Stopwatch.GetElapsedTime(started).TotalSeconds < 3)
            {
                if (disposed || requestedSession != session || globalEpoch != globalCancellationEpoch || epoch != AxisEpoch(axis.Node)) return;
                if (visionStart && (channel != vision.ChannelInterruption || vision.Latest?.Current.Identity != sample?.Identity))
                    throw new IOException("视觉恢复期间连接或来源变化");
                if (ControlSourceIndex != source || !CanAuthorize) throw new InvalidOperationException("恢复来源或授权变化");
                if (StateFreshNow && CurrentState?.Revision == intentRevision &&
                    ReferenceEquals(axis.State, CurrentState.Axes.FirstOrDefault(a => a.Node == axis.Node)) &&
                    axis.State is { Connected: true, Enabled: true, Gate: true, MotorMode: 5, Holding: false,
                        Valid: true, Complete: true, Stationary: true, Phase: "位置控制", HasDriveFault: false })
                { ready = true; break; }
                await Task.Delay(25);
            }
            if (!ready) throw new TimeoutException("恢复控制等待新鲜运行反馈超时");
            if (visionStart)
            {
                if (sample is null || reply?.TryGetProperty("modelReference", out var reference) != true)
                    throw new IOException("未收到本次视觉恢复基准");
                axis.VisionSelected = true;
                axis.DirectionConfirmed = axis.RangeConfirmed = axis.HomeConfirmed = axis.ReferenceConfirmed = true;
                alignedIdentity = sample.Identity;
                alignedNodes = alignedNodes.Append(axis.Node).Distinct().Order().ToArray();
                if (alignedReferences.Length != 3) alignedReferences = sample.Angles.ToArray();
                alignedReferences[axis.Node - 1] = reference.GetDouble();
                Busy = false;
                await BeginStreamAsync("vision", [axis]);
                if (!StreamRunning || !streamNodes.Contains(axis.Node)) throw new IOException("视觉恢复流未确认");
            }
            else
            {
                RealTimeControl = true;
                if (StreamRunning) await BeginStreamAsync("manual", [axis]);
            }
            if (requestedSession == session && epoch == AxisEpoch(axis.Node) && globalEpoch == globalCancellationEpoch)
                Message = $"ID{axis.Node} 已恢复{(visionStart ? "新基准视觉跟随" : "滑条控制；须重新拖动")}；未重发 START，不执行旧目标。";
        }
        catch (Exception ex)
        {
            if (disposed || requestedSession != session || globalEpoch != globalCancellationEpoch || epoch != AxisEpoch(axis.Node)) return;
            Message = "恢复控制未完成：" + ex.Message;
            if (ex is TimeoutException or IOException) await ForceMotorStopAsync(Message);
            else if (bridge.Latest is { } state && state.SessionId == session) intentRevision = ConfirmedControlRevision(state);
        }
        finally
        {
            if (requestedSession == session && globalEpoch == globalCancellationEpoch && epoch == AxisEpoch(axis.Node))
            { Busy = false; structuralNode = 0; }
            RefreshCommands();
        }
    }

    public async Task StopAxisAsync(AxisViewModel axis, string failureContext = "")
    {
        if (!Connected || stoppingAxes.Contains(axis.Node)) return;
        var requestedSession = session;
        var requestedGlobalEpoch = globalCancellationEpoch;
        var axisEpoch = axisOperationEpoch[axis.Node] = AxisEpoch(axis.Node) + 1;
        if (Busy && structuralNode == axis.Node)
        {
            if (StartingPositionNode == axis.Node) ++positionStartEpoch;
            StartingPositionNode = structuralNode = 0;
            preparingPosition = Busy = false;
        }
        stoppingAxes.Add(axis.Node);
        RemoveLocalAxis(axis);
        RefreshCommands();
        try
        {
            var revision = ++intentRevision;
            stopFeedbackRevisions[axis.Node] = revision;
            var reply = await bridge.SendAsync("stop_axis", requestedSession, revision, new { nodes = new[] { axis.Node } });
            if (disposed || requestedSession != session || axisEpoch != AxisEpoch(axis.Node)) return;
            if (requestedGlobalEpoch != globalCancellationEpoch) return;
            AcceptStructureReply(reply, revision);
            stopFeedbackRevisions[axis.Node] = reply?.TryGetProperty("revision", out var accepted) == true ? accepted.GetInt64() : revision;
            Message = reply?.TryGetProperty("message", out var text) == true ? text.GetString()! :
                $"ID{axis.Node} 停止已接受；仅本轴，等待新门极关闭确认，无保持力矩。";
        }
        catch (Exception ex)
        {
            if (disposed || requestedSession != session || requestedGlobalEpoch != globalCancellationEpoch || axisEpoch != AxisEpoch(axis.Node)) return;
            Message = (failureContext.Length > 0 ? failureContext + "；" : "") + $"ID{axis.Node} 停止未完成：{ex.Message}";
            if (ex is TimeoutException or IOException) await ForceMotorStopAsync(Message);
        }
        finally
        {
            if (requestedSession == session && axisEpoch == AxisEpoch(axis.Node)) stoppingAxes.Remove(axis.Node);
            RefreshCommands();
        }
    }
    private long positionStartEpoch, globalCancellationEpoch, observedVisionInterruption, observedVisionChannelInterruption;
    private JsonElement? firstPositionStartFailure;
    private long unownedStopRevision = -1;
    private string lastUnownedStopNotice = "";
    public bool UnownedStopPending => unownedStopRevision >= 0 || CurrentState?.UnownedStop.Pending == true;
    [ObservableProperty] private int startingPositionNode;
    [ObservableProperty] private string positionStartStatus = "";
    partial void OnStartingPositionNodeChanged(int value) => RefreshCommands();
    partial void OnPositionStartStatusChanged(string value) { foreach (var a in Axes) a.RefreshPositionStatus(); }
    public bool NeedsConnectionRecovery => Connected && (!authorizationValid || CurrentState?.Fault.Length > 0);
    public bool CanRestoreConnection => NeedsConnectionRecovery && !Busy && !Active && !RecoveryPending && !UnownedStopPending &&
        StateFreshNow && !string.IsNullOrWhiteSpace(Port) && CurrentState!.Axes.Any(a => a.Connected) &&
        CurrentState.Axes.All(a => a.Phase is not ("停止待确认" or "停机未确认")) &&
        CurrentState.Axes.Where(a => a.Connected).All(a => a.Valid && a.Gate == false);
    public string ConnectionRecoveryHint
    {
        get
        {
            if (!StateFreshNow)
                return "桥接反馈过期或未知；不能确认电机停机。先检查硬件急停、机械支撑与实际停止，再用顶部“断开／取消→连接电机”建立只读新会话；不清驱动急停、不自动运动。";
            if (CurrentState!.Axes.All(a => !a.Connected))
                return "所有电机节点已失联／隔离；桥接快照新鲜不代表电机在线。先确认机械支撑与实际停止，再用顶部“断开／取消→连接电机”只读重连；新会话复位本地锁存，不清驱动急停、不自动运动。";
            if (CanRestoreConnection)
                return "新鲜反馈显示在线轴门极关闭。检查原因后点击“恢复连接（不启动）”建立新会话；不会清驱动故障或自动运动。";
            return "本地保护已锁存；先停止并核验门极关闭。反馈未知或停机未确认时，请检查硬件急停与机械支撑；失联轴须在确认实际停止后用顶部“断开／取消→连接电机”只读重连，不自动运动。";
        }
    }
    public IAsyncRelayCommand RestoreConnectionCommand { get; private set; } = null!;

    public async Task StartPositionAsync(AxisViewModel axis)
    {
        if (!axis.CanStartPosition) { Message = axis.StartPositionBlockReason; return; }
        if (axis.State.Holding) { await ResumePositionAsync(axis); return; }
        if (!axis.TryGetResponse(out var rpm, out var acceleration)) { Message = axis.ResponseDraftIssue; return; }
        var requestedSession = session;
        var requestedSource = ControlSourceIndex;
        var visionStart = IsVision;
        var visionIdentity = visionStart ? vision.Latest!.Current.Identity : "";
        var visionInterruption = vision.SourceInterruption;
        var visionChannelInterruption = vision.ChannelInterruption;
        var epoch = ++positionStartEpoch;
        var axisEpoch = AxisEpoch(axis.Node);
        var failureStage = "初始化";
        Busy = true;
        StartingPositionNode = structuralNode = axis.Node;
        RemoveLocalAxis(axis);
        void Check()
        {
            if (disposed || epoch != positionStartEpoch || requestedSession != session || axisEpoch != AxisEpoch(axis.Node))
                throw new OperationCanceledException("位置环启动已取消");
            if (ControlSourceIndex != requestedSource || !IsPositionSource || !authorizationValid || !Connected || CurrentState?.Fault.Length > 0)
                throw new InvalidOperationException("控制来源、会话授权或本地保护发生变化，启动中止");
            if (visionStart && (vision.SourceInterruption != visionInterruption || vision.ChannelInterruption != visionChannelInterruption ||
                VisionStartBlockReason.Length > 0 || vision.Latest?.Current.Identity != visionIdentity))
                throw new InvalidOperationException("视觉采集、订阅或校准已变化，启动中止");
        }
        async Task WaitForState(Func<AxisState, bool> ready, string stage)
        {
            failureStage = "反馈核验：" + stage;
            PositionStartStatus = stage;
            Message = $"ID{axis.Node}：{stage}";
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started).TotalSeconds < 3)
            {
                Check();
                if (StateFreshNow && CurrentState?.Revision == intentRevision &&
                    ReferenceEquals(axis.State, CurrentState.Axes.FirstOrDefault(a => a.Node == axis.Node)))
                {
                    var state = axis.State;
                    if (!state.Connected || state.HasDriveFault)
                        throw new InvalidOperationException("本轴失联或出现驱动故障，启动中止");
                    if (state.Valid && ready(state)) return;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException(stage + "超过 3 秒，未确认启动成功");
        }
        async Task Stage(string action, object data, int timeout = 1000)
        {
            failureStage = "提交前核验：" + action;
            var started = Stopwatch.GetTimestamp();
            var waiting = false;
            while (true)
            {
                Check();
                // 只等待尚未使能时的新 UI 快照；后台接收本身不能续租或授权。
                if (StateFreshNow && CurrentState?.Revision == intentRevision &&
                    ReferenceEquals(axis.State, CurrentState.Axes.FirstOrDefault(a => a.Node == axis.Node)))
                {
                    if (!axis.State.Connected || !axis.State.Valid || axis.State.HasDriveFault ||
                        axis.State.Gate != false || axis.State.MotorMode != 2 || axis.State.Enabled || axis.State.Phase is "停止待确认" or "停机未确认")
                        throw new InvalidOperationException("发送前反馈、门极或故障状态发生变化，启动中止");
                    if (action != "prepare_position" && axis.State.Issues.Length > 0)
                        throw new InvalidOperationException("发送前参数校验已变化，启动中止");
                    if (action is "settings" or "enable" && !axis.State.Reference.HasValue)
                        throw new InvalidOperationException("发送前软件参考已失效，启动中止");
                    if (action == "enable" && (!axis.State.HasCommandRange ||
                        Math.Abs(axis.State.Rpm - rpm) >= 1e-9 || Math.Abs(axis.State.Acceleration - acceleration) >= 1e-9))
                        throw new InvalidOperationException("发送前行程或响应设置未核验，禁止使能");
                    // 准备后的静止证据会重新积累；只有已消费的新快照重新合格才推进，不重发请求。
                    if (action == "prepare_position" ||
                        axis.State.PreparationStage == "准备完成" && axis.State.Stationary) break;
                }
                if (Stopwatch.GetElapsedTime(started).TotalSeconds >= 3)
                    throw new InvalidOperationException($"等待 {action} 前的新鲜界面反馈超过 3 秒；未提交该步骤");
                if (!waiting)
                {
                    waiting = true;
                    PositionStartStatus = $"等待 {action} 前的新鲜界面反馈与连续静止；尚未提交";
                    Message = $"ID{axis.Node}：{PositionStartStatus}";
                }
                await Task.Delay(25);
            }
            Check();
            // 属性通知也可能耗时；等待结束后不再刷新整张卡片，立即复核并提交。
            if (!StateFreshNow) throw new IOException("发送前界面反馈已过期，未提交该步骤");
            failureStage = "请求应答：" + action;
            var revision = ++intentRevision;
            var reply = await bridge.SendAsync(action, requestedSession, revision, data, timeout);
            Check();
            AcceptStructureReply(reply, revision);
        }
        try
        {
            // 先收完旧租约，避免同步 RAM 事务让旧续约在普通队列里等待超时。
            preparingPosition = true;
            PositionStartStatus = "准备位置环：只改 RAM 并读回，不写 Flash";
            while (pendingLease is not null) { CompletePendingLease(); Check(); if (pendingLease is not null) await Task.Delay(10); }
            await Stage("prepare_position", new { nodes = new[] { axis.Node }, confirmed = true }, 5000);
            preparingPosition = false;
            // revision 在准备入口就提高；同版本的“最终读回”快照仍含旧静止证据。
            // 完成状态只在后端清空证据后发布，再等待该状态下重新合格的连续静止。
            await WaitForState(a => a.PreparationStage == "准备完成" && a.Gate == false &&
                !a.Enabled && a.Issues.Length == 0 && a.Stationary,
                "等待本次准备完成后的新反馈及连续静止");
            await Stage("reference", new { nodes = new[] { axis.Node } });
            await WaitForState(a => a.Reference.HasValue && a.Gate == false && a.Stationary,
                "确认当前位置已记录为本次相对 0°");
            PositionStartStatus = $"应用本轴响应：{rpm:G} RPM / {acceleration:G}°/s²，不改电流和行程";
            await Stage("settings", new { nodes = new[] { axis.Node }, rpm, acceleration });
            await WaitForState(a => a.Gate == false && a.Stationary && a.HasCommandRange &&
                Math.Abs(a.Rpm - rpm) < 1e-9 && Math.Abs(a.Acceleration - acceleration) < 1e-9,
                "确认所选速度与加减速度已在后端生效");
            if (visionStart)
            {
                axis.VisionSelected = true;
                // 本轴启动是显式映射授权；后端仍核验方向、有效行程、归位及静止参考姿态。
                axis.DirectionConfirmed = axis.RangeConfirmed = axis.HomeConfirmed = axis.ReferenceConfirmed = true;
                var sample = vision.Latest!.Current;
                PositionStartStatus = $"捕获 J{axis.Node}→ID{axis.Node} 基准；仅启动本轴，不执行旧目标";
                await Stage("align", new
                {
                    nodes = new[] { axis.Node }, sample,
                    confirmations = new Dictionary<string, object>
                    { [axis.Node.ToString()] = new { direction = true, range = true, home = true, reference = true } }
                });
                alignedIdentity = sample.Identity;
                alignedNodes = alignedNodes.Append(axis.Node).Distinct().Order().ToArray();
                if (alignedReferences.Length != 3) alignedReferences = sample.Angles.ToArray();
                alignedReferences[axis.Node - 1] = sample.Angles[axis.Node - 1];
                await WaitForState(a => a.Reference.HasValue && a.Gate == false && a.Stationary,
                    "确认视觉基准与电机参考已捕获");
            }
            while (pendingLease is not null) { CompletePendingLease(); Check(); if (pendingLease is not null) await Task.Delay(10); }
            await LeaseAsync(CurrentState!);
            Check();
            await Stage("enable", new { nodes = new[] { axis.Node }, confirmed = true });
            await WaitForState(a => a.Enabled && a.Gate == true && a.MotorMode == 5 && a.Phase == "位置控制" &&
                a.Issues.Length == 0 && a.Stationary && a.Complete,
                "等待启动、位置保持与新鲜反馈确认");
            Check();
            Busy = false;
            if (visionStart)
            {
                await BeginStreamAsync("vision", [axis]);
                Check();
                if (!StreamRunning) throw new IOException("视觉控制流未开启");
                PositionStartStatus = $"AI视觉已启动：J{axis.Node}→ID{axis.Node}；{rpm:G} RPM / {acceleration:G}°/s²";
                Message = PositionStartStatus + "；从本次捕获姿态计算相对转角，其他轴保持原控制；日常停止转动后仍有保持力矩。";
            }
            else
            {
                RealTimeControl = true;
                if (StreamRunning) await BeginStreamAsync("manual", [axis]);
                Check();
                PositionStartStatus = $"位置环已启动：{rpm:G} RPM / {acceleration:G}°/s²；保持当前位置，拖动滑条自动下发角度";
                Message = $"ID{axis.Node} {PositionStartStatus}；仅滑条控制角度。日常停止仅减速保持，不关闭电机输出。";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (epoch == positionStartEpoch && requestedSession == session && axisEpoch == AxisEpoch(axis.Node))
            {
                firstPositionStartFailure ??= JsonSerializer.SerializeToElement(new
                {
                    observedAt = DateTimeOffset.UtcNow,
                    requestedSession,
                    node = axis.Node,
                    stage = failureStage,
                    errorType = ex.GetType().Name,
                    error = ex.Message,
                    intentRevision,
                    stateRevision = CurrentState?.Revision,
                    fresh = StateFreshNow,
                    stateAgeMs = CurrentState is { } s ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - s.Time * 1000 : (double?)null,
                    axis = axis.State,
                    axes = CurrentState?.Axes,
                    uiTickIntervalMs,
                    uiTickDurationMs,
                    maxUiTickDurationMs
                }, Protocol.Json);
                var stopping = ex is TimeoutException or IOException || !CanAuthorize || axis.State.HasDriveFault
                    ? ForceMotorStopAsync("位置环启动异常：" + ex.Message)
                    : StopAxisAsync(axis, "位置环启动未完成：" + ex.Message);
                var stopRevision = intentRevision;
                await stopping;
                if (requestedSession != session || stopRevision != intentRevision) return;
                var stopResult = Message;
                PositionStartStatus = "位置环启动未完成：" + ex.Message;
                Message = PositionStartStatus + "；" + stopResult + "；未重试、未恢复旧目标。";
            }
        }
        finally
        {
            if (requestedSession == session && epoch == positionStartEpoch)
            {
                preparingPosition = false;
                StartingPositionNode = structuralNode = 0;
                Busy = false;
                RefreshCommands();
            }
        }
    }

    internal string AbnormalRecoveryBlockReason(AxisViewModel axis, bool inProgress = false)
    {
        if (disposed || !Connected || !axis.State.Connected) return "本轴未连接，不能解锁";
        if (!inProgress && axis.State.Fault != 4) return "仅允许处置单独急停 Fault=0x4；其他故障不清除，须排查原因";
        if (!StateFreshNow || !axis.State.Valid || CurrentState?.SessionId != session ||
            !ReferenceEquals(axis.State, CurrentState.Axes.FirstOrDefault(a => a.Node == axis.Node)))
            return "反馈过期或未知，不能解锁";
        if ((!inProgress && (Busy || abnormalRecoveryNode != 0)) || RecoveryPending || UnownedStopPending)
            return "恢复或上一操作正在核验";
        if (Active || StreamRunning || CurrentState.Active || CurrentState.Axes.Any(a => a.Enabled))
            return "须先停止所有轴输出和控制流";
        if (CurrentState.Axes.Any(a => a.Phase is "停止待确认" or "停机未确认") ||
            CurrentState.Axes.Where(a => a.Connected).Any(a => !a.Valid || a.Gate != false))
            return "等待所有在线轴的新鲜门极关闭及停止确认";
        if (motorProtectionNodes.Any(n => CurrentState.Axes.FirstOrDefault(a => a.Node == n)?.Connected != true))
            return "受保护轴仍失联；须核对实际停机后手动重连";
        if (axis.State.Issues.Length > 0) return "参数安全校验异常，须先排查";
        if (axis.State.HasDriveFault && (axis.State.Fault != 4 || axis.State.Error != 9 ||
            (axis.State.HeartbeatFaultFlags & ~1) != 0))
            return "仅允许解锁单独 CAN 急停 Fault=4 / Error=9；其他驱动故障须排查";
        if (NeedsConnectionRecovery)
            return string.IsNullOrWhiteSpace(Port) ? "缺少原连接端口，须手动重连" : "";
        return axis.State.HasDriveFault ? axis.State.EstopClearBlockReason : "";
    }

    internal async Task RecoverAxisAsync(AxisViewModel axis)
    {
        if (!axis.IsAbnormal) return;
        var reason = AbnormalRecoveryBlockReason(axis);
        if (reason.Length > 0) { Message = "解锁未提交：" + reason; return; }
        var token = ++abnormalRecoveryToken;
        abnormalRecoveryNode = axis.Node;
        var epoch = positionStartEpoch;
        var requestedSession = session;
        try
        {
            if (NeedsConnectionRecovery)
            {
                epoch = positionStartEpoch + 1;
                if (!await RestoreConnectionAsync()) return;
                requestedSession = session;
            }
            if (disposed || token != abnormalRecoveryToken || epoch != positionStartEpoch || requestedSession != session) return;
            Busy = true;
            reason = AbnormalRecoveryBlockReason(axis, inProgress: true);
            if (reason.Length > 0) { Message = "解锁未完成：" + reason; return; }
            ClearLocalStream(); ClearAlignment();
            foreach (var a in Axes) { a.ResetMapping(); a.Draft = "0"; }
            if (axis.State.HasDriveFault)
                await IntentAsync("clear_estop", new { nodes = new[] { axis.Node }, requestedBy = "abnormal_button" }, true);
            else
                Message = $"ID{axis.Node} 本地锁存已恢复；未使能、未恢复旧目标，请显式启动。";
        }
        finally
        {
            if (token == abnormalRecoveryToken)
            {
                abnormalRecoveryNode = 0;
                if (epoch == positionStartEpoch && requestedSession == session) Busy = false;
                RefreshCommands();
            }
        }
    }

    private async Task<bool> RestoreConnectionAsync()
    {
        if (!CanRestoreConnection) return false;
        var port = Port;
        ++globalCancellationEpoch;
        var epoch = ++positionStartEpoch;
        var requestedSession = session;
        connectionTransitionEpoch = epoch;
        void CheckRecovery()
        {
            if (disposed || epoch != positionStartEpoch || requestedSession != session)
                throw new OperationCanceledException("恢复连接已取消");
        }
        ClearLocalStream(); ClearAlignment();
        foreach (var a in Axes) { a.ResetMapping(); a.Draft = "0"; }
        Busy = true;
        try
        {
            await bridge.SendAsync("stop", session, ++intentRevision);
            CheckRecovery();
            var reply = await bridge.SendAsync("disconnect", session, ++intentRevision);
            CheckRecovery();
            var stopped = reply?.Deserialize<BridgeState>(Protocol.Json);
            if (stopped is not { Connected: false } || stopped.Axes.Any(a => a.Phase == "停机未确认"))
                throw new IOException("断开后停机未确认；未重新连接");
            Connected = false;
            Port = port;
            await OpenMotorSessionAsync();
            requestedSession = session;
            CheckRecovery();
            PositionStartStatus = "已恢复连接，尚未启动；驱动故障需另行处理";
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (epoch == positionStartEpoch) Message = "恢复连接未完成：" + ex.Message + "；不会自动启动。";
            return false;
        }
        finally
        {
            if (connectionTransitionEpoch == epoch) connectionTransitionEpoch = -1;
            if (epoch == positionStartEpoch && requestedSession == session) Busy = false;
            RefreshCommands();
        }
    }
    public string[] ControlSources { get; } = ["位置控制", "速度控制", "力矩控制", "AI视觉随动"];
    [ObservableProperty] private int controlSourceIndex;
    [ObservableProperty] private bool realTimeControl;
    [ObservableProperty] private string visionStatus = "视觉通道未连接；连接通道不会打开相机或使能电机";
    public string VisionSubscriptionStatus => vision.Connected ? "已订阅" : vision.DesiredConnected ? "重连中" : "未连接";
    public double? VisionCaptureAgeMs => vision.Connected && vision.Latest?.Current is { } sample &&
        double.IsFinite(sample.AgeMs) && sample.AgeMs >= 0 ? sample.AgeMs : null;
    public double? DesktopSnapshotAgeMs
    {
        get
        {
            if (!bridge.Running || CurrentState is not { Version: 1, Mode: "hardware" } state ||
                !double.IsFinite(state.Time) || state.Time <= 0) return null;
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.Time * 1000;
            // 与既有快照判据保持一致：超过50ms的未来时间无效，不能钳成零后显示绿色。
            return age >= -50 ? Math.Max(0, age) : null;
        }
    }
    public double? BackendAcceptedLeaseAgeMs => StateFreshNow && CurrentState is { Connected: true, LeaseCount: > 0, LeaseAgeMs: { } age } &&
        double.IsFinite(age) && age >= 0 ? age + (DesktopSnapshotAgeMs ?? 0) : null;
    private static readonly IBrush HealthUnknown = Brush.Parse("#8091A7"), HealthFresh = Brush.Parse("#70E4B5"), HealthWarning = Brush.Parse("#F3BD69");
    private static bool HealthWithinLimit(double age, double limit) => limit == 300 ? age <= limit : age < limit;
    private static string HealthAgeText(double? age, double limit) => age is { } value ?
        $"{value:0}ms / {limit:0}ms · {(HealthWithinLimit(value, limit) ? "新鲜" : "过期")}" : $"未知 / {limit:0}ms";
    private static IBrush HealthAgeBrush(double? age, double limit) => age is null ? HealthUnknown : HealthWithinLimit(age.Value, limit) ? HealthFresh : HealthWarning;
    public string VisionCaptureHealth => "原始采集 " + HealthAgeText(VisionCaptureAgeMs, 300);
    public string DesktopSnapshotHealth => "桌面快照 " + HealthAgeText(DesktopSnapshotAgeMs, 150);
    public string BackendLeaseHealth => "已接受 lease " + HealthAgeText(BackendAcceptedLeaseAgeMs, 200);
    public IBrush VisionCaptureHealthBrush => HealthAgeBrush(VisionCaptureAgeMs, 300);
    public IBrush DesktopSnapshotHealthBrush => HealthAgeBrush(DesktopSnapshotAgeMs, 150);
    public IBrush BackendLeaseHealthBrush => HealthAgeBrush(BackendAcceptedLeaseAgeMs, 200);
    public string CurrentControlFlowState => !StateFreshNow || CurrentState?.Connected != true ? "未知" :
        motorProtectionReason.Length > 0 || !authorizationValid || CurrentState.Fault.Length > 0 ? "故障" :
        CurrentState.Axes.Any(a => a.Connected && !a.Valid) || CurrentState.Active && BackendAcceptedLeaseAgeMs is not (< 200) ? "未知" :
        CurrentState.Stream.State is "采集等待保持" or "失锁保持" ? CurrentState.Stream.State :
        CurrentState.Axes.Any(a => a.Connected && a.Enabled && a.Holding) ?
            CurrentState.Stream.State == "跟随" ? "正在跟随 · 部分轴日常保持" : "日常保持" :
        CurrentState.Stream.State == "跟随" ? "正在跟随" :
        CurrentState.Stream.State == "未开启" ? "仅预览／未开流" : "未知";
    public string ControlFlowHealth => "控制流 " + CurrentControlFlowState;
    public IBrush ControlFlowHealthBrush => CurrentControlFlowState == "未知" || CurrentControlFlowState == "仅预览／未开流" ? HealthUnknown :
        CurrentControlFlowState == "正在跟随" ? HealthFresh : HealthWarning;
    [ObservableProperty] private string mappingPreview = "等待网页采集、校准及新鲜关节角";
    [ObservableProperty] private string controlStatus = "手动位置控制 · 启动前滑条仅修改待下发值";
    public bool IsManual => ControlSourceIndex == 0;
    public bool IsVision => ControlSourceIndex == 3;
    public bool IsPositionSource => IsManual || IsVision;
    internal string VisionStartBlockReason => GetVisionStartBlockReason(vision.Latest?.Current);
    private string GetVisionStartBlockReason(VisionSample? sample)
    {
        if (!vision.Connected)
            return vision.DesiredConnected ? "视觉通道重连中；仅恢复订阅预览，不自动启动" :
                "请先点击顶部“连接视觉通道”；连接不会打开相机或使能电机";
        if (vision.ChannelInterruption != observedVisionChannelInterruption)
            return "视觉连接或来源代次中断待消费；旧控制授权失效";
        if (vision.Error.Length > 0)
            return "视觉来源不可用：" + vision.Error + "；订阅保留，请在网页重新建立合法采集来源";
        if (vision.SourceInterruption != observedVisionInterruption)
            return "视觉采集状态待消费；保持等待新帧，同来源稳定后自动继续";
        if (vision.SuspensionReason.Length > 0)
            return vision.SuspensionReason + "；等待同源新采集，订阅保留";
        // 仅细化已有禁用原因；不改变有效样本判据或300ms原始采集保护。
        if (sample is { IsSafe: true, Valid: true }) return "";
        if (sample is null)
            return "已订阅，等待采集：尚未收到新鲜 J角；请在网页人工连接相机、校准并开始跟随，无需配对码";
        if (sample.AgeMs > 300)
            return $"原始采集已过期（{sample.AgeMs:0}ms，超过300ms）；请检查网页采集与跟随是否持续更新";
        if (!sample.Valid || sample.FaceCount != 1 || sample.Status != "tracking")
            return "未获得有效单人追踪；请检查相机画面、网页校准和跟随状态，等待新鲜 J角";
        return "J角身份、校准或数据格式无效；请检查网页状态并建立新来源";
    }
    public bool StreamRunning => streamGeneration.Length > 0 || streamOpening;
    public bool CanSelectSource => !Busy && !StreamRunning && Axes.All(a => !a.State.Enabled ||
        StateFreshNow && a.State is { Valid: true, Complete: true, Stationary: true });
    public string RealTimeBlockReason
    {
        get
        {
            if (!IsManual) return "请先暂停并切回手动位置控制";
            if (!Connected) return "请先连接电机；单轴在线即可，不需要相机";
            if (!StateFreshNow) return "桥接反馈过期或未知，禁止实时控制";
            if (RecoveryPending) return "急停清除核验中；成功后须重新记录参考并使能";
            if (UnownedStopPending) return "人工停止核验中；不会自动启动";
            if (!CanAuthorize) return "会话授权或本地保护已锁存，请停止、检查原因并断开重连";
            if (Busy && structuralNode == 0) return "上一操作尚未完成";
            var enabled = Axes.Where(a => a.State.Enabled).ToArray();
            if (enabled.Length == 0)
            {
                var faulted = Axes.FirstOrDefault(a => a.State.Connected && a.State.HasDriveFault);
                return faulted is not null
                    ? $"ID{faulted.Node}：{faulted.EnableBlockReason}；先处理故障，再记录参考并使能"
                    : "请先点击本轴“启动位置环”；单轴在线即可，不需要相机";
            }
            if (enabled.Any(a => !a.State.Valid || a.State.Gate != true || a.State.HasDriveFault || a.State.Issues.Length > 0))
                return "已使能轴的反馈、门极或故障状态异常，禁止实时控制";
            if (enabled.FirstOrDefault(a => !a.State.HasCommandRange) is { } rangeBlocked)
                return $"ID{rangeBlocked.Node}：{rangeBlocked.RangeHint}";
            if (enabled.Any(a => a.State.Phase != "位置控制")) return "等待门极与位置控制反馈确认";
            if (!StreamRunning && enabled.Any(a => !a.State.Stationary || !a.State.Complete))
                return "等待当前目标到位并通过连续静止判定，再开启实时控制";
            return "";
        }
    }
    public bool CanRealTime => RealTimeBlockReason.Length == 0;
    public bool CanToggleRealTime => RealTimeControl || CanRealTime;
    public string RealTimeHint => RealTimeBlockReason.Length > 0 ? "实时控制未就绪：" + RealTimeBlockReason :
        RealTimeControl ? "实时已开启：目标变化即提交；在途只保留最新值，松开后保持仍有力矩" :
        "实时已就绪：勾选后拖动滑条即可自动下发；勾选本身不发送目标";
    private bool VisionAligned => vision.Connected && vision.Error.Length == 0 &&
                vision.SourceInterruption == observedVisionInterruption &&
                vision.ChannelInterruption == observedVisionChannelInterruption && alignedIdentity.Length > 0 &&
        vision.Latest?.Current is { IsSafe: true, Valid: true } sample && sample.Identity == alignedIdentity &&
        alignedNodes.Length > 0 && alignedNodes.All(n => Axes[n - 1] is { VisionSelected: true, MappingConfirmed: true } axis && axis.State.Connected);
    public bool CanEnableForSource(AxisViewModel axis) => IsManual || IsVision && VisionAligned && axis.VisionSelected && alignedNodes.Contains(axis.Node);
    public IAsyncRelayCommand ConnectVisionCommand { get; private set; } = null!;
    public IAsyncRelayCommand DisconnectVisionCommand { get; private set; } = null!;
    public IAsyncRelayCommand AlignVisionCommand { get; private set; } = null!;
    public IAsyncRelayCommand FollowVisionCommand { get; private set; } = null!;


    /// <summary>
    /// 初始化控制相关命令和状态。
    /// </summary>
    private void InitializeControl()
    {
        vision.Changed += WakeControlTargets;
        RestoreConnectionCommand = new AsyncRelayCommand(async () => { await RestoreConnectionAsync(); },
            () => CanRestoreConnection && abnormalRecoveryNode == 0);
        ConnectVisionCommand = new AsyncRelayCommand(async () =>
        {
            try { await vision.ConnectAsync(); }
            catch (Exception ex) { VisionStatus = ex.Message; }
        }, () => !vision.DesiredConnected && !StreamRunning);
        DisconnectVisionCommand = new AsyncRelayCommand(async () =>
        {
            await vision.DisconnectAsync();
            HandleVisionInterruption();
            ClearAlignment();
        }, () => vision.DesiredConnected);
        AlignVisionCommand = new AsyncRelayCommand(AlignVisionAsync, () => IsVision && !Active && !Busy && CanAuthorize &&
            vision.Connected && vision.Error.Length == 0 && vision.SourceInterruption == observedVisionInterruption && vision.ChannelInterruption == observedVisionChannelInterruption &&
            vision.Latest?.Current is { IsSafe: true, Valid: true } && SelectedVisionAxes().Length > 0 &&
            SelectedVisionAxes().All(a => a.CanReference && a.MappingConfirmed));
        FollowVisionCommand = new AsyncRelayCommand(() => BeginStreamAsync("vision", SelectedVisionAxes()), () =>
            IsVision && !Busy && !StreamRunning && CanAuthorize && VisionAligned &&
            SelectedVisionAxes().All(a => a.State.Enabled && !a.State.Holding && a.State.Valid && a.State.HasCommandRange &&
                !a.State.HasDriveFault && a.State.Phase == "位置控制" && a.MappingConfirmed) &&
            Axes.All(a => !a.State.Enabled || a.State is { Stationary: true, Complete: true }));
    }

    partial void OnControlSourceIndexChanged(int value)
    {
        if (StreamRunning) { _ = CancelControlAsync(); }
        ClearAlignment();
        if (RealTimeControl) RealTimeControl = false;
        RefreshCommands();
    }
    partial void OnRealTimeControlChanged(bool value)
    {
        if (value && !CanRealTime) { RealTimeControl = false; return; }
        if (!value && streamSource == "manual") _ = CancelControlAsync();
        RefreshCommands();
    }
    private AxisViewModel[] SelectedVisionAxes() => Axes.Where(a => a.VisionSelected).ToArray();
    public void ClearAlignment()
    {
        alignedIdentity = "";
        alignedNodes = [];
        alignedReferences = [];
        foreach (var axis in Axes) axis.UpdateVision(null, null, false);
        if (streamSource == "vision") _ = IntentAsync("stop", null, true);
        FollowVisionCommand?.NotifyCanExecuteChanged();
    }
    private void ClearLocalStream()
    {
        controlEpoch++;
        streamGeneration = "";
        streamSource = "none";
        streamNodes = [];
        streamOpening = attachingStream = streamRequestPending = streamLeaseHandoffPending = false;
        acknowledgedStreamRevision = -1;
        acknowledgedTargetRevision = -1;
        targetLeaseHandoffPending = false;
        pendingManual.Clear();
        streamAxisTokens.Clear();
        streamSequence = 0;
        lastCapture = -1;
        RealTimeControl = false;
    }
    private async Task CancelControlAsync()
    {
        ClearLocalStream();
        if (CanAuthorize && Active)
            await IntentAsync("pause", new { nodes = Axes.Where(a => a.State.Enabled).Select(a => a.Node).ToArray() }, true);
    }

    public Task CancelDragAsync() => StreamRunning && IsManual ? CancelControlAsync() : Task.CompletedTask;

    // 仅由指针/键盘真实事件或步进按钮调用；绑定更新不进入此入口。
    public void QueueManualTarget(AxisViewModel axis)
    {
        if (!RealTimeControl || !IsManual || !axis.CanExecute) return;
        pendingManual[axis.Node] = axis.DraftValue;
        if (!StreamRunning || !streamNodes.Contains(axis.Node)) _ = BeginStreamAsync("manual", [axis]);
        else WakeControlTargets();
    }

    private async Task AlignVisionAsync()
    {
        var sample = vision.Latest?.Current;
        var axes = SelectedVisionAxes();
        if (!AlignVisionCommand.CanExecute(null) || sample is not { IsSafe: true, Valid: true } || axes.Length == 0) return;
        Busy = true;
        var epoch = controlEpoch;
        var visionInterruption = vision.SourceInterruption;
        var visionChannelInterruption = vision.ChannelInterruption;
        try
        {
            var nodes = axes.Select(a => a.Node).ToArray();
            await bridge.SendAsync("align", session, ++intentRevision, new
            {
                nodes,
                sample,
                confirmations = axes.ToDictionary(a => a.Node.ToString(), a => new
                {
                    direction = a.DirectionConfirmed,
                    range = a.RangeConfirmed,
                    home = a.HomeConfirmed,
                    reference = a.ReferenceConfirmed
                })
            });
            if (epoch != controlEpoch || visionInterruption != vision.SourceInterruption || visionChannelInterruption != vision.ChannelInterruption ||
                vision.Latest?.Current.Identity != sample.Identity) return;
            alignedIdentity = sample.Identity; alignedNodes = nodes; alignedReferences = sample.Angles.ToArray();
            Message = "已捕获两端参考姿态；不会移动电机。逐轴使能后显式开始实机跟随。";
        }
        catch (Exception ex) { ClearAlignment(); Message = "对齐失败：" + ex.Message; }
        finally { Busy = false; RefreshCommands(); }
    }

    private async Task BeginStreamAsync(string source, AxisViewModel[] axes)
    {
        HandleVisionInterruption();
        if (streamOpening || !CanAuthorize || axes.Length == 0) return;
        var attaching = streamGeneration.Length > 0;
        if (attaching && streamSource != source) { Message = "控制来源互斥，请先停止旧来源"; return; }
        var nodes = axes.Select(a => a.Node).Where(n => !attaching || !streamNodes.Contains(n)).ToArray();
        if (nodes.Length == 0) return;
        var operationEpochs = nodes.ToDictionary(n => n, AxisEpoch);
        streamOpening = true;
        attachingStream = attaching;
        if (!attaching) { streamSource = source; streamNodes = nodes; }
        var epoch = controlEpoch;
        var requestedSession = session;
        try
        {
            var sample = source == "vision" ? vision.Latest?.Current : null;
            if (source == "vision" && (!VisionAligned || sample is not { IsSafe: true, Valid: true } || sample.Identity != alignedIdentity))
                throw new InvalidOperationException("视觉对齐或采集已过期");
            streamRevision = ++intentRevision;
            var result = await bridge.SendAsync(attaching ? "stream_attach" : "stream_begin", session, streamRevision,
                new { nodes, source, sample, generation = attaching ? streamGeneration : null });
            if (disposed || epoch != controlEpoch || requestedSession != session || nodes.Any(n => operationEpochs[n] != AxisEpoch(n))) return;
            HandleVisionInterruption();
            if (epoch != controlEpoch) return;
            if (source == "vision" && (!vision.Connected || vision.Error.Length > 0 ||
                vision.Latest?.Current is { } current && current.Identity != alignedIdentity))
                throw new IOException("视觉通道断开或身份变化；禁止沿用本次控制流");
            if (result?.TryGetProperty("revision", out var accepted) != true || accepted.GetInt64() < streamRevision ||
                accepted.GetInt64() > Math.Max(streamRevision, intentRevision))
                throw new IOException("未收到当前开流版本确认");
            streamGeneration = result.Value.GetProperty("generation").GetString() ?? throw new IOException("未收到流代次");
            if (attaching) streamNodes = streamNodes.Concat(nodes).Distinct().Order().ToArray();
            if (result.Value.TryGetProperty("axisTokens", out var tokens))
            {
                foreach (var n in streamNodes)
                    if (tokens.TryGetProperty(n.ToString(), out var value)) streamAxisTokens[n.ToString()] = value.GetInt64();
            }
            else foreach (var n in streamNodes) streamAxisTokens[n.ToString()] = Axes[n - 1].State.ControlToken;
            if (sample is not null && !attaching) lastCapture = sample.CaptureSeq;
            AcceptStructureReply(result, streamRevision);
            streamRevision = accepted.GetInt64();
            streamLeaseHandoffPending = true;
            // 开流确认只唤醒一次 UI 续约；不等待下一次整页刷新，不允许接收线程续租。
            if (Dispatcher.UIThread.CheckAccess()) RenewOpenedStreamLease();
            else await Dispatcher.UIThread.InvokeAsync(RenewOpenedStreamLease, DispatcherPriority.Input);
            if (disposed || epoch != controlEpoch || requestedSession != session || streamRevision != intentRevision) return;
            Message = source == "vision" ? "视觉跟随已授权；归位和保持仍有力矩" : "手动实时流已开启；松开后到达最后目标并保持";
        }
        catch (Exception ex)
        {
            if (!disposed && epoch == controlEpoch && requestedSession == session &&
                nodes.All(n => operationEpochs[n] == AxisEpoch(n)))
            {
                if (!attaching) ClearLocalStream();
                Message = "开启控制流失败：" + ex.Message;
                if (ex is TimeoutException or IOException || !CanAuthorize)
                { if (HasMotorExposure) await ForceMotorStopAsync("控制流开启异常：" + ex.Message); }
                else foreach (var axis in axes) await StopAxisAsync(axis);
            }
        }
        finally
        {
            if (epoch == controlEpoch && requestedSession == session)
            {
                streamOpening = attachingStream = false;
                WakeControlTargets();
                RefreshCommands();
            }
        }
    }

    private void RenewOpenedStreamLease()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (disposed || !streamLeaseHandoffPending || acknowledgedStreamRevision != intentRevision || !StreamRunning) return;
        CompletePendingLease();
        if (!streamLeaseHandoffPending || bridge.Latest is not { } state || state.SessionId != session ||
            !state.Connected || state.Fault.Length > 0 || !Protocol.IsFresh(state, DateTimeOffset.UtcNow) ||
            state.Axes.Any(a => a.Enabled && AxisViewModel.MotorSafetyIssue(a, true).Length > 0)) return;
        _ = LeaseAsync(state);
    }

    // 两种来源只唤醒同一个最新目标入口；接收线程不发电机命令，也不续 UI 租约。
    private void WakeControlTargets()
    {
        if (Interlocked.Exchange(ref controlWakeQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref controlWakeQueued, 0);
            if (!disposed) PublishControlTargets();
        }, DispatcherPriority.Input);
    }

    private string BackendProtectionReason(string requestedSession, long requestedRevision)
    {
        var state = bridge.Latest;
        return state is not null && state.SessionId == requestedSession && state.Revision >= requestedRevision &&
            Protocol.IsFresh(state, DateTimeOffset.UtcNow) && state.Fault.Length > 0 ? "本地保护：" + state.Fault : "";
    }

    private void HandleVisionInterruption()
    {
        var channel = vision.ChannelInterruption;
        if (channel != observedVisionChannelInterruption)
        {
            observedVisionChannelInterruption = channel;
            var controlling = streamSource == "vision" || IsVision && (StartingPositionNode != 0 || Busy && structuralNode != 0);
            // 先撤销旧流和启动；同一 UI tick 随后看到新来源也不能漏保护。
            if (controlling && HasMotorExposure)
                _ = ForceMotorStopAsync(vision.FirstChannelBreak?.Reason ?? "视觉通道或来源代次中断；旧流撤权");
            else
            {
                if (IsVision && StartingPositionNode != 0)
                {
                    ++positionStartEpoch; ++globalCancellationEpoch;
                    StartingPositionNode = structuralNode = 0;
                    preparingPosition = Busy = false;
                }
                if (streamSource == "vision") ClearLocalStream();
                ClearAlignment();
            }
        }
        var interruption = vision.SourceInterruption;
        if (interruption == observedVisionInterruption) return;
        observedVisionInterruption = interruption;
        // 同身份采集暂挂仅等待：后端300ms时钟减速保持，稳定新帧自动恢复。
        // 真实视觉通道断开、身份变化及电机故障仍走独立保护。
        if (IsVision && StreamRunning)
            Message = "视觉暂缺：减速保持等待新采集，同一来源稳定后自动继续，无需重新启动。";
    }

    private void ControlTick()
    {
        HandleVisionInterruption();
        OnPropertyChanged(nameof(VisionSubscriptionStatus));
        var sample = vision.Latest?.Current;
        var visionBlock = GetVisionStartBlockReason(sample);
        VisionStatus = visionBlock.Length > 0 ? "视觉未就绪：" + visionBlock :
            $"视觉已就绪 · 已订阅 · 新鲜J角 · {sample!.TargetKind} · 采集 {sample.CaptureSeq} / {sample.AgeMs:0}ms；仍须核验本轴启动条件";
        var preview = vision.Connected && vision.Error.Length == 0 ? sample : null;
        foreach (var axis in Axes)
            axis.UpdateVision(preview, alignedReferences.Length == 3 ? alignedReferences[axis.Node - 1] : null,
                VisionAligned && alignedNodes.Contains(axis.Node));
        MappingPreview = string.Join("\n", Axes.Select(a => $"J{a.Node}→ID{a.Node} / 方向 {a.State.Direction:+0;-0} / {a.VisionText}"));
        if (RealTimeControl && !StreamRunning && (!CanAuthorize ||
            Axes.Any(a => a.State.Enabled && a.State.HasDriveFault) ||
            !Axes.Any(a => a.State.Enabled && a.State.Valid && a.State.Gate == true && !a.State.HasDriveFault)))
            ClearLocalStream();
        ControlStatus = StreamRunning ? $"{streamSource} · {CurrentState?.Stream.State} · ID {string.Join("/", streamNodes)} · 保持仍有力矩" :
            IsManual ? RealTimeControl ? "手动实时已开启 · 等待拖动已使能轴滑条" : "手动位置控制 · 实时关闭；滑条仅修改待下发值" : IsVision ? "视觉仅预览 · 点击本轴启动：捕获基准、使能并跟随对应 J 角" : "该控制来源尚未实现；请选择位置控制或AI视觉随动";
        if (Environment.TickCount64 >= nextSummaryAt)
        {
            nextSummaryAt = Environment.TickCount64 + 100;
            _ = vision.PublishStatusAsync(CurrentState, StateFreshNow);
        }
        PublishControlTargets();
    }

    private void PublishControlTargets()
    {
        if (disposed) return;
        HandleVisionInterruption();
        if (!StreamRunning) return;
        // 应答只收割一次；目标事件不能在已知续约失败后继续发送，也不能代替续约。
        CompletePendingLease();
        if (!StreamRunning) return;
        var state = bridge.Latest;
        if (state is not null && state.SessionId != session) { ClearLocalStream(); return; }
        var sample = vision.Latest?.Current;
        if (!CanAuthorize || state is null || !Protocol.IsFresh(state, DateTimeOffset.UtcNow) || !state.Connected || state.Fault.Length > 0 ||
            Axes.Any(a => a.State.Enabled && a.State.HasDriveFault) ||
            streamGeneration.Length > 0 && streamNodes.Any(n =>
                state.Axes.FirstOrDefault(a => a.Node == n) is not
                    { Enabled: true, Valid: true, Gate: true, Phase: "位置控制", HasDriveFault: false }) ||
            streamSource == "vision" &&
            (sample is not null && sample.Identity != alignedIdentity || !vision.Connected || vision.Error.Length > 0))
        {
            var reason = BackendProtectionReason(session, streamRevision);
            _ = ForceMotorStopAsync(reason.Length > 0 ? reason :
                streamSource == "vision" ? "视觉控制采集、对齐或通信异常" : "实时控制授权或驱动状态异常");
            return;
        }
        if (streamGeneration.Length == 0 || streamRequestPending || streamLeaseHandoffPending ||
            streamOpening && !attachingStream || Busy && structuralNode == 0) return;
        if (state.Revision >= streamRevision && state.Stream.Generation != streamGeneration)
        { ClearLocalStream(); return; }
        object? payload = null;
        if (streamSource == "manual" && pendingManual.Count > 0)
        {
            var targets = pendingManual.Where(row => streamNodes.Contains(row.Key)).ToDictionary(row => row.Key.ToString(), row => row.Value);
            foreach (var key in targets.Keys) pendingManual.Remove(int.Parse(key));
            if (targets.Count == 0) return;
            payload = new
            {
                generation = streamGeneration,
                seq = ++streamSequence,
                targets,
                axisTokens = new Dictionary<string, long>(streamAxisTokens)
            };
        }
        if (streamSource == "vision" && sample is { IsSafe: true } && sample.CaptureSeq > lastCapture)
        {
            lastCapture = sample.CaptureSeq;
            payload = new { generation = streamGeneration, seq = ++streamSequence, sample,
                axisTokens = new Dictionary<string, long>(streamAxisTokens) };
        }
        if (payload is not null) _ = SendStreamSampleAsync(payload, controlEpoch, state);
    }


    private async Task SendStreamSampleAsync(object payload, long epoch, BridgeState snapshot)
    {
        streamRequestPending = true;
        var requestedSession = session;
        // 成功应答是同会话的版本确认；旧广播不能降级，也不使用尚未确认的乐观意图版本。
        var requestedRevision = ConfirmedControlRevision(snapshot);
        var acknowledgedRevision = acknowledgedStreamRevision;
        var submittedIntentRevision = intentRevision;
        var generation = streamGeneration;
        var source = streamSource;
        var sequence = streamSequence;
        var started = Stopwatch.GetTimestamp();
        try { await bridge.SendAsync("stream_sample", requestedSession, requestedRevision, payload, 150); }
        catch (Exception ex)
        {
            if (!disposed && epoch == controlEpoch && requestedSession == session)
            {
                // 状态广播可能晚于拒绝应答；只使用新鲜同会话、未倒退版本的后端首因。
                var reason = BackendProtectionReason(requestedSession, requestedRevision);
                if (reason.Length == 0 && ex is BridgeCommandRejected rejected)
                {
                    var fault = rejected.ProtectionFault(requestedSession, requestedRevision, DateTimeOffset.UtcNow);
                    if (fault.Length > 0) reason = "本地保护：" + fault;
                }
                if (reason.Length == 0) reason = "连续目标发送异常：" + ex.Message;
                firstStreamSampleFailure ??= JsonSerializer.SerializeToElement(new
                {
                    observedAt = DateTimeOffset.UtcNow,
                    requestedSession,
                    requestedRevision,
                    snapshotRevision = snapshot.Revision,
                    snapshotTime = snapshot.Time,
                    acknowledgedRevision,
                    submittedIntentRevision,
                    generation,
                    source,
                    sequence,
                    requestData = payload,
                    elapsedUntilUiObservedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    errorType = ex.GetType().Name,
                    error = ex.Message,
                    reason,
                    rejectionReply = (ex as BridgeCommandRejected)?.Reply,
                    backendSession = bridge.Latest?.SessionId,
                    backendRevision = bridge.Latest?.Revision,
                    backendFault = bridge.Latest?.Fault,
                    backendFirstProtection = bridge.Latest?.FirstProtection,
                    uiTickIntervalMs,
                    uiTickDurationMs,
                    currentRequestTimeout = (ex as BridgeTimeoutException)?.Diagnostic,
                    firstRequestTimeout = bridge.FirstTimeout
                }, Protocol.Json);
                Message = "连续目标已停止：" + reason;
                await ForceMotorStopAsync(reason);
            }
        }
        finally
        {
            if (epoch == controlEpoch)
            {
                streamRequestPending = false;
                WakeControlTargets();
            }
        }
    }

    private void RefreshControlCommands()
    {
        OnPropertyChanged(nameof(IsManual)); OnPropertyChanged(nameof(IsVision)); OnPropertyChanged(nameof(CanSelectSource)); OnPropertyChanged(nameof(CanRealTime));
        OnPropertyChanged(nameof(CanToggleRealTime)); OnPropertyChanged(nameof(RealTimeBlockReason)); OnPropertyChanged(nameof(RealTimeHint));
        OnPropertyChanged(nameof(NeedsConnectionRecovery)); OnPropertyChanged(nameof(CanRestoreConnection));
        OnPropertyChanged(nameof(ConnectionRecoveryHint)); RestoreConnectionCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(VisionCaptureHealth)); OnPropertyChanged(nameof(DesktopSnapshotHealth));
        OnPropertyChanged(nameof(BackendLeaseHealth)); OnPropertyChanged(nameof(ControlFlowHealth));
        OnPropertyChanged(nameof(VisionCaptureHealthBrush)); OnPropertyChanged(nameof(DesktopSnapshotHealthBrush));
        OnPropertyChanged(nameof(BackendLeaseHealthBrush)); OnPropertyChanged(nameof(ControlFlowHealthBrush));
        ConnectVisionCommand?.NotifyCanExecuteChanged(); DisconnectVisionCommand?.NotifyCanExecuteChanged();
        AlignVisionCommand?.NotifyCanExecuteChanged(); FollowVisionCommand?.NotifyCanExecuteChanged();
    }
}
