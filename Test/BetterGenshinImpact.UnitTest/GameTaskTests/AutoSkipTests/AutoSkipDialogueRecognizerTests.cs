using BetterGenshinImpact.GameTask.AutoSkip.DialogueLog;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoSkipTests;

/// <summary>验证对白区域从 1080P 基准换算到实际画面。</summary>
public sealed class AutoSkipDialogueRecognizerTests
{
    /// <summary>1080P 归一化画面上，对白区域为 (300,840,1320,200)。</summary>
    [Fact]
    public void TryGetCropRect_uses_reference_box_on_1080p()
    {
        using var mat = new Mat(1080, 1920, MatType.CV_8UC3);
        using var region = new ImageRegion(mat.Clone(), 0, 0);

        Assert.True(AutoSkipDialogueRecognizer.TryGetCropRect(region, out var rect));
        Assert.Equal(300, rect.X);
        Assert.Equal(840, rect.Y);
        Assert.Equal(1320, rect.Width);
        Assert.Equal(200, rect.Height);
    }

    /// <summary>原始 2560x1440 截图区域按高度换算为 (400,1120,1760,267)。</summary>
    [Fact]
    public void TryGetCropRect_scales_to_native_frame()
    {
        using var mat = new Mat(1440, 2560, MatType.CV_8UC3);
        using var region = new ImageRegion(mat.Clone(), 0, 0);

        Assert.True(AutoSkipDialogueRecognizer.TryGetCropRect(region, out var rect));
        Assert.Equal(400, rect.X);
        Assert.Equal(1120, rect.Y);
        Assert.Equal(1760, rect.Width);
        Assert.Equal(267, rect.Height);
    }
}
