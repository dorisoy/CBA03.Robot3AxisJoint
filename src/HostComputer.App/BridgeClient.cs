using HostComputer.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace HostComputer.App;

/// <summary>
/// 桥接请求超时诊断信息
/// </summary>
/// <param name="Action"></param>
/// <param name="Stage"></param>
/// <param name="LimitMs"></param>
/// <param name="ElapsedMs"></param>
/// <param name="QueueMs"></param>
/// <param name="WriteMs"></param>
/// <param name="ReplyWaitMs"></param>
/// <param name="LatestStateAgeMs"></param>
public sealed record BridgeRequestTimeout(string Action, string Stage, int LimitMs, double ElapsedMs,
    double QueueMs, double? WriteMs, double? ReplyWaitMs, double? LatestStateAgeMs);

// 拒绝应答携带后端首保护；保留原异常类别，不能把旧流拒绝当作新的停机首因。
public sealed class BridgeCommandRejected(string message, JsonElement reply) : InvalidOperationException(message)
{
    public JsonElement Reply { get; } = reply.Clone();

    public string ProtectionFault(string session, long revision, DateTimeOffset now)
    {
        if (!Reply.TryGetProperty("sessionId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != session ||
            !Reply.TryGetProperty("revision", out var rev) || rev.ValueKind != JsonValueKind.Number || !rev.TryGetInt64(out var value) || value < revision ||
            !Reply.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.Number || !time.TryGetDouble(out var seconds) || !double.IsFinite(seconds) ||
            !Reply.TryGetProperty("fault", out var fault) || fault.ValueKind != JsonValueKind.String) return "";
        var age = now.ToUnixTimeMilliseconds() - seconds * 1000;
        return age is >= -50 and <= 150 ? fault.GetString() ?? "" : "";
    }
}

/// <summary>
/// 桥接客户端接口
/// </summary>
public interface IBridgeClient : IAsyncDisposable
{
    BridgeState? Latest { get; }
    bool Running { get; }
    string Error { get; }
    BridgeRequestTimeout? FirstTimeout => null;
    void Start();
    Task<JsonElement?> SendAsync(string action, string session, long revision, object? data = null, int timeoutMs = 1000);
}

/// <summary>
/// 桥接客户端实现
/// </summary>
public sealed class BridgeClient : IBridgeClient
{
    private Process? process;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> requests = new();
    private Task? writerTask;
    private sealed class Outbound(string line, string id, long queuedAt, int timeoutMs, bool stop)
    {
        public readonly string Line = line, Id = id;
        public readonly long QueuedAt = queuedAt;
        public readonly int TimeoutMs = timeoutMs;
        public readonly bool Stop = stop;
        public long WriteStarted, WriteCompleted;
    }
    private readonly Channel<Outbound> normal = Channel.CreateBounded<Outbound>(32);
    private readonly Channel<Outbound> urgent = Channel.CreateBounded<Outbound>(4);
    private readonly Channel<Outbound> leases = Channel.CreateBounded<Outbound>(4);
    private readonly AutoResetEvent writeReady = new(false);
    private BridgeRequestTimeout? firstTimeout;
    public BridgeRequestTimeout? FirstTimeout => Volatile.Read(ref firstTimeout);
    private BridgeState? latest;
    private string error = "";
    public BridgeState? Latest => Volatile.Read(ref latest);
    public string Error => Volatile.Read(ref error);
    public bool Running => process is { HasExited: false } && !lifetime.IsCancellationRequested;

    /// <summary>
    ///查找桥接根目录（包含 bridge/server.py 的目录），优先使用 CBA03_HOST_ROOT 环境变量指定的路径
    /// </summary>
    /// <returns></returns>
    /// <exception cref="DirectoryNotFoundException"></exception>
    public static string FindRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CBA03_HOST_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "bridge", "server.py")))
            return Path.GetFullPath(configured);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "bridge", "server.py"))) return dir.FullName;
        throw new DirectoryNotFoundException("找不到 bridge/server.py；请从完整发布目录启动。");
    }

    /// <summary>
    /// 开始桥接客户端；若已启动则忽略。启动失败会抛出异常。
    /// </summary>
    /// <exception cref="FileNotFoundException"></exception>
    /// <exception cref="IOException"></exception>
    public void Start()
    {
        if (process != null) return;
        var root = FindRoot();
        var python = Environment.GetEnvironmentVariable("CBA03_PYTHON") ?? Path.Combine(root, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python)) throw new FileNotFoundException("缺少独立 Python 环境；请先运行 Prepare.ps1，或设置 CBA03_PYTHON。", python);
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-X"); start.ArgumentList.Add("utf8"); start.ArgumentList.Add("-u");
        start.ArgumentList.Add(Path.Combine(root, "bridge", "server.py"));
        process = Process.Start(start) ?? throw new IOException("无法启动 Python 桥接");
        _ = ReadLoopAsync(process.StandardOutput);
        writerTask = WriteLoopAsync();
        _ = LogLoopAsync(process.StandardError);
    }

    /// <summary>
    /// 失败处理：记录错误信息，取消所有挂起请求，关闭标准输入。
    /// </summary>
    /// <param name="message"></param>
    private void Fail(string message)
    {
        Volatile.Write(ref error, message);
        lifetime.Cancel();
        foreach (var tcs in requests.Values) tcs.TrySetException(new IOException(message));
        // EOF 撤销运动授权；不结束其他软件。
        try { process?.StandardInput.Close(); } catch (IOException) { }
    }

    /// <summary>
    /// 读取桥接标准输出的循环；解析 JSON 消息，更新 Latest 或完成请求。
    /// </summary>
    /// <param name="reader"></param>
    /// <returns></returns>
    /// <exception cref="EndOfStreamException"></exception>
    /// <exception cref="IOException"></exception>
    private async Task ReadLoopAsync(StreamReader reader)
    {
        try
        {
            var buffer = new char[4096];
            var line = new StringBuilder();
            while (!lifetime.IsCancellationRequested)
            {
                var count = await reader.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("桥接已退出；实机停机状态须检查");
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] != '\n')
                    {
                        line.Append(buffer[i]);
                        if (line.Length > Protocol.MaxLine) throw new IOException("桥接消息过长");
                        continue;
                    }
                    using var doc = JsonDocument.Parse(line.ToString());
                    line.Clear();
                    var obj = doc.RootElement;
                    if (obj.GetProperty("version").GetInt32() != 1) throw new IOException("桥接版本不符");
                    if (obj.GetProperty("kind").GetString() == "state")
                        Volatile.Write(ref latest, obj.Deserialize<BridgeState>(Protocol.Json));
                    else if (obj.TryGetProperty("requestId", out var id) && requests.TryRemove(id.GetString()!, out var tcs))
                        tcs.TrySetResult(obj.Clone());
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(ex.Message); }
    }


    /// <summary>
    /// 写入桥接标准输入的循环；优先处理 urgent 队列，再处理 normal 队列。每条消息写入后会记录时间戳。
    /// </summary>
    /// <returns></returns>
    private Task WriteLoopAsync() => Task.Factory.StartNew(WriteLoop, CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void WriteLoop()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                // 独立写线程由入队事件唤醒，不依赖 UI 活动或线程池的轮询定时器。
                if (!urgent.Reader.TryRead(out var line) && !leases.Reader.TryRead(out line) && !normal.Reader.TryRead(out line))
                {
                    writeReady.WaitOne(50);
                    continue;
                }
                if (!line.Stop && (!requests.ContainsKey(line.Id) || Stopwatch.GetElapsedTime(line.QueuedAt).TotalMilliseconds >= line.TimeoutMs))
                    continue; // 超时目标、旧租约不能在 UI 切窗后迟到发送。
                Interlocked.Exchange(ref line.WriteStarted, Stopwatch.GetTimestamp());
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(150);
                process!.StandardInput.WriteLineAsync(line.Line.AsMemory(), deadline.Token).GetAwaiter().GetResult();
                process.StandardInput.FlushAsync(deadline.Token).GetAwaiter().GetResult();
                Interlocked.Exchange(ref line.WriteCompleted, Stopwatch.GetTimestamp());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !lifetime.IsCancellationRequested)
        { Fail("桥接写入失败：" + ex.Message); }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 日志循环：读取桥接标准错误输出，记录到 error 字段。
    /// </summary>
    /// <param name="reader"></param>
    /// <returns></returns>
    private async Task LogLoopAsync(StreamReader reader)
    {
        try
        {
            var chars = new char[2048];
            while (await reader.ReadAsync(chars, lifetime.Token).ConfigureAwait(false) is var n && n > 0)
                Volatile.Write(ref error, new string(chars, 0, n));
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// 异步发送请求到桥接；若桥接未运行或请求超时会抛出异常。请求成功返回 JSON 数据，失败返回 null。
    /// </summary>
    /// <param name="action"></param>
    /// <param name="session"></param>
    /// <param name="revision"></param>
    /// <param name="data"></param>
    /// <param name="timeoutMs"></param>
    /// <returns></returns>
    /// <exception cref="IOException"></exception>
    /// <exception cref="TimeoutException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task<JsonElement?> SendAsync(string action, string session, long revision, object? data = null, int timeoutMs = 1000)
    {
        if (!Running) throw new IOException("桥接未运行：" + Error);
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        var queuedAt = Stopwatch.GetTimestamp();
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (requests.Count >= 64) throw new IOException("桥接待应答请求过多");
        requests[id] = tcs;
        try
        {
            var line = JsonSerializer.Serialize(new { version = 1, requestId = id, sessionId = session, revision, action, data }, Protocol.Json);
            var channel = action is "stop" or "stop_axis" or "hold" or "hold_axis" or "safety_stop" or "stop_unowned" or "estop" or "disconnect" or "pause" or "stream_end" ? urgent :
                action == "lease" ? leases : normal;
            var outbound = new Outbound(line, id, queuedAt, timeoutMs, ReferenceEquals(channel, urgent));
            if (!channel.Writer.TryWrite(outbound)) throw new IOException("桥接发送队列已满");
            writeReady.Set();
            JsonElement reply;
            try
            {
                var remainingMs = timeoutMs - Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
                if (remainingMs <= 0) throw new TimeoutException();
                reply = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(remainingMs)).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                var now = Stopwatch.GetTimestamp();
                var completed = Interlocked.Read(ref outbound.WriteCompleted);
                var started = Interlocked.Read(ref outbound.WriteStarted);
                double Ms(long from, long to) => Math.Max(0, Stopwatch.GetElapsedTime(from, to).TotalMilliseconds);
                var stage = started == 0 ? "发送排队" : completed == 0 ? "管道写入" : "等待桥接应答";
                var diagnostic = new BridgeRequestTimeout(action, stage, timeoutMs, Ms(outbound.QueuedAt, now),
                    Ms(outbound.QueuedAt, started == 0 ? now : started),
                    started == 0 ? null : Ms(started, completed == 0 ? now : completed),
                    completed == 0 ? null : Ms(completed, now),
                    Latest is { } state ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.Time * 1000 : null);
                Interlocked.CompareExchange(ref firstTimeout, diagnostic, null);
                throw new TimeoutException($"{action} 超时（限制 {timeoutMs}ms；{stage}；总计 {diagnostic.ElapsedMs:0}ms；排队 {diagnostic.QueueMs:0}ms；写入 {diagnostic.WriteMs:0}ms；应答等待 {diagnostic.ReplyWaitMs:0}ms）", ex);
            }
            if (!reply.GetProperty("ok").GetBoolean())
                throw new BridgeCommandRejected(reply.GetProperty("error").GetString() ?? "桥接请求被拒绝", reply);
            return reply.TryGetProperty("data", out var payload) ? payload.Clone() : null;
        }
        finally { requests.TryRemove(id, out _); }
    }

    /// <summary>
    /// 异步释放桥接客户端；若桥接正在运行，会尝试发送 disconnect 请求并等待退出，超时则强制杀死子进程。
    /// </summary>
    /// <returns></returns>
    public async ValueTask DisposeAsync()
    {
        if (process == null) return;
        if (Running && Latest is { } state)
        {
            try { await SendAsync("disconnect", state.SessionId, state.Revision + 1, timeoutMs: 700).ConfigureAwait(false); }
            catch (Exception) { }
        }
        try { process.StandardInput.Close(); } catch (Exception) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Volatile.Write(ref error, "桥接未及时退出，已回收本应用子进程；停机未确认");
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        lifetime.Cancel();
        writeReady.Set();
        if (writerTask is not null) await writerTask.ConfigureAwait(false);
        process.Dispose();
        process = null;
    }
}
