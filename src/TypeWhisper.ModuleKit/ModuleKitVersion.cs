namespace TypeWhisper.ModuleKit;

/// <summary>The version of the interface between the host and its modules.</summary>
/// <remarks>
/// A change that existing modules can still use raises <see cref="Minor"/>. A change that breaks them raises
/// <see cref="Major"/> and sets <see cref="Minor"/> to 0.
/// </remarks>
/// <param name="Major">Raised by changes that break existing modules.</param>
/// <param name="Minor">Raised by changes that existing modules can still use.</param>
public readonly record struct ModuleKitVersion(int Major, int Minor) : IComparable<ModuleKitVersion>
{
    /// <summary>The version this assembly provides.</summary>
    public static ModuleKitVersion Current { get; } = new(1, 1);

    /// <summary>Whether modules written against <paramref name="required"/> can run against this version.</summary>
    public bool Supports(ModuleKitVersion required) => Major == required.Major && Minor >= required.Minor;

    /// <inheritdoc />
    public int CompareTo(ModuleKitVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    /// <inheritdoc />
    public override string ToString() => $"{Major}.{Minor}";
}
