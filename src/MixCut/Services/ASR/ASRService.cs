using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MixCut.Infrastructure;
using MixCut.Models;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.ASR;

/// <summary>
/// ASR 语音识别服务（whisper.cpp）。对应 macOS 版 ASRService。
/// Windows 版仅支持 whisper.cpp（内置 <c>bin\whisper-cli.exe</c>）。注册为单例服务。
/// </summary>
public sealed class ASRService
{
    private static readonly string[] ModelNames =
        { "ggml-large-v3-turbo", "ggml-medium", "ggml-small", "ggml-base" };

    private static readonly Dictionary<string, string[]> ModelDownloadUrls = new()
    {
        ["ggml-large-v3-turbo"] = new[]
        {
            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin",
        },
        ["ggml-small"] = new[]
        {
            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
        },
        ["ggml-base"] = new[]
        {
            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
        },
    };

    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<ASRService> _logger;

    public ASRService(FFmpegRunner ffmpeg, ILogger<ASRService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>
    /// 全局 whisper-cli 串行信号量。同一时刻只允许一个 whisper-cli 进程运行，
    /// 避免在 4-8 核 CPU 上并行抢资源导致全部超时。
    /// </summary>
    private static readonly SemaphoreSlim WhisperSemaphore = new(1, 1);

    /// <summary>whisper 槽位是否已被占用（前面有视频正在跑 whisper）。ImportViewModel 据此在
    /// ASR 阶段显示「排队中」，避免多视频时后面的卡在 0% 让用户以为卡死（CLAUDE.md §1）。</summary>
    public static bool IsWhisperBusy => WhisperSemaphore.CurrentCount == 0;

    /// <summary>解析 whisper-cli stderr 中 "progress = N%" 的正则。</summary>
    private static readonly Regex ProgressRegex = new(
        @"progress\s*=\s*(\d+)\s*%", RegexOptions.Compiled);

    /// <summary>对视频进行语音识别。</summary>
    /// <param name="videoPath">视频文件路径。</param>
    /// <param name="language">识别语言，默认 zh。</param>
    /// <param name="videoDurationSec">视频时长（秒），用于动态计算 whisper 超时。0 表示未知。</param>
    /// <param name="progress">进度回调，0.0 - 1.0；ASR 内部细分音频提取/模型查找/whisper 运行 3 段。</param>
    public async Task<TranscriptionResult> TranscribeAsync(
        string videoPath, string language = "zh",
        double videoDurationSec = 0,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!BundledBinaries.WhisperAvailable)
        {
            _logger.LogError("Whisper 未找到，语音识别跳过");
            throw AsrException.WhisperNotFound();
        }

        // AVX2 兜底：内置 whisper-cli 用 AVX2 指令集编译，老 CPU 跑会立即 SIGILL (ExitCode=-1073741795 / 0xC000001D)。
        // 启动期 EnvironmentDiagnostics 已经弹过窗，这里再硬挡一道避免真的产生外部进程崩溃 → 让上层捕获到友好异常。
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            _logger.LogError("[CpuDiag] CPU 不支持 AVX2，无法运行内置 whisper-cli；跳过 ASR");
            throw AsrException.CpuNotSupported();
        }

        // Step 1: 提取音频为 16kHz mono WAV。
        progress?.Report(0.02);
        var tempWav = Path.Combine(FileHelper.TempDirectory, $"audio_{Guid.NewGuid():N}.wav");
        try
        {
            await _ffmpeg.ExtractAudioAsync(videoPath, tempWav, cancellationToken: cancellationToken);
            progress?.Report(0.10);

            // Step 2: 查找/下载模型。
            var modelPath = FindModel();
            if (modelPath is null)
            {
                _logger.LogInformation("Whisper 模型未找到，开始自动下载...");
                modelPath = await DownloadModelIfNeededAsync(cancellationToken: cancellationToken);
            }
            progress?.Report(0.12);

            // Step 3: 运行 whisper.cpp（串行 + 重试 1 次）。
            // 串行：避免多个 whisper-cli 抢 CPU 导致全部超时。
            // 重试：whisper.cpp 偶发卡顿/段错误，重试一次往往能成功。
            _logger.LogInformation("等待 whisper 串行槽位（视频 {File}）...", Path.GetFileName(videoPath));
            await WhisperSemaphore.WaitAsync(cancellationToken);
            try
            {
                // ASR 阶段进度区间 0.12 → 1.0，留给 whisper-cli 内部进度。
                var whisperProgress = new Progress<double>(p =>
                    progress?.Report(0.12 + p * 0.88));

                TranscriptionResult result;
                try
                {
                    result = await RunWhisperCppAsync(
                        modelPath, tempWav, language,
                        videoDurationSec, whisperProgress, cancellationToken);
                }
                catch (WhisperRetryableException ex)
                {
                    _logger.LogWarning("Whisper 首次失败（{Reason}），重试一次", ex.Message);
                    progress?.Report(0.15);
                    try
                    {
                        result = await RunWhisperCppAsync(
                            modelPath, tempWav, language,
                            videoDurationSec, whisperProgress, cancellationToken);
                    }
                    catch (WhisperRetryableException ex2)
                    {
                        // 重试也失败 → 抛完整人话（模型名 + 原因 + 换小模型建议），
                        // 让 ImportViewModel 直接判定该视频失败并原样展示（不再拿空文本去跑 AI）。
                        throw AsrException.WhisperStalled(
                            Path.GetFileNameWithoutExtension(modelPath), ex2.Message);
                    }
                }
                progress?.Report(1.0);

                // 健康指标日志（对齐 Mac transcribe 末尾埋点）：
                // 输出原生 segments / words / 最终句数 + ASR 异常告警（单 segment + >8s）。
                var rawCount = result.RawSentences.Count;
                var finalCount = result.Sentences.Count;
                _logger.LogInformation(
                    "ASR 完成: 原生 {Raw} segments / words {Words} / 最终 {Final} 句 / duration {Dur:F1}s",
                    rawCount, result.Words.Count, finalCount, videoDurationSec);
                if (rawCount == 1 && videoDurationSec > 8.0)
                {
                    _logger.LogWarning(
                        "⚠ ASR 输出粒度异常：单段长视频（{Dur:F1}s），用户可在分镜库 → 重做 ASR 重新识别",
                        videoDurationSec);
                }
                return result;
            }
            finally
            {
                WhisperSemaphore.Release();
            }
        }
        finally
        {
            TryDelete(tempWav);
        }
    }

    /// <summary>whisper-cli 临时性失败（超时 / 进程崩溃）—— 触发上层重试。</summary>
    private sealed class WhisperRetryableException : Exception
    {
        public WhisperRetryableException(string message) : base(message) { }
    }

    private async Task<TranscriptionResult> RunWhisperCppAsync(
        string modelPath, string audioPath, string language,
        double videoDurationSec, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var outputPrefix = Path.Combine(FileHelper.TempDirectory, $"whisper_{Guid.NewGuid():N}");
        var jsonPath = outputPrefix + ".json";

        // v0.15.0 超时改「按进度判活」（对齐导出 v0.8.2 的 stall-based 思路）：
        // 旧公式「时长×4，封顶 30min」假设机器不慢 —— 但 4 核轻薄本（i5-1145G7）+ 后台企业软件抢占下
        // large-v3-turbo 实测只有 0.03~0.3× 实时，96s 视频 6.4 分钟才到 12%，被绝对超时反复冤杀
        // （2026-07-24 实测：进程 CPU 一直满负荷、进度一直在涨，却被杀了两次 → 用户以为「卡死」）。
        // 新规则：**进度还在涨就绝不杀**；只有进度停滞 stallLimit 分钟（真挂死）或超出宽松绝对上限
        // （防「永远蠕动」）才中止。whisper --print-progress 每 5% 打一条，慢机 5% 可能要 3-4 分钟，
        // 故 stallLimit 给 10 分钟。
        var stallLimit = TimeSpan.FromMinutes(10);
        var ceiling = videoDurationSec > 0
            ? TimeSpan.FromSeconds(Math.Clamp(videoDurationSec * 60, 1800, 4 * 3600))
            : TimeSpan.FromHours(4);

        // 留 2 个逻辑核给系统 + UI（之前吃满所有核导致风扇狂转）。
        // 8 核 → 6 线程；4 核 → 2 线程；2 核 → 2 线程。
        var threads = Math.Max(2, Environment.ProcessorCount - 2);

        // 关键修复（v0.10.2）：whisper.cpp 用 ANSI fopen 读文件，**任一路径含非 ASCII 就原生崩溃**
        // ExitCode=-1073740791 (0xC0000409 STATUS_STACK_BUFFER_OVERRUN) → 导入全挂在 ASR。触发两类：
        //   ① 用户把数据目录改到「D:\新建文件夹」→ 模型落在中文路径；
        //   ② **Windows 用户名是中文**（国内极常见）→ 默认 %LOCALAPPDATA% 模型目录 + %TEMP% 音频/输出全含中文，
        //      全新安装什么都不改、一导入就崩（首发 0.3.0 潜伏至今，开发/测试机都是英文用户名没暴露）。
        // 彻底绕过：进程工作目录设为模型目录（WorkingDirectory 走 Windows 宽字符 API，中文 OK），
        // 模型 / 音频 / 输出**三者全部用相对 ASCII 文件名**传给 whisper，其 fopen 相对 cwd 打开即可。
        // 音频原在 %TEMP%（中文用户名下也含中文），故非 ASCII 时把音频复制进模型目录、输出也写模型目录，
        // 用相对名喂 whisper（复制仅几 MB、whisper 本就跑几十秒，开销可忽略）；纯 ASCII 环境不复制、零开销。
        // 已实测中文目录 exit=0 且识别正确。whisper-cli 依赖 DLL 仍从其 exe 目录加载，不受 cwd 影响。
        var modelDir = Path.GetDirectoryName(modelPath);
        var haveModelDir = !string.IsNullOrEmpty(modelDir);
        var modelArg = haveModelDir ? Path.GetFileName(modelPath) : modelPath;

        var audioArg = audioPath;
        var ofArg = outputPrefix;
        string? tempAudioInModelDir = null;
        if (haveModelDir && (HasNonAscii(audioPath) || HasNonAscii(outputPrefix)))
        {
            var tag = Guid.NewGuid().ToString("N");
            tempAudioInModelDir = Path.Combine(modelDir!, "asr_" + tag + ".wav");
            File.Copy(audioPath, tempAudioInModelDir, overwrite: true);
            audioArg = "asr_" + tag + ".wav";               // 相对 cwd(=modelDir) 的 ASCII 名
            ofArg = "asr_" + tag;                            // whisper 会写 modelDir\asr_<tag>.json
            jsonPath = Path.Combine(modelDir!, "asr_" + tag + ".json"); // 读取 + 清理都指向真正的输出
        }

        var psi = new ProcessStartInfo
        {
            FileName = BundledBinaries.WhisperCli,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = haveModelDir ? modelDir! : Environment.CurrentDirectory,
        };
        foreach (var arg in new[]
                 {
                     "-m", modelArg, "-f", audioArg, "-l", language,
                     "-t", threads.ToString(CultureInfo.InvariantCulture),
                     "--print-progress",
                     "--output-json-full", "-of", ofArg,
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        ChildProcessTracker.AddProcess(process);
        // 把 whisper 进程优先级降到 BelowNormal，让 UI / 系统抢占（用户不会被风扇响吓到）
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* ignore */ }

        // stdout 直接丢弃（whisper 把识别文本也写 stdout，我们用 JSON 文件结果即可）。
        var drainOut = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);

        // 进度活性追踪：看门狗据此判断「还在干活」还是「真挂死」。
        var lastProgressTicks = DateTime.UtcNow.Ticks;
        var lastPct = 0;

        // stderr 行式读取，解析 "progress = N%" 报告给上层。
        var drainErr = Task.Run(async () =>
        {
            try
            {
                using var reader = process.StandardError;
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                {
                    var m = ProgressRegex.Match(line);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var pct))
                    {
                        if (pct > Volatile.Read(ref lastPct))
                        {
                            Volatile.Write(ref lastPct, pct);
                            Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
                        }
                        progress?.Report(Math.Clamp(pct / 100.0, 0.0, 1.0));
                    }
                }
            }
            catch (Exception)
            {
                // stderr 读取失败不影响主流程
            }
        }, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runSw = Stopwatch.StartNew();
        string? killReason = null;

        // 看门狗：每 10s 巡检一次。进度停滞超 stallLimit 或总时长超 ceiling 才取消，进度在涨绝不杀。
        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (!process.HasExited)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), timeoutCts.Token);
                    if (process.HasExited) return;
                    var pct = Volatile.Read(ref lastPct);
                    var idle = TimeSpan.FromTicks(
                        DateTime.UtcNow.Ticks - Interlocked.Read(ref lastProgressTicks));
                    if (idle > stallLimit)
                    {
                        killReason = $"进度停滞 {idle.TotalMinutes:F0} 分钟（停在 {pct}%）";
                        timeoutCts.Cancel();
                        return;
                    }
                    if (runSw.Elapsed > ceiling)
                    {
                        killReason = $"运行超 {ceiling.TotalMinutes:F0} 分钟仍未完成（当前 {pct}%）";
                        timeoutCts.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { /* 进程退出 / 用户取消，正常收尾 */ }
        });

        _logger.LogInformation(
            "启动 whisper-cli: 视频 {Dur}s, 停滞上限 {Stall}min / 绝对上限 {Ceil}min, threads={T}",
            videoDurationSec, stallLimit.TotalMinutes, ceiling.TotalMinutes, threads);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            _logger.LogError("Whisper 被看门狗中止：{Reason}", killReason ?? "未知原因");
            throw new WhisperRetryableException(killReason ?? "看门狗中止");
        }
        finally
        {
            timeoutCts.Cancel();   // 让看门狗退出巡检循环
        }
        await Task.WhenAll(drainOut, drainErr, watchdog);
        _logger.LogInformation("[AsrSpeedDiag] whisper 完成：{Dur:F0}s 音频用时 {Elapsed:F0}s（{Speed:F2}x 实时）",
            videoDurationSec, runSw.Elapsed.TotalSeconds,
            videoDurationSec > 0 ? videoDurationSec / Math.Max(1, runSw.Elapsed.TotalSeconds) : 0);

        if (process.ExitCode != 0)
        {
            _logger.LogError("Whisper 进程异常退出: ExitCode={Code}", process.ExitCode);
            throw new WhisperRetryableException($"进程异常退出 ExitCode={process.ExitCode}");
        }

        try
        {
            if (!File.Exists(jsonPath))
            {
                _logger.LogWarning("Whisper 未产出 JSON 文件（{Path}），视为空结果", Path.GetFileName(jsonPath));
                return TranscriptionResult.Empty(language);
            }
            var raw = await File.ReadAllTextAsync(jsonPath, Encoding.UTF8, cancellationToken);
            return ParseWhisperCppOutput(raw, language);
        }
        finally
        {
            TryDelete(jsonPath);
            if (tempAudioInModelDir is not null) TryDelete(tempAudioInModelDir); // 清理复制进模型目录的临时音频
        }
    }

    /// <summary>路径是否含非 ASCII 字符（whisper-cli 的 ANSI fopen 遇非 ASCII 会崩，据此决定是否改走相对路径）。</summary>
    private static bool HasNonAscii(string s)
    {
        foreach (var c in s)
        {
            if (c > 127) return true;
        }
        return false;
    }

    // ---- whisper.cpp JSON 解析 ----

    private TranscriptionResult ParseWhisperCppOutput(string raw, string language)
    {
        // whisper.cpp 输出可能含 UTF-8 解码失败残留的 U+FFFD，先清洗。
        var cleaned = raw.Replace("�", string.Empty);

        WhisperCppOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<WhisperCppOutput>(cleaned,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogError("解析 whisper.cpp 输出失败: {Message}", ex.Message);
            return TranscriptionResult.Empty(language);
        }

        if (output?.Transcription is null)
        {
            return TranscriptionResult.Empty(language);
        }

        var words = new List<AsrWord>();
        foreach (var segment in output.Transcription)
        {
            if (segment.Tokens is null)
            {
                continue;
            }
            foreach (var token in segment.Tokens)
            {
                var text = CleanWhisperToken(token.Text);
                if (text.Length == 0 || token.Timestamps is null)
                {
                    continue;
                }
                var start = ParseTimestamp(token.Timestamps.From);
                var end = ParseTimestamp(token.Timestamps.To);
                if (end > start)
                {
                    words.Add(new AsrWord(text, start, end));
                }
            }
        }

        var rawSentences = new List<AsrSentence>();
        foreach (var segment in output.Transcription)
        {
            var text = CleanWhisperText(segment.Text);
            if (text.Length == 0)
            {
                continue;
            }
            rawSentences.Add(new AsrSentence(
                text,
                ParseTimestamp(segment.Timestamps?.From ?? "00:00:00.000"),
                ParseTimestamp(segment.Timestamps?.To ?? "00:00:00.000")));
        }

        var fullText = string.Concat(rawSentences.Select(s => s.Text));
        var duration = words.Count > 0 ? words[^1].End : 0;
        return new TranscriptionResult
        {
            Text = fullText,
            Words = words,
            RawSentences = rawSentences,
            Language = language,
            Duration = duration,
        };
    }

    /// <summary>清洗 whisper.cpp 单个 token：过滤特殊标记、空白、U+FFFD。</summary>
    private static string CleanWhisperToken(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('[') || trimmed.StartsWith("<|", StringComparison.Ordinal))
        {
            return string.Empty;
        }
        var cleaned = trimmed.Replace("�", string.Empty);
        return cleaned.Trim().Length == 0 ? string.Empty : cleaned;
    }

    /// <summary>清洗 whisper.cpp segment 级文本：trim、移除 U+FFFD、合并连续空格。</summary>
    private static string CleanWhisperText(string raw)
    {
        var text = raw.Trim().Replace("�", string.Empty);
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ");
        }
        return text.Trim();
    }

    /// <summary>解析时间戳 "00:00:01.234" 或 "00:00:01,234" → 秒。</summary>
    private static double ParseTimestamp(string str)
    {
        var parts = str.Replace(',', '.').Split(':');
        if (parts.Length != 3)
        {
            return 0;
        }
        double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h);
        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m);
        double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s);
        return h * 3600 + m * 60 + s;
    }

    // ---- 模型查找与下载 ----

    /// <summary>查找 whisper.cpp 模型文件（优先大模型，准确率更高）。</summary>
    public string? FindModel()
    {
        foreach (var name in ModelNames)
        {
            var bundled = Path.Combine(BundledBinaries.BinDirectory, name + ".bin");
            if (File.Exists(bundled))
            {
                return bundled;
            }
            var cached = Path.Combine(AppPaths.WhisperModelsDirectory, name + ".bin");
            if (File.Exists(cached))
            {
                return cached;
            }
        }
        return null;
    }

    /// <summary>模型是否可用。</summary>
    public bool IsModelAvailable() => FindModel() is not null;

    // 设置页下载和素材分析共用同一个临时模型文件，必须覆盖整个下载与最终替换过程。
    private static readonly SemaphoreSlim ModelDownloadGate = new(1, 1);

    /// <summary>下载 whisper 模型到本地缓存目录（自动尝试多个镜像源）。</summary>
    public async Task<string> DownloadModelIfNeededAsync(
        string modelName = "ggml-large-v3-turbo",
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (ModelDownloadGate.CurrentCount == 0)
        {
            _logger.LogInformation("[ModelDownloadDiag] 等待已有模型下载完成，避免重复写入临时文件");
        }
        await ModelDownloadGate.WaitAsync(cancellationToken);
        try
        {
            // 排队期间模型可能已下载完成，进入后重新查缓存；取消等待不会取消前一个下载。
            return await DownloadModelCoreAsync(modelName, onProgress, cancellationToken);
        }
        finally
        {
            ModelDownloadGate.Release();
        }
    }

    private async Task<string> DownloadModelCoreAsync(
        string modelName,
        Action<DownloadProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        if (FindModel() is { } existing)
        {
            return existing;
        }

        if (!ModelDownloadUrls.TryGetValue(modelName, out var urls) || urls.Length == 0)
        {
            throw AsrException.ModelNotFound();
        }

        var destPath = Path.Combine(AppPaths.WhisperModelsDirectory, modelName + ".bin");
        var tempPath = destPath + ".download";

        // 用 SocketsHttpHandler 关闭超时，让大文件长连接不被 .NET 自动断。
        using var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(20) };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        const int maxAttemptsPerUrl = 3;
        Exception? lastError = null;

        var isFirstSource = true;
        foreach (var url in urls)
        {
            var source = url.Contains("hf-mirror", StringComparison.Ordinal) ? "国内镜像" : "HuggingFace";

            // 换源时丢弃上一个源的半成品：续传的 Range 起点是按 tempPath 长度算的，
            // 拿着「从 HuggingFace 下到 300MB」的偏移去问镜像站要后续字节，等于假设两边字节完全一致。
            // 同名模型通常一致，但这是无保障的假设 —— 一旦镜像版本不同就是静默拼接出一个损坏文件。
            if (!isFirstSource && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                    _logger.LogInformation("换源重下，已丢弃上一个源的半成品文件");
                }
                catch (Exception ex) { _logger.LogWarning(ex, "删除半成品失败，忽略"); }
            }
            isFirstSource = false;

            for (var attempt = 1; attempt <= maxAttemptsPerUrl; attempt++)
            {
                _logger.LogInformation("开始下载 Whisper 模型: {Model}（{Source}，第 {Attempt}/{Max} 次）",
                    modelName, source, attempt, maxAttemptsPerUrl);
                try
                {
                    var resumeFrom = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (resumeFrom > 0)
                    {
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
                    }

                    onProgress?.Invoke(new DownloadProgress(
                        resumeFrom > 0 ? 0.05 : 0.02, resumeFrom, 0));

                    using var response = await http.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    // 声明必须在下面的 goto Finalize 之前：跳转会跳过声明语句，
                    // 而 Finalize 处要用 total 做完整性校验（`total > 0` 才比对，0 表示「无从比对」）。
                    long total = 0;

                    // 416 表示 Range 不可满足（文件已完整）→ 当作完成。
                    if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable
                        && resumeFrom > 0)
                    {
                        _logger.LogInformation("服务端返回 416，认为下载已完成");
                        goto Finalize;
                    }
                    response.EnsureSuccessStatusCode();

                    var isResume = response.StatusCode == System.Net.HttpStatusCode.PartialContent
                                   && resumeFrom > 0;
                    if (isResume && response.Content.Headers.ContentRange?.Length is long rangeTotal)
                    {
                        total = rangeTotal;
                    }
                    else
                    {
                        var len = response.Content.Headers.ContentLength ?? 0;
                        total = isResume ? len + resumeFrom : len;
                        if (!isResume)
                        {
                            // 服务端不支持续传，从 0 开始重下。
                            resumeFrom = 0;
                        }
                    }

                    await using (var src = await response.Content.ReadAsStreamAsync(cancellationToken))
                    await using (var dst = new FileStream(tempPath,
                        isResume ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                    {
                        var buffer = new byte[1 << 20];
                        long received = isResume ? resumeFrom : 0;
                        int read;
                        var lastReport = 0L;
                        while ((read = await src.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            received += read;
                            if (received - lastReport >= 512 * 1024 || received == total)
                            {
                                lastReport = received;
                                var pct = total > 0 ? received / (double)total : 0;
                                onProgress?.Invoke(new DownloadProgress(pct, received, total));
                            }
                        }
                    }

                Finalize:
                    // 完整性校验（关键）：ReadAsync 返回 0 只代表「流结束了」，**不代表下载完整**。
                    // 国内网络下中间设备经常把连接干净地关掉（服务端 FIN / chunked 截断），
                    // 此时循环正常退出、不抛异常，一个残缺文件就会被 Move 成正式模型 —— 之后
                    // FindModel 永远命中它、IsModelAvailable 返回 true、依赖徽章熄灭、设置页显示「已就绪」，
                    // 但每次 ASR 都失败，而且「重新下载」也会因为开头的 FindModel 短路而无效。
                    // 用户唯一的出路是手动去 whisper-models 目录删文件 —— 这是不可接受的死局。
                    // 这里长度不符就当失败抛出，交给外层的多源重试（tempPath 保留供续传）。
                    var downloadedLength = new FileInfo(tempPath).Length;
                    if (total > 0 && downloadedLength != total)
                    {
                        throw new IOException(
                            $"模型下载不完整（已收到 {downloadedLength / 1024 / 1024} MB，应为 {total / 1024 / 1024} MB），可能是网络中断。");
                    }

                    if (File.Exists(destPath))
                    {
                        File.Delete(destPath);
                    }
                    File.Move(tempPath, destPath);
                    onProgress?.Invoke(new DownloadProgress(1.0,
                        new FileInfo(destPath).Length, new FileInfo(destPath).Length));
                    _logger.LogInformation("Whisper 模型下载完成: {Model}", modelName);
                    return destPath;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.LogError("从 {Source} 下载失败（第 {Attempt} 次）: {Message}",
                        source, attempt, ex.Message);
                    if (attempt < maxAttemptsPerUrl)
                    {
                        var delayMs = 2000 * attempt;
                        _logger.LogInformation("{Ms} 毫秒后重试同一源（已下载部分会续传）...", delayMs);
                        await Task.Delay(delayMs, cancellationToken);
                    }
                }
            }
            _logger.LogInformation("源 {Source} 3 次都失败，尝试下一个源...", source);
        }

        throw new AsrException(AsrErrorKind.ModelNotFound,
            lastError is null
                ? "Whisper 模型文件未找到，所有下载源均失败"
                : "Whisper 模型下载失败：" + lastError.Message
                  + "\n临时文件保留在 " + Path.GetFileName(tempPath) + "，下次重试会自动续传");
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("删除临时文件失败 {Path}: {Message}", path, ex.Message);
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    // ---- whisper.cpp --output-json-full DTO ----

    private sealed class WhisperCppOutput
    {
        [JsonPropertyName("transcription")]
        public List<Segment>? Transcription { get; set; }

        public sealed class Segment
        {
            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;

            [JsonPropertyName("timestamps")]
            public Stamps? Timestamps { get; set; }

            [JsonPropertyName("tokens")]
            public List<Token>? Tokens { get; set; }
        }

        public sealed class Token
        {
            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;

            [JsonPropertyName("timestamps")]
            public Stamps? Timestamps { get; set; }
        }

        public sealed class Stamps
        {
            [JsonPropertyName("from")]
            public string From { get; set; } = "00:00:00.000";

            [JsonPropertyName("to")]
            public string To { get; set; } = "00:00:00.000";
        }
    }
}
