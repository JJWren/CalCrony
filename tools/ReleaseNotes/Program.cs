using System.Text;
using CalCrony.ReleaseNotes;

// Checks, previews and renders the hand-written release summaries in docs/release-notes.
// CONTRIBUTING.md ("Release summary") describes them. Run it with no arguments for the commands:
//
//   dotnet run --project tools/ReleaseNotes -- preview --version 0.38.0

Console.OutputEncoding = Encoding.UTF8;
return Cli.Run(args, Console.Out, Console.Error);
