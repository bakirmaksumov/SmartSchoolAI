using SmartSchoolAI.ViewModels;
using Xunit;

namespace SmartSchoolAI.Tests.Services;

public class PageInfoTests
{
    [Theory]
    [InlineData(null, 34, 1, 1, 10, 4)]
    [InlineData(2, 34, 2, 11, 20, 4)]
    [InlineData(4, 34, 4, 31, 34, 4)]
    [InlineData(9, 34, 4, 31, 34, 4)]   // past the end → the last page
    [InlineData(0, 34, 1, 1, 10, 4)]    // below 1 → the first page
    [InlineData(-3, 10, 1, 1, 10, 1)]
    [InlineData(2, 0, 1, 0, 0, 1)]      // nothing at all: one empty page
    public void Page_number_is_kept_in_range(int? asked, int total, int page, int from, int to, int pages)
    {
        var p = PageInfo.For(asked, total);

        Assert.Equal(page, p.Page);
        Assert.Equal(from, p.From);
        Assert.Equal(to, p.To);
        Assert.Equal(pages, p.TotalPages);
    }

    [Fact]
    public void Pager_shows_first_last_and_neighbours_with_gaps()
    {
        Assert.Equal(new int?[] { 1, null, 5, 6, 7, 8, 9, null, 20 }, PageInfo.For(7, 200).Window());
        Assert.Equal(new int?[] { 1, 2, 3, null, 20 }, PageInfo.For(1, 200).Window());
        Assert.Equal(new int?[] { 1, 2 }, PageInfo.For(2, 11).Window());
    }
}
