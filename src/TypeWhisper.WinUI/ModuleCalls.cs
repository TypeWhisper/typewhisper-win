namespace TypeWhisper.WinUI;

// Every call into module code goes through here. An exception is reported with the stage
// modules.<point>.failed id=<id> and never reaches the caller, so a module cannot end the app from a callback.
internal static class ModuleCalls
{
    internal static string FailureStage(string point, string id) =>
        ModuleContributionCatalog.IsValidId(id) ? $"modules.{point}.failed id={id}" : $"modules.{point}.failed";

    // Returns false when the call threw.
    internal static bool Run(string point, string id, Action call, Action<string, Exception> report)
    {
        try
        {
            call();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            report(FailureStage(point, id), ex);
            return false;
        }
    }

    // Returns null when the call threw or returned null.
    internal static T? Get<T>(string point, string id, Func<T?> call, Action<string, Exception> report) where T : class
    {
        try { return call(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            report(FailureStage(point, id), ex);
            return null;
        }
    }
}
