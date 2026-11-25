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
/// 常驻视觉订阅；连接与来源代次中断独立于同源采集暂挂，不授予电机权限。
/// </summary>
public interface IVisionClient : IAsyncDisposable
{
    bool Connected { get; }
    bool DesiredConnected => Connected;
    long ChannelInterruption => 0;
    VisionChannelBreak? FirstChannelBreak => null;
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
public sealed record VisionChannelBreak(long Sequence, long ObservedAtMs, string Reason);

public sealed class VisionClient : IVisionClient
{
    private WebSocket? socket;
    private CancellationTokenSource? cancellation;
    private Task? receiver;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly Func<CancellationToken, Task<WebSocket>> connector;
    private readonly Func<int, CancellationToken, Task> delay;
    private volatile VisionReceipt? latest;
    private volatile string error = "", desktopSession = "", publisher = "";
    private long sourceInterruption, channelInterruption;
    private volatile VisionChannelBreak? firstChannelBreak;
    private volatile string suspensionReason = "";
    private volatile bool desiredConnected, subscribed, disposed;
    private int sending;
    public bool Connected => subscribed && socket?.State == WebSocketState.Open;
    public bool DesiredConnected => desiredConnected;
    public long ChannelInterruption => Interlocked.Read(ref channelInterruption);
    public VisionChannelBreak? FirstChannelBreak => firstChannelBreak;

    public VisionClient(Func<CancellationToken, Task<WebSocket>>? connector = null,
        Func<int, CancellationToken, Task>? delay = null)
    {
        this.connector = connector ?? OpenSocketAsync;
        this.delay = delay ?? ((ms, token) => Task.Delay(ms, token));
    }
    public static int RetryDelay(int attempt) => attempt >= 4 ? 5000 : 500 << Math.Max(0, attempt);
    private static async Task<WebSocket> OpenSocketAsync(CancellationToken token)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "http://127.0.0.1:5189");
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        try { await ws.ConnectAsync(new Uri("ws://127.0.0.1:5189/ws/joints"), token).ConfigureAwait(false); return ws; }
        catch { ws.Dispose(); throw; }
    }
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
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (desiredConnected) return;
            desiredConnected = true;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            receiver = Task.Run(() => ConnectionLoopAsync(token));
            Changed?.Invoke();
        }
        finally { lifecycle.Release(); }
    }

    private async Task ConnectionLoopAsync(CancellationToken token)
    {
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            WebSocket? ws = null;
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(3000);
            try
            {
                ws = await connector(handshake.Token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                socket = ws;
                var hello = JsonSerializer.SerializeToUtf8Bytes(new { action = "subscription", schema = 2, protocol = 2, role = "desktop" });
                await ws.SendAsync(hello.AsMemory(), WebSocketMessageType.Text, true, handshake.Token).ConfigureAwait(false);
                await ReceiveAsync(ws, token, handshake, () => attempt = 0).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                error = "视觉通道重连中：" + ex.Message;
            }
            finally
            {
                // 单一连接任务先收割旧接收循环，再建立下一代；迟到发送只能终止其自有旧 socket。
                if (subscribed || publisher.Length > 0) BreakChannel(error.Length > 0 ? error : "视觉通道已断开");
                subscribed = false; latest = null; desktopSession = publisher = suspensionReason = "";
                if (ReferenceEquals(socket, ws)) socket = null;
                ws?.Abort(); ws?.Dispose();
                Changed?.Invoke();
            }
            if (token.IsCancellationRequested) break;
            try { await delay(RetryDelay(attempt++), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private void BreakChannel(string reason)
    {
        latest = null;
        var sequence = Interlocked.Increment(ref channelInterruption);
        Interlocked.CompareExchange(ref firstChannelBreak,
            new VisionChannelBreak(sequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), reason), null);
        Changed?.Invoke();
    }

    /// <summary>
    /// 接收线程只更新不可变最新快照；不持有 BridgeClient，也不续 UI 租约。
    /// </summary>
    /// <param name="ws"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    /// <exception cref="IOException"></exception>
    private async Task ReceiveAsync(WebSocket ws, CancellationToken token,
        CancellationTokenSource? handshake = null, Action? onSubscribed = null)
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
                    received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count),
                        !subscribed && handshake is not null ? handshake.Token : token).ConfigureAwait(false);
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
                    case "subscribed":
                        if (row.GetProperty("protocol").GetInt32() != 2 || row.GetProperty("schema").GetInt32() != 2 ||
                            row.GetProperty("role").GetString() != "desktop")
                            throw new IOException("订阅协议不兼容；请同步更新三端 protocol=2");
                        var session = row.GetProperty("desktopSession").GetString();
                        if (string.IsNullOrEmpty(session) || session.Length > 128 || subscribed && session != desktopSession)
                            throw new IOException("桌面订阅会话非法");
                        desktopSession = session;
                        subscribed = true;
                        handshake?.CancelAfter(Timeout.Infinite);
                        onSubscribed?.Invoke();
                        if (publisher.Length == 0) error = "";
                        Changed?.Invoke();
                        break;
                    case "source_selected":
                        if (!subscribed || row.GetProperty("protocol").GetInt32() != 2 || row.GetProperty("schema").GetInt32() != 2)
                            throw new IOException("来源确认协议不兼容");
                        var selected = row.GetProperty("publisher").GetString();
                        if (string.IsNullOrEmpty(selected) || selected.Length > 128) throw new IOException("来源代次非法");
                        if (selected == publisher) break;
                        if (publisher.Length > 0) BreakChannel("视觉来源代次改变；旧控制授权失效");
                        publisher = selected;
                        error = suspensionReason = "";
                        latest = null; lastAccepted = null;
                        Changed?.Invoke();
                        break;
                    case "pair_code":
                    case "paired":
                    case "channel_error":
                    case "error":
                        throw new IOException(row.TryGetProperty("reason", out var reason) ? reason.GetString() :
                            "订阅协议不兼容；请同步更新三端 protocol=2，不支持旧配对码");
                    case "joints":
                        if (!subscribed || row.GetProperty("protocol").GetInt32() != 2) throw new IOException("视觉信封协议非法");
                        if (error.Length > 0 || publisher.Length == 0 || row.GetProperty("publisher").GetString() != publisher) break;
                        var sample = row.Deserialize<VisionSample>(Protocol.Json)!;
                        if (!sample.HasValidData) throw new IOException("视觉数据非法");
                        var previous = lastAccepted;
                        if (previous is not null && (sample.Identity != previous.Identity ||
                            !sample.Home.SequenceEqual(previous.Home)))
                            throw new IOException("视觉 session、校准或归位姿态改变，须建立新来源并重新对齐");
                        if (previous is not null && (sample.Seq <= previous.Seq || sample.CaptureSeq <= previous.CaptureSeq ||
                            sample.CapturedAtMs <= previous.CapturedAtMs)) break;
                        if (!sample.IsSafe)
                        {
                            SuspendSource("桌面接收时原始采集超过 300ms；订阅保留，等待新采集");
                            break;
                        }
                        lastAccepted = sample;
                        suspensionReason = "";
                        latest = new VisionReceipt(sample, Environment.TickCount64);
                        Changed?.Invoke();
                        break;
                    case "source_suspended":
                        if (error.Length > 0) break;
                        if (row.GetProperty("protocol").GetInt32() != 2) throw new IOException("暂挂信封协议非法");
                        if (publisher.Length == 0 || row.GetProperty("publisher").GetString() != publisher) break;
                        SuspendSource(row.GetProperty("reason").GetString() ?? "视觉数据暂挂；订阅保留，等待新采集");
                        break;
                    case "source_invalid":
                        if (row.GetProperty("protocol").GetInt32() != 2) throw new IOException("撤权信封协议非法");
                        if (publisher.Length == 0 || row.GetProperty("publisher").GetString() != publisher) break;
                        if (error.Length == 0) {
                            error = row.GetProperty("reason").GetString() ?? "视觉来源已撤销";
                            BreakChannel(error);
                        }
                        latest = null;
                        Changed?.Invoke();
                        break;
                    default: throw new IOException("未知视觉通道消息");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        { if (error.Length == 0) error = "视觉来源失效：" + ex.Message; }
        finally { latest = null; ws.Abort(); Changed?.Invoke(); }
    }

    private void SuspendSource(string reason)
    {
        var interrupted = suspensionReason.Length == 0;
        latest = null;
        suspensionReason = reason.Length > 0 ? reason : "视觉数据暂挂；订阅保留，等待新采集";
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
        var ws = socket; var session = desktopSession; var owner = cancellation;
        if (!Connected || ws is null || owner is null || session.Length == 0 || Interlocked.Exchange(ref sending, 1) != 0) return;
        try
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "motor_status",
                desktopSession = session,
                state = StatusSnapshot(state, fresh, DateTimeOffset.UtcNow)
            }, Protocol.Json);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
            timeout.CancelAfter(200);
            await ws.SendAsync(data.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(socket, ws))
            {
                error = "视觉摘要发送失败：" + ex.Message;
                subscribed = false;
                BreakChannel(error);
                ws.Abort();
            }
        }
        finally { Volatile.Write(ref sending, 0); }
    }

    /// <summary>
    /// 取消连接意愿并收割唯一连接任务；迟到回调不能重新连接或恢复旧授权。
    /// </summary>
    /// <returns></returns>
    public async Task DisconnectAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            desiredConnected = false;
            cancellation?.Cancel();
            socket?.Abort();
            if (receiver is not null)
                try { await receiver.ConfigureAwait(false); } catch (OperationCanceledException) { }
            receiver = null;
            socket?.Dispose(); socket = null;
            cancellation?.Dispose(); cancellation = null;
            subscribed = false;
            latest = null; desktopSession = publisher = suspensionReason = ""; error = "";
            Changed?.Invoke();
        }
        finally { lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
    }
}
