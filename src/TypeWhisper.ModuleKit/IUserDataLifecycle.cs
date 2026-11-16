namespace TypeWhisper.ModuleKit;

/// <summary>Keeps module data with the user's data.</summary>
/// <remarks>
/// Files in <see cref="DataFolder"/> are exported by "Export all data" and erased by "Delete all data", following the
/// same rules as the host's own files: for example, files ending in <c>.secret</c>, <c>.dat</c> or <c>.tmp</c> stay out
/// of the export.
/// </remarks>
public interface IUserDataLifecycle
{
    /// <summary>The module's folder inside the user's data folder. It is created when missing.</summary>
    /// <exception cref="InvalidOperationException">The user is erasing all data; the app restarts afterwards.</exception>
    /// <param name="moduleId">The module's <see cref="IHostModule.Id"/>: ASCII letters, digits, dots, hyphens and underscores.</param>
    string DataFolder(string moduleId);

    /// <summary>
    /// Registers work that runs when the user erases all data, before the host deletes it, such as stopping a writer.
    /// A later registration with the same identifier replaces the earlier one.
    /// </summary>
    void Register(ModuleUserData userData);
}

/// <summary>What the host runs for one module's data when the user erases all data.</summary>
/// <param name="Id">The module's identifier.</param>
/// <param name="Erase">Runs on the UI thread before the host deletes the data. A failure is reported; the deletion goes on.</param>
public sealed record ModuleUserData(string Id, Func<Task> Erase);
