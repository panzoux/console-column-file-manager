using Xunit;

public class SearchMatchTests
{
    // ── MatchesSearchQuery ──────────────────────────────────────────────

    [Fact]
    public void Match_LiteralSubstring_CaseInsensitive()
    {
        Assert.True(SearchHelper.MatchesSearchQuery("Report_2025.pdf", "report", false, null));
    }

    [Fact]
    public void Match_LiteralNoMatch()
    {
        Assert.False(SearchHelper.MatchesSearchQuery("budget.xlsx", "report", false, null));
    }

    [Fact]
    public void Match_EmptyQuery_ReturnsFalse()
    {
        Assert.False(SearchHelper.MatchesSearchQuery("anything.txt", "", false, null));
    }

    [Fact]
    public void Match_RegexMode_ValidPattern()
    {
        Assert.True(SearchHelper.MatchesSearchQuery("report_2025.pdf", @"^rep.*\.pdf$", true, null));
    }

    [Fact]
    public void Match_RegexMode_InvalidPattern_ReturnsFalse()
    {
        // unclosed group — must not throw
        Assert.False(SearchHelper.MatchesSearchQuery("anything.txt", @"^([", true, null));
    }

    [Fact]
    public void Match_RegexMode_CaseInsensitive()
    {
        Assert.True(SearchHelper.MatchesSearchQuery("REPORT.pdf", "report", true, null));
    }

    // ── FindNearestMatchIndex ───────────────────────────────────────────

    [Fact]
    public void Nearest_PicksFirstAtOrAfterAnchor()
    {
        var matches = new List<int> { 2, 7, 11 };
        Assert.Equal(1, SearchHelper.FindNearestMatchIndex(matches, anchor: 5));
    }

    [Fact]
    public void Nearest_WrapsToBeginnningWhenNoMatchAfterAnchor()
    {
        var matches = new List<int> { 2, 7 };
        Assert.Equal(0, SearchHelper.FindNearestMatchIndex(matches, anchor: 8));
    }

    [Fact]
    public void Nearest_EmptyMatches_ReturnsZero()
    {
        Assert.Equal(0, SearchHelper.FindNearestMatchIndex(new List<int>(), anchor: 3));
    }

    [Fact]
    public void Nearest_ExactAnchorMatch()
    {
        var matches = new List<int> { 5, 10 };
        Assert.Equal(0, SearchHelper.FindNearestMatchIndex(matches, anchor: 5));
    }
}
