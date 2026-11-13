using System;
using System.Collections.Generic;
using System.Text;

namespace BetterGenshinImpact.GameTask.AutoSkip.DialogueLog;

/// <summary>
/// 剧情对白的文本整理：多行拼接、比较键归一化和“是否同一句”判断。
/// 归一化只用于比较，写入日志时始终使用 OCR 原文。
/// </summary>
internal static class AutoSkipDialogueText
{
    /// <summary>
    /// 把多段 OCR 文本拼成一行。中文直接相连；两侧都是英文字母或数字时补一个空格，避免单词粘连。
    /// </summary>
    /// <param name="lines">按阅读顺序排列的文本片段，允许为空或含空项。</param>
    public static string JoinLines(IEnumerable<string>? lines)
    {
        if (lines is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // 单个识别结果内部如果带换行，直接去掉，最终都写在一行里
            var part = line.ReplaceLineEndings(string.Empty).Trim();
            if (part.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0 && IsAsciiWord(builder[^1]) && IsAsciiWord(part[0]))
            {
                builder.Append(' ');
            }

            builder.Append(part);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 判断字符是否属于英文或数字，用于决定拼接时是否补空格。
    /// </summary>
    private static bool IsAsciiWord(char character)
    {
        return character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
    }

    /// <summary>
    /// 整理角色名：去掉所有空白、内部换行和结尾的冒号。识别失败时返回空字符串。
    /// </summary>
    public static string NormalizeSpeaker(string? speaker)
    {
        if (string.IsNullOrWhiteSpace(speaker))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(speaker.Length);
        foreach (var character in speaker.ReplaceLineEndings(string.Empty))
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString().TrimEnd(':', '：');
    }

    /// <summary>
    /// 生成用于比较的键：去掉所有空白、全角 ASCII 转半角、英文转小写。
    /// </summary>
    public static string NormalizeKey(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            var normalized = character;
            // 全角标点、字母、数字换算成半角，消除输入法或 OCR 造成的全半角差异
            if (character is >= '\uFF01' and <= '\uFF5E')
            {
                normalized = (char)(character - 0xFEE0);
            }

            if (normalized is >= 'A' and <= 'Z')
            {
                normalized = (char)(normalized + ('a' - 'A'));
            }

            builder.Append(normalized);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 判断两段文字是否可能是同一句：完全相同、逐字出现时的前缀关系、
    /// 短文本被完整包含在长文本中（OCR 漏读首尾或改读括号），
    /// 或较长文本的二字组相似度足够高（OCR 抖动）。
    /// </summary>
    public static bool IsSameLine(string? left, string? right)
    {
        var leftRaw = NormalizeKey(left);
        var rightRaw = NormalizeKey(right);
        if (leftRaw.Length == 0 || rightRaw.Length == 0)
        {
            return false;
        }

        if (string.Equals(leftRaw, rightRaw, StringComparison.Ordinal))
        {
            return true;
        }

        // 首尾的括号、引号、逗号、省略号经常会被 OCR 多读或少读，比较前先去掉
        var leftKey = TrimEdgePunctuation(leftRaw);
        var rightKey = TrimEdgePunctuation(rightRaw);
        if (leftKey.Length == 0 || rightKey.Length == 0)
        {
            return false;
        }

        if (string.Equals(leftKey, rightKey, StringComparison.Ordinal))
        {
            return true;
        }

        var shorter = leftKey.Length <= rightKey.Length ? leftKey : rightKey;
        var longer = leftKey.Length <= rightKey.Length ? rightKey : leftKey;

        // 逐字出现：允许单个字符的短句继续增长，例如“哦”→“哦，怎么说？”
        if (longer.StartsWith(shorter, StringComparison.Ordinal))
        {
            return true;
        }

        // OCR 漏掉开头或结尾时，短文本会落在长文本中间
        if (shorter.Length >= 3 && longer.Contains(shorter, StringComparison.Ordinal))
        {
            return true;
        }

        // 短句差一个字就可能是完全不同的对白，只在较长文本上做模糊容错
        if (shorter.Length < 6)
        {
            return false;
        }

        return BigramDice(leftKey, rightKey) >= 0.8;
    }

    /// <summary>
    /// 去掉文本首尾的标点和符号。只影响比较，不改变写入日志的原文。
    /// </summary>
    private static string TrimEdgePunctuation(string key)
    {
        var start = 0;
        var end = key.Length - 1;
        while (start <= end && IsEdgePunctuation(key[start]))
        {
            start++;
        }

        while (end >= start && IsEdgePunctuation(key[end]))
        {
            end--;
        }

        return start > end ? string.Empty : key.Substring(start, end - start + 1);
    }

    /// <summary>
    /// 判断字符是否属于首尾可忽略的标点或符号。
    /// </summary>
    private static bool IsEdgePunctuation(char character)
    {
        return char.IsPunctuation(character) || char.IsSymbol(character);
    }

    /// <summary>
    /// 计算两段文本的二字组 Dice 相似度（0～1）。
    /// </summary>
    private static double BigramDice(string left, string right)
    {
        if (left.Length < 2 || right.Length < 2)
        {
            return 0;
        }

        var bigrams = new Dictionary<string, int>(left.Length - 1, StringComparer.Ordinal);
        for (var i = 0; i < left.Length - 1; i++)
        {
            var key = left.Substring(i, 2);
            bigrams[key] = bigrams.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        var common = 0;
        for (var i = 0; i < right.Length - 1; i++)
        {
            var key = right.Substring(i, 2);
            if (bigrams.TryGetValue(key, out var count) && count > 0)
            {
                bigrams[key] = count - 1;
                common++;
            }
        }

        return 2d * common / (left.Length - 1 + right.Length - 1);
    }
}
