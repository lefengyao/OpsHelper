using System;

namespace ServerOpsHelper.Models;

/// <summary>
/// 服务器信息数据模型
/// </summary>
public class ServerInfo
{
    /// <summary>
    /// 唯一标识
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 显示名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 主机地址或 IP
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// SSH / WinRM 等远程端口
    /// </summary>
    public int Port { get; set; } = 22;

    /// <summary>
    /// 登录用户名
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 登录密码（当前为明文存储，后续可升级为加密存储）
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// 操作系统类型
    /// </summary>
    public ServerOsType OsType { get; set; } = ServerOsType.Linux;

    /// <summary>
    /// 备注说明
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 分组标签，逗号分隔
    /// </summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 最后一次连接时间
    /// </summary>
    public DateTime? LastConnectedAt { get; set; }

    /// <summary>
    /// 是否被选中（仅 UI 使用，不持久化）
    /// </summary>
    public bool IsSelected { get; set; }
}

/// <summary>
/// 服务器操作系统类型
/// </summary>
public enum ServerOsType
{
    Linux,
    Windows
}
