using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ServerOpsHelper.Models;

/// <summary>
/// 一个扩展包（`extensions/&lt;目录&gt;/extension.json` 或 `extensions/&lt;名字&gt;.json`）。
/// <para>
/// 刻意<b>不做成 DLL 插件</b>：清单式扩展不执行任何外来代码，只描述「在服务器上跑什么命令」，
/// 能力全部走既有的 SSH 通道 —— 写权限闸门、审计日志、连接超时都自动继承。
/// 好处是扩展可以被直接编辑、复制给别人、用 AI 生成，坏了也只是少几个按钮，不会把主程序带崩。
/// </para>
/// </summary>
public sealed class ExtensionPack
{
    /// <summary>扩展标识，只能用小写字母/数字/下划线/短横线，用于拼 MCP 工具名</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>版本号（纯展示，不参与比较）</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>作者（纯展示）</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>说明（纯展示）</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>是否启用：关掉后它的命令不出现、MCP 也不暴露</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>这个扩展包含的命令</summary>
    public List<ExtensionCommand> Commands { get; set; } = new();

    /// <summary>来源文件路径（运行时字段）</summary>
    [JsonIgnore]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>加载失败原因；空表示这个包可用（运行时字段）</summary>
    [JsonIgnore]
    public string LoadError { get; set; } = string.Empty;

    /// <summary>
    /// 是否被用户在界面上停用（记在 extensions/.state.json，不改进清单文件本身）。
    /// 与清单里的 <see cref="Enabled"/> 分开，是为了让"用户停用"和"作者写死停用"能区分开。
    /// </summary>
    [JsonIgnore]
    public bool DisabledByUser { get; set; }

    [JsonIgnore]
    public bool IsValid => string.IsNullOrEmpty(LoadError);

    /// <summary>界面上显示的一行摘要</summary>
    [JsonIgnore]
    public string Summary => IsValid
        ? $"{Commands.Count} 个命令" + (string.IsNullOrWhiteSpace(Author) ? string.Empty : $" · {Author}")
        : LoadError;
}

/// <summary>
/// 扩展里的一条命令。
/// <para>
/// <see cref="Command"/> 支持 <c>${参数名}</c> 占位符。替换时会自动做 shell 安全引用
/// （单引号包裹 + 转义内部单引号），所以<b>模板里不要再自己加引号</b>。
/// </para>
/// </summary>
public sealed class ExtensionCommand
{
    /// <summary>命令标识（同一扩展内唯一），参与拼 MCP 工具名</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名，例如「重载 Nginx」</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>给人和模型看的说明：什么时候该用它</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>要执行的 shell 命令模板</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>分组（界面按这个排序/归类，可空）</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// 是否属于变更类操作：为 true 时，智能体调用它需要用户在设置里打开
    /// 「允许智能体执行变更操作」。只读命令（tail/ls/df）建议设为 false。
    /// </summary>
    public bool WriteAccess { get; set; } = true;

    /// <summary>是否暴露给智能体（MCP）。只给人点的命令设 false 即可</summary>
    public bool ExposeToAgent { get; set; } = true;

    /// <summary>超时秒数（会被夹到 5~600）</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>参数定义（模板里出现的占位符必须在这里声明）</summary>
    public List<ExtensionParameter> Parameters { get; set; } = new();
}

/// <summary>
/// 命令参数。值一律按字符串处理，执行前按 <see cref="Type"/> 校验并做 shell 引用。
/// </summary>
public sealed class ExtensionParameter
{
    /// <summary>参数名（模板里写作 ${name}）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>说明，会写进 MCP 工具的参数描述里给模型看</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>类型：string / int / bool / path</summary>
    public string Type { get; set; } = "string";

    /// <summary>默认值；留空则视为必填</summary>
    public string Default { get; set; } = string.Empty;

    /// <summary>int 类型的最小值</summary>
    public int? Min { get; set; }

    /// <summary>int 类型的最大值</summary>
    public int? Max { get; set; }
}
