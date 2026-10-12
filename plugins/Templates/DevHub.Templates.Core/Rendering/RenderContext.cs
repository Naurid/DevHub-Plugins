namespace DevHub.Templates.Core.Rendering;

/// <summary>
/// The values one generation renders with: the template's variables (already checked, see
/// <see cref="VariableValues"/>) and the built-ins (§5): <c>Year</c>, <c>Date</c>, <c>Author</c>,
/// <c>Guid1</c>…<c>Guid9</c>. Create one per generation: its GUIDs are made once and then stay the
/// same, so one GUID can be used in several files (a .sln and its .csproj).
/// </summary>
public sealed class RenderContext
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <param name="variables">Variable name → value (bools as "true" / "false").</param>
    /// <param name="author">The author name from settings ("" when not set).</param>
    /// <param name="now">The time of the generation (a fixed one in tests).</param>
    /// <param name="newGuid">Makes the GUIDs (a fixed sequence in tests).</param>
    public RenderContext(IReadOnlyDictionary<string, string> variables, string author, DateTimeOffset now, Func<Guid>? newGuid = null)
    {
        newGuid ??= Guid.NewGuid;
        foreach (var (name, value) in variables)
            _values[name] = value;

        // Built-ins win over a variable of the same name (the validator refuses those anyway).
        _values["Year"] = now.Year.ToString("D4");
        _values["Date"] = now.ToString("yyyy-MM-dd");
        _values["Author"] = author;
        for (var i = 1; i <= 9; i++)
            _values["Guid" + i] = newGuid().ToString("D").ToUpperInvariant();
    }

    /// <summary>The value of a variable or built-in, or null when no such name exists (the token stays as written).</summary>
    public string? this[string name] => _values.GetValueOrDefault(name);
}
