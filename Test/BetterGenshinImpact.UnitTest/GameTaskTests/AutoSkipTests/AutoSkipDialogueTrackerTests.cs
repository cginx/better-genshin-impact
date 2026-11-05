using System;
using BetterGenshinImpact.GameTask.AutoSkip.DialogueLog;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoSkipTests;

/// <summary>验证对白合并、换句输出和重复抑制。</summary>
public sealed class AutoSkipDialogueTrackerTests
{
    /// <summary>测试基准时间，避免依赖真实时钟。</summary>
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>打字机逐字出现时只保留最终完整的一句，并保留识别到的角色名。</summary>
    [Fact]
    public void Accept_merges_typewriter_variants_and_keeps_speaker()
    {
        var tracker = new AutoSkipDialogueTracker();

        Assert.Null(tracker.Accept("", "前面好像有人", Start));
        Assert.Null(tracker.Accept("凯瑟琳", "前面好像有人，我们", Start.AddMilliseconds(200)));
        Assert.Null(tracker.Accept("凯瑟琳", "前面好像有人，我们过去看看吧！", Start.AddMilliseconds(400)));

        var line = tracker.Accept("凯瑟琳", "后面有人追上来了。", Start.AddMilliseconds(600));
        Assert.Equal("凯瑟琳", line?.Speaker);
        Assert.Equal("前面好像有人，我们过去看看吧！", line?.Text);

        Assert.Equal("后面有人追上来了。", tracker.Flush(Start.AddMilliseconds(800))?.Text);
    }

    /// <summary>最后一两句可能没有后续识别，超时后补输出。</summary>
    [Fact]
    public void FlushIfIdle_emits_pending_line_after_timeout()
    {
        var tracker = new AutoSkipDialogueTracker();
        Assert.Null(tracker.Accept("凯瑟琳", "我们过去看看吧！", Start));

        Assert.Null(tracker.FlushIfIdle(Start.AddSeconds(1)));
        Assert.Equal("我们过去看看吧！", tracker.FlushIfIdle(Start.AddSeconds(2.1))?.Text);
        Assert.Null(tracker.FlushIfIdle(Start.AddSeconds(3)));
    }

    /// <summary>离开对白后同一句仍在显示时不应重复输出。</summary>
    [Fact]
    public void Repeated_same_line_is_suppressed_within_window()
    {
        var tracker = new AutoSkipDialogueTracker();
        Assert.Null(tracker.Accept("凯瑟琳", "我们过去看看吧！", Start));
        Assert.Equal("我们过去看看吧！", tracker.Flush(Start.AddSeconds(1))?.Text);

        Assert.Null(tracker.Accept("凯瑟琳", "我们过去看看吧！", Start.AddSeconds(2)));
        Assert.Null(tracker.Flush(Start.AddSeconds(3)));

        // 不同的一句仍然正常输出
        Assert.Null(tracker.Accept("凯瑟琳", "你好。", Start.AddSeconds(4)));
        Assert.Equal("你好。", tracker.Flush(Start.AddSeconds(5))?.Text);
    }

    /// <summary>停止自动剧情时输出最后一句，并清空去重记录。</summary>
    [Fact]
    public void Reset_flushes_pending_and_clears_history()
    {
        var tracker = new AutoSkipDialogueTracker();
        Assert.Null(tracker.Accept("", "最后一句。", Start));

        Assert.Equal("最后一句。", tracker.Reset(Start.AddSeconds(1))?.Text);

        Assert.Null(tracker.Accept("", "最后一句。", Start.AddSeconds(2)));
        Assert.Equal("最后一句。", tracker.Flush(Start.AddSeconds(3))?.Text);
    }
}
