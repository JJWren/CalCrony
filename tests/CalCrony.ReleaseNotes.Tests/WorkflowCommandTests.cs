namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The warnings the tool writes for GitHub Actions to show on a run. Whatever goes into
/// one is escaped, so that it can neither end the command early nor start another.</summary>
public class WorkflowCommandTests
{
    [Fact]
    public void A_warning_names_the_file_and_the_line()
    {
        Assert.Equal(
            "::warning file=docs/release-notes/0.38.0.md,line=5::Tables do not render in Discord; use a list.",
            WorkflowCommand.Warning("docs/release-notes/0.38.0.md", 5, "Tables do not render in Discord; use a list."));
    }

    [Fact]
    public void A_warning_about_the_whole_file_has_no_line()
    {
        Assert.Equal(
            "::warning file=docs/release-notes/0.38.0.md::The file is empty.",
            WorkflowCommand.Warning("docs/release-notes/0.38.0.md", 0, "The file is empty."));
    }

    [Fact]
    public void A_message_cannot_start_another_command_or_another_line()
    {
        Assert.Equal(
            "::warning file=a.md,line=1::100%25 sure%0D%0A::error::not a command",
            WorkflowCommand.Warning("a.md", 1, "100% sure\r\n::error::not a command"));
    }

    [Fact]
    public void A_file_name_cannot_add_a_property_or_end_the_list()
    {
        Assert.Equal(
            "::warning file=a%2Cline=9%3A%3Ab.md,line=1::message",
            WorkflowCommand.Warning("a,line=9::b.md", 1, "message"));
    }
}
