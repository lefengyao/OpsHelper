namespace ServerOpsHelper.Frp;

public class ProxyRule
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "tcp";
    public string LocalIP { get; set; } = "127.0.0.1";
    public int LocalPort { get; set; } = 8080;
    public int RemotePort { get; set; } = 8080;
    public List<string> CustomDomains { get; set; } = new();
    public Dictionary<string, string> Extra { get; set; } = new();

    public bool IsPortBased => Type is "tcp" or "udp";

    public string CustomDomainsText => string.Join(", ", CustomDomains);

    /// <summary>表格里显示的「本地服务」</summary>
    public string LocalTarget => $"{LocalIP}:{LocalPort}";

    /// <summary>表格里显示的「远端映射」：端口型显示端口，域名型显示域名</summary>
    public string RemoteTarget => IsPortBased
        ? RemotePort.ToString()
        : (CustomDomains.Count > 0 ? CustomDomainsText : "—");

    public ProxyRule Clone() => new()
    {
        Name = Name,
        Type = Type,
        LocalIP = LocalIP,
        LocalPort = LocalPort,
        RemotePort = RemotePort,
        CustomDomains = new List<string>(CustomDomains),
        Extra = new Dictionary<string, string>(Extra)
    };
}

public class FrpcConfig
{
    public string ServerAddr { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 7000;
    public string Token { get; set; } = "";

    /// <summary>传输协议: tcp | kcp | quic | websocket | wss (transport.protocol)</summary>
    public string Protocol { get; set; } = "tcp";

    public List<ProxyRule> Proxies { get; set; } = new();
    public Dictionary<string, string> Extra { get; set; } = new();
}

public class FrpsConfig
{
    public string BindAddr { get; set; } = "0.0.0.0";
    public int BindPort { get; set; } = 7000;
    public string Token { get; set; } = "";

    /// <summary>传输协议: tcp | kcp | quic | websocket | wss (transport.protocol)</summary>
    public string Protocol { get; set; } = "tcp";

    public string WebAddr { get; set; } = "0.0.0.0";
    public int WebPort { get; set; } = 7500;
    public string WebUser { get; set; } = "admin";
    public string WebPassword { get; set; } = "";
    public Dictionary<string, string> Extra { get; set; } = new();
}
