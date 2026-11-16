using ServerOpsHelper.Models;
using ServerOpsHelper.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ServerOpsHelper.ViewModels;

/// <summary>
/// 密码箱页视图模型：左列表 + 右详情的双栏结构，负责搜索、选中项与密码显隐。
/// </summary>
public class VaultViewModel : ViewModelBase
{
    private readonly ConfigService _configService = ConfigService.Instance;
    private readonly List<VaultItem> _allItems = new();

    private string _searchKeyword = string.Empty;
    private VaultItem? _selectedItem;
    private bool _isPasswordVisible;
    private bool _isLoading;

    /// <summary>
    /// 当前展示（已过滤 / 已排序）的密码条目
    /// </summary>
    public ObservableCollection<VaultItem> Items { get; } = new();

    /// <summary>
    /// 搜索关键字，命中名称、分类、账号、地址、备注
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
    /// 当前选中的条目
    /// </summary>
    public VaultItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                // 切换条目时自动把密码重新遮起来，避免"上一条还露着"
                IsPasswordVisible = false;
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(PasswordDisplay));
                OnPropertyChanged(nameof(DetailTitle));
            }
        }
    }

    /// <summary>
    /// 是否明文显示密码（仅 UI 状态，不持久化）
    /// </summary>
    public bool IsPasswordVisible
    {
        get => _isPasswordVisible;
        set
        {
            if (SetProperty(ref _isPasswordVisible, value))
            {
                OnPropertyChanged(nameof(PasswordDisplay));
                OnPropertyChanged(nameof(RevealButtonText));
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
    /// 是否有选中项（详情区显隐）
    /// </summary>
    public bool HasSelection => _selectedItem != null;

    /// <summary>
    /// 详情区标题
    /// </summary>
    public string DetailTitle => _selectedItem?.Title ?? string.Empty;

    /// <summary>
    /// 密码显示文本：明文或等长圆点
    /// </summary>
    public string PasswordDisplay
    {
        get
        {
            var password = _selectedItem?.Password ?? string.Empty;
            if (password.Length == 0)
            {
                return "（未填写）";
            }

            return _isPasswordVisible ? password : new string('\u25CF', Math.Min(password.Length, 20));
        }
    }

    /// <summary>
    /// 显隐按钮文案
    /// </summary>
    public string RevealButtonText => _isPasswordVisible ? "隐藏" : "显示";

    /// <summary>
    /// 列表为空时的提示文案
    /// </summary>
    public string EmptyHint => _allItems.Count == 0
        ? "密码箱是空的。点击「新增记录」把常用的后台、数据库、云平台账号记进来，密码会用 Windows DPAPI 加密保存。"
        : "没有匹配的记录，换个关键字试试。";

    /// <summary>
    /// 列表是否为空（用于显示占位提示）
    /// </summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// 统计信息
    /// </summary>
    public string SummaryText
    {
        get
        {
            if (_allItems.Count == 0)
            {
                return "暂无数据";
            }

            var categories = _allItems
                .Select(i => i.Category?.Trim())
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Count();

            var weak = _allItems.Count(i => i.StrengthLabel == "弱");

            return categories > 0
                ? $"共 {_allItems.Count} 条 · {categories} 个分类 · 弱密码 {weak} 条"
                : $"共 {_allItems.Count} 条 · 弱密码 {weak} 条";
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
            var selectedId = _selectedItem?.Id;
            var items = await _configService.LoadVaultAsync();

            _allItems.Clear();
            _allItems.AddRange(items);

            ApplyFilter();

            // 尽量保留原选中项，避免保存后详情区跳回空白
            SelectedItem = selectedId.HasValue
                ? Items.FirstOrDefault(i => i.Id == selectedId.Value) ?? Items.FirstOrDefault()
                : Items.FirstOrDefault();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 保存（新增或更新）一条记录并刷新列表
    /// </summary>
    public async Task SaveAsync(VaultItem item)
    {
        item.UpdatedAt = DateTime.Now;
        await _configService.SaveVaultItemAsync(item);
        await LoadAsync();
    }

    /// <summary>
    /// 删除一条记录并刷新列表
    /// </summary>
    public async Task DeleteAsync(VaultItem item)
    {
        await _configService.DeleteVaultItemAsync(item.Id);

        if (_selectedItem?.Id == item.Id)
        {
            SelectedItem = null;
        }

        await LoadAsync();
    }

    /// <summary>
    /// 按关键字过滤 + 按「置顶 → 最近修改 → 名称」排序
    /// </summary>
    private void ApplyFilter()
    {
        var keyword = _searchKeyword.Trim();
        IEnumerable<VaultItem> query = _allItems;

        if (keyword.Length > 0)
        {
            query = query.Where(i =>
                Contains(i.Title, keyword) ||
                Contains(i.Category, keyword) ||
                Contains(i.Username, keyword) ||
                Contains(i.Url, keyword) ||
                Contains(i.Notes, keyword));
        }

        var ordered = query
            .OrderByDescending(i => i.IsPinned)
            .ThenByDescending(i => i.UpdatedAt ?? i.CreatedAt)
            .ThenBy(i => i.Title, StringComparer.CurrentCulture)
            .ToList();

        Items.Clear();
        foreach (var item in ordered)
        {
            Items.Add(item);
        }

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static bool Contains(string? source, string keyword)
        => !string.IsNullOrEmpty(source)
           && source.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
}
