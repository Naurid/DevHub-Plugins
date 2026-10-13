namespace DevHub.Board.Core.Model;

/// <summary>
/// Ranks order issues in the backlog and in columns. A rank is a string of base-36 digits compared ordinally,
/// read as a fraction (".i" = 18/36). Putting an issue between two others gives it a rank strictly between
/// theirs, so a move rewrites one file instead of every issue below it (Jira calls this LexoRank).
/// A rank never ends in '0', so there's always room between two of them.
/// </summary>
public static class Rank
{
    private const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
    private const int Base = 36;

    /// <summary>Above every other rank would be the empty string; this is the usual first rank.</summary>
    public const string Middle = "i";

    /// <summary>
    /// A rank strictly between <paramref name="before"/> and <paramref name="after"/>. Null (or empty) means
    /// "no neighbour on that side": <c>Between(null, x)</c> is above x, <c>Between(x, null)</c> below it.
    /// </summary>
    /// <exception cref="ArgumentException">A rank isn't valid or <paramref name="before"/> isn't below <paramref name="after"/>.</exception>
    public static string Between(string? before, string? after)
    {
        before ??= "";
        if (after == "")
            after = null;
        Validate(before, nameof(before));
        if (after is not null)
        {
            Validate(after, nameof(after));
            if (string.CompareOrdinal(before, after) >= 0)
                throw new ArgumentException($"Rank \"{before}\" isn't below \"{after}\".");
        }
        return Midpoint(before, after);
    }

    /// <summary><paramref name="count"/> ranks spread evenly, for giving a whole list fresh short ranks.</summary>
    public static IReadOnlyList<string> Spread(int count)
    {
        // Enough digits that count values fit with gaps between them.
        var width = 1;
        while (Math.Pow(Base, width) < (count + 1) * 4)
            width++;
        var space = Math.Pow(Base, width);
        var ranks = new List<string>(count);
        for (var i = 1; i <= count; i++)
        {
            var value = (long)Math.Round(space * i / (count + 1));
            var chars = new char[width];
            for (var d = width - 1; d >= 0; d--)
            {
                chars[d] = Digits[(int)(value % Base)];
                value /= Base;
            }
            ranks.Add(new string(chars).TrimEnd('0'));
        }
        return ranks;
    }

    /// <summary>True for a non-empty string of base-36 digits not ending in '0'.</summary>
    public static bool IsValid(string? rank) =>
        !string.IsNullOrEmpty(rank) && rank[^1] != '0' && rank.All(c => Digits.Contains(c));

    private static void Validate(string rank, string name)
    {
        if (rank.Length > 0 && !IsValid(rank))
            throw new ArgumentException($"\"{rank}\" isn't a rank.", name);
    }

    // a < b (b null = 1.0). Both are fractions written as digits after the point.
    private static string Midpoint(string a, string? b)
    {
        if (b is not null)
        {
            // Skip the digits they share (a padded with zeros); the answer starts with them too.
            var n = 0;
            while (n < b.Length && (n < a.Length ? a[n] : '0') == b[n])
                n++;
            if (n > 0)
                return b[..n] + Midpoint(n < a.Length ? a[n..] : "", b[n..]);
        }

        var digitA = a.Length > 0 ? Digits.IndexOf(a[0]) : 0;
        var digitB = b is not null ? Digits.IndexOf(b[0]) : Base;
        if (digitB - digitA > 1)
            return Digits[(digitA + digitB + 1) / 2].ToString();

        // Adjacent first digits.
        if (b is not null && b.Length > 1)
            return b[..1];                                  // b's first digit alone is above a and below b
        return Digits[digitA] + Midpoint(a.Length > 0 ? a[1..] : "", null);
    }
}

/// <summary>Issue keys: "DEV-12" = project key + number.</summary>
public static class IssueKey
{
    /// <summary>A project key: 1–10 capital letters or digits, starting with a letter.</summary>
    public static bool IsValidProjectKey(string? key) =>
        key is { Length: >= 1 and <= 10 } && char.IsAsciiLetterUpper(key[0])
        && key.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c));

    public static string Format(string projectKey, int number) => $"{projectKey}-{number}";

    /// <summary>Splits "DEV-12" into ("DEV", 12); false for anything else.</summary>
    public static bool TryParse(string? text, out string projectKey, out int number)
    {
        projectKey = "";
        number = 0;
        var dash = text?.LastIndexOf('-') ?? -1;
        if (dash <= 0 || !IsValidProjectKey(text![..dash]) || !int.TryParse(text[(dash + 1)..],
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number)
            || number <= 0)
            return false;
        projectKey = text[..dash];
        return true;
    }
}

/// <summary>Short random ids for issues, statuses, comments… (8 lowercase letters / digits).</summary>
public static class BoardIds
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static string New()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Alphabet.Length)];
        // Start with a letter, so an id never looks like a number.
        chars[0] = Alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(26)];
        return new string(chars);
    }

    /// <summary>True for a string that's safe as a file name: lowercase letters, digits and '-'.</summary>
    public static bool IsValid(string? id) =>
        id is { Length: >= 1 and <= 64 } && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
