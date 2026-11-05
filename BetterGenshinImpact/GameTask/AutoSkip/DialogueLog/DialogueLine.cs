namespace BetterGenshinImpact.GameTask.AutoSkip.DialogueLog;

/// <summary>
/// 一句已经确认的剧情对白。
/// </summary>
/// <param name="Speaker">角色名，已去除首尾空白和结尾冒号；没有识别到时为空字符串。</param>
/// <param name="Text">对白正文，多行已合并成一行。</param>
internal readonly record struct DialogueLine(string Speaker, string Text);
