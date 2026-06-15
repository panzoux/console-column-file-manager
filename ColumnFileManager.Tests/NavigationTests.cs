using Xunit;

public class NavigationTests
{
    static Column MakeColumn(int entryCount, int selected = 0, int scrollOffset = 0)
    {
        var c = new Column("test");
        for (int i = 0; i < entryCount; i++)
            c.Entries.Add($"file{i:D3}.txt");
        c.Selected = selected;
        c.ScrollOffset = scrollOffset;
        return c;
    }

    [Fact]
    public void NavigatePage_Down_AdvancesByVisibleHeight()
    {
        var c = MakeColumn(50, selected: 0);
        NavigationHelper.PageDown(c, visibleHeight: 10);
        Assert.Equal(10, c.Selected);
    }

    [Fact]
    public void NavigatePage_Down_ClampsAtLastEntry()
    {
        var c = MakeColumn(10, selected: 5);
        NavigationHelper.PageDown(c, visibleHeight: 20);
        Assert.Equal(9, c.Selected);
    }

    [Fact]
    public void NavigatePage_Up_DecrementsByVisibleHeight()
    {
        var c = MakeColumn(50, selected: 30);
        NavigationHelper.PageUp(c, visibleHeight: 10);
        Assert.Equal(20, c.Selected);
    }

    [Fact]
    public void NavigatePage_Up_ClampsAtZero()
    {
        var c = MakeColumn(50, selected: 3);
        NavigationHelper.PageUp(c, visibleHeight: 10);
        Assert.Equal(0, c.Selected);
    }

    [Fact]
    public void NavigatePage_Home_JumpsToFirst()
    {
        var c = MakeColumn(50, selected: 25);
        NavigationHelper.GoHome(c);
        Assert.Equal(0, c.Selected);
        Assert.Equal(0, c.ScrollOffset);
    }

    [Fact]
    public void NavigatePage_End_JumpsToLast()
    {
        var c = MakeColumn(50, selected: 0);
        NavigationHelper.GoEnd(c);
        Assert.Equal(49, c.Selected);
    }
}
