using System.Collections.Generic;
using System.Linq;

namespace ServerOpsHelper.Services;

/// <summary>一个内置的推荐扩展（离线"插件商店"的内容）。</summary>
/// <param name="Id">扩展 id（同时是安装目录名）</param>
/// <param name="Name">显示名</param>
/// <param name="Version">版本</param>
/// <param name="Description">一句话说明，给用户看的</param>
/// <param name="Json">extension.json 的完整内容</param>
public sealed record BuiltInExtension(
    string Id,
    string Name,
    string Version,
    string Description,
    string Json);

/// <summary>
/// 内置推荐扩展。
/// <para>
/// 没有服务端市场，也不想为了"浏览插件"去连一个随时会挂的地址，所以内置几套最常用的运维动作。
/// 点「安装」就是把这段 JSON 写到 <c>extensions/&lt;id&gt;/extension.json</c> ——
/// 装完和用户自己写的扩展完全等价，可以卸载、可以停用、也可以直接编辑。
/// </para>
/// <para>
/// 命令都按最小惊讶原则设计：只读的标 <c>writeAccess=false</c>，会改服务器的标 true，
/// 参数只留真正需要用户改的那几个。
/// </para>
/// </summary>
public static class ExtensionCatalog
{
    private static readonly List<BuiltInExtension> Items = new()
    {
        new BuiltInExtension(
            "nginx-tools",
            "Nginx 维护",
            "1.0.0",
            "测配置、平滑重载、看错误日志、统计访问来源 IP",
            """
            {
              "id": "nginx-tools",
              "name": "Nginx 维护",
              "version": "1.0.0",
              "author": "内置",
              "description": "测配置、平滑重载、看错误日志、统计访问来源 IP",
              "commands": [
                {
                  "id": "test_config",
                  "name": "测试配置",
                  "description": "nginx -t，只检查不重载",
                  "command": "sudo nginx -t",
                  "writeAccess": false,
                  "timeoutSeconds": 30
                },
                {
                  "id": "reload",
                  "name": "平滑重载",
                  "description": "不中断连接地重新加载配置",
                  "command": "sudo nginx -t && sudo nginx -s reload",
                  "writeAccess": true,
                  "timeoutSeconds": 60
                },
                {
                  "id": "error_log",
                  "name": "错误日志",
                  "description": "看错误日志最后若干行",
                  "command": "tail -n ${lines} /var/log/nginx/error.log",
                  "writeAccess": false,
                  "parameters": [
                    { "name": "lines", "type": "int", "default": "100", "min": 1, "max": 5000, "description": "行数" }
                  ]
                },
                {
                  "id": "top_ips",
                  "name": "访问来源 TOP",
                  "description": "统计访问日志里最多的来源 IP",
                  "command": "awk '{print $1}' /var/log/nginx/access.log | sort | uniq -c | sort -rn | head -n ${top}",
                  "writeAccess": false,
                  "parameters": [
                    { "name": "top", "type": "int", "default": "10", "min": 1, "max": 100, "description": "显示前几名" }
                  ]
                }
              ]
            }
            """),

        new BuiltInExtension(
            "sys-check",
            "系统巡检",
            "1.0.0",
            "磁盘、内存、负载、最吃 CPU 的进程，一次看全",
            """
            {
              "id": "sys-check",
              "name": "系统巡检",
              "version": "1.0.0",
              "author": "内置",
              "description": "磁盘、内存、负载、最吃 CPU 的进程，一次看全",
              "commands": [
                {
                  "id": "disk",
                  "name": "磁盘占用",
                  "description": "所有挂载点的使用率",
                  "command": "df -h",
                  "writeAccess": false,
                  "timeoutSeconds": 30
                },
                {
                  "id": "mem",
                  "name": "内存占用",
                  "description": "free -h；没有 free 的极简系统回退到 /proc/meminfo",
                  "command": "free -h 2>/dev/null || head -5 /proc/meminfo",
                  "writeAccess": false,
                  "timeoutSeconds": 30
                },
                {
                  "id": "load",
                  "name": "负载与核数",
                  "description": "uptime + CPU 核数，判断负载高不高",
                  "command": "uptime && nproc",
                  "writeAccess": false,
                  "timeoutSeconds": 30
                },
                {
                  "id": "top_cpu",
                  "name": "最占 CPU 的进程",
                  "description": "按 CPU 使用率倒序",
                  "command": "ps aux --sort=-%cpu | head -n ${count}",
                  "writeAccess": false,
                  "parameters": [
                    { "name": "count", "type": "int", "default": "10", "min": 3, "max": 50, "description": "显示几条" }
                  ]
                }
              ]
            }
            """),

        new BuiltInExtension(
            "log-check",
            "日志排查",
            "1.0.0",
            "按关键字搜日志、拉系统错误日志",
            """
            {
              "id": "log-check",
              "name": "日志排查",
              "version": "1.0.0",
              "author": "内置",
              "description": "按关键字搜日志、拉系统错误日志",
              "commands": [
                {
                  "id": "grep_log",
                  "name": "搜日志",
                  "description": "在指定日志里搜关键字，取最后若干条",
                  "command": "grep -a ${keyword} ${path} 2>/dev/null | tail -n ${lines}",
                  "writeAccess": false,
                  "timeoutSeconds": 60,
                  "parameters": [
                    { "name": "keyword", "type": "string", "default": "error", "description": "关键字" },
                    { "name": "path", "type": "path", "default": "/var/log/syslog", "description": "日志文件路径" },
                    { "name": "lines", "type": "int", "default": "100", "min": 1, "max": 5000, "description": "显示多少行" }
                  ]
                },
                {
                  "id": "journal_err",
                  "name": "系统错误日志",
                  "description": "journalctl 里最近的 error 级别记录",
                  "command": "journalctl -p err -n ${lines} --no-pager",
                  "writeAccess": false,
                  "timeoutSeconds": 60,
                  "parameters": [
                    { "name": "lines", "type": "int", "default": "50", "min": 1, "max": 2000, "description": "条数" }
                  ]
                }
              ]
            }
            """),
    };

    public static IReadOnlyList<BuiltInExtension> All => Items;

    public static BuiltInExtension? Find(string id)
        => Items.FirstOrDefault(i => string.Equals(i.Id, id, System.StringComparison.OrdinalIgnoreCase));
}
