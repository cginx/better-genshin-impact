using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using BetterGenshinImpact.Core.Mask;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoSkip.DialogueLog;

/// <summary>
/// 自动剧情对白记录：自动剧情跳过对话时，在后台识别屏幕下方文本框中的剧情文字并写入日志。
/// <para>
/// 只由 <see cref="AutoSkipTrigger"/> 的 OnCapture / OnDisabled 调用。取帧时只复制对白区域并投递给独立线程，
/// 识别和日志写入都不占用实时触发器的线程；没有对白变化时跳过识别。
/// </para>
/// <para>
/// 本模块不读写自动剧情的配置，也不改变跳过的行为，删除对 AutoSkipTrigger 的两处调用即可完全移除。
/// </para>
/// </summary>
internal static class AutoSkipDialogueLogger
{
    /// <summary>两次取帧的最小间隔（毫秒）。自动剧情每帧调用，这里限制识别频率。</summary>
    private const int SampleIntervalMilliseconds = 180;

    /// <summary>识别线程等待处理的帧数上限。超出时丢弃较旧的帧，保证日志跟得上剧情。</summary>
    private const int MaximumQueuedFrames = 3;

    /// <summary>画面指纹的宽度（像素），用于判断对白区域是否发生变化。</summary>
    private const int ThumbnailWidth = 32;

    /// <summary>画面指纹的高度（像素）。</summary>
    private const int ThumbnailHeight = 8;

    /// <summary>指纹单个像素的最大差值，超过则重新识别。</summary>
    private const int ThumbnailMaximumDifference = 2;

    /// <summary>指纹全部像素的差值总和，超过则重新识别。</summary>
    private const int ThumbnailTotalDifference = 16;

    /// <summary>识别异常日志的最小间隔（毫秒）。</summary>
    private const long ErrorLogIntervalMilliseconds = 30_000;

    /// <summary>对白日志的写入器，首次使用时从容器解析；退出阶段解析失败时返回 null。</summary>
    private static ILogger? _logger;

    /// <summary>对白日志的写入器。延迟到首次写日志时获取，避免在容器已经释放时初始化失败。</summary>
    private static ILogger? Logger
    {
        get
        {
            try
            {
                // 与自动剧情的其他日志使用同一分类，日志里仍然归在“自动剧情”下
                return _logger ??= App.GetLogger<AutoSkipTrigger>();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>对白合并与去重状态机，截图线程和识别线程共用。</summary>
    private static readonly AutoSkipDialogueTracker Tracker = new();

    /// <summary>保护待识别帧队列和识别线程的创建。</summary>
    private static readonly object QueueGate = new();

    /// <summary>等待识别线程处理的帧。由 <see cref="QueueGate"/> 保护。</summary>
    private static readonly Queue<Mat> PendingFrames = new();

    /// <summary>通知识别线程有新帧。</summary>
    private static readonly AutoResetEvent FrameReady = new(false);

    /// <summary>后台识别线程，首次取帧时创建。</summary>
    private static Thread? _worker;

    /// <summary>上一次取帧的时间，只由截图线程读写。</summary>
    private static DateTime _lastSampleTime = DateTime.MinValue;

    /// <summary>上一次投递画面的指纹，只由截图线程读写。</summary>
    private static byte[]? _lastThumbnail;

    /// <summary>上一次输出异常日志的时间（Environment.TickCount64）。</summary>
    private static long _lastErrorLogTime;

    /// <summary>
    /// 自动剧情每帧回调。只有剧情播放（<paramref name="isPlaying"/>）时才取帧；
    /// 不在剧情中时结束当前句并输出尚未写入的最后一句。
    /// </summary>
    /// <param name="captureRegion">当前游戏画面区域。</param>
    /// <param name="isPlaying">当前是否处于剧情播放界面。</param>
    public static void Observe(ImageRegion captureRegion, bool isPlaying)
    {
        try
        {
            if (!isPlaying)
            {
                LogLine(Tracker.Flush(DateTime.Now));
                ClearDebugFrame(captureRegion);
                return;
            }

            var now = DateTime.Now;
            if ((now - _lastSampleTime).TotalMilliseconds < SampleIntervalMilliseconds)
            {
                return;
            }

            _lastSampleTime = now;
            if (!AutoSkipDialogueRecognizer.TryGetCropRect(captureRegion, out var rect))
            {
                return;
            }

            using var crop = new Mat(captureRegion.SrcMat, rect);
            DrawDebugFrame(captureRegion, rect);
            var thumbnail = CreateThumbnail(crop);
            if (!ThumbnailChanged(thumbnail))
            {
                // 对白区域没有任何变化时不再重复识别
                return;
            }

            _lastThumbnail = thumbnail;
            Enqueue(crop.Clone());
        }
        catch (Exception exception)
        {
            // 这里运行在实时触发器的线程上，任何异常都不能影响到自动剧情本身
            LogError(exception);
        }
    }

    /// <summary>
    /// 自动剧情停止时调用：丢弃尚未识别的帧，输出最后一句并清空状态，下次启用时重新开始。
    /// </summary>
    public static void End()
    {
        try
        {
            lock (QueueGate)
            {
                while (PendingFrames.Count > 0)
                {
                    PendingFrames.Dequeue().Dispose();
                }
            }

            _lastSampleTime = DateTime.MinValue;
            _lastThumbnail = null;
            LogLine(Tracker.Reset(DateTime.Now));
        }
        catch (Exception exception)
        {
            LogError(exception);
        }
    }

    /// <summary>
    /// 把一帧对白区域加入队列，并确保识别线程已经启动。
    /// </summary>
    private static void Enqueue(Mat frame)
    {
        lock (QueueGate)
        {
            EnsureWorker();
            while (PendingFrames.Count >= MaximumQueuedFrames)
            {
                PendingFrames.Dequeue().Dispose();
            }

            PendingFrames.Enqueue(frame);
        }

        FrameReady.Set();
    }

    /// <summary>
    /// 首次取帧时创建后台识别线程。线程是后台线程，程序退出时不需要额外清理。
    /// </summary>
    private static void EnsureWorker()
    {
        if (_worker is not null)
        {
            return;
        }

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "AutoSkipDialogueLogger",
            Priority = ThreadPriority.BelowNormal
        };
        _worker.Start();
    }

    /// <summary>
    /// 识别线程主循环：依次识别队列中的帧；长时间没有新帧时结束当前句。
    /// </summary>
    private static void WorkerLoop()
    {
        while (true)
        {
            try
            {
                if (!FrameReady.WaitOne(500))
                {
                    LogLine(Tracker.FlushIfIdle(DateTime.Now));
                    continue;
                }

                while (TryDequeue(out var frame))
                {
                    using (frame)
                    {
                        if (frame is null
                            || !AutoSkipDialogueRecognizer.TryRecognize(frame, out var speaker, out var text))
                        {
                            continue;
                        }

                        LogLine(Tracker.Accept(speaker, text, DateTime.Now));
                    }
                }
            }
            catch (Exception exception)
            {
                // 模型切换、模型卸载等情况下单帧识别会失败，记录后继续处理后续帧
                LogError(exception);
            }
        }
    }

    /// <summary>
    /// 从队列取出一帧，队列为空时返回 false。
    /// </summary>
    private static bool TryDequeue(out Mat? frame)
    {
        lock (QueueGate)
        {
            if (PendingFrames.Count > 0)
            {
                frame = PendingFrames.Dequeue();
                return true;
            }
        }

        frame = null;
        return false;
    }

    /// <summary>
    /// 计算对白区域的小尺寸灰度指纹，用于便宜地判断画面是否变化。
    /// </summary>
    private static byte[] CreateThumbnail(Mat image)
    {
        using var gray = new Mat();
        switch (image.Channels())
        {
            case 4:
                Cv2.CvtColor(image, gray, ColorConversionCodes.BGRA2GRAY);
                break;
            case 3:
                Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
                break;
            default:
                image.CopyTo(gray);
                break;
        }

        // Resize 的输出是连续内存，可以直接按字节读取
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(ThumbnailWidth, ThumbnailHeight), 0, 0, InterpolationFlags.Area);
        var data = new byte[ThumbnailWidth * ThumbnailHeight];
        Marshal.Copy(small.Data, data, 0, data.Length);
        return data;
    }

    /// <summary>
    /// 判断指纹与上一次相比是否发生了变化。对白文字变化会明显改变若干块的灰度均值。
    /// </summary>
    private static bool ThumbnailChanged(byte[] thumbnail)
    {
        var previous = _lastThumbnail;
        if (previous is null || previous.Length != thumbnail.Length)
        {
            return true;
        }

        var maximum = 0;
        var total = 0;
        for (var i = 0; i < thumbnail.Length; i++)
        {
            var difference = Math.Abs(thumbnail[i] - previous[i]);
            if (difference > maximum)
            {
                maximum = difference;
            }

            total += difference;
        }

        return maximum > ThumbnailMaximumDifference || total > ThumbnailTotalDifference;
    }

    /// <summary>
    /// 测试用：在遮罩上画出对白 OCR 区域，用来确认对白文字有没有超出框。只在 Debug 构建中生效。
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void DrawDebugFrame(ImageRegion captureRegion, Rect rect)
    {
        var group = new MaskWindowDrawingGroup("AutoSkipDialogueLog", MaskWindowDrawingKind.Feature);
        captureRegion.DrawingBoard.Set(group, captureRegion.ToMaskWindowDrawingRect(rect, System.Drawing.Pens.Red));
    }

    /// <summary>
    /// 测试用：离开对白画面时擦掉调试红框。只在 Debug 构建中生效。
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void ClearDebugFrame(ImageRegion captureRegion)
    {
        captureRegion.DrawingBoard.Clear(new MaskWindowDrawingGroup("AutoSkipDialogueLog", MaskWindowDrawingKind.Feature));
    }

    /// <summary>
    /// 按“角色名：正文”的格式写入日志；没有角色名时只写正文。
    /// </summary>
    private static void LogLine(DialogueLine? line)
    {
        try
        {
            if (line is not { } value)
            {
                return;
            }

            var text = string.IsNullOrEmpty(value.Speaker) ? value.Text : $"{value.Speaker}：{value.Text}";
            Logger?.LogInformation("自动剧情：{Text}", text);
        }
        catch
        {
            // 退出阶段日志系统可能已经释放，不能因为写日志失败影响识别线程
        }
    }

    /// <summary>
    /// 输出识别异常，限制为每 30 秒最多一条，避免模型不可用时刷屏。
    /// </summary>
    private static void LogError(Exception exception)
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastErrorLogTime) < ErrorLogIntervalMilliseconds)
            {
                return;
            }

            Interlocked.Exchange(ref _lastErrorLogTime, now);
            Logger?.LogWarning(exception, "自动剧情对白识别失败，稍后继续重试");
        }
        catch
        {
            // 退出阶段日志系统可能已经释放，忽略即可
        }
    }
}
