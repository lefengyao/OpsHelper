using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ServerOpsHelper.ViewModels;

/// <summary>
/// 访问记录页视图模型：本次会话条带 + 历史记录列表（搜索 / 删除 / 清空）。
/// </summary>
public class HistoryViewModel : ViewModelBase
{
    private readonly ConfigService _configService = ConfigService.Instance;
    private readonly VisitHistoryService _historyService = VisitHistoryService.Instance;
    private readonly List<VisitRecord> _allRecords = new();

    private string _searchKeyword = string.Empty;
    private bool _isLoading;

    /// <summary>
    /// 历史记录（已过滤 / 已排序）
    /// </summary>
    public ObservableCollection<VisitRecord> Records { get; } = new();

    /// <summary>
    /// 本次打开应用后访问过的页面（不落盘，关掉应用即清空）
    /// </summary>
    public ObservableCollection<VisitRecord> SessionRecords { get; } = new();

    /// <summary>
    /// 搜索关键字，命中标题、地址与来源链接名
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
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    /// 本次会话是否有记录（无记录时整条条带隐藏）
    /// </summary>
    public bool HasSession => SessionRecords.Count > 0;

    /// <summary>
    /// 本次会话条带的说明文案
    /// </summary>
    public string SessionSummaryText => SessionRecords.Count == 0
        ? "本次打开应用后还没有访问过网页。"
        : $"本次打开应用后访问了 {SessionRecords.Count} 个页面（关掉应用即清空）。";

    /// <summary>
    /// 列表是否为空（用于显示占位提示）
    /// </summary>
    public bool IsEmpty => Records.Count == 0;

    /// <summary>
    /// 列表为空时的提示文案
    /// </summary>
    public string EmptyHint => _allHistoryCount == 0
        ? "还没有访问记录。在「Web 控制台」里打开过的网页会自动记到这里，最多保留 300 条。"
        : "没有匹配的记录，换个关键字试试。";

    /// <summary>
    /// 统计信息
    /// </summary>
    public string SummaryText
    {
        get
        {
            if (_allHistoryCount == 0)
            {
                return "暂无数据";
            }

            var today = _allRecords.Count(r => r.LastVisitedAt.Date == DateTime.Now.Date);
            var total = _allRecords.Sum(r => r.VisitCount);

            return $"共 {_allHistoryCount} 个地址 · 今天访问过 {today} 个 · 累计打开 {total} 次";
        }
    }

    /// <summary>
    /// 磁盘上的记录总数（不受搜索过滤影响）
    /// </summary>
    private int _allHistoryCount;

    /// <summary>
    /// 加载历史与本次会话
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var records = await _configService.LoadHistoryAsync();

            _allRecords.Clear();
            _allRecords.AddRange(records);
            _allHistoryCount = records.Count;

            SessionRecords.Clear();
            foreach (var visit in _historyService.SessionVisits)
            {
                SessionRecords.Add(visit);
            }

            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 删除一条历史记录
    /// </summary>
    public async Task DeleteAsync(VisitRecord record)
    {
        await _configService.DeleteVisitAsync(record.Id);
        await LoadAsync();
    }

    /// <summary>
    /// 清空全部历史
    /// </summary>
    public async Task ClearAsync()
    {
        await _configService.ClearHistoryAsync();
        await LoadAsync();
    }

    /// <summary>
    /// 按关键字过滤 + 按最近访问时间倒序
    /// </summary>
    private void ApplyFilter()
    {
        var keyword = _searchKeyword.Trim();
        IEnumerable<VisitRecord> query = _allRecords;

        if (keyword.Length > 0)
        {
            query = query.Where(r =>
                Contains(r.Title, keyword) ||
                Contains(r.Url, keyword) ||
                Contains(r.LinkName, keyword));
        }

        var ordered = query
            .OrderByDescending(r => r.LastVisitedAt)
            .ToList();

        Records.Clear();
        foreach (var record in ordered)
        {
            // 本次会话访问过的地址打个标记，方便一眼看出「这次刚开过」
            record.VisitedThisSession = _historyService.WasVisitedThisSession(record.Url);
            Records.Add(record);
        }

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(SessionSummaryText));
    }

    private static bool Contains(string? source, string keyword)
        => !string.IsNullOrEmpty(source)
           && source.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
}
