using Xunit;

public class SearchStatusBarTests
{
    static string Strip(string s)
    {
        return System.Text.RegularExpressions.Regex.Replace(s, @"\x1b\[[0-9;]*m", "");
    }

    [Fact]
    public void StatusBar_EmptyQuery_ShowsPromptOnly()
    {
        var s = new SearchState { Active = true, Query = "", SearchDone = true };
        string bar = SearchHelper.BuildSearchStatusBar(s, new System.Collections.Generic.List<int>(), done: true, width: 40);
        Assert.StartsWith("/ ", Strip(bar));
    }

    [Fact]
    public void StatusBar_MatchesDone_ShowsCount()
    {
        var s = new SearchState { Active = true, Query = "rep", MatchIndex = 0, SearchDone = true };
        var matches = new System.Collections.Generic.List<int> { 1, 3, 7 };
        string bar = SearchHelper.BuildSearchStatusBar(s, matches, done: true, width: 60);
        string plain = Strip(bar);
        Assert.Contains("(1/3)", plain);
        Assert.DoesNotContain("*", plain);
        Assert.DoesNotContain("[regex]", plain);
    }

    [Fact]
    public void StatusBar_Scanning_ShowsStar()
    {
        var s = new SearchState { Active = true, Query = "rep", MatchIndex = 0 };
        var matches = new System.Collections.Generic.List<int> { 1, 3 };
        string bar = SearchHelper.BuildSearchStatusBar(s, matches, done: false, width: 60);
        string plain = Strip(bar);
        Assert.Contains("(1/2*)", plain);
    }

    [Fact]
    public void StatusBar_NoMatch_ShowsRedZero()
    {
        var s = new SearchState { Active = true, Query = "xyz", SearchDone = true };
        var matches = new System.Collections.Generic.List<int>();
        string bar = SearchHelper.BuildSearchStatusBar(s, matches, done: true, width: 60);
        Assert.Contains("\x1b[31m", bar);
        Assert.Contains("(0)", Strip(bar));
    }

    [Fact]
    public void StatusBar_RegexMode_ShowsTag()
    {
        var s = new SearchState { Active = true, Query = "^rep", RegexMode = true, MatchIndex = 0, SearchDone = true };
        var matches = new System.Collections.Generic.List<int> { 2 };
        string bar = SearchHelper.BuildSearchStatusBar(s, matches, done: true, width: 60);
        Assert.Contains("[regex]", Strip(bar));
    }

    [Fact]
    public void StatusBar_CountPinnedRight_CountAtEnd()
    {
        var s = new SearchState { Active = true, Query = "r", MatchIndex = 0, SearchDone = true };
        var matches = new System.Collections.Generic.List<int> { 0, 5, 9 };
        string bar = SearchHelper.BuildSearchStatusBar(s, matches, done: true, width: 40);
        string plain = Strip(bar);
        int pos = plain.IndexOf("(1/3)", StringComparison.Ordinal);
        Assert.True(pos > plain.Length - 10);
    }
}
