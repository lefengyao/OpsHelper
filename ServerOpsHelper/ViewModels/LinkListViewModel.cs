using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ServerOpsHelper.ViewModels;

/// <summary>
/// 常用链接页视图模型：负责数据加载、搜索过滤与「经常打开」排序。
/// </summary>
public class LinkListViewModel : ViewModelBase
{
    private readonly ConfigService _configService = ConfigService.Instance;
    private readonly List<LinkItem> _allLinks = new();

    private string _searchKeyword = string.Empty;
    private bool _isLoading;

    /// <summary>
    /// 当前展示（已过滤 / 已排序）的链接集合
    /// </summary>
    public ObservableCollection<LinkItem> Links { get; } = new();

    /// <summary>
    /// 搜索关键字，命中名称、地址、账号、标签
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
    /// 列表为空时的提示文案
    /// </summary>
    public string EmptyHint => _allLinks.Count == 0
        ? "还没有保存任何链接。点击「新增网页」保存经常访问的后台，或「新增宝塔」记录面板专用链接。"
        : "没有匹配的链接，换个关键字试试。";

    /// <summary>
    /// 列表是否为空（用于显示占位提示）
    /// </summary>
    public bool IsEmpty => Links.Count == 0;

    /// <summary>
    /// 统计信息，例如：共 6 条 · 常用网页 4 · 宝塔面板 2
    /// </summary>
    public string SummaryText
    {
        get
        {
            if (_allLinks.Count == 0)
            {
                return "暂无数据";
            }

            var webCount = _allLinks.Count(l => l.Kind == LinkKind.WebPage);
            var btCount = _allLinks.Count(l => l.Kind == LinkKind.BtPanel);
            var openedCount = _allLinks.Count(l => l.OpenCount > 0);

            return $"共 {_allLinks.Count} 条 · 常用网页 {webCount} · 宝塔面板 {btCount} · 已打开过 {openedCount} 条";
        }
    }

    /// <summary>
    /// 从本地配置加载并刷新列表
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var links = await _configService.LoadLinksAsync();

            _allLinks.Clear();
            _allLinks.AddRange(links);

            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 保存（新增或更新）一条链接并刷新列表
    /// </summary>
    public async Task SaveAsync(LinkItem link)
    {
        await _configService.SaveLinkAsync(link);
        await LoadAsync();
    }

    /// <summary>
    /// 删除一条链接并刷新列表
    /// </summary>
    public async Task DeleteAsync(LinkItem link)
    {
        await _configService.DeleteLinkAsync(link.Id);
        await LoadAsync();
    }

    /// <summary>
    /// 记录一次打开，用于「经常打开」排序
    /// </summary>
    public async Task MarkOpenedAsync(LinkItem link)
    {
        await _configService.MarkLinkOpenedAsync(link);

        OnPropertyChanged(nameof(SummaryText));

        // 保持当前筛选关键字，仅重排
        ApplyFilter();
    }

    /// <summary>
    /// 按关键字过滤 + 按「置顶 → 打开次数 → 名称」排序
    /// </summary>
    private void ApplyFilter()
    {
        var keyword = _searchKeyword.Trim();
        IEnumerable<LinkItem> query = _allLinks;

        if (keyword.Length > 0)
        {
            query = query.Where(l =>
                Contains(l.Name, keyword) ||
                Contains(l.Url, keyword) ||
                Contains(l.Host, keyword) ||
                Contains(l.SafeEntry, keyword) ||
                Contains(l.Username, keyword) ||
                Contains(l.Tags, keyword) ||
                Contains(l.Notes, keyword));
        }

        var ordered = query
            .OrderByDescending(l => l.IsPinned)
            .ThenByDescending(l => l.OpenCount)
            .ThenByDescending(l => l.LastOpenedAt ?? DateTime.MinValue)
            .ThenBy(l => l.Name, StringComparer.CurrentCulture)
            .ToList();

        Links.Clear();
        foreach (var link in ordered)
        {
            Links.Add(link);
        }

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyHint));
    }

    private static bool Contains(string? source, string keyword)
        => !string.IsNullOrEmpty(source)
           && source.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
}
