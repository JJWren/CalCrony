# Release summaries

One file per release, named for its version: `0.38.0.md`. Each says in plain words what changed
and how to use it, for the people who use CalCrony rather than the people who build it.

When a release is cut, the release workflow puts the file's text on top of the GitHub release
notes and into the Discord announcement, exactly as written. A release without a file goes out
with the changelog lines alone, as every release up to 0.37.0 did. (`0.37.0.md` is the first file
here and was written after that release had gone out, as the example to start from.)

[CONTRIBUTING.md](../../CONTRIBUTING.md#release-summary) has the whole convention: who writes the
file and when, how to pick its name, and what happens at release time. In short:

```markdown
### What's new

- **The feature's name.** One sentence on what members can do now.

### How to use it

1. The first step, with the command in backticks: `/command option:value`.
2. The next step.
```

- Start with `### What's new` and at least one bullet. `### How to use it` is optional and comes after it.
- No other headings, and no tables, images, HTML or horizontal rules: Discord and GitHub show those differently.
- Write `@name` only inside backticks, so that nobody is mentioned, and anything in `<angle brackets>` too. A link as `<https://…>` is fine.
- Close every code block you open, and keep invisible control characters out.
- At most 1,800 characters, so the announcement fits in one Discord message.

The `build-test` check holds every file here to those rules. To see what a release would post:

```bash
dotnet run --project tools/ReleaseNotes -- preview --version 0.38.0
```
