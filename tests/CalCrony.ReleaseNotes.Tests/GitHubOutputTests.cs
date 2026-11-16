namespace CalCrony.ReleaseNotes.Tests;

/// <summary>A step's outputs, in the form the runner reads back.</summary>
public class GitHubOutputTests
{
    [Fact]
    public void A_one_line_value_is_name_equals_value()
    {
        Assert.Equal("has_summary=true\n", GitHubOutput.Format("has_summary", "true"));
        Assert.Equal("discord_summary=\n", GitHubOutput.Format("discord_summary", string.Empty));
    }

    [Fact]
    public void A_value_of_several_lines_sits_between_a_delimiter_that_is_not_in_it()
    {
        string value = "### What's new\n\n- One change.\nghadelimiter_ is only part of a delimiter";

        string[] lines = GitHubOutput.Format("discord_summary", value).Split('\n');

        string delimiter = lines[0]["discord_summary<<".Length..];
        Assert.StartsWith("discord_summary<<ghadelimiter_", lines[0], StringComparison.Ordinal);
        Assert.Equal(delimiter, lines[^2]);
        Assert.Equal(string.Empty, lines[^1]);
        Assert.Equal(value, string.Join('\n', lines[1..^2]));
        Assert.DoesNotContain(delimiter, value, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_delimiter_is_new()
    {
        string first = GitHubOutput.Format("name", "a\nb").Split('\n')[0];
        string second = GitHubOutput.Format("name", "a\nb").Split('\n')[0];

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("name=value")]
    [InlineData("line\nbreak")]
    public void A_name_that_could_be_read_as_something_else_is_refused(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => GitHubOutput.Format(name, "value"));
    }

    [Fact]
    public void Outputs_are_appended_to_the_file_one_after_the_other()
    {
        using TemporaryCheckout folder = new();
        string path = folder.PathOf("outputs.txt");

        GitHubOutput.Append(path, "has_summary", "true");
        GitHubOutput.Append(path, "other", "value");

        Assert.Equal("has_summary=true\nother=value\n", File.ReadAllText(path));
    }
}
