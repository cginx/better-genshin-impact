using BetterGenshinImpact.GameTask.AutoSkip.DialogueLog;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoSkipTests;

/// <summary>验证剧情对白的多行拼接、角色名整理和同一句判断。</summary>
public sealed class AutoSkipDialogueTextTests
{
    /// <summary>中文正文行直接相连，英文单词之间补空格，空项和换行会被清理。</summary>
    [Fact]
    public void JoinLines_keeps_cjk_continuous_and_separates_ascii_words()
    {
        Assert.Equal("前面好像有人，我们过去看看吧！", AutoSkipDialogueText.JoinLines([" 前面好像有人， ", "", "我们过去\r\n看看吧！", null!]));
        Assert.Equal("Hello world", AutoSkipDialogueText.JoinLines(["Hello", "world"]));
    }

    /// <summary>比较键忽略空白、全角半角和英文大小写。</summary>
    [Fact]
    public void NormalizeKey_ignores_whitespace_and_full_width()
    {
        Assert.Equal("abc123!", AutoSkipDialogueText.NormalizeKey("ＡＢＣ １２３！"));
        Assert.Equal("", AutoSkipDialogueText.NormalizeKey(null));
    }

    /// <summary>角色名去掉首尾空白和结尾冒号。</summary>
    [Fact]
    public void NormalizeSpeaker_trims_colon()
    {
        Assert.Equal("凯瑟琳", AutoSkipDialogueText.NormalizeSpeaker(" 凯瑟琳： "));
        Assert.Equal("", AutoSkipDialogueText.NormalizeSpeaker(null));
    }

    /// <summary>打字机效果和常见 OCR 抖动应视为同一句，短句之间不做模糊合并。</summary>
    [Theory]
    [InlineData("前面好像有人", "前面好像有人，我们过去看看吧！", true)]
    [InlineData("前面好像有人，我们过去看看吧！", "前面好像有人,我们过去看看吧!", true)]
    [InlineData("前面好像有人，我们过去看看吧！", "前面好像有人，我们过去看一看吧！", true)]
    [InlineData("我们走吧", "我们跑吧", false)]
    [InlineData("好", "好的", false)]
    [InlineData("", "前面好像有人", false)]
    public void IsSameLine_handles_typewriter_and_ocr_noise(string left, string right, bool expected)
    {
        Assert.Equal(expected, AutoSkipDialogueText.IsSameLine(left, right));
        Assert.Equal(expected, AutoSkipDialogueText.IsSameLine(right, left));
    }
}
