using ServerOpsHelper.Models;
using System;

namespace ServerOpsHelper.ViewModels;

/// <summary>
/// 内置浏览器的一个标签页。
/// <para>
/// 标签只表示「这次打开过、还开着的页面」，不落盘（关掉应用就没了）；
/// 完整的历史记录在「访问记录」页里，属于另一套数据。
/// </para>
/// </summary>
public class BrowserTab : ViewModelBase
{
    /// <summary>
    /// 标签宽度的下限：再窄就看不出标题了，此时改为横向滚动
    /// </summary>
    public const double MinWidth = 92;

    /// <summary>
    /// 标签宽度的上限：页面少的时候也不用拉得太宽
    /// </summary>
    public const double MaxWidth = 220;

    /// <summary>
    /// 标签之间的间距（由模板里的 Margin 决定，这里只用于算宽度）
    /// </summary>
    public const double Gap = 4;

    private string _url = string.Empty;
    private string _title = string.Empty;
    private bool _isActive;
    private double _width = MaxWidth;

    /// <summary>
    /// 这个标签是从哪条常用链接打开的（有值时「填充账号密码」可以直接用它的凭据）
    /// </summary>
    public ServerOpsHelper.Models.LinkItem? Link { get; set; }

    /// <summary>
    /// 页面地址
    /// </summary>
    public string Url
    {
        get => _url;
        set
        {
            if (SetProperty(ref _url, value))
            {
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    /// <summary>
    /// 页面标题（拿不到时回退成主机名）
    /// </summary>
    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(ToolTipText));
            }
        }
    }

    /// <summary>
    /// 是否是当前显示的那一个
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>
    /// 当前计算出来的标签宽度（页面越多越窄）
    /// </summary>
    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    /// <summary>
    /// 标签上显示的标题：优先页面标题，没有就用主机名
    /// </summary>
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title))
            {
                return Title.Trim();
            }

            var host = UrlHelper.GetHost(Url);
            return host.Length > 0 ? host : Url;
        }
    }

    /// <summary>
    /// 悬停提示：标题 + 完整地址
    /// </summary>
    public string ToolTipText => string.IsNullOrWhiteSpace(Title)
        ? Url
        : $"{Title}\n{Url}";

    /// <summary>
    /// 按可用宽度和标签数量算每个标签该多宽：页面越多越窄，到下限为止（再多的靠横向滚动）。
    /// </summary>
    /// <param name="availableWidth">标签栏可用宽度（DIP）</param>
    /// <param name="tabCount">标签数量</param>
    public static double ComputeWidth(double availableWidth, int tabCount)
    {
        if (tabCount <= 0 || availableWidth <= 0)
        {
            return MaxWidth;
        }

        // 每组标签还带一个 Gap 的间距，先扣掉；再留一点边距给滚动条
        var share = availableWidth / tabCount - Gap - 2;

        return Math.Clamp(share, MinWidth, MaxWidth);
    }
}
