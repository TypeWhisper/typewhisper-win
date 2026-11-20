using TypeWhisper.ModuleKit;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// IPremiumAccessReading over the access the Premium page shows, including development overrides in Debug builds.
internal sealed class ModulePremiumAccessReading : IPremiumAccessReading
{
    private readonly PremiumAccessState _access;
    private readonly Action _showPremiumAccess;
    private readonly Action<Action> _post;
    private readonly Action<string, Exception> _report;
    private readonly Func<string?> _signedAccountEntitlement;

    internal ModulePremiumAccessReading(PremiumAccessState access, Action showPremiumAccess, Action<Action> post, Action<string, Exception> report,
        Func<string?> signedAccountEntitlement)
    {
        _access = access;
        _signedAccountEntitlement = signedAccountEntitlement;
        _showPremiumAccess = showPremiumAccess;
        _post = post;
        _report = report;
    }

    public bool HasPremiumAccess => _access.Current.Any;

    public bool Meets(ModulePremiumRequirement requirement) =>
        _access.Current.Requirement(ToHostRequirement(requirement)) == PremiumRequirement.Available;

    public IDisposable ObserveChanges(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var observation = new ModuleObservation(_post, _report, "modules.premium.observer-failed");
        Action listener = () => observation.Deliver(handler);
        _access.Changed += listener;
        observation.Unsubscribe = () => _access.Changed -= listener;
        return observation;
    }

    public void ShowPremiumAccess() => _showPremiumAccess();

    public string? SignedAccountEntitlement => _signedAccountEntitlement();

    internal static PremiumRequirement ToHostRequirement(ModulePremiumRequirement requirement) => requirement switch
    {
        ModulePremiumRequirement.CommercialOrPremium => PremiumRequirement.CommercialOrPremium,
        ModulePremiumRequirement.Commercial => PremiumRequirement.Commercial,
        ModulePremiumRequirement.PremiumAccount => PremiumRequirement.PremiumAccount,
        _ => throw new ArgumentOutOfRangeException(nameof(requirement))
    };
}
