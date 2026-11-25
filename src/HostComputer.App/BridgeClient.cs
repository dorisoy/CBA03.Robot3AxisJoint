using HostComputer.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
    double QueueMs, double? WriteMs, double? ReplyWaitMs, double? LatestStateAgeMs)
{
    public string Queue { get; init; } = "";
    public int RequestBytes { get; init; }
    public string WriterState { get; init; } = "未知";
    public string? InflightAction { get; init; }
    public double? InflightAgeMs { get; init; }
    public double? LastWriteAgeMs { get; init; }
    public int UrgentDepth { get; init; }
    public int LeaseDepth { get; init; }
    public int NormalDepth { get; init; }
    public int AvailableWorkers { get; init; }
    public long OriginalDeadlineStamp { get; init; }
    public double? WriteBudgetMs { get; init; }
    public string CancellationResult { get; init; } = "未请求";
}

public sealed class BridgeTimeoutException(BridgeRequestTimeout diagnostic, Exception? inner = null)
    : TimeoutException($"{diagnostic.Action} 超时（限制 {diagnostic.LimitMs}ms；{diagnostic.Stage}；总计 {diagnostic.ElapsedMs:0}ms；排队 {diagnostic.QueueMs:0}ms；写入 {diagnostic.WriteMs:0}ms；应答等待 {diagnostic.ReplyWaitMs:0}ms）", inner)
{
    public BridgeRequestTimeout Diagnostic { get; } = diagnostic;
}

// 不保留请求正文、配对码或凭据；只记录有界传输时序。
public sealed record BridgeTransportEvent(string SessionId, string Action, string Queue, string Stage,
    long Stamp, double ElapsedMs, int RequestBytes, string CancellationResult);

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
    IReadOnlyList<BridgeTransportEvent> RecentTransport => [];
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
    private readonly ConcurrentDictionary<string, Outbound> outboundRequests = new();
    private sealed class Outbound(string line, string id, string action, string session, string queue, long queuedAt, int timeoutMs, bool stop)
    {
        public readonly string Line = line, Id = id, Action = action, Session = session, Queue = queue;
        public readonly byte[] Bytes = Encoding.UTF8.GetBytes(line + "\n");
        public readonly long QueuedAt = queuedAt;
        public readonly long Deadline = queuedAt + (long)(timeoutMs * (double)Stopwatch.Frequency / 1000);
        public readonly int TimeoutMs = timeoutMs;
        public readonly bool Stop = stop;
        public long WriteStarted, WriteCompleted;
        public double WriteBudgetMs;
    }
    private readonly Channel<Outbound> normal = Channel.CreateBounded<Outbound>(32);
    private readonly Channel<Outbound> urgent = Channel.CreateBounded<Outbound>(4);
    private readonly Channel<Outbound> leases = Channel.CreateBounded<Outbound>(4);
    private readonly AutoResetEvent writeReady = new(false);
    private readonly object transportGate = new();
    private readonly Queue<BridgeTransportEvent> transportHistory = new();
    private Outbound? inflight;
    private long lastWrite;
    private string writerState = "未启动", cancellationResult = "未请求";
    private Task? monitorTask, recoveryTask;
    private readonly ManualResetEventSlim writerExited = new(false);
    private NativeThreadHandle? writerHandle;
    private int failed, disposing;
    public IReadOnlyList<BridgeTransportEvent> RecentTransport
    {
        get { lock (transportGate) return transportHistory.ToArray(); }
    }
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
        if (Interlocked.CompareExchange(ref failed, 1, 0) != 0) return;
        Volatile.Write(ref error, message + "；停机未确认，须检查硬件急停与机械支撑");
        lifetime.Cancel();
        writeReady.Set();
        foreach (var tcs in requests.Values) tcs.TrySetException(new IOException(Error));
        // 关闭 stdin 不能与阻塞中的写入同步竞争；独立回收仅处理本应用创建的子进程。
        lock (transportGate)
            recoveryTask ??= Task.Factory.StartNew(RecoverOwnedProcess, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
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
                    {
                        if (outboundRequests.TryGetValue(id.GetString()!, out var outbound)) RecordTransport(outbound, "收到应答");
                        tcs.TrySetResult(obj.Clone());
                    }
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
            if (OperatingSystem.IsWindows())
            {
                var handle = NativeThreadHandle.OpenCurrent();
                lock (transportGate) writerHandle = handle;
            }
            lock (transportGate)
                monitorTask = Task.Factory.StartNew(SuperviseWrites, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            while (!lifetime.IsCancellationRequested)
            {
                lock (transportGate) writerState = "等待入队";
                // 独立写线程由入队事件唤醒，不依赖 UI 活动或线程池的轮询定时器。
                if (!urgent.Reader.TryRead(out var line) && !leases.Reader.TryRead(out line) && !normal.Reader.TryRead(out line))
                {
                    writeReady.WaitOne(50);
                    continue;
                }
                lock (transportGate)
                {
                    if (lifetime.IsCancellationRequested) break;
                    var now = Stopwatch.GetTimestamp();
                    if (!line.Stop && (!requests.ContainsKey(line.Id) || now >= line.Deadline))
                    { RecordTransport(line, "写前拒绝过期"); continue; }
                    line.WriteBudgetMs = line.Stop ? 150 : Math.Min(150,
                        Stopwatch.GetElapsedTime(now, line.Deadline).TotalMilliseconds);
                    Interlocked.Exchange(ref line.WriteStarted, now);
                    inflight = line;
                    writerState = OperatingSystem.IsWindows() ? "同步写入" : "异步写入";
                    RecordTransport(line, "开始写入");
                }
                if (lifetime.IsCancellationRequested) break;
                // 开始 I/O 前再核验原请求；监督与真实完成之间仍允许取消竞争，不刷新预算。
                if (!line.Stop && (!requests.ContainsKey(line.Id) || Stopwatch.GetTimestamp() >= line.Deadline))
                {
                    lock (transportGate)
                    {
                        RecordTransport(line, "写前拒绝过期");
                        inflight = null; Interlocked.Exchange(ref line.WriteStarted, 0);
                    }
                    continue;
                }
                if (OperatingSystem.IsWindows())
                {
                    // 唯一写线程直接写 UTF-8 JSON Lines，不等待线程池异步 I/O；部分写入失败不重试。
                    var input = process!.StandardInput.BaseStream;
                    input.Write(line.Bytes);
                    input.Flush();
                }
                else
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(.1, line.WriteBudgetMs)));
                    process!.StandardInput.WriteLineAsync(line.Line.AsMemory(), deadline.Token).GetAwaiter().GetResult();
                    process.StandardInput.FlushAsync(deadline.Token).GetAwaiter().GetResult();
                }
                // 真实完成时间先于诊断锁，正常完成竞争不能因诊断等待被误判为 I/O 超限。
                Interlocked.Exchange(ref line.WriteCompleted, Stopwatch.GetTimestamp());
                var overBudget = false;
                lock (transportGate)
                {
                    lastWrite = line.WriteCompleted;
                    overBudget = Stopwatch.GetElapsedTime(line.WriteStarted, line.WriteCompleted).TotalMilliseconds >= line.WriteBudgetMs;
                    RecordTransport(line, lifetime.IsCancellationRequested || overBudget ? "写入结束但已失效" : "写入完成");
                    inflight = null;
                }
                if (overBudget) { FailWriteTimeout(line); break; }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !lifetime.IsCancellationRequested)
        {
            // 监督取消可立即唤醒写线程；等待监督冻结当次超时后再报告写入错误，避免覆盖首因。
            lock (transportGate) Fail("桥接写入失败：" + ex.Message);
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (transportGate)
            {
                writerState = "已退出";
                if (inflight is { } unfinished) RecordTransport(unfinished, "写线程已退出；不代表机械停止");
                writerHandle?.Dispose(); writerHandle = null;
                inflight = null;
            }
            writerExited.Set();
        }
    }

    private void SuperviseWrites()
    {
        while (!lifetime.IsCancellationRequested)
        {
            Outbound? expired;
            lock (transportGate)
                expired = inflight is { } line && Interlocked.Read(ref line.WriteCompleted) == 0 &&
                    Stopwatch.GetElapsedTime(line.WriteStarted).TotalMilliseconds >= line.WriteBudgetMs ? line : null;
            if (expired is not null)
            {
                FailWriteTimeout(expired);
                if (lifetime.IsCancellationRequested) return;
            }
            // 单调时钟独立监督；不使用依赖线程池的 Timer 作为取消保障。
            writerExited.Wait(5);
        }
    }

    private void FailWriteTimeout(Outbound line)
    {
        lock (transportGate)
        {
            if (lifetime.IsCancellationRequested) return;
            var completed = Interlocked.Read(ref line.WriteCompleted);
            if (completed != 0 && Stopwatch.GetElapsedTime(line.WriteStarted, completed).TotalMilliseconds < line.WriteBudgetMs ||
                completed == 0 && !ReferenceEquals(inflight, line)) return;
            cancellationResult = completed == 0 ? CancelWriter() : "写入已结束但超过原预算；不重试";
            var diagnostic = TimeoutDiagnostic(line, "管道写入");
            Interlocked.CompareExchange(ref firstTimeout, diagnostic, null);
            RecordTransport(line, "写入超限");
            if (requests.TryGetValue(line.Id, out var request)) request.TrySetException(new BridgeTimeoutException(diagnostic));
            // 在解除监督锁前封闭写泵，取消成功不等于 I/O 已退出。
            Fail("桥接管道写入超限；" + cancellationResult);
        }
    }

    private void RecordTransport(Outbound line, string stage)
    {
        lock (transportGate)
        {
            transportHistory.Enqueue(new(line.Session, line.Action, line.Queue, stage, Stopwatch.GetTimestamp(),
                Stopwatch.GetElapsedTime(line.QueuedAt).TotalMilliseconds, line.Bytes.Length, cancellationResult));
            while (transportHistory.Count > 16) transportHistory.Dequeue();
        }
    }

    private BridgeRequestTimeout TimeoutDiagnostic(Outbound line, string? forcedStage = null)
    {
        lock (transportGate)
        {
            var now = Stopwatch.GetTimestamp();
            var started = Interlocked.Read(ref line.WriteStarted);
            var completed = Interlocked.Read(ref line.WriteCompleted);
            double Ms(long from, long to) => Math.Max(0, Stopwatch.GetElapsedTime(from, to).TotalMilliseconds);
            ThreadPool.GetAvailableThreads(out var workers, out _);
            return new(line.Action, forcedStage ?? (started == 0 ? "发送排队" : completed == 0 ? "管道写入" : "等待桥接应答"),
                line.TimeoutMs, Ms(line.QueuedAt, now), Ms(line.QueuedAt, started == 0 ? now : started),
                started == 0 ? null : Ms(started, completed == 0 ? now : completed), completed == 0 ? null : Ms(completed, now),
                Latest is { } state ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.Time * 1000 : null)
            {
                Queue = line.Queue, RequestBytes = line.Bytes.Length, WriterState = writerState,
                InflightAction = inflight?.Action, InflightAgeMs = inflight is { } active ? Ms(active.WriteStarted, now) : null,
                LastWriteAgeMs = lastWrite == 0 ? null : Ms(lastWrite, now),
                UrgentDepth = urgent.Reader.Count, LeaseDepth = leases.Reader.Count, NormalDepth = normal.Reader.Count,
                AvailableWorkers = workers, OriginalDeadlineStamp = line.Deadline,
                WriteBudgetMs = started == 0 ? null : line.WriteBudgetMs, CancellationResult = cancellationResult
            };
        }
    }

    private string CancelWriter()
    {
        // transportGate 保护 SafeHandle 生命周期；仅取消本实例独立写线程。
        if (!OperatingSystem.IsWindows()) return "非Windows由有界异步路径取消";
        if (writerHandle is null) return "写线程已结束或尚未启动";
        return writerHandle.Cancel();
    }

    private void RecoverOwnedProcess()
    {
        var owned = process;
        if (owned is null) return;
        var started = Stopwatch.GetTimestamp();
        try
        {
            // ERROR_NOT_FOUND 可发生在开始写入前的竞争中，持续核验并再次请求取消；不重试 JSON。
            while (writerTask is not null && !writerExited.IsSet && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 800)
            {
                lock (transportGate) cancellationResult = CancelWriter();
                writerExited.Wait(5);
            }
            if (writerTask is null || writerExited.IsSet)
            {
                try { owned.StandardInput.Close(); }
                catch (IOException) { /* EOF 关闭失败仍须完成自有进程的有界回收。 */ }
            }
            var remaining = Math.Max(0, 800 - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (!owned.HasExited && !owned.WaitForExit(remaining))
            {
                Volatile.Write(ref error, Error + "；桥接未及时退出，仅回收自有子进程；停机未确认");
                owned.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Volatile.Write(ref error, Error + "；自有桥接回收结果待核验：" + ex.Message); }
    }

    private sealed class NativeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private NativeThreadHandle() : base(true) { }
        public static NativeThreadHandle OpenCurrent()
        {
            var result = OpenThread(0x0001, false, GetCurrentThreadId()); // THREAD_TERMINATE
            if (result.IsInvalid)
            {
                var code = Marshal.GetLastWin32Error();
                result.Dispose(); throw new System.ComponentModel.Win32Exception(code);
            }
            return result;
        }
        public string Cancel()
        {
            if (CancelSynchronousIo(this)) return "已请求取消；仍须核验写线程结束";
            var code = Marshal.GetLastWin32Error();
            return code == 1168 ? "ERROR_NOT_FOUND；完成或开始竞争，仍须核验" : $"取消失败 Win32={code}；仍须核验";
        }
        protected override bool ReleaseHandle() => CloseHandle(handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern NativeThreadHandle OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CancelSynchronousIo(NativeThreadHandle handle);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
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
                if (Volatile.Read(ref failed) == 0) Volatile.Write(ref error, new string(chars, 0, n));
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
            var outbound = new Outbound(line, id, action, session,
                ReferenceEquals(channel, urgent) ? "urgent" : ReferenceEquals(channel, leases) ? "lease" : "normal",
                queuedAt, timeoutMs, ReferenceEquals(channel, urgent));
            lock (transportGate)
            {
                if (!Running) throw new IOException("桥接已失效：" + Error);
                if (!channel.Writer.TryWrite(outbound)) throw new IOException("桥接发送队列已满");
                outboundRequests[id] = outbound;
                RecordTransport(outbound, "入队");
            }
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
                var diagnostic = ex is BridgeTimeoutException timeout ? timeout.Diagnostic : TimeoutDiagnostic(outbound);
                Interlocked.CompareExchange(ref firstTimeout, diagnostic, null);
                RecordTransport(outbound, "请求超时：" + diagnostic.Stage);
                throw ex is BridgeTimeoutException ? ex : new BridgeTimeoutException(diagnostic, ex);
            }
            if (!reply.GetProperty("ok").GetBoolean())
                throw new BridgeCommandRejected(reply.GetProperty("error").GetString() ?? "桥接请求被拒绝", reply);
            return reply.TryGetProperty("data", out var payload) ? payload.Clone() : null;
        }
        finally { requests.TryRemove(id, out _); outboundRequests.TryRemove(id, out _); }
    }

    /// <summary>
    /// 异步释放桥接客户端；若桥接正在运行，会尝试发送 disconnect 请求并等待退出，超时则强制杀死子进程。
    /// </summary>
    /// <returns></returns>
    public async ValueTask DisposeAsync()
    {
        if (process == null || Interlocked.Exchange(ref disposing, 1) != 0) return;
        if (Running && Latest is { } state)
        {
            try { await SendAsync("disconnect", state.SessionId, state.Revision + 1, timeoutMs: 700).ConfigureAwait(false); }
            catch (Exception) { }
        }
        lifetime.Cancel(); writeReady.Set();
        Task recovery;
        lock (transportGate)
        {
            cancellationResult = CancelWriter();
            recovery = recoveryTask ??= Task.Factory.StartNew(RecoverOwnedProcess, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        try { await recovery.WaitAsync(TimeSpan.FromMilliseconds(1100)).ConfigureAwait(false); }
        catch (TimeoutException) { Volatile.Write(ref error, "自有桥接回收等待超限；停机未确认"); }
        if (writerTask is not null)
        {
            try { await writerTask.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false); }
            catch (TimeoutException) { Volatile.Write(ref error, "桥接写线程退出未确认；停机未确认，不再发令，须检查硬件急停与支撑"); }
        }
        if (monitorTask is not null)
        {
            try { await monitorTask.WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false); }
            catch (TimeoutException) { Volatile.Write(ref error, "桥接监督线程退出未确认；停机未确认"); }
        }
        if (writerTask is null || writerExited.IsSet) { process.Dispose(); process = null; }
    }
}
