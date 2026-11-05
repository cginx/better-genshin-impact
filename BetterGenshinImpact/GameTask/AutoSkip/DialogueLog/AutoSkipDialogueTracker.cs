using System;

namespace BetterGenshinImpact.GameTask.AutoSkip.DialogueLog;

/// <summary>
/// 对白去重与合并状态机。
/// OCR 每帧的结果会因打字机效果和识别抖动而不同，同一句的多个版本合并成一条，
/// 直到出现不同的一句、离开对白或超时，才输出上一条。
/// 截图线程调用 <see cref="Flush"/> / <see cref="Reset"/>，识别线程调用 <see cref="Accept"/>，
/// 所有成员都在内部加锁。
/// </summary>
internal sealed class AutoSkipDialogueTracker
{
    /// <summary>同一句在多久以内再次出现时不重复输出。</summary>
    private static readonly TimeSpan RepeatSuppressionWindow = TimeSpan.FromSeconds(5);

    /// <summary>多久没有新的识别结果就认为当前这一句已经显示完毕。</summary>
    private static readonly TimeSpan IdleFlushDelay = TimeSpan.FromSeconds(2);

    /// <summary>保护 <see cref="_pending"/> 和最近输出记录。</summary>
    private readonly object _gate = new();

    /// <summary>正在收集 OCR 版本、尚未输出的当前句。</summary>
    private PendingLine? _pending;

    /// <summary>最近一次输出的对白，用于抑制同一句的重复输出。</summary>
    private DialogueLine _lastLogged;

    /// <summary><see cref="_lastLogged"/> 是否已经赋过值。</summary>
    private bool _hasLastLogged;

    /// <summary>最近一次输出的时间。</summary>
    private DateTime _lastLoggedAt;

    /// <summary>
    /// 输入一帧 OCR 结果。同一句继续合并；出现新的一句时返回需要输出的上一句，否则返回 null。
    /// </summary>
    public DialogueLine? Accept(string speaker, string text, DateTime now)
    {
        var normalizedSpeaker = AutoSkipDialogueText.NormalizeSpeaker(speaker);
        var displayText = text.Trim();
        if (displayText.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_pending is null)
            {
                _pending = new PendingLine(normalizedSpeaker, displayText, now);
                return null;
            }

            if (_pending.IsSameLine(normalizedSpeaker, displayText))
            {
                _pending.Merge(normalizedSpeaker, displayText, now);
                return null;
            }

            // 对白已经换句：先输出上一句，再把当前结果作为新的一句
            var flushed = TakePending(now);
            _pending = new PendingLine(normalizedSpeaker, displayText, now);
            return flushed;
        }
    }

    /// <summary>
    /// 结束当前句并返回需要输出的内容。用于离开对白、停止自动剧情等场景。
    /// </summary>
    public DialogueLine? Flush(DateTime now)
    {
        lock (_gate)
        {
            return TakePending(now);
        }
    }

    /// <summary>
    /// 一段时间没有新的识别结果时结束当前句。用于自动剧情最后一个循环没有回调 OnDisabled 的场景。
    /// </summary>
    public DialogueLine? FlushIfIdle(DateTime now)
    {
        lock (_gate)
        {
            if (_pending is null || now - _pending.LastSeen < IdleFlushDelay)
            {
                return null;
            }

            return TakePending(now);
        }
    }

    /// <summary>
    /// 自动剧情停止时复位，输出尚未写入的最后一句并清空去重记录。
    /// </summary>
    public DialogueLine? Reset(DateTime now)
    {
        lock (_gate)
        {
            var flushed = TakePending(now);
            _hasLastLogged = false;
            return flushed;
        }
    }

    /// <summary>
    /// 取出当前句；与最近输出相同则抑制，并返回 null。
    /// </summary>
    private DialogueLine? TakePending(DateTime now)
    {
        var pending = _pending;
        _pending = null;
        if (pending is null)
        {
            return null;
        }

        var line = new DialogueLine(pending.Speaker, pending.Text.Trim());
        if (line.Text.Length == 0)
        {
            return null;
        }

        if (_hasLastLogged
            && now - _lastLoggedAt < RepeatSuppressionWindow
            && SpeakersCompatible(_lastLogged.Speaker, line.Speaker)
            && string.Equals(
                AutoSkipDialogueText.NormalizeKey(_lastLogged.Text),
                AutoSkipDialogueText.NormalizeKey(line.Text),
                StringComparison.Ordinal))
        {
            // 同一句仍在显示，延长抑制窗口，避免离开对白或超时后再次输出
            _lastLoggedAt = now;
            return null;
        }

        _lastLogged = line;
        _hasLastLogged = true;
        _lastLoggedAt = now;
        return line;
    }

    /// <summary>
    /// 判断两个角色名是否兼容：任一侧为空（旁白或漏识别）时只看正文。
    /// </summary>
    private static bool SpeakersCompatible(string left, string right)
    {
        return left.Length == 0 || right.Length == 0 || string.Equals(left, right, StringComparison.Ordinal);
    }

    /// <summary>
    /// 正在收集的一句中，当前最完整的角色名和正文。
    /// </summary>
    private sealed class PendingLine
    {
        /// <summary>角色名，可能为空。</summary>
        public string Speaker { get; private set; }

        /// <summary>目前观察到的最完整正文。</summary>
        public string Text { get; private set; }

        /// <summary>最近一次出现该句的版本的时间。</summary>
        public DateTime LastSeen { get; private set; }

        /// <summary>
        /// 创建当前句。传入的文本已去除首尾空白。
        /// </summary>
        public PendingLine(string speaker, string text, DateTime now)
        {
            Speaker = speaker;
            Text = text;
            LastSeen = now;
        }

        /// <summary>
        /// 判断本帧结果是否仍属于当前句。
        /// </summary>
        public bool IsSameLine(string speaker, string text)
        {
            var speakerMatches = Speaker.Length == 0 || speaker.Length == 0
                                 || AutoSkipDialogueText.IsSameLine(Speaker, speaker);
            return speakerMatches && AutoSkipDialogueText.IsSameLine(Text, text);
        }

        /// <summary>
        /// 合并同一句的新版本：保留更长的正文，姓名以有值的一侧为准。
        /// </summary>
        public void Merge(string speaker, string text, DateTime now)
        {
            if (speaker.Length > 0)
            {
                Speaker = speaker;
            }

            if (AutoSkipDialogueText.NormalizeKey(text).Length
                > AutoSkipDialogueText.NormalizeKey(Text).Length)
            {
                Text = text;
            }

            LastSeen = now;
        }
    }
}
