using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Infrastructure.Network;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.View.Windows;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace BetterGenshinImpact.Service;

public class UpdateService : IUpdateService
{
    private readonly ILogger<UpdateService> _logger;
    private readonly IConfigService _configService;

    /// <summary>
    /// 本仓库的 GitHub Release API，版本检查与更新日志都从这里读取
    /// </summary>
    private const string GitHubReleasesUrl = "https://api.github.com/repos/cgix/better-genshin-impact/releases";

    /// <summary>
    /// 最新正式版本，GitHub 的 latest 接口不会返回预发布版本
    /// </summary>
    private const string LatestReleaseUrl = GitHubReleasesUrl + "/latest";

    /// <summary>
    /// 测试版列表地址，只取最新一条
    /// </summary>
    private const string AlphaReleasesUrl = GitHubReleasesUrl + "?per_page=1";

    /// <summary>
    /// 本仓库的 Release 页面，更新窗口的“其他更新方式”跳转到这里
    /// </summary>
    private const string ReleasesPageUrl = "https://github.com/cgix/better-genshin-impact/releases";

    /// <summary>
    /// 命名 HttpClient 工厂，版本检查与更新日志使用默认客户端
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    public AllConfig Config { get; set; }

    public UpdateService(IConfigService configService, IHttpClientFactory httpClientFactory)
    {
        _logger = App.GetLogger<UpdateService>();
        _configService = configService;
        _httpClientFactory = httpClientFactory;
        Config = _configService.Get();
    }


    /// <summary>
    /// Please call me in main thread
    /// </summary>
    /// <param name="option"></param>
    public async Task CheckUpdateAsync(UpdateOption option)
    {
        try
        {
#if DEBUG && false
            return;
#endif
            string newVersion = await GetLatestVersionAsync(option);

            if (string.IsNullOrWhiteSpace(newVersion))
            {
                return;
            }

            // ---- 如果是调试模式且手动的检查更新的情况下，强制打开更新窗口 -----
            // 方便调试窗口
            if (RuntimeHelper.IsDebuggerAttached && option.Trigger == UpdateTrigger.Manual)
            {
                await OpenCheckUpdateWindow(option, newVersion);
                return;
            }
            // ---- 如果是调试模式且手动的检查更新的情况下，强制打开更新窗口 -----

            if (!Global.IsNewVersion(newVersion))
            {
                if (option.Trigger == UpdateTrigger.Manual)
                {
                    await ThemedMessageBox.InformationAsync("当前已是最新版本！");
                }

                return;
            }

            if (!string.IsNullOrEmpty(Config.NotShowNewVersionNoticeEndVersion)
                && !Global.IsNewVersion(Config.NotShowNewVersionNoticeEndVersion, newVersion)
                && option.Trigger == UpdateTrigger.Auto)
            {
                return;
            }

            await OpenCheckUpdateWindow(option, newVersion);
        }
        catch (Exception e)
        {
            Debug.WriteLine("获取最新版本信息失败：" + e.Source + "\r\n--" + Environment.NewLine + e.StackTrace + "\r\n---" +
                            Environment.NewLine + e.Message);
            _logger.LogWarning("获取 BetterGI 最新版本信息失败");
        }
    }

    private async Task OpenCheckUpdateWindow(UpdateOption option, string newVersion)
    {
        CheckUpdateWindow win = new(option)
        {
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = $"发现新版本 {newVersion}",
            UserInteraction = async (sender, button) =>
            {
                CheckUpdateWindow win = (CheckUpdateWindow)sender;

                switch (button)
                {
                    case CheckUpdateWindow.CheckUpdateWindowButton.BackgroundUpdate:
                        // TBD
                        break;

                    case CheckUpdateWindow.CheckUpdateWindowButton.OtherUpdate:
                        Process.Start(new ProcessStartInfo(ReleasesPageUrl) { UseShellExecute = true });
                        break;

                    case CheckUpdateWindow.CheckUpdateWindowButton.Update:
                    {
                        // 唤起更新程序
                        string updaterExePath = Global.Absolute("BetterGI.update.exe");
                        if (!File.Exists(updaterExePath))
                        {
                            await ThemedMessageBox.ErrorAsync("更新程序不存在，请选择其他更新方式！");
                            return;
                        }

                        // 启动
                        Process.Start(updaterExePath, "-I");

                        // 退出程序
                        Application.Current.Shutdown();
                    }
                        break;

                    case CheckUpdateWindow.CheckUpdateWindowButton.Ignore:
                        Config.NotShowNewVersionNoticeEndVersion = newVersion;
                        win.Close();
                        break;

                    case CheckUpdateWindow.CheckUpdateWindowButton.Cancel:
                        win.ShowUpdateStatus = false;
                        win.Close();
                        break;
                }
            }
        };

        if (option.Channel == UpdateChannel.Stable)
        {
            win.NavigateToHtml(await GetReleaseMarkdownHtmlAsync(newVersion));
        }

        win.ShowDialog();
    }

    private async Task<string> GetLatestVersionAsync(UpdateOption option)
    {
        return await GetLatestGitHubVersionAsync(option.Channel == UpdateChannel.Alpha);
    }

    /// <summary>
    /// 从本仓库的 GitHub Release 读取最新版本号。
    /// 稳定版只取 latest（不含预发布），测试版取第一条发布记录。
    /// </summary>
    private async Task<string> GetLatestGitHubVersionAsync(bool includePrerelease)
    {
        var httpClient = _httpClientFactory.CreateClient(HttpClientNames.Default);
        try
        {
            if (!includePrerelease)
            {
                var latestJson = await httpClient.GetStringAsync(LatestReleaseUrl);
                return ReadTagName(JsonConvert.DeserializeObject<Dictionary<string, object>>(latestJson));
            }

            var releasesJson = await httpClient.GetStringAsync(AlphaReleasesUrl);
            var releases = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(releasesJson);
            return ReadTagName(releases?.FirstOrDefault());
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            // 仓库还没有发布 Release 或 GitHub API 触发限流时静默跳过，避免每次启动都提示失败
            _logger.LogDebug("GitHub Release 不可用（{StatusCode}），跳过更新检查", e.StatusCode);
            return string.Empty;
        }
    }

    /// <summary>
    /// 读取 Release 的 tag_name 并去掉可选的 v 前缀。
    /// </summary>
    private static string ReadTagName(Dictionary<string, object>? release)
    {
        if (release is null || !release.TryGetValue("tag_name", out var tagName))
        {
            return string.Empty;
        }

        var tag = tagName?.ToString()?.Trim() ?? string.Empty;
        return tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
    }

    private async Task<string> GetReleaseMarkdownHtmlAsync(string version)
    {
        try
        {
            // GitHub API 要求带 UA，默认客户端会补上 BetterGI/{版本}
            var httpClient = _httpClientFactory.CreateClient(HttpClientNames.Default);
            string jsonString;
            try
            {
                // 按版本对应的 tag 读取更新日志；测试版不会被 latest 接口返回
                var releaseUrl = string.IsNullOrWhiteSpace(version)
                    ? LatestReleaseUrl
                    : $"{GitHubReleasesUrl}/tags/{Uri.EscapeDataString(version)}";
                jsonString = await httpClient.GetStringAsync(releaseUrl);
            }
            catch (HttpRequestException)
            {
                jsonString = await httpClient.GetStringAsync(LatestReleaseUrl);
            }

            var jsonDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonString);

            if (jsonDict != null)
            {
                string? name = jsonDict["name"] as string;
                string? body = jsonDict["body"] as string;
                string md = $"# {name}{new string('\n', 2)}{body}";

                md = WebUtility.HtmlEncode(md);
                string md2html = File.ReadAllText(Global.Absolute(@"Assets\Strings\md2html.html"), Encoding.UTF8);
                var html = md2html.Replace("{{content}}", md);

                return html;
            }
        }
        catch (Exception e)
        {
            _ = e;
        }

        return GetReleaseMarkdownHtmlFallback();
    }

    private string GetReleaseMarkdownHtmlFallback()
    {
        return
            """
            <!DOCTYPE html>
            <html lang="zh">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>更新日志</title>
                <style>
                    body {
                        background-color: #212121;
                        color: white;
                        font-family: Arial, sans-serif;
                        display: flex;
                        justify-content: center;
                        align-items: center;
                        height: 100vh;
                        margin: 0;
                    }
                    .message {
                        text-align: center;
                        font-size: 20px;
                    }
                </style>
            </head>
            <body>
                <div class="message">
                    获取更新日志失败，请自行选择是否更新！
                </div>
            </body>
            </html>
            """;
    }
}