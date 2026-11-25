using Microsoft.EntityFrameworkCore;

namespace SmartSchoolAI.ViewModels;

/// <summary>Which slice of a long list is shown: page number, size and how many items there are in all.</summary>
public class PageInfo
{
    public const int DefaultSize = 10;

    public int Page     { get; init; } = 1;
    public int PageSize { get; init; } = DefaultSize;
    public int Total    { get; init; }

    public int TotalPages => Total == 0 ? 1 : (Total + PageSize - 1) / PageSize;
    public int From       => Total == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int To         => Math.Min(Page * PageSize, Total);
    public bool HasPrevious => Page > 1;
    public bool HasNext     => Page < TotalPages;

    /// <summary>A page number from the address made safe: below 1 becomes 1, past the end becomes the last page
    /// (so deleting the only item on the last page lands on the page before, not on an empty one).</summary>
    public static PageInfo For(int? page, int total, int pageSize = DefaultSize)
    {
        var pages = total == 0 ? 1 : (total + pageSize - 1) / pageSize;
        return new PageInfo { Page = Math.Clamp(page ?? 1, 1, pages), PageSize = pageSize, Total = total };
    }

    /// <summary>The numbers to show in the pager: the first, the last, and two either side of the current one;
    /// null marks a gap ("…").</summary>
    public IReadOnlyList<int?> Window()
    {
        var shown = Enumerable.Range(1, TotalPages)
            .Where(p => p == 1 || p == TotalPages || Math.Abs(p - Page) <= 2)
            .ToList();
        var result = new List<int?>();
        for (var i = 0; i < shown.Count; i++)
        {
            if (i > 0 && shown[i] - shown[i - 1] > 1) result.Add(null);
            result.Add(shown[i]);
        }
        return result;
    }
}

public static class PagingExtensions
{
    /// <summary>Counts the (already ordered) query, then loads only the requested page.</summary>
    public static async Task<(List<T> Items, PageInfo Page)> ToPageAsync<T>(
        this IQueryable<T> ordered, int? page, int pageSize = PageInfo.DefaultSize)
    {
        var info = PageInfo.For(page, await ordered.CountAsync(), pageSize);
        var items = await ordered.Skip((info.Page - 1) * info.PageSize).Take(info.PageSize).ToListAsync();
        return (items, info);
    }
}
