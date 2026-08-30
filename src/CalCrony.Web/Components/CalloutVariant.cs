namespace CalCrony.Web.Components;

/// <summary>Semantic callout flavors from the design system: Note is background info and tips,
/// Important is needed to succeed, Warning needs attention now, Caution has destructive
/// consequences. Hues are semantic per light/dark face — except Warning, which borrows each
/// theme's gilt token so it inherits the theme's temperature.</summary>
public enum CalloutVariant
{
    Note,
    Important,
    Warning,
    Caution,
}
