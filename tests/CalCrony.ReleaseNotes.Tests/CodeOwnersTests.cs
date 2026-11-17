namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The repository's code owners file. The <c>protect-master</c> ruleset asks for a code
/// owner's review on a pull request that changes a file with a code owner, and this file is
/// where GitHub finds who that is. No check on a pull request reads it, so a rule taken out of
/// it or added to it would fail nothing: some files would have no owner, or another one, and
/// the ruleset would ask accordingly. This test holds the file to one rule that gives every
/// file the repository's owner.</summary>
public class CodeOwnersTests
{
    private const string RepositoryPath = ".github/CODEOWNERS";

    [Fact]
    public void Every_file_has_the_repositorys_owner_as_its_code_owner_and_no_other_rule_stands_beside_that()
    {
        // GitHub goes by the last rule that matches a file, and a rule that names nobody leaves
        // the files it matches without an owner. So a second rule can take files away from the
        // first as easily as it can add an owner to them. If you change the file on purpose,
        // change this list with it.
        Assert.Equal(["* @JJWren"], Rules());
    }

    /// <summary>Gives the file's rules: every line that is neither blank nor a comment.</summary>
    /// <returns>The rules, in the order of the file, without their outer spaces.</returns>
    private static IReadOnlyList<string> Rules() =>
        Repository.Read(RepositoryPath)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
}
