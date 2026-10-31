namespace CodeLogic.Core.Utilities;

/// <summary>
/// A <see href="https://semver.org/spec/v2.0.0.html">Semantic Versioning 2.0.0</see> version:
/// <c>major.minor.patch[-prerelease][+build]</c>, for example <c>5.0.0</c>, <c>5.0.0-preview.1</c> or
/// <c>5.0.0-rc.2+sha.5114f85</c>.
/// </summary>
/// <remarks>
/// Ordering follows SemVer precedence: the numeric core first; a pre-release version is lower than the
/// same core without one (<c>1.0.0-rc.1 &lt; 1.0.0</c>); pre-release identifiers are compared one by
/// one, numeric ones by value and below alphanumeric ones, which compare ordinally; a longer list wins
/// when all shared identifiers are equal. Build metadata is kept but ignored for ordering and equality.
/// </remarks>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    /// <summary>Gets the major version component.</summary>
    public int Major { get; }
    /// <summary>Gets the minor version component.</summary>
    public int Minor { get; }
    /// <summary>Gets the patch version component.</summary>
    public int Patch { get; }

    /// <summary>The pre-release label without the leading <c>-</c> (e.g. <c>preview.1</c>); empty for a release.</summary>
    public string Prerelease { get; }

    /// <summary>The build metadata without the leading <c>+</c>; empty when there is none. Ignored for ordering.</summary>
    public string BuildMetadata { get; }

    /// <summary>True when the version has a pre-release label.</summary>
    public bool IsPrerelease => Prerelease.Length > 0;

    private readonly string[] _prereleaseIdentifiers;

    /// <summary>Initializes a new release version with the specified components.</summary>
    public SemanticVersion(int major, int minor, int patch)
        : this(major, minor, patch, prerelease: null, buildMetadata: null)
    {
    }

    /// <summary>Initializes a new semantic version with a pre-release label and/or build metadata.</summary>
    /// <param name="major">The major version, zero or more.</param>
    /// <param name="minor">The minor version, zero or more.</param>
    /// <param name="patch">The patch version, zero or more.</param>
    /// <param name="prerelease">Dot-separated pre-release identifiers without the leading <c>-</c>, or null.</param>
    /// <param name="buildMetadata">Dot-separated build identifiers without the leading <c>+</c>, or null.</param>
    /// <exception cref="ArgumentException">A label is not valid SemVer.</exception>
    public SemanticVersion(int major, int minor, int patch, string? prerelease, string? buildMetadata = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);

        if (!string.IsNullOrEmpty(prerelease) && !IsValidLabel(prerelease, prerelease: true))
            throw new ArgumentException($"Invalid pre-release label: '{prerelease}'.", nameof(prerelease));
        if (!string.IsNullOrEmpty(buildMetadata) && !IsValidLabel(buildMetadata, prerelease: false))
            throw new ArgumentException($"Invalid build metadata: '{buildMetadata}'.", nameof(buildMetadata));

        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease ?? "";
        BuildMetadata = buildMetadata ?? "";
        _prereleaseIdentifiers = Prerelease.Length == 0 ? [] : Prerelease.Split('.');
    }

    /// <summary>
    /// Parses a semantic version string such as <c>1.2.3</c>, <c>1.2.3-preview.1</c> or <c>1.2.3+build.7</c>.
    /// Throws <see cref="FormatException"/> on invalid input.
    /// </summary>
    public static SemanticVersion Parse(string value)
    {
        if (TryParseCore(value, out var result, out var error))
            return result!;
        throw new FormatException(error);
    }

    /// <summary>
    /// Attempts to parse a semantic version string. Returns false on invalid input.
    /// </summary>
    public static bool TryParse(string? value, out SemanticVersion? result) =>
        TryParseCore(value, out result, out _);

    private static bool TryParseCore(string? value, out SemanticVersion? result, out string error)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Version string cannot be null or empty.";
            return false;
        }

        var text = value.Trim();
        string? build = null;
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            build = text[(plus + 1)..];
            text = text[..plus];
            if (!IsValidLabel(build, prerelease: false))
            {
                error = $"Invalid build metadata in '{value}'.";
                return false;
            }
        }

        string? prerelease = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
            if (!IsValidLabel(prerelease, prerelease: true))
            {
                error = $"Invalid pre-release label in '{value}'.";
                return false;
            }
        }

        var parts = text.Split('.');
        if (parts.Length != 3)
        {
            error = $"Invalid semantic version format: '{value}'. Expected 'major.minor.patch[-prerelease][+build]'.";
            return false;
        }

        if (!TryParseNumber(parts[0], out var major)) { error = $"Invalid major version component: '{parts[0]}'."; return false; }
        if (!TryParseNumber(parts[1], out var minor)) { error = $"Invalid minor version component: '{parts[1]}'."; return false; }
        if (!TryParseNumber(parts[2], out var patch)) { error = $"Invalid patch version component: '{parts[2]}'."; return false; }

        result = new SemanticVersion(major, minor, patch, prerelease, build);
        error = "";
        return true;
    }

    /// <summary>Digits only, no sign, no leading zero (except "0" itself), fits an int.</summary>
    private static bool TryParseNumber(string s, out int value)
    {
        value = 0;
        if (!IsNumeric(s) || (s.Length > 1 && s[0] == '0')) return false;
        return int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool IsValidLabel(string label, bool prerelease)
    {
        if (label.Length == 0) return false;
        foreach (var identifier in label.Split('.'))
        {
            if (identifier.Length == 0) return false;
            foreach (var c in identifier)
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
            // Numeric pre-release identifiers must not have leading zeros; build identifiers may.
            if (prerelease && identifier.Length > 1 && identifier[0] == '0' && IsNumeric(identifier)) return false;
        }
        return true;
    }

    private static bool IsNumeric(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
            if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;

        int cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;

        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;

        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;

        // A release ranks above any pre-release of the same core.
        var mine = _prereleaseIdentifiers;
        var theirs = other._prereleaseIdentifiers;
        if (mine.Length == 0 || theirs.Length == 0)
            return theirs.Length.CompareTo(mine.Length);

        for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            cmp = CompareIdentifier(mine[i], theirs[i]);
            if (cmp != 0) return cmp;
        }
        return mine.Length.CompareTo(theirs.Length);
    }

    private static int CompareIdentifier(string a, string b)
    {
        var aNumeric = IsNumeric(a);
        var bNumeric = IsNumeric(b);
        if (aNumeric && bNumeric)
        {
            // No leading zeros, so a longer number is larger; compare digit strings to avoid overflow.
            var byLength = a.Length.CompareTo(b.Length);
            return byLength != 0 ? byLength : Math.Sign(string.CompareOrdinal(a, b));
        }
        if (aNumeric) return -1;
        if (bNumeric) return 1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    /// <summary>Equal in precedence: same core and pre-release label. Build metadata is ignored.</summary>
    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SemanticVersion);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Major, Minor, Patch, StringComparer.Ordinal.GetHashCode(Prerelease));

    /// <summary>The full version, including the pre-release label and build metadata when present.</summary>
    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        if (IsPrerelease) core += "-" + Prerelease;
        if (BuildMetadata.Length > 0) core += "+" + BuildMetadata;
        return core;
    }

    /// <summary>Determines whether two semantic versions are equal.</summary>
    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right is null : left.CompareTo(right) == 0;

    /// <summary>Determines whether two semantic versions are not equal.</summary>
    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) =>
        !(left == right);

    /// <summary>Determines whether the left version is less than the right version.</summary>
    public static bool operator <(SemanticVersion? left, SemanticVersion? right)
    {
        if (left is null) return right is not null;
        return left.CompareTo(right) < 0;
    }

    /// <summary>Determines whether the left version is greater than the right version.</summary>
    public static bool operator >(SemanticVersion? left, SemanticVersion? right)
    {
        if (left is null) return false;
        return left.CompareTo(right) > 0;
    }

    /// <summary>Determines whether the left version is less than or equal to the right version.</summary>
    public static bool operator <=(SemanticVersion? left, SemanticVersion? right) =>
        !(left > right);

    /// <summary>Determines whether the left version is greater than or equal to the right version.</summary>
    public static bool operator >=(SemanticVersion? left, SemanticVersion? right) =>
        !(left < right);
}
