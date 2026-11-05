using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoSkip.DialogueLog;

/// <summary>
/// 从游戏画面中截取剧情对白区域并调用全局 OCR。
/// <para>
/// 区域按自动配音的对白布局（Resources/QuestVoice/auto-scene.json，2560x1440）并在
/// Test/Images/test1.png 上核对：1080P 下为 x=300..1620、y=840..1040，
/// 从上到下依次是角色名、称号与分隔线、一到两行正文。区域随画面高度等比缩放、横向以画面中心对齐。
/// </para>
/// <para>
/// 暂不处理黑屏居中文字、全屏演出字幕和 UI 被遮挡的场景。
/// </para>
/// </summary>
internal static class AutoSkipDialogueRecognizer
{
    /// <summary>对白区域顶部（1080P 坐标），包含角色名上方的留白。</summary>
    private const double UnionTop1080P = 840;

    /// <summary>对白区域宽度（1080P 坐标），覆盖正文可能到达的最宽范围。</summary>
    private const double UnionWidth1080P = 1320;

    /// <summary>对白区域高度（1080P 坐标），从角色名带到正文最后一行下方。</summary>
    private const double UnionHeight1080P = 200;

    /// <summary>角色名带下边界占区域高度的比例。再往下是称号和分隔线，不属于对白。</summary>
    private const double NameBottomRatio = 0.25;

    /// <summary>正文带上边界占区域高度的比例，跳过称号、分隔线和行间留白。</summary>
    private const double BodyTopRatio = 0.40;

    /// <summary>角色名允许的最大字符数，超过时视为误识别并丢弃。</summary>
    private const int SpeakerMaximumLength = 24;

    /// <summary>对白正文至少包含这么多字符才视为有效，过滤背景噪声。</summary>
    private const int MinimumTextLength = 1;

    /// <summary>只接受横向中心在中间这个范围内的文字，两侧的 HUD 和对话选项不参与对白识别。</summary>
    private const double CenterMinimumRatio = 0.20;

    /// <summary>只接受横向中心在中间这个范围内的文字，两侧的 HUD 和对话选项不参与对白识别。</summary>
    private const double CenterMaximumRatio = 0.80;

    /// <summary>
    /// 计算一帧画面中的对白 OCR 区域。画面过小或比例异常时返回 false，调用方直接跳过本帧。
    /// </summary>
    /// <param name="region">当前游戏画面区域，可以是 1080P 归一化后的区域，也可以是原始截图区域。</param>
    /// <param name="rect">计算出的裁剪区域，位于 <paramref name="region"/> 的像素坐标系。</param>
    public static bool TryGetCropRect(ImageRegion region, out Rect rect)
    {
        rect = default;
        if (region.Width <= 0 || region.Height <= 0)
        {
            return false;
        }

        // 原神对白 UI 按画面高度缩放；宽度只影响左右留白，因此用高度换算比例
        var scale = region.Height / 1080d;
        var cropWidth = (int)Math.Round(UnionWidth1080P * scale);
        var cropHeight = (int)Math.Round(UnionHeight1080P * scale);
        var cropTop = (int)Math.Round(UnionTop1080P * scale);
        var cropLeft = (int)Math.Round((region.Width - cropWidth) / 2d);
        if (cropWidth < 120 || cropHeight < 40
            || cropLeft < 0 || cropTop < 0
            || cropLeft + cropWidth > region.Width
            || cropTop + cropHeight > region.Height)
        {
            return false;
        }

        rect = new Rect(cropLeft, cropTop, cropWidth, cropHeight);
        return true;
    }

    /// <summary>
    /// 识别对白区域中的角色名与正文。返回 false 表示本帧没有可用的对白文本。
    /// </summary>
    /// <param name="image">由 <see cref="TryGetCropRect"/> 得到的区域图像，调用方保留所有权。</param>
    /// <param name="speaker">角色名，没有则为空字符串。</param>
    /// <param name="text">按阅读顺序合并后的对白正文。</param>
    public static bool TryRecognize(Mat image, out string speaker, out string text)
    {
        speaker = string.Empty;
        text = string.Empty;
        if (image.Width <= 0 || image.Height <= 0)
        {
            return false;
        }

        return TryBuildDialogue(OcrFactory.Paddle.OcrResult(image), image.Width, image.Height,
            out speaker, out text);
    }

    /// <summary>
    /// 从 OCR 结果中按角色名带和正文带整理对白。独立成方法便于用真实 OCR 结果做验证。
    /// </summary>
    /// <param name="result">对白区域的 OCR 结果。</param>
    /// <param name="imageWidth">对白区域宽度。</param>
    /// <param name="imageHeight">对白区域高度。</param>
    /// <param name="speaker">角色名，没有则为空字符串。</param>
    /// <param name="text">按阅读顺序合并后的对白正文。</param>
    internal static bool TryBuildDialogue(OcrResult result, int imageWidth, int imageHeight,
        out string speaker, out string text)
    {
        speaker = string.Empty;
        text = string.Empty;
        if (result.Regions.Length == 0 || imageWidth <= 0 || imageHeight <= 0)
        {
            return false;
        }

        var nameBottom = imageHeight * NameBottomRatio;
        var bodyTop = imageHeight * BodyTopRatio;
        var centerMinimum = imageWidth * CenterMinimumRatio;
        var centerMaximum = imageWidth * CenterMaximumRatio;
        var nameParts = new List<string>();
        var bodyParts = new List<string>();
        // 按从上到下、从左到右的顺序取文本，保证多行正文的阅读顺序
        foreach (var region in result.Regions
                     .OrderBy(region => region.Rect.Center.Y)
                     .ThenBy(region => region.Rect.Center.X))
        {
            var part = region.Text?.Trim() ?? string.Empty;
            if (part.Length == 0)
            {
                continue;
            }

            // 对白正文和角色名都居中；横向偏到两侧的文字是 HUD 日志或对话选项
            var centerX = region.Rect.Center.X;
            if (centerX < centerMinimum || centerX > centerMaximum)
            {
                continue;
            }

            var centerY = region.Rect.Center.Y;
            if (centerY < nameBottom)
            {
                nameParts.Add(part);
            }
            else if (centerY >= bodyTop)
            {
                bodyParts.Add(part);
            }
            // 两条带之间的文字是称号、分隔线或选项，不属于对白，直接忽略
        }

        speaker = AutoSkipDialogueText.NormalizeSpeaker(string.Join(string.Empty, nameParts));
        if (speaker.Length > SpeakerMaximumLength)
        {
            speaker = string.Empty;
        }

        text = AutoSkipDialogueText.JoinLines(bodyParts);
        return text.Length >= MinimumTextLength;
    }
}
