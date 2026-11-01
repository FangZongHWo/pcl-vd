using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Mcdl.Core.Net;

/// <summary>
/// 分片多线程下载引擎，行为对齐 PCL2 的 <c>NetTaskManager</c>：
/// <list type="bullet">
/// <item>全局限线程数（不是每文件），默认 32，等于 PCL2 的「下载线程数 + 1」。</item>
/// <item>动态分片：只有当总速度低于阈值、且某个分片剩余量 ≥ 256 KB 时才切分，
/// 从该分片剩余区间的尾部 40% 处切一刀（PCL2 <c>StartPosition = DownloadEnd - DownloadUndone * 0.4</c>）。</item>
/// <item>分片用 <c>Range: bytes=start-</c> 开放式请求，靠「链式端点」截断写入，避免多发一次请求。</item>
/// <item>严格校验 <c>ContentLength == 文件大小 - 起始点</c>，不符合就判定该源不支持 Range，
/// 该源降级为单线程（对应 PCL2 的 <c>RangeNotSupportedException</c>）。</item>
/// <item>多镜像轮换、分片写临时文件、合并后做 SHA1 校验，失败重试三次。</item>
/// </list>
/// </summary>
public sealed class SegmentedDownloader
{
    /// <summary>全局并发连接数上限。</summary>
    public int MaxThreads { get; init; } = 32;

    /// <summary>可切分的最小剩余量（PCL2 的 FilePieceLimit = 256 KB）。</summary>
    public long PieceLimit { get; init; } = 256 * 1024;

    /// <summary>总速度低于该值时才会考虑继续切分，速度够了就不再开线程。</summary>
    public long SplitSpeedThreshold { get; init; } = 512 * 1024;

    /// <summary>单个文件合并校验失败后的整体重试次数。</summary>
    public int MaxFileRetries { get; init; } = 3;

    /// <summary>小于该大小的文件不分片，避免 HTTP 开销大于收益。</summary>
    public long MinSplitSize { get; init; } = 4 * 1024 * 1024;

    /// <summary>单个文件最多切几片。</summary>
    public int MaxSegmentsPerFile { get; init; } = 12;

    public Action<string>? Log { get; init; }

    public Task<DownloadResult> DownloadAsync(
        IReadOnlyList<DownloadFile> files,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken ct = default)
        => new Session(this, files, onProgress, ct).RunAsync();

    private sealed class Session
    {
        private readonly SegmentedDownloader _opt;
        private readonly List<FileJob> _jobs;
        private readonly Action<DownloadProgress>? _progress;
        private readonly CancellationToken _ct;
        private readonly object _lock = new();

        private long _done;
        private long _total;
        private int _active;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastDoneAtReport;
        private long _lastReportMs;
        private double _speed;
        private string _currentFile = "";
        private readonly List<string> _errors = new();

        public Session(SegmentedDownloader opt, IReadOnlyList<DownloadFile> files,
            Action<DownloadProgress>? progress, CancellationToken ct)
        {
            _opt = opt;
            _jobs = files.Select(f => new FileJob(f)).ToList();
            _progress = progress;
            _ct = ct;

            // 清单里已知的大小先计入总量，避免进度百分比在前几秒来回跳
            _total = files.Where(f => f.Size > 0).Sum(f => f.Size);
        }

        public async Task<DownloadResult> RunAsync()
        {
            var downloaded = 0;
            var skipped = 0;

            // 1) 已存在的文件直接跳过（断点续传/增量更新的基础）
            foreach (var job in _jobs)
            {
                if (await PrecheckAsync(job).ConfigureAwait(false))
                {
                    skipped++;
                    job.Completed = true;
                }
            }

            Report(force: true);

            // 2) 调度循环
            while (true)
            {
                _ct.ThrowIfCancellationRequested();
                if (_jobs.All(j => j.Failed || j.IsDownloaded)) break;

                while (TryTakeSlot())
                {
                    if (!LaunchOne())
                    {
                        ReleaseSlot();
                        DumpStalledState();
                        break;
                    }
                }

                if (_clock.ElapsedMilliseconds - _lastReportMs >= 200) Report();

                await Task.Delay(25, _ct).ConfigureAwait(false);
            }

            // 3) 合并 + 校验
            foreach (var job in _jobs)
            {
                if (job.Completed) { continue; }
                if (job.Failed) { _errors.Add($"{job.Spec.Name}：{job.LastError}"); continue; }
                if (await FinishAsync(job).ConfigureAwait(false)) downloaded++;
                else _errors.Add($"{job.Spec.Name}：{job.LastError}");
            }

            Report(force: true);
            _opt.Log?.Invoke($"下载结束：新下载 {downloaded}，跳过 {skipped}，失败 {_errors.Count}，" +
                             $"累计 {FormatSize(Interlocked.Read(ref _done))}");
            return new DownloadResult(downloaded, skipped, _errors.Count, _errors);
        }

        // ---------- 预处理 ----------

        private long _lastDumpMs;
        private long _stallSinceMs = -1;

        /// <summary>调度器发现"没活可干但还有文件没完成"时把状态打出来；持续卡住则直接判失败，避免无限空转。</summary>
        private void DumpStalledState()
        {
            // 还有线程在跑就不是卡住，只是"没有新活可派"（比如所有分片都在传输中）
            if (_active > 0)
            {
                _stallSinceMs = -1;
                return;
            }

            if (_clock.ElapsedMilliseconds - _lastDumpMs < 2000) return;
            _lastDumpMs = _clock.ElapsedMilliseconds;
            if (_stallSinceMs < 0) _stallSinceMs = _clock.ElapsedMilliseconds;

            var stuck = _clock.ElapsedMilliseconds - _stallSinceMs;

            lock (_lock)
            {
                foreach (var job in _jobs)
                {
                    if (job.Failed || job.IsDownloaded) continue;

                    var detail = string.Join(", ", job.Segments.Select(s =>
                        $"[{s.Start}+{s.Done}/{job.EndOfLocked(s)}{(s.Running ? " R" : "")}{(s.Fail > 0 ? $" F{s.Fail}" : "")}]"));
                    _opt.Log?.Invoke($"诊断 卡住：{job.Spec.Name} started={job.Started} sizeKnown={job.SizeKnown} " +
                                     $"size={job.Size} segments={job.Segments.Count} done={job.Segments.Sum(s => s.Done)} active={_active} :: {detail}");

                    if (stuck > 20_000)
                    {
                        job.Failed = true;
                        job.LastError = "调度器长时间没有可用工作（可能是所有下载源都不可用）";
                    }
                }
            }
        }

        private async Task<bool> PrecheckAsync(FileJob job)
        {
            var path = job.Spec.TargetPath;
            if (!File.Exists(path)) return false;

            var info = new FileInfo(path);
            if (job.Spec.Size > 0 && info.Length != job.Spec.Size) return false;

            if (!string.IsNullOrEmpty(job.Spec.Sha1))
            {
                var actual = await Task.Run(() => HashFile(path), _ct).ConfigureAwait(false);
                if (!string.Equals(actual, job.Spec.Sha1, StringComparison.OrdinalIgnoreCase)) return false;
            }
            else if (job.Spec.Size <= 0 && info.Length == 0)
            {
                return false;
            }

            job.Size = info.Length;
            Interlocked.Add(ref _done, info.Length);
            if (job.Spec.Size <= 0) Interlocked.Add(ref _total, info.Length);
            return true;
        }

        // ---------- 调度 ----------

        private bool TryTakeSlot()
        {
            lock (_lock)
            {
                if (_active >= _opt.MaxThreads) return false;
                _active++;
                return true;
            }
        }

        private void ReleaseSlot() { lock (_lock) _active--; }

        /// <summary>找一件可做的事并启动。返回 false 表示当前无活可干。</summary>
        private bool LaunchOne()
        {
            FileJob? target; Segment? segment;

            lock (_lock)
            {
                // a) 尚未开始的文件优先
                target = _jobs.FirstOrDefault(j => !j.Completed && !j.Failed && !j.Started);
                segment = null;
                if (target is not null)
                {
                    target.Started = true;
                    segment = new Segment { Start = 0, Url = PickUrl(target, 0) };
                    target.Segments.Add(segment);
                }
                else
                {
                    // b) 有分片被截断/失败 → 重新拉起来（接力）
                    foreach (var job in _jobs)
                    {
                        if (job.Completed || job.Failed) continue;
                        foreach (var seg in job.Segments)
                        {
                            if (seg.Running) continue;
                            if (seg.Start + seg.Done >= job.EndOfLocked(seg)) continue;
                            target = job;
                            segment = seg;
                            break;
                        }
                        if (segment is not null) break;
                    }

                    // c) 速度不够、或者还有空闲线程额度，就再切一刀
                    if (segment is null && (Volatile.Read(ref _speed) < _opt.SplitSpeedThreshold
                                            || _active < _opt.MaxThreads / 2))
                        (target, segment) = TrySplitLocked();
                }

                if (target is null || segment is null) return false;
                segment.Running = true;
            }

            _ = Task.Run(() => RunSegmentAsync(target!, segment!), CancellationToken.None);
            return true;
        }

        private (FileJob?, Segment?) TrySplitLocked()
        {
            foreach (var job in _jobs)
            {
                if (job.Completed || job.Failed || !job.SizeKnown || job.NoSplit || !job.Spec.AllowSplit) continue;

                // 小文件切太碎只会被 HTTP 开销吃掉（模组通常就几百 KB ~ 几 MB）
                if (job.Size < _opt.MinSplitSize) continue;
                if (job.Segments.Count >= _opt.MaxSegmentsPerFile) continue;

                Segment? best = null;
                long bestRemain = 0;
                foreach (var seg in job.Segments)
                {
                    var end = job.EndOfLocked(seg);
                    var remain = end - seg.Start - seg.Done;
                    if (remain >= _opt.PieceLimit && remain > bestRemain)
                    {
                        best = seg;
                        bestRemain = remain;
                    }
                }

                if (best is null) continue;

                var startAt = best.Start + best.Done + (long)(bestRemain * 0.6);
                var fresh = new Segment { Start = startAt, Url = PickUrl(job, job.Segments.Count) };
                job.Segments.Add(fresh);
                job.Segments.Sort((a, b) => a.Start.CompareTo(b.Start));
                return (job, fresh);
            }

            return (null, null);
        }

        private string PickUrl(FileJob job, int i)
        {
            // 默认只用首选源；失败后由 seg.Fail 轮换到备用镜像，避免把请求打到没有该文件的镜像上
            var urls = job.Spec.Urls;
            return urls.Count == 0 ? "" : urls[0];
        }

        // ---------- 单个分片 ----------

        private async Task RunSegmentAsync(FileJob job, Segment seg)
        {
            try
            {
                await TransferAsync(job, seg).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                throw;
            }
            catch (RangeNotSupportedException ex)
            {
                job.NoSplit = true;
                _opt.Log?.Invoke($"源不支持分片，降级单线程：{seg.Url}（{ex.Message}）");
                lock (_lock)
                {
                    var idx = job.Segments.IndexOf(seg);
                    if (idx > 0)
                    {
                        job.Segments.RemoveAt(idx); // 让前一个分片重新吞回这段
                        if (seg.TempPath is not null) TryDelete(seg.TempPath);
                        if (seg.Done > 0) Interlocked.Add(ref _done, -seg.Done);
                        seg.Done = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                seg.Fail++;
                // 换下一个镜像再试
                var urls = job.Spec.Urls;
                if (urls.Count > 1) seg.Url = urls[seg.Fail % urls.Count];

                if (seg.Fail > Math.Max(6, urls.Count * 2))
                {
                    job.Failed = true;
                    job.LastError = ex.Message;
                }
                _opt.Log?.Invoke($"分片失败（第 {seg.Fail} 次）：{job.Spec.Name} @{seg.Start} — {ex.Message}");
            }
            finally
            {
                seg.Running = false;
                ReleaseSlot();
            }
        }

        private async Task TransferAsync(FileJob job, Segment seg)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, seg.Url);
            if (seg.Start > 0) req.Headers.Range = new RangeHeaderValue(seg.Start, null);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));

            using var resp = await HttpUtil.Shared
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.StatusCode}");

            var len = resp.Content.Headers.ContentLength ?? -1;

            if (seg.Start > 0)
            {
                if (resp.StatusCode == HttpStatusCode.OK)
                    throw new RangeNotSupportedException("服务器忽略了 Range 请求");

                if (job.SizeKnown && len >= 0 && len != job.Size - seg.Start)
                    throw new RangeNotSupportedException($"分段长度不符：期望 {job.Size - seg.Start}，实际 {len}");
            }
            else if (len > 0)
            {
                lock (_lock)
                {
                    if (!job.SizeKnown)
                    {
                        job.Size = len;
                        job.SizeKnown = true;
                        if (job.Spec.Size <= 0) Interlocked.Add(ref _total, len);
                    }
                }

                if (job.Spec.Size > 0 && len != job.Spec.Size)
                    _opt.Log?.Invoke($"{job.Spec.Name}：清单大小 {job.Spec.Size}，实际 {len}，按实际处理");
            }

            timeout.CancelAfter(Timeout.InfiniteTimeSpan);

            var dir = Path.GetDirectoryName(job.Spec.TargetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 重试时会从 Start 重新写这个临时文件，之前的进度作废
            lock (_lock)
            {
                if (seg.Done > 0)
                {
                    Interlocked.Add(ref _done, -seg.Done);
                    seg.Done = 0;
                }
            }

            seg.TempPath = Path.Combine(
                Path.GetTempPath(),
                $"mcdl_{Environment.ProcessId}_{job.Id}_{seg.Id}.tmp");

            await using var src = await resp.Content.ReadAsStreamAsync(_ct).ConfigureAwait(false);
            await using var dst = new FileStream(seg.TempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (true)
                {
                    long remaining;
                    lock (_lock) remaining = job.EndOfLocked(seg) - seg.Start - seg.Done;
                    if (remaining <= 0) break;

                    var want = (int)Math.Min(buffer.Length, remaining);
                    // 服务器可能握手成功、给完响应头就再也不发数据，读也要有超时，否则整条流水线会卡死
                    var read = await src.ReadAsync(buffer.AsMemory(0, want), _ct)
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(20), _ct)
                        .ConfigureAwait(false);

                    // 连接提前结束（镜像偶尔会截断响应）：已知长度时当成错误抛出，
                    // 否则会静默地无限重连，进度卡住但没有任何日志。
                    // 长度未知时读到 EOF 就是正常结束。
                    if (read <= 0)
                    {
                        if (job.SizeKnown && remaining > 0)
                            throw new IOException($"连接提前结束，还差 {remaining} 字节");
                        break;
                    }

                    await dst.WriteAsync(buffer.AsMemory(0, read), _ct).ConfigureAwait(false);

                    lock (_lock) seg.Done += read;
                    Interlocked.Add(ref _done, read);
                    _currentFile = job.Spec.Name;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // 未知长度（服务端没给 Content-Length）：读完即为整个文件
            lock (_lock)
            {
                if (!job.SizeKnown && seg.Start == 0 && seg.Done > 0)
                {
                    job.Size = seg.Done;
                    job.SizeKnown = true;
                    Interlocked.Add(ref _total, seg.Done);
                }
            }
        }

        // ---------- 合并与校验 ----------

        private async Task<bool> FinishAsync(FileJob job)
        {
            for (var attempt = 1; attempt <= _opt.MaxFileRetries; attempt++)
            {
                try
                {
                    var path = job.Spec.TargetPath;
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    var partial = path + ".mcdl-part";
                    await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                                     64 * 1024, FileOptions.Asynchronous))
                    {
                        foreach (var seg in job.Segments.OrderBy(s => s.Start))
                        {
                            if (seg.Done <= 0) continue;
                            await using var input = new FileStream(seg.TempPath!, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
                            await input.CopyToAsync(output, _ct).ConfigureAwait(false);
                        }
                    }

                    var length = new FileInfo(partial).Length;
                    if (job.Spec.Size > 0 && length != job.Spec.Size)
                        throw new IOException($"合并后大小 {length} 与预期 {job.Spec.Size} 不符");

                    if (!string.IsNullOrEmpty(job.Spec.Sha1))
                    {
                        var actual = await Task.Run(() => HashFile(partial), _ct).ConfigureAwait(false);
                        if (!string.Equals(actual, job.Spec.Sha1, StringComparison.OrdinalIgnoreCase))
                            throw new IOException($"SHA1 校验失败（{actual}）");
                    }

                    File.Move(partial, path, overwrite: true);
                    job.Completed = true;
                    Cleanup(job);
                    return true;
                }
                catch (Exception ex)
                {
                    job.LastError = ex.Message;
                    _opt.Log?.Invoke($"{job.Spec.Name} 合并失败（第 {attempt} 次）：{ex.Message}");
                    ResetJob(job);
                    await Task.Delay(500 * attempt, _ct).ConfigureAwait(false);
                    if (attempt < _opt.MaxFileRetries && await RefillAsync(job).ConfigureAwait(false)) continue;
                }
            }

            job.Failed = true;
            return false;
        }

        /// <summary>校验失败后重下：把该文件的分片清空，重新排队。</summary>
        private async Task<bool> RefillAsync(FileJob job)
        {
            job.Completed = false;
            job.Failed = false;
            job.Started = false;
            job.NoSplit = false;
            job.Segments.Clear();

            while (true)
            {
                _ct.ThrowIfCancellationRequested();
                if (job.IsDownloaded || job.Failed) return job.IsDownloaded;

                while (TryTakeSlot())
                {
                    if (!LaunchOne()) { ReleaseSlot(); break; }
                }

                await Task.Delay(25, _ct).ConfigureAwait(false);
            }
        }

        private void ResetJob(FileJob job)
        {
            var lost = job.Segments.Sum(s => s.Done);
            foreach (var seg in job.Segments)
            {
                if (seg.TempPath is not null) TryDelete(seg.TempPath);
                seg.Done = 0;
                seg.Fail = 0;
                seg.Running = false;
                seg.TempPath = null;
            }

            Interlocked.Add(ref _done, -lost);
            job.Started = false;
        }

        private void Cleanup(FileJob job)
        {
            foreach (var seg in job.Segments)
                if (seg.TempPath is not null) TryDelete(seg.TempPath);
        }

        // ---------- 进度 ----------

        private void Report(bool force = false)
        {
            var nowMs = _clock.ElapsedMilliseconds;
            var done = Interlocked.Read(ref _done);

            if (nowMs - _lastReportMs >= 200 || force)
            {
                var deltaBytes = done - _lastDoneAtReport;
                var deltaMs = Math.Max(1, nowMs - _lastReportMs);
                if (_lastReportMs > 0) _speed = deltaBytes * 1000.0 / deltaMs;
                _lastDoneAtReport = done;
                _lastReportMs = nowMs;
            }

            _progress?.Invoke(new DownloadProgress(
                done,
                Interlocked.Read(ref _total),
                _speed,
                _currentFile,
                Volatile.Read(ref _active)));
        }

        public static string FormatSize(long bytes) => bytes switch
        {
            < 0 => "未知",
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.##} MB",
            _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
        };

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* 忽略 */ }
        }

        private static string HashFile(string path)
        {
            using var fs = File.OpenRead(path);
            using var sha1 = SHA1.Create();
            return Convert.ToHexString(sha1.ComputeHash(fs)).ToLowerInvariant();
        }
    }

    // ---------- 状态对象 ----------

    private sealed class FileJob
    {
        public FileJob(DownloadFile spec) { Spec = spec; Id = Interlocked.Increment(ref _seq); }

        private static int _seq;

        public int Id { get; }
        public DownloadFile Spec { get; }
        public long Size { get; set; } = -1;
        public bool SizeKnown { get; set; }
        public bool NoSplit { get; set; }
        public bool Started { get; set; }
        public bool Completed { get; set; }
        public bool Failed { get; set; }
        public string? LastError { get; set; }
        public List<Segment> Segments { get; } = new();

        public bool IsDownloaded
            => Completed
               || (SizeKnown && Segments.Count > 0 && Segments.Sum(s => s.Done) >= Size);

        /// <summary>分片区间右端（开区间），由链表里下一个分片的起点决定——这就是 PCL2 的链式截断。</summary>
        public long EndOfLocked(Segment seg)
        {
            var idx = Segments.IndexOf(seg);
            if (idx >= 0 && idx + 1 < Segments.Count) return Segments[idx + 1].Start;
            return SizeKnown ? Size : long.MaxValue;
        }
    }

    private sealed class Segment
    {
        private static int _seq;
        public Segment() { Id = Interlocked.Increment(ref _seq); }

        public int Id { get; }
        public long Start { get; init; }
        public long Done;
        public bool Running;
        public int Fail;
        public string Url { get; set; } = "";
        public string? TempPath { get; set; }
    }

    private sealed class RangeNotSupportedException(string message) : Exception(message);
}
