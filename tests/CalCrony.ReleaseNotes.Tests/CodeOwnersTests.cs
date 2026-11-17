using System.Diagnostics;
using System.Text;

namespace CalCrony.ReleaseNotes.Tests;

/// <summary>The repository's code owners file. The <c>protect-master</c> ruleset asks for a code
/// owner's review on a pull request that changes a file with a code owner, and this file is
/// where GitHub finds who that is. No check on a pull request reads it, so a rule taken out of
/// it or added to it would fail nothing: some files would have no owner, or another one, and
/// the ruleset would ask accordingly. These tests hold the file to one rule that gives every
/// file the repository's owner, and to a plain form: they read its bytes as they are, compare
/// each line whole, and check that git gives the file no attribute that would make what it
/// stores, which GitHub reads, differ from what a checkout holds, which these tests read.</summary>
public class CodeOwnersTests
{
    /// <summary>The one rule the file has: every file, the repository's owner.</summary>
    private const string Rule = "* @JJWren";

    /// <summary>The file's path from the repository root, as git takes it.</summary>
    private const string RepositoryPath = ".github/CODEOWNERS";

    /// <summary>The most bytes the file may have: far under the 3 MB above which GitHub does not
    /// load a code owners file at all, and room enough for comments.</summary>
    private const int MaxBytes = 16 * 1024;

    /// <summary>How long git may take to answer before the test gives up on it.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Where the file is in this checkout: in <c>.github/</c>, the first of the three
    /// places GitHub looks.</summary>
    private static readonly string FilePath = Path.Combine(Repository.Root, ".github", "CODEOWNERS");

    [Fact]
    public void Every_file_has_the_repositorys_owner_as_its_code_owner_and_no_other_rule_stands_beside_that()
    {
        // GitHub goes by the last rule that matches a file, and a rule that names nobody leaves
        // the files it matches without an owner. So a second rule can take files away from the
        // first as easily as it can add an owner to them. A line is a comment only when its first
        // character is a number sign, and blank only when it is empty; every other line is a
        // rule, and is compared whole. The pin is strict on purpose: a rule GitHub likely reads
        // the same way but written otherwise, with a comment after it or other spacing, fails
        // too. If you change the file on purpose, change this list with it.
        Assert.Equal([Rule], Rules());
    }

    [Fact]
    public void The_file_is_a_plain_file_of_printable_ascii_lines_far_under_githubs_size_limit()
    {
        // A link could show GitHub and this test different text; a byte outside printable ASCII
        // could be read one way here and another way there; and a carriage return that ends no
        // line ends one for some readers only. The two line ends a checkout can have both pass:
        // a line feed alone, and a carriage return before a line feed.
        FileInfo file = new(FilePath);
        byte[] bytes = File.ReadAllBytes(FilePath);

        Assert.Null(file.LinkTarget);
        Assert.InRange(bytes.Length, 1, MaxBytes);

        for (int index = 0; index < bytes.Length; index++)
        {
            byte current = bytes[index];
            bool isPrintable = current is >= 0x20 and <= 0x7E;
            bool isLineFeed = current == (byte)'\n';
            bool isLineEndPair = current == (byte)'\r' && index + 1 < bytes.Length && bytes[index + 1] == (byte)'\n';

            Assert.True(
                isPrintable || isLineFeed || isLineEndPair,
                $"Byte 0x{current:X2} at offset {index} is neither printable ASCII nor part of a line end.");
        }
    }

    [GitCheckoutFact]
    public async Task Git_gives_the_file_no_attribute_but_the_one_every_file_here_has()
    {
        // GitHub reads the file as the repository stores it, and these tests read it as a
        // checkout writes it. An attribute given to the file in a .gitattributes file, such as
        // working-tree-encoding, can make the two differ without a byte of this file changing.
        // text=auto, which every file here has from the repository's .gitattributes, changes no
        // more than the line ends, and the tests above take either.
        Assert.Equal(["text: auto"], await GitAttributesAsync());
    }

    /// <summary>Gives the file's rules: every line that is neither empty nor a comment, as it
    /// stands in the file, without the carriage return of a line end.</summary>
    /// <returns>The rules, in the order of the file.</returns>
    private static IReadOnlyList<string> Rules() =>
        Encoding.UTF8.GetString(File.ReadAllBytes(FilePath))
            .Split('\n')
            .Select(line => line.EndsWith('\r') ? line[..^1] : line)
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

    /// <summary>Asks git which attributes it gives the file, from every place it reads them.</summary>
    /// <returns>Each attribute with its value, as <c>name: value</c>, in the order git gives them.</returns>
    private static async Task<IReadOnlyList<string>> GitAttributesAsync()
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = Repository.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add("check-attr");
        start.ArgumentList.Add("--all");
        start.ArgumentList.Add("-z");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(RepositoryPath);

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("git did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(GitTimeout);

        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, $"git check-attr failed: {await error}");

        // With -z, git writes each attribute as the path, the name and the value, each followed
        // by a NUL character.
        string[] fields = (await output).Split('\0');
        List<string> attributes = [];

        for (int index = 0; index + 2 < fields.Length; index += 3)
        {
            attributes.Add($"{fields[index + 1]}: {fields[index + 2]}");
        }

        return attributes;
    }
}
