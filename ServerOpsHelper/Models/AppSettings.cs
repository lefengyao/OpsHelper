namespace ServerOpsHelper.Models;

/// <summary>
/// 应用设置（持久化到 settings.json）
/// </summary>
public class AppSettings
{
    /// <summary>
    /// 默认管理后台地址（Web 控制台页使用）
    /// </summary>
    public string DefaultAdminUrl { get; set; } = "https://www.baidu.com";

    /// <summary>
    /// 主题：Dark / Light
    /// </summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>
    /// 是否记录网页访问历史（关闭后 Web 控制台不再写入 history.json）
    /// </summary>
    public bool RecordVisitHistory { get; set; } = true;

    /// <summary>
    /// 在内置控制台检测到手动登录时，是否提示保存到密码箱
    /// </summary>
    public bool CaptureCredentials { get; set; } = true;

    /// <summary>
    /// 网页视觉辅助：自动填充账号密码后，在网页里高亮闪烁被填的输入框
    /// </summary>
    public bool FillHighlightAid { get; set; } = true;

    /// <summary>
    /// 浏览器下载目录。为空时使用默认的 %USERPROFILE%\Downloads\ServerOpsHelper。
    /// </summary>
    public string DownloadDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 下载可执行文件时是否弹出风险提示（默认开，安全边界别随手关）。
    /// </summary>
    public bool ConfirmDangerousDownload { get; set; } = true;

    // ───────────────────────── 智能体接入（MCP） ─────────────────────────

    /// <summary>
    /// 是否启用本地 MCP 的 HTTP 传输（默认关）。
    /// <para>
    /// stdio 传输不受这个开关影响 —— 那是客户端自己拉起子进程，
    /// 开关在这里没有意义，也拦不住。
    /// </para>
    /// </summary>
    public bool McpHttpEnabled { get; set; }

    /// <summary>
    /// MCP HTTP 监听端口（只绑 127.0.0.1）
    /// </summary>
    public int McpHttpPort { get; set; } = 9260;

    /// <summary>
    /// 是否允许智能体执行变更类操作（上传/覆盖文件、执行 shell 命令）。
    /// <para>
    /// **默认关闭**：只读能力（列机器、看指标、看文件）不需要授权，
    /// 但"能在服务器上跑命令"必须由用户明确打开。这条是安全边界，不要改成默认 true。
    /// </para>
    /// </summary>
    public bool McpAllowWriteTools { get; set; }
}
