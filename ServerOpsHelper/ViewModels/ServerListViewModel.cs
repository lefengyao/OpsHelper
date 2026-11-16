using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ServerOpsHelper.ViewModels;

/// <summary>
/// 服务器列表页面视图模型
/// </summary>
public class ServerListViewModel : ViewModelBase
{
    private readonly ConfigService _configService;
    private readonly List<ServerInfo> _allServers = new();

    private ServerInfo? _selectedServer;
    private string _searchKeyword = string.Empty;
    private bool _isLoading;

    public ServerListViewModel()
    {
        _configService = ConfigService.Instance;
        Servers = new ObservableCollection<ServerInfo>();

        LoadServersCommand = new RelayCommand(async _ => await LoadServersAsync());
        AddServerCommand = new RelayCommand(_ => AddServer());
        DeleteServerCommand = new RelayCommand(async param =>
        {
            if (param is ServerInfo server)
            {
                await DeleteAsync(server);
            }
        });
    }

    /// <summary>
    /// 服务器集合（已按搜索关键字过滤）
    /// </summary>
    public ObservableCollection<ServerInfo> Servers { get; }

    /// <summary>
    /// 当前选中的服务器
    /// </summary>
    public ServerInfo? SelectedServer
    {
        get => _selectedServer;
        set => SetProperty(ref _selectedServer, value);
    }

    /// <summary>
    /// 搜索关键字
    /// </summary>
    public string SearchKeyword
    {
        get => _searchKeyword;
        set
        {
            if (SetProperty(ref _searchKeyword, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>
    /// 是否正在加载
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    /// 列表是否为空（用于显示占位提示）
    /// </summary>
    public bool IsEmpty => Servers.Count == 0;

    /// <summary>
    /// 列表为空时的提示文案
    /// </summary>
    public string EmptyHint => _allServers.Count == 0
        ? "还没有添加服务器。点击「新增」录入 IP、账号与密码，密码会用 Windows DPAPI 加密保存。"
        : "没有匹配的服务器，换个关键字试试。";

    /// <summary>
    /// 统计信息
    /// </summary>
    public string SummaryText => _allServers.Count == 0
        ? "暂无数据"
        : $"共 {_allServers.Count} 台 · Linux {_allServers.Count(s => s.OsType == ServerOsType.Linux)} · Windows {_allServers.Count(s => s.OsType == ServerOsType.Windows)}";

    public ICommand LoadServersCommand { get; }
    public ICommand AddServerCommand { get; }
    public ICommand DeleteServerCommand { get; }

    /// <summary>
    /// 从本地配置加载服务器列表
    /// </summary>
    public async Task LoadServersAsync()
    {
        IsLoading = true;
        try
        {
            var servers = await _configService.LoadServersAsync();

            _allServers.Clear();
            _allServers.AddRange(servers);

            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    // 新增/编辑由 ServerEditDialog 处理（见 ServerListPage.xaml.cs），此处预留命令占位
    private void AddServer()
    {
    }

    /// <summary>
    /// 删除指定服务器并刷新列表
    /// </summary>
    public async Task DeleteAsync(ServerInfo server)
    {
        await _configService.DeleteServerAsync(server.Id);
        await LoadServersAsync();
    }

    /// <summary>
    /// 按关键字过滤（内存过滤，不再重新读盘，避免异步竞态）
    /// </summary>
    private void ApplyFilter()
    {
        var keyword = _searchKeyword.Trim();

        var query = keyword.Length == 0
            ? _allServers.AsEnumerable()
            : _allServers.Where(s =>
                Contains(s.Name, keyword) ||
                Contains(s.Host, keyword) ||
                Contains(s.Tags, keyword) ||
                Contains(s.Username, keyword) ||
                Contains(s.Description, keyword));

        var ordered = query.OrderBy(s => s.Name, StringComparer.CurrentCulture).ToList();

        Servers.Clear();
        foreach (var server in ordered)
        {
            Servers.Add(server);
        }

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyHint));
    }

    private static bool Contains(string? source, string keyword)
        => !string.IsNullOrEmpty(source)
           && source.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>
/// 简易 ICommand 实现，避免引入额外 NuGet 包
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);
}
