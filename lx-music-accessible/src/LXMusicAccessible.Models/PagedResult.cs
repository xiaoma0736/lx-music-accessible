namespace LXMusicAccessible.Models;

/// <summary>
/// 分页结果
/// </summary>
/// <typeparam name="T">数据类型</typeparam>
public class PagedResult<T>
{
    /// <summary>当前页码</summary>
    public int CurrentPage { get; set; }

    /// <summary>每页大小</summary>
    public int PageSize { get; set; }

    /// <summary>总项目数（搜索会话中为“已加载条数”）</summary>
    public int TotalItems { get; set; }

    /// <summary>是否仍可能有更多结果（搜索“加载更多”用）</summary>
    public bool HasMore { get; set; }

    /// <summary>总页数</summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalItems / PageSize) : 0;

    /// <summary>是否有上一页</summary>
    public bool HasPreviousPage => CurrentPage > 1;

    /// <summary>是否有下一页</summary>
    public bool HasNextPage => CurrentPage < TotalPages;

    /// <summary>数据项列表</summary>
    public List<T> Items { get; set; } = new();

    /// <summary>创建空结果</summary>
    public static PagedResult<T> Empty(int page, int pageSize) => new()
    {
        CurrentPage = page,
        PageSize = pageSize,
        TotalItems = 0,
        Items = new List<T>()
    };
}
