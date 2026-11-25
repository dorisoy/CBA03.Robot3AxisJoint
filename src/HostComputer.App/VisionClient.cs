using HostComputer.Core;
using System.Net.WebSockets;
using System.Text.Json;

namespace HostComputer.App;

/// <summary>
/// 可视化后端的视觉样本快照，包含采集时间、序列号、身份信息、归位姿态、节点信息和年龄等数据。
/// </summary>
/// <param name="Sample"></param>
/// <param name="ReceivedAt"></param>
public sealed record VisionReceipt(VisionSample Sample, long ReceivedAt)
{
    public VisionSample Current => Sample with
    {
        AgeMs = Math.Max(Sample.AgeMs + Environment.TickCount64 - ReceivedAt,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Sample.CapturedAtMs)
    };
}


/// <summary>
/// 可视化后端的 WebSocket 客户端接口，提供配对码、最新视觉样本、连接状态和错误信息，并支持异步连接、断开和发布状态。
/// </summary>
public interface IVisionClient : IAsyncDisposable
{
    bool Connected { get; }
    string PairCode { get; }
    string Error { get; }
    VisionReceipt? Latest { get; }
    long SourceInterruption => 0;
    string SuspensionReason => "";
    event Action? Changed;
    Task ConnectAsync();
    Task DisconnectAsync();
    Task PublishStatusAsync(BridgeState? state, bool fresh);
}

/// <summary>
/// 接收线程只更新不可变最新快照；不持有 BridgeClient，也不续 UI 租约。
/// </summary>
public sealed class VisionClient : IVisionClient
{
    private ClientWebSocket? socket;
    private CancellationTokenSource? cancellation;
    private Task? receiver;
    private volatile VisionReceipt? latest;
    private volatile string pairCode = "", error = "", desktopSession = "", publisher = "";
    private long codeExpires, sourceInterruption;
    private volatile string suspensionReason = "";
    private int sending;
    public bool Connected => socket?.State == WebSocketState.Open;
    public string PairCode => Environment.TickCount64 < codeExpires ? pairCode : "";
    public string Error => error;
    public VisionReceipt? Latest => latest;
    public long SourceInterruption => Interlocked.Read(ref sourceInterruption);
    public string SuspensionReason => suspensionReason;
    public event Action? Changed;

    /// <summary>
    /// 异步连接到视觉后端的 WebSocket，设置请求头、超时和接收线程，并在失败时记录错误信息。
    /// </summary>
    /// <returns></returns>
    public async Task ConnectAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        error = "";
        cancellation = new CancellationTokenSource();
        socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "http://127.0.0.1:5189");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        timeout.CancelAfter(3000);
        try
        {
            await socket.ConnectAsync(new Uri("ws://127.0.0.1:5189/ws/joints"), timeout.Token).ConfigureAwait(false);
            receiver = ReceiveAsync(socket, cancellation.Token);
        }
        catch (Exception ex) { error = "视觉通道连接失败：" + ex.Message; socket.Abort(); throw; }
    }

    /// <summary>
    /// 接收线程只更新不可变最新快照；不持有 BridgeClient，也不续 UI 租约。
    /// </summary>
    /// <param name="ws"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    /// <exception cref="IOException"></exception>
    private async Task ReceiveAsync(WebSocket ws, CancellationToken token)
    {
        var buffer = new byte[8193];
        VisionSample? lastAccepted = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var count = 0;
                WebSocketReceiveResult received;
                do
                {
                    received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), token).ConfigureAwait(false);
                    count += received.Count;
                    if (received.MessageType == WebSocketMessageType.Close)
                        throw new IOException("视觉通道已由后端关闭；请检查后端日志并重新连接");
                    if (received.MessageType != WebSocketMessageType.Text || count > 8192)
                        throw new IOException("视觉通道消息类型非法或超过 8KiB");
                } while (!received.EndOfMessage);
                using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
                var row = document.RootElement;
                switch (row.GetProperty("type").GetString())
                {
                    case "pair_code":
                        var code = row.GetProperty("code").GetString();
                        var expiresIn = row.GetProperty("expiresIn").GetInt32();
                        if (code is null || code.Length != 4 || code.Any(c => c < '0' || c > '9') || expiresIn is <= 0 or > 300)
                            throw new IOException("配对协议不匹配；请重启最新版视觉后端，使用4位数字配对码");
                        desktopSession = row.GetProperty("desktopSession").GetString()!;
                        codeExpires = Environment.TickCount64 + expiresIn * 1000L;
                        pairCode = code;
                        break;
                    case "paired":
                        publisher = row.GetProperty("publisher").GetString()!;
                        pairCode = "";
                        latest = null;
                        lastAccepted = null;
                        suspensionReason = "";
                        break;
                    case "joints":
                        if (error.Length > 0) break;
                        var sample = row.Deserialize<VisionSample>(Protocol.Json)!;
                        if (!sample.HasValidData || sample.Publisher != publisher)
                            throw new IOException("视觉数据非法或配对身份不符");
                        var previous = lastAccepted;
                        if (previous is not null && (sample.Identity != previous.Identity ||
                            !sample.Home.SequenceEqual(previous.Home)))
                            throw new IOException("视觉 session、校准或归位姿态改变，须重新配对对齐");
                        if (previous is not null && (sample.Seq <= previous.Seq || sample.CaptureSeq <= previous.CaptureSeq ||
                            sample.CapturedAtMs <= previous.CapturedAtMs)) break;
                        if (!sample.IsSafe)
                        {
                            SuspendSource("桌面接收时原始采集超过 300ms；配对保留，等待新采集");
                            break;
                        }
                        lastAccepted = sample;
                        suspensionReason = "";
                        latest = new VisionReceipt(sample, Environment.TickCount64);
                        Changed?.Invoke();
                        break;
                    case "source_suspended":
                        if (error.Length > 0) break;
                        if (publisher.Length == 0 || row.GetProperty("publisher").GetString() != publisher)
                            throw new IOException("视觉暂挂配对身份不符");
                        SuspendSource(row.GetProperty("reason").GetString() ?? "视觉数据暂挂；配对保留，等待新采集");
                        break;
                    case "source_invalid":
                        error = row.GetProperty("reason").GetString() ?? "视觉来源已撤销";
                        latest = null;
                        Changed?.Invoke();
                        break;
                    default: throw new IOException("未知视觉通道消息");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        { if (error.Length == 0) error = "视觉来源失效：" + ex.Message; }
        finally { latest = null; pairCode = ""; ws.Abort(); Changed?.Invoke(); }
    }

    private void SuspendSource(string reason)
    {
        var interrupted = suspensionReason.Length == 0;
        latest = null;
        suspensionReason = reason.Length > 0 ? reason : "视觉数据暂挂；配对保留，等待新采集";
        // 递增标记不会被紧随其后的新帧覆盖；UI 必须先停旧控制流。
        if (interrupted) Interlocked.Increment(ref sourceInterruption);
        Changed?.Invoke();
    }

    /// <summary>
    /// 状态快照，包含模式、连接状态、控制状态、节点信息和轴信息，用于发布给视觉后端。
    /// </summary>
    /// <param name="state"></param>
    /// <param name="fresh"></param>
    /// <param name="now"></param>
    /// <returns></returns>
    public static JsonElement StatusSnapshot(BridgeState? state, bool fresh, DateTimeOffset now)
    {
        fresh = fresh && state?.Mode == "hardware" && Protocol.IsFresh(state, now);
        var lag = state is null ? 0 : Math.Max(0, now.ToUnixTimeMilliseconds() - state.Time * 1000);
        return JsonSerializer.SerializeToElement(new
        {
            schema = 2,
            snapshotAtMs = state is null ? (double?)null : state.Time * 1000,
            mode = "hardware",
            connected = fresh && state?.Connected == true,
            source = state?.Stream.Source ?? "none",
            controlState = state?.Stream.State ?? "未连接",
            nodes = state?.Stream.Nodes ?? [],
            fault = state?.Fault ?? "",
            axes = Enumerable.Range(1, 3).Select(n =>
            {
                var a = state?.Axes.FirstOrDefault(axis => axis.Node == n);
                var valid = fresh && state?.Connected == true && a is { Connected: true, Valid: true, AgeMs: >= 0 } && a.AgeMs + lag <= 200;
                var tx = valid && a!.Enabled && a.TxCount > 0 && a.TxAgeMs is >= 0 && a.TxAgeMs + lag <= 200;
                return new
                {
                    node = n,
                    position = valid && a!.Reference.HasValue ? a.Position : null,
                    absolute = valid ? a!.Absolute : null,
                    target = valid && a!.Enabled && a.Reference.HasValue ? a.Target : null,
                    transmitted = tx && a!.Reference.HasValue ? (a.TxAbsolute - a.Reference) * a.Direction : null,
                    ageMs = a?.AgeMs,
                    txAgeMs = a?.TxAgeMs,
                    valid
                };
            }).ToArray()
        }, Protocol.Json);
    }

    /// <summary>
    /// 异步发布状态快照给视觉后端，包含模式、连接状态、控制状态、节点信息和轴信息。若未连接或正在发送中，则直接返回。
    /// </summary>
    /// <param name="state"></param>
    /// <param name="fresh"></param>
    /// <returns></returns>
    public async Task PublishStatusAsync(BridgeState? state, bool fresh)
    {
        if (!Connected || desktopSession.Length == 0 || Interlocked.Exchange(ref sending, 1) != 0) return;
        try
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "motor_status",
                desktopSession,
                state = StatusSnapshot(state, fresh, DateTimeOffset.UtcNow)
            }, Protocol.Json);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation!.Token);
            timeout.CancelAfter(200);
            await socket!.SendAsync(data.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { error = "视觉摘要发送失败：" + ex.Message; latest = null; socket?.Abort(); Changed?.Invoke(); }
        finally { Volatile.Write(ref sending, 0); }
    }

    /// <summary>
    /// 释放资源并断开连接，取消接收线程，关闭 WebSocket，清空最新快照、配对码、桌面会话和发布者信息。
    /// </summary>
    /// <returns></returns>
    public async Task DisconnectAsync()
    {
        cancellation?.Cancel();
        socket?.Abort();
        if (receiver is not null)
            try { await receiver.ConfigureAwait(false); } catch (OperationCanceledException) { }
        receiver = null;
        socket?.Dispose(); socket = null;
        cancellation?.Dispose(); cancellation = null;
        latest = null; pairCode = ""; desktopSession = ""; publisher = ""; suspensionReason = "";
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
