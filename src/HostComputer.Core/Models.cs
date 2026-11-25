using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HostComputer.Core;

public sealed record PortInfo
{
    public string Device { get; init; } = "";
    public string Description { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Product { get; init; } = "";
    public int? Vid { get; init; }
    public int? Pid { get; init; }
    public string SerialNumber { get; init; } = "";
    public bool Candidate { get; init; }
    public string Evidence { get; init; } = "";
    public string Display => $"{Device} · {(Description.Length > 0 ? Description : "未知串口")}";
    public override string ToString() => Display;
}

public sealed record Sample(double? Value, double AgeMs, bool Valid);
public sealed record LogEntry(long Id, double Time, int Node, string Text);
public sealed record AxisState
{
    public int Node { get; init; }
    public long ControlToken { get; init; }
    public string PreparationStage { get; init; } = "";
    public JsonElement? PreparationDiagnostics { get; init; }
    public string LocalCancelReason { get; init; } = "";
    public bool Holding { get; init; }
    public string HoldState { get; init; } = "";
    public bool Connected { get; init; }
    public string ConnectionError { get; init; } = "";
    public double? Position { get; init; }
    public double? Absolute { get; init; }
    public double? Velocity { get; init; }
    public double? Planned { get; init; }
    public double? FollowingErrorDeg { get; init; }
    public double? FollowingReferenceAbsolute { get; init; }
    public double FollowingDurationMs { get; init; }
    public double? Target { get; init; }
    public double? Reference { get; init; }
    public double StationaryMs { get; init; }
    public bool Stationary { get; init; }
    public string StationaryReason { get; init; } = "等待后端连续位置静止判定";
    public double StationaryWindowMs { get; init; }
    public int StationarySamples { get; init; }
    public double? StationarySpan { get; init; }
    public double? PositionResolutionDeg { get; init; }
    public string StationarySource { get; init; } = "0x41/float32";
    public double? StationaryResolutionDeg { get; init; }
    public double? EncoderCountAgeMs { get; init; }
    public double? StationaryPathVelocity { get; init; }
    public double? StationaryDriftVelocity { get; init; }
    public string[] PrepareIssues { get; init; } = ["等待位置准备能力校验"];
    public string EstopClearBlockReason { get; init; } = "等待后端急停清除能力校验";
    public string UnownedStopBlockReason { get; init; } = "等待后端人工停止能力校验";
    public double PositionWindowMs { get; init; }
    public double? PositionSpan { get; init; }
    public double? PositionWindowVelocity { get; init; }
    public long TxCount { get; init; }
    public double? TxAbsolute { get; init; }
    public double? TxAgeMs { get; init; }
    public long TxRevision { get; init; } = -1;
    public long StopTxCount { get; init; }
    public double? StopTxAgeMs { get; init; }
    public long StopTxRevision { get; init; } = -1;
    public string StopTxKind { get; init; } = "未发送";
    public string StopTxError { get; init; } = "";
    // 门极关闭的会话内历史确认；缺失字段或失联不能视作当前已停机。
    public double? StopConfirmedAt { get; init; }
    public long StopConfirmedTxCount { get; init; }
    public string SerialNumber { get; init; } = "";
    public bool Enabled { get; init; }
    public int? MotorMode { get; init; }
    public bool? Gate { get; init; }
    public bool Valid { get; init; }
    public bool Complete { get; init; }
    public double? AgeMs { get; init; }
    public string Phase { get; init; } = "未连接";
    public long Fault { get; init; }
    public double? FaultAgeMs { get; init; }
    public int Error { get; init; }
    public double? ErrorAgeMs { get; init; }
    public int? HeartbeatFlags { get; init; }
    public double? HeartbeatAgeMs { get; init; }
    public int HeartbeatFaultFlags { get; init; }
    [JsonIgnore]
    public bool HasDriveFault => Fault != 0 || Error != 0 || HeartbeatFaultFlags != 0;
    public string[] Issues { get; init; } = [];
    public Dictionary<string, Sample> Samples { get; init; } = [];
    public double Low { get; init; } = Protocol.JointMinimumDeg;
    public double High { get; init; } = Protocol.JointMaximumDeg;
    public double? HardLow { get; init; }
    public double? HardHigh { get; init; }
    public double? CommandLow { get; init; }
    public double? CommandHigh { get; init; }
    public double CommandMarginDeg { get; init; }
    public string CommandRangeIssue { get; init; } = "等待后端有效行程核验";
    [JsonIgnore]
    public bool HasCommandRange => CommandRangeIssue.Length == 0 &&
        Protocol.ValidJointRange(Low, High) &&
        double.IsFinite(CommandMarginDeg) && CommandMarginDeg >= .1 &&
        HardLow is { } hl && HardHigh is { } hh && CommandLow is { } cl && CommandHigh is { } ch &&
        new[] { hl, hh, cl, ch }.All(double.IsFinite) &&
        Low <= hl && hl < cl && cl < ch && ch < hh && hh <= High;
    public double Rpm { get; init; } = 100;
    public double Amps { get; init; } = .5;
    public double Acceleration { get; init; } = Protocol.FastAcceleration;
    public int Direction { get; init; } = 1;
}

public sealed record ControlStreamState
{
    public string Generation { get; init; } = "";
    public string Source { get; init; } = "none";
    public int[] Nodes { get; init; } = [];
    public string State { get; init; } = "未开启";
    public bool Aligned { get; init; }
    public Dictionary<string, long> AxisTokens { get; init; } = [];
    public JsonElement? CaptureWait { get; init; }
}

public sealed record VisionSample
{
    public int Schema { get; init; }
    public string Unit { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string VisionSession { get; init; } = "";
    public string Calibration { get; init; } = "";
    public string TargetKind { get; init; } = "";
    public long Seq { get; init; }
    public long CaptureSeq { get; init; }
    public double CapturedAtMs { get; init; }
    public double AgeMs { get; init; }
    public double[] Angles { get; init; } = [];
    public double[] Home { get; init; } = [];
    public bool Valid { get; init; }
    public int FaceCount { get; init; }
    public string Status { get; init; } = "";
    [JsonIgnore]
    public string Identity => $"{Publisher}/{VisionSession}/{Calibration}/{TargetKind}";
    [JsonIgnore]
    public bool IsSafe => HasValidData && AgeMs <= 300 &&
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - CapturedAtMs <= 300;
    [JsonIgnore]
    public bool HasValidData => Schema == 1 && Unit == "deg" && Mode == "face" &&
        new[] { Publisher, VisionSession, Calibration }.All(s => s.Length is > 0 and <= 128) &&
        TargetKind is "face" or "mouth" && Seq is >= 0 and <= 9007199254740991 && CaptureSeq is >= 0 and <= 9007199254740991 &&
        double.IsFinite(CapturedAtMs) && double.IsFinite(AgeMs) && AgeMs >= 0 &&
        CapturedAtMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 50 &&
        FaceCount is 0 or 1 && Status is "tracking" or "no_face" or "lost" &&
        (!Valid || FaceCount == 1 && Status == "tracking") && ValidAngles(Angles) && ValidAngles(Home);
    private static bool ValidAngles(double[] values) => values.Length == 3 && values.All(double.IsFinite) &&
        values[0] is >= -135 and <= 135 && values[1] is >= -135 and <= 135 && values[2] is >= 0 and <= 80;
}

public sealed record EstopRecoveryState
{
    public bool Pending { get; init; }
    public int Node { get; init; }
    public long Revision { get; init; }
    public string SessionId { get; init; } = "";
    public string Stage { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool Sent { get; init; }
    public double? RequestedAt { get; init; }
    public double? SentAt { get; init; }
    public double? CompletedAt { get; init; }
    public long OriginalFault { get; init; }
    public int OriginalError { get; init; }
}

public sealed record BridgeState
{
    public int Version { get; init; }
    public string Kind { get; init; } = "state";
    public string SessionId { get; init; } = "";
    public long Revision { get; init; }
    public string Mode { get; init; } = "";
    public bool Connected { get; init; }
    public bool Active { get; init; }
    public ControlStreamState Stream { get; init; } = new();
    public EstopRecoveryState EstopRecovery { get; init; } = new();
    public EstopRecoveryState UnownedStop { get; init; } = new();
    public string Fault { get; init; } = "";
    public JsonElement? FirstProtection { get; init; }
    public double? LeaseAgeMs { get; init; }
    public long LeaseCount { get; init; }
    public long LastLeaseRevision { get; init; } = -1;
    public double PeriodMs { get; init; }
    public double Time { get; init; }
    public AxisState[] Axes { get; init; } = [];
    public LogEntry[] Logs { get; init; } = [];
}

public static class Protocol
{
    // 三个电机轴共用相对行程上限；视觉模型角度另按几何约束校验。
    public const double JointMinimumDeg = -135;
    public const double JointMaximumDeg = 135;
    public const double FastAcceleration = 360;
    public const int MaxLine = 262144;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool IsFresh(BridgeState? state, DateTimeOffset now) => state is { Version: 1 } &&
        now.ToUnixTimeMilliseconds() / 1000.0 - state.Time is >= -.1 and < .15;
    public static bool ValidJointRange(double low, double high) =>
        double.IsFinite(low) && double.IsFinite(high) &&
        JointMinimumDeg <= low && low < high && high <= JointMaximumDeg;
    public static bool ValidResponse(double rpm, double acceleration) =>
        double.IsFinite(rpm) && rpm is >= 1 and <= 32767 && rpm == Math.Truncate(rpm) &&
        double.IsFinite(acceleration) && acceleration > 0 && acceleration <= FastAcceleration;
    public static bool ValidStep(double step) => double.IsFinite(step) && step is >= .1 and <= 10;
    public static bool ValidTarget(double value, double low, double high) =>
        double.IsFinite(value) && double.IsFinite(low) && double.IsFinite(high) && low < high && value >= low && value <= high;
    public static bool CanEnable(BridgeState? state, AxisState axis, bool confirmed) =>
        state is { Mode: "hardware", Connected: true, Fault: "" } && confirmed && axis.Connected && axis.Valid && axis.Reference.HasValue &&
        IsFresh(state, DateTimeOffset.UtcNow) && !state.EstopRecovery.Pending && !state.UnownedStop.Pending && axis.Stationary &&
        !axis.Enabled && axis.Gate == false && !axis.HasDriveFault && axis.Issues.Length == 0 && axis.HasCommandRange;
}

public sealed record CurvePoint(double Time, string Mode, int Node, double? Target, double? Position, double? Velocity, double? Current);
public sealed class CurveBuffer(int capacity = 3600)
{
    private readonly Queue<CurvePoint> points = new();
    private IReadOnlyList<CurvePoint>? snapshot;
    public IReadOnlyList<CurvePoint> Points => snapshot ??= Array.AsReadOnly(points.ToArray());
    public void Add(CurvePoint point)
    {
        snapshot = null;
        points.Enqueue(point);
        while (points.Count > capacity || points.Count > 0 && points.Peek().Time < point.Time - 60)
            points.Dequeue();
    }
    public void Clear() { points.Clear(); snapshot = null; }
    public string ToCsv()
    {
        var text = new StringBuilder("time_unix_s,mode,node,target_deg,position_deg,velocity_deg_s,iq_A\r\n");
        foreach (var p in points)
            text.AppendLine(string.Join(',', N(p.Time), p.Mode == "hardware" ? "hardware" : "unknown", p.Node,
                N(p.Target), N(p.Position), N(p.Velocity), N(p.Current)));
        return text.ToString();
    }
    private static string N(double? n) => n?.ToString("G17", CultureInfo.InvariantCulture) ?? "";
}
