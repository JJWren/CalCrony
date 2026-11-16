using System.Globalization;

namespace CalCrony.ReleaseNotes;

/// <summary>A released version as release-please writes it: three numbers, "0.38.0". A release
/// summary's file is named for one.</summary>
/// <param name="Major">The major number.</param>
/// <param name="Minor">The minor number.</param>
/// <param name="Patch">The patch number.</param>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    private const int PartCount = 3;
    private const int MaxPartLength = 9;

    /// <summary>Reads a version written as three numbers separated by dots, each without a sign,
    /// spaces or leading zeros: "0.38.0". Anything else — "v0.38.0", "0.38", "1.0.0-rc.1" — is
    /// not one.</summary>
    /// <param name="text">The text to read.</param>
    /// <param name="version">The version, when the text is one.</param>
    /// <returns>Whether the text is a version.</returns>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;

        if (text is null)
        {
            return false;
        }

        string[] parts = text.Split('.');

        if (parts.Length != PartCount)
        {
            return false;
        }

        int[] numbers = new int[PartCount];

        for (int index = 0; index < PartCount; index++)
        {
            if (!TryParsePart(parts[index], out numbers[index]))
            {
                return false;
            }
        }

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>Orders versions by major, then minor, then patch number.</summary>
    /// <param name="other">The version to compare with.</param>
    /// <returns>A negative number when this version is the older one, zero when they are the
    /// same, a positive number when it is the newer one.</returns>
    public int CompareTo(ReleaseVersion other)
    {
        int byMajor = Major.CompareTo(other.Major);

        if (byMajor != 0)
        {
            return byMajor;
        }

        int byMinor = Minor.CompareTo(other.Minor);
        return byMinor != 0 ? byMinor : Patch.CompareTo(other.Patch);
    }

    /// <summary>Writes the version the way it was read: "0.38.0".</summary>
    /// <returns>The three numbers separated by dots.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>Reads one of the three numbers: ASCII digits only, no leading zero unless the
    /// number is zero itself.</summary>
    /// <param name="part">The text between two dots.</param>
    /// <param name="number">The number, when the text is one.</param>
    /// <returns>Whether the text is a number in that form.</returns>
    private static bool TryParsePart(string part, out int number)
    {
        number = 0;

        bool isCanonical = part.Length is > 0 and <= MaxPartLength
            && part.All(char.IsAsciiDigit)
            && (part.Length == 1 || part[0] != '0');

        return isCanonical
            && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
}
