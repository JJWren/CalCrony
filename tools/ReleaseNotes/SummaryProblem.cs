namespace CalCrony.ReleaseNotes;

/// <summary>One thing wrong with a release summary.</summary>
/// <param name="Line">The line it is on, counted from 1, or 0 when it concerns the whole file.</param>
/// <param name="Message">What is wrong and what to change.</param>
/// <param name="IsLength">Whether the problem is the summary's length. A summary that is only
/// too long still goes out, cut short for Discord; any other problem keeps it out of a release.</param>
public sealed record SummaryProblem(int Line, string Message, bool IsLength);
