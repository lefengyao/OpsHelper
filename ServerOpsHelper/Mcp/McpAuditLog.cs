using ServerOpsHelper.Services;
using System;
using System.IO;
using System.Text;

namespace ServerOpsHelper.Mcp;

/// <summary>
/// MCP 调用审计日志。
/// <para>
/// 智能体发起的每一次工具调用都落一行：时间、传输方式、工具、目标服务器、参数摘要、
/// 成功与否、耗时。**这是「谁在什么时候对哪台机器干了什么」的唯一凭据** ——
/// 桌面程序里人操作有界面可看，智能体操作没有，所以必须留痕。
/// </para>
/// <para>
/// 日志只写在数据目录的 <c>logs\</c> 下，与崩溃日志同处，不碰任何既有数据文件。
/// 写入失败一律吞掉：审计记录不该反过来把正常调用搞崩。
/// </para>
/// </summary>
internal static class McpAuditLog
{
    /// <summary>单条参数摘要的最大长度（命令可能有几十 KB，日志不该被一条撑爆）</summary>
    private const int MaxFieldLength = 500;

    private static readonly object Gate = new();

    public static string LogDirectory
        => Path.Combine(ConfigService.Instance.ConfigDirectory, "logs");

    public static string CurrentFilePath
        => Path.Combine(LogDirectory, $"mcp-audit-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>记录一次工具调用。</summary>
    /// <param name="transport">stdio / http</param>
    /// <param name="tool">工具名</param>
    /// <param name="target">目标服务器（批量时是逗号分隔的名称或数量）</param>
    /// <param name="summary">参数摘要（命令原文、路径等，**绝不含密码**）</param>
    public static void Write(
        string transport,
        string tool,
        string? target,
        string? summary,
        bool success,
        TimeSpan elapsed,
        string? note = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);

                var sb = new StringBuilder(256);
                sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ");
                sb.Append(transport.PadRight(5)).Append(' ');
                sb.Append(tool.PadRight(24)).Append(' ');
                sb.Append(success ? "OK " : "ERR").Append(' ');
                sb.Append(elapsed.TotalMilliseconds.ToString("0").PadLeft(6)).Append("ms");
                sb.Append("  target=").Append(Compact(target));

                if (!string.IsNullOrEmpty(summary))
                {
                    sb.Append("  args=").Append(Compact(summary));
                }

                if (!string.IsNullOrEmpty(note))
                {
                    sb.Append("  note=").Append(Compact(note));
                }

                File.AppendAllText(CurrentFilePath, sb.ToString() + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 审计写盘失败不能影响业务调用
        }
    }

    /// <summary>压成单行并截断，保证一条记录只占一行、长度可控。</summary>
    private static string Compact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var oneLine = value
            .Replace("\r\n", " ⏎ ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();

        return oneLine.Length <= MaxFieldLength
            ? oneLine
            : oneLine[..MaxFieldLength] + $"…(+{oneLine.Length - MaxFieldLength})";
    }
}
