using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace ServerOpsHelper.Frp;

public static class FrpToml
{
    public static FrpcConfig ParseFrpc(string text)
    {
        var cfg = new FrpcConfig();
        if (string.IsNullOrWhiteSpace(text)) return cfg;

        TomlTable doc;
        try
        {
            doc = Toml.ToModel(text);
        }
        catch
        {
            // 配置文本损坏（如手工编辑出语法错误）时降级按行提取关键字段，
            // 避免返回空配置后触发自动保存把用户内容覆盖掉。
            var kv = ExtractKeyValues(text);
            if (kv.TryGetValue("serverAddr", out var fsa)) cfg.ServerAddr = Sanitize(fsa);
            if (kv.TryGetValue("serverPort", out var fsp) && int.TryParse(fsp, out var fspv)) cfg.ServerPort = fspv;
            if (kv.TryGetValue("auth.token", out var ftk)) cfg.Token = Sanitize(ftk);
            if (kv.TryGetValue("transport.protocol", out var fpr)) cfg.Protocol = Sanitize(fpr);
            return cfg;
        }

        if (doc.TryGetValue("serverAddr", out var sa)) cfg.ServerAddr = Sanitize(sa?.ToString());
        if (doc.TryGetValue("serverPort", out var sp) && int.TryParse(sp?.ToString(), out var spv)) cfg.ServerPort = spv;

        if (doc.TryGetValue("auth", out var authObj) && authObj is TomlTable auth)
        {
            if (auth.TryGetValue("token", out var tk)) cfg.Token = Sanitize(tk?.ToString());
        }

        if (doc.TryGetValue("transport", out var transportObj) && transportObj is TomlTable transport)
        {
            if (transport.TryGetValue("protocol", out var proto)) cfg.Protocol = Sanitize(proto?.ToString());
        }

        foreach (var kv in doc)
        {
            if (kv.Key is "serverAddr" or "serverPort" or "auth" or "transport" or "proxies") continue;
            cfg.Extra[kv.Key] = Toml.FromModel(new TomlTable { [kv.Key] = kv.Value });
        }

        if (doc.TryGetValue("proxies", out var proxiesObj) && proxiesObj is TomlTableArray proxies)
        {
            foreach (var p in proxies)
            {
                var rule = new ProxyRule();
                if (p.TryGetValue("name", out var n)) rule.Name = Sanitize(n?.ToString());
                if (p.TryGetValue("type", out var t)) rule.Type = Sanitize(t?.ToString()) is { Length: > 0 } ty ? ty : "tcp";
                if (p.TryGetValue("localIP", out var lip)) rule.LocalIP = Sanitize(lip?.ToString());
                if (p.TryGetValue("localPort", out var lp) && int.TryParse(lp?.ToString(), out var lpv)) rule.LocalPort = lpv;
                if (p.TryGetValue("remotePort", out var rp) && int.TryParse(rp?.ToString(), out var rpv)) rule.RemotePort = rpv;
                if (p.TryGetValue("customDomains", out var cd) && cd is TomlArray arr)
                {
                    rule.CustomDomains = arr
                        .Select(x => Sanitize(x?.ToString()))
                        .Where(x => !string.IsNullOrEmpty(x))
                        .ToList();
                }

                foreach (var rkv in p)
                {
                    if (rkv.Key is "name" or "type" or "localIP" or "localPort" or "remotePort" or "customDomains") continue;
                    rule.Extra[rkv.Key] = Toml.FromModel(new TomlTable { [rkv.Key] = rkv.Value });
                }

                cfg.Proxies.Add(rule);
            }
        }

        return cfg;
    }

    public static FrpsConfig ParseFrps(string text)
    {
        var cfg = new FrpsConfig();
        if (string.IsNullOrWhiteSpace(text)) return cfg;

        TomlTable doc;
        try
        {
            doc = Toml.ToModel(text);
        }
        catch
        {
            // 同上：降级按行提取，避免损坏文本导致配置被判为空
            var kv = ExtractKeyValues(text);
            if (kv.TryGetValue("bindAddr", out var fba)) cfg.BindAddr = Sanitize(fba);
            if (kv.TryGetValue("bindPort", out var fbp) && int.TryParse(fbp, out var fbpv)) cfg.BindPort = fbpv;
            if (kv.TryGetValue("auth.token", out var ftk)) cfg.Token = Sanitize(ftk);
            if (kv.TryGetValue("transport.protocol", out var fpr)) cfg.Protocol = Sanitize(fpr);
            if (kv.TryGetValue("webServer.addr", out var fwa)) cfg.WebAddr = Sanitize(fwa);
            if (kv.TryGetValue("webServer.port", out var fwp) && int.TryParse(fwp, out var fwpv)) cfg.WebPort = fwpv;
            if (kv.TryGetValue("webServer.user", out var fwu)) cfg.WebUser = Sanitize(fwu);
            if (kv.TryGetValue("webServer.password", out var fpw)) cfg.WebPassword = Sanitize(fpw);
            return cfg;
        }

        if (doc.TryGetValue("bindAddr", out var ba)) cfg.BindAddr = Sanitize(ba?.ToString());
        if (doc.TryGetValue("bindPort", out var bp) && int.TryParse(bp?.ToString(), out var bpv)) cfg.BindPort = bpv;

        if (doc.TryGetValue("auth", out var authObj) && authObj is TomlTable auth)
        {
            if (auth.TryGetValue("token", out var tk)) cfg.Token = Sanitize(tk?.ToString());
        }

        if (doc.TryGetValue("transport", out var transportObj) && transportObj is TomlTable transport)
        {
            if (transport.TryGetValue("protocol", out var proto)) cfg.Protocol = Sanitize(proto?.ToString());
        }

        if (doc.TryGetValue("webServer", out var wsObj) && wsObj is TomlTable ws)
        {
            if (ws.TryGetValue("addr", out var wa)) cfg.WebAddr = Sanitize(wa?.ToString());
            if (ws.TryGetValue("port", out var wp) && int.TryParse(wp?.ToString(), out var wpv)) cfg.WebPort = wpv;
            if (ws.TryGetValue("user", out var wu)) cfg.WebUser = Sanitize(wu?.ToString());
            if (ws.TryGetValue("password", out var pwd)) cfg.WebPassword = Sanitize(pwd?.ToString());
        }

        foreach (var kv in doc)
        {
            if (kv.Key is "bindAddr" or "bindPort" or "auth" or "transport" or "webServer") continue;
            cfg.Extra[kv.Key] = Toml.FromModel(new TomlTable { [kv.Key] = kv.Value });
        }

        return cfg;
    }

    public static string SerializeFrpc(FrpcConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"serverAddr = \"{Escape(cfg.ServerAddr)}\"");
        sb.AppendLine($"serverPort = {cfg.ServerPort}");
        if (cfg.Protocol is not "" and not "tcp")
        {
            sb.AppendLine();
            sb.AppendLine($"transport.protocol = \"{Escape(cfg.Protocol)}\"");
        }

        if (!string.IsNullOrEmpty(cfg.Token))
        {
            sb.AppendLine();
            sb.AppendLine("auth.method = \"token\"");
            sb.AppendLine($"auth.token = \"{Escape(cfg.Token)}\"");
        }

        foreach (var kv in cfg.Extra)
        {
            sb.AppendLine(kv.Value);
        }

        foreach (var p in cfg.Proxies)
        {
            sb.AppendLine();
            sb.AppendLine("[[proxies]]");
            sb.AppendLine($"name = \"{Escape(p.Name)}\"");
            sb.AppendLine($"type = \"{Escape(p.Type)}\"");
            sb.AppendLine($"localIP = \"{Escape(p.LocalIP)}\"");
            sb.AppendLine($"localPort = {p.LocalPort}");
            if (p.IsPortBased)
            {
                sb.AppendLine($"remotePort = {p.RemotePort}");
            }
            else if (p.CustomDomains.Count > 0)
            {
                sb.AppendLine($"customDomains = [{string.Join(", ", p.CustomDomains.Select(d => $"\"{Escape(d)}\""))}]");
            }

            foreach (var kv in p.Extra)
            {
                sb.AppendLine(kv.Value);
            }
        }

        return sb.ToString();
    }

    public static string SerializeFrps(FrpsConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"bindAddr = \"{Escape(cfg.BindAddr)}\"");
        sb.AppendLine($"bindPort = {cfg.BindPort}");
        if (cfg.Protocol is not "" and not "tcp")
        {
            sb.AppendLine();
            sb.AppendLine($"transport.protocol = \"{Escape(cfg.Protocol)}\"");
        }

        if (!string.IsNullOrEmpty(cfg.Token))
        {
            sb.AppendLine();
            sb.AppendLine("auth.method = \"token\"");
            sb.AppendLine($"auth.token = \"{Escape(cfg.Token)}\"");
        }

        sb.AppendLine();
        sb.AppendLine($"webServer.addr = \"{Escape(cfg.WebAddr)}\"");
        sb.AppendLine($"webServer.port = {cfg.WebPort}");
        sb.AppendLine($"webServer.user = \"{Escape(cfg.WebUser)}\"");
        sb.AppendLine($"webServer.password = \"{Escape(cfg.WebPassword)}\"");

        foreach (var kv in cfg.Extra)
        {
            sb.AppendLine(kv.Value);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 转义 TOML 基本字符串里的反斜杠与双引号，保证拼接结果始终是合法 TOML。
    /// </summary>
    private static string Escape(string? value) =>
        (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// 清理粘贴导致的常见误输入：首尾空白、以及多余的引号
    /// （例如把 `"60d8a83c"` 或 `"60d8a83c` 粘进 Token 框）。
    /// </summary>
    private static string Sanitize(string? value)
    {
        var s = (value ?? "").Trim();
        return s.Trim('"').Trim();
    }

    /// <summary>
    /// 降级解析辅助：按行提取 `key = value`（含 `段.键` 内联写法），
    /// 供 TOML 语法损坏时尽力恢复字段。仅用于容错，正常路径不走这里。
    /// </summary>
    private static Dictionary<string, string> ExtractKeyValues(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']', ' ').Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            var fullKey = section.Length == 0 ? key : $"{section}.{key}";
            map[fullKey] = val;
        }
        return map;
    }
}