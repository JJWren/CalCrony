namespace CalCrony.ReleaseNotes;

/// <summary>One thing wrong with a release summary.</summary>
/// <param name="Line">The line it is on, counted from 1, or 0 when it concerns the whole file.</param>
/// <param name="Message">What is wrong and what to change.</param>
public sealed record SummaryProblem(int Line, string Message);
