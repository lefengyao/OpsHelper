using System;
using System.IO;

namespace ServerOpsHelper.Frp;

/// <summary>
/// FRP 管理的门面：把配置读写、进程控制、日志、安装器集中到一处，页面只跟它打交道。
/// <para>
/// 数据目录沿用 FrpManager 的 `%APPDATA%\FrpManager`（<see cref="FrpEnvironment"/> 里写死），
/// 所以两边**共用同一份配置档案与 frp 目录设置** —— 先在 FrpManager 里配好的东西，
/// 切到这边直接可用，不用重录。
/// </para>
/// <para>
/// 懒初始化：静态单例在**第一次访问**时才构造，而 <see cref="EnsureInitialized"/> 才真正
/// 去建目录 / 恢复 state.json。这样不用 FRP 的人打开 OpsHelper 不会平白多出一个目录。
/// </para>
/// </summary>
public sealed class FrpService
{
    private static FrpService? _instance;

    /// <summary>
    /// 单例。刻意用"首次访问才 new"而不是静态字段初始化：
    /// 构造函数里会 new FrpEnvironment（它建 %APPDATA%\FrpManager 目录），
    /// 不用 FRP 的人不该平白多出这个目录。
    /// </summary>
    public static FrpService Instance => _instance ??= new FrpService();

    /// <summary>只在本会话真的用过 FRP 时才收尾（避免退出时创建实例）</summary>
    public static void ShutdownIfCreated() => _instance?.Shutdown();

    public FrpState State { get; }

    public FrpEnvironment Environment { get; }

    public FrpProcess Process { get; }

    public FrpInstaller Installer { get; }

    private bool _initialized;

    private FrpService()
    {
        State = new FrpState();
        Environment = new FrpEnvironment();
        Process = new FrpProcess(State, Environment);
        Installer = new FrpInstaller(State, Environment, Process);
    }

    /// <summary>恢复 frp 目录与当前档案（幂等，可重复调用）</summary>
    public void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            Environment.Restore();
        }
        catch (Exception ex)
        {
            State.AppendLog($"[错误] 恢复 FRP 状态失败：{ex.Message}", "err");
        }
    }

    /// <summary>当前档的配置文件路径（用于界面展示）</summary>
    public string ConfigPath(string mode) => Environment.GetConfigPath(mode);

    /// <summary>读取原始 TOML 文本；档案不存在或文件缺失时返回空串</summary>
    public string ReadConfigText(string mode, string profile, out string path)
    {
        path = string.Empty;

        if (!Environment.TryResolveProfileDirectory(profile, out var dir, out _))
        {
            return string.Empty;
        }

        path = Path.Combine(dir, $"{mode}.toml");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <summary>写入原始 TOML 文本（文本模式保存）</summary>
    public string SaveConfigText(string mode, string profile, string text)
    {
        if (!Environment.TryResolveProfileDirectory(profile, out var dir, out var resolved))
        {
            throw new InvalidOperationException($"配置「{profile}」不存在");
        }

        var path = Path.Combine(dir, $"{mode}.toml");
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, text);

        State.AppendLog($"[系统] 配置已保存：{path}", "sys");
        return resolved;
    }

    public FrpcConfig LoadFrpc(string profile) => FrpToml.ParseFrpc(ReadConfigText("frpc", profile, out _));

    public FrpsConfig LoadFrps(string profile) => FrpToml.ParseFrps(ReadConfigText("frps", profile, out _));

    public string SaveFrpc(string profile, FrpcConfig config)
        => SaveConfigText("frpc", profile, FrpToml.SerializeFrpc(config));

    public string SaveFrps(string profile, FrpsConfig config)
        => SaveConfigText("frps", profile, FrpToml.SerializeFrps(config));

    /// <summary>退出程序时收尾：FRP 进程不能变成没人管的孤儿。</summary>
    public void Shutdown()
    {
        try
        {
            if (State.IsRunning)
            {
                Process.Stop();
            }
        }
        catch (Exception)
        {
            // 退出路径上不要再抛
        }
    }
}
