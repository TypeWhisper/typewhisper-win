using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal enum PremiumDevScenario { Actual, Free, Supporter, Commercial, Premium, All, SignedOut }

// Development overrides never enter license stores, backups, sync or release access decisions.
internal sealed class PremiumAccessState
{
    private readonly string _path;
    private readonly Func<PremiumAccess> _actual;
    internal event Action? Changed;
    internal PremiumDevScenario Scenario { get; private set; }
    internal string? Error { get; private set; }
    internal static bool CanOverride
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
    internal bool IsOverridden => CanOverride && Scenario != PremiumDevScenario.Actual;
    internal PremiumAccess Current
    {
        get
        {
#if DEBUG
            return Scenario switch
            {
                PremiumDevScenario.Free => new(),
                PremiumDevScenario.Supporter => new(Supporter: true),
                PremiumDevScenario.Commercial => new(Commercial: true),
                PremiumDevScenario.Premium => new(PremiumAccount: true, SignedIn: true),
                PremiumDevScenario.All => new(Commercial: true, PremiumAccount: true, SignedIn: true),
                PremiumDevScenario.SignedOut => new(PremiumAccount: true),
                _ => _actual()
            };
#else
            return _actual();
#endif
        }
    }

    internal PremiumAccessState(string path, Func<PremiumAccess> actual)
    {
        _path = path; _actual = actual;
#if DEBUG
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 64) throw new InvalidDataException();
            var value = File.ReadAllText(path).Trim();
            if (!Enum.TryParse<PremiumDevScenario>(value, out var scenario) || !Enum.IsDefined(scenario))
                throw new InvalidDataException();
            Scenario = scenario;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { Error = "Could not read the development access setting. Actual access is used."; }
#endif
    }

    internal bool SetScenario(PremiumDevScenario scenario)
    {
#if DEBUG
        if (!Enum.IsDefined(scenario)) throw new ArgumentOutOfRangeException(nameof(scenario));
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (scenario == PremiumDevScenario.Actual) File.Delete(_path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                File.WriteAllText(temporary, scenario.ToString());
                File.Move(temporary, _path, true);
            }
            Scenario = scenario; Error = null; Changed?.Invoke(); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Error = "Could not save development access. The previous access remains active."; Changed?.Invoke(); return false; }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
#else
        return false;
#endif
    }
}
