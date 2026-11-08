using Tickbird.Core;

namespace Tickbird.Tests;

/// <summary>The warranty and liability notice is the same everywhere it appears.</summary>
public class LegalNoticeTests
{
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Tickbird.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    [Fact]
    public void TheNotice_SaysWhatItMust()
    {
        Assert.Contains("AS IS", Legal.Warranty);
        Assert.Contains("NO WARRANTY OF ANY KIND", Legal.Warranty);
        Assert.Contains("MERCHANTABILITY", Legal.Warranty);
        Assert.Contains("FITNESS FOR A PARTICULAR PURPOSE", Legal.Warranty);
        Assert.Contains("not responsible or liable for any loss of data", Legal.Liability);
        Assert.Contains("Back up your data", Legal.Liability);
        Assert.Contains("not liable for any loss of data", Legal.Short);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/help/timescaledb.md")]
    [InlineData("docs/help/cratedb.md")]
    public void TheDocumentation_HasTheFullNotice(string file)
    {
        var text = RepoFile(file);
        Assert.Contains(Legal.Warranty, text);
        Assert.Contains(Legal.Liability, text);
    }

    [Fact]
    public void TheReadme_LinksTheDisclaimer_AndHasItsSection()
    {
        var readme = RepoFile("README.md");
        Assert.Contains("## Disclaimer", readme);
        Assert.Contains("(#disclaimer)", readme);
    }

    [Fact]
    public void TheCurrentReleaseNotes_HaveTheShortNotice()
    {
        var props = RepoFile("Version.props");
        var version = System.Text.RegularExpressions.Regex.Match(props, "<Version>(.*?)</Version>").Groups[1].Value;
        var notes = RepoFile($"docs/release-notes/v{version}.md");
        Assert.Contains(Legal.Short, notes);
        Assert.Contains($"Tickbird-{version}-Setup-win-x64.exe", notes);       // the downloads table names this version's files
    }
}
