using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Provides license section view model behavior.
/// </summary>
public sealed partial class LicenseSectionViewModel : ObservableObject
{
    private const string CustomerPortalUrl = "https://polar.sh/typewhisper/portal";
    private const string CheckoutUrlIndividual = "https://buy.polar.sh/polar_cl_Yfw7BSIXSNFESlrNPL0fNG8GHPqX9qhmxGce32wZfYJ";
    private const string CheckoutUrlTeam = "https://buy.polar.sh/polar_cl_kSqGfvss0Ces3W7R4xw7hr5NdgvEbPbhhUGRH4ad3Hj";
    private const string CheckoutUrlEnterprise = "https://buy.polar.sh/polar_cl_uzCNIsF0vY9gx2peWljyJU7JQoEzxHUueCPTA0MoOQe";
    private const string CheckoutUrlIndividualLifetime = "https://buy.polar.sh/polar_cl_Uiv5AnvLoQjx4JowO3gGciT7MLOovY4oY4ESz3PIxgI";
    private const string CheckoutUrlTeamLifetime = "https://buy.polar.sh/polar_cl_GjG4jf1fT9HGQn051cgN6xsWH9Xm6Z7oe0Ke71xq6Po";
    private const string CheckoutUrlEnterpriseLifetime = "https://buy.polar.sh/polar_cl_ngagiyJjXtxDBqv19EooEGJOLRcgzBWKBFYrZ2V2Xm7";
    private const string CheckoutUrlSupporterBronze = "https://buy.polar.sh/polar_cl_yilyo1V90RnuUX59V2PyLUIg45FpzYI8aMhG824wYn8";
    private const string CheckoutUrlSupporterSilver = "https://buy.polar.sh/polar_cl_lXFAqnanhrrPd1RZ95SCb2L05L3lNrUQIkYVd0ZmK5b";
    private const string CheckoutUrlSupporterGold = "https://buy.polar.sh/polar_cl_FpojMlLmyF73gOqpXLihSE0lNYnoQoaMxGp724IIor4";

    private CommercialLicenseTier _selectedCommercialPlanTier = CommercialLicenseTier.Individual;
    private ActivatedLicenseEntitlementKind? _activationNoticeKind;

    /// <summary>
    /// Initializes a new instance of the LicenseSectionViewModel class.
    /// </summary>
    public LicenseSectionViewModel(LicenseService license, SupporterDiscordService discord)
    {
        License = license;
        Discord = discord;

        MonthlyCommercialOptions =
        [
            new LicensePurchaseOption("Individual", "5 EUR/mo", Loc.Instance["License.TierIndividualHint"], CheckoutUrlIndividual),
            new LicensePurchaseOption("Team", "19 EUR/mo", Loc.Instance["License.TierTeamHint"], CheckoutUrlTeam),
            new LicensePurchaseOption("Enterprise", "99 EUR/mo", Loc.Instance["License.TierEnterpriseHint"], CheckoutUrlEnterprise),
        ];

        LifetimeCommercialOptions =
        [
            new LicensePurchaseOption("Individual", "99 EUR", Loc.Instance["License.TierIndividualHint"], CheckoutUrlIndividualLifetime),
            new LicensePurchaseOption("Team", "299 EUR", Loc.Instance["License.TierTeamHint"], CheckoutUrlTeamLifetime),
            new LicensePurchaseOption("Enterprise", "999 EUR", Loc.Instance["License.TierEnterpriseHint"], CheckoutUrlEnterpriseLifetime),
        ];

        SupporterOptions =
        [
            new LicensePurchaseOption("Bronze", "10 EUR", Loc.Instance["License.SupporterBronzeHint"], CheckoutUrlSupporterBronze),
            new LicensePurchaseOption("Silver", "25 EUR", Loc.Instance["License.SupporterSilverHint"], CheckoutUrlSupporterSilver),
            new LicensePurchaseOption("Gold", "50 EUR", Loc.Instance["License.SupporterGoldHint"], CheckoutUrlSupporterGold),
        ];

        SelectPrivateUserCommand = new RelayCommand(() => License.SetUserType(LicenseUserType.PrivateUser));
        SelectBusinessUserCommand = new RelayCommand(() => License.SetUserType(LicenseUserType.Business));
        SelectPlanCommand = new RelayCommand<LicensePlanOption>(SelectPlan);
        OpenUrlCommand = new RelayCommand<string>(OpenUrl);
        OpenCustomerPortalCommand = new RelayCommand(() => OpenUrl(CustomerPortalUrl));
        ActivateLicenseCommand = new AsyncRelayCommand(ActivateLicenseAsync, CanActivateLicense);
        RefreshCommercialLicenseCommand = new AsyncRelayCommand(() => License.RefreshCommercialLicenseAsync(), CanManageCommercialLicense);
        RefreshSupporterLicenseCommand = new AsyncRelayCommand(() => License.RefreshSupporterLicenseAsync(), CanManageSupporterLicense);
        DeactivateCommercialLicenseCommand = new AsyncRelayCommand(DeactivateCommercialLicenseAsync, CanManageCommercialLicense);
        DeactivateSupporterLicenseCommand = new AsyncRelayCommand(DeactivateSupporterLicenseAsync, CanManageSupporterLicense);
        ConnectDiscordCommand = new AsyncRelayCommand(ConnectDiscordAsync, CanConnectDiscord);
        ReconnectDiscordCommand = new AsyncRelayCommand(ReconnectDiscordAsync, CanReconnectDiscord);
        RefreshDiscordStatusCommand = new AsyncRelayCommand(RefreshDiscordStatusAsync, CanRefreshDiscord);
        OpenGitHubSponsorsClaimCommand = new RelayCommand(() => OpenUrl(Discord.GitHubSponsorsUrl));

        License.PropertyChanged += OnServicePropertyChanged;
        Discord.PropertyChanged += OnServicePropertyChanged;
    }

    /// <summary>
    /// Gets the license.
    /// </summary>
    public LicenseService License { get; }
    /// <summary>
    /// Gets the discord.
    /// </summary>
    public SupporterDiscordService Discord { get; }
    /// <summary>
    /// Gets the monthly commercial options.
    /// </summary>
    public IReadOnlyList<LicensePurchaseOption> MonthlyCommercialOptions { get; }
    /// <summary>
    /// Gets the lifetime commercial options.
    /// </summary>
    public IReadOnlyList<LicensePurchaseOption> LifetimeCommercialOptions { get; }
    /// <summary>
    /// Gets the supporter options.
    /// </summary>
    public IReadOnlyList<LicensePurchaseOption> SupporterOptions { get; }
    /// <summary>
    /// Gets the plan options.
    /// </summary>
    public IReadOnlyList<LicensePlanOption> PlanOptions =>
    [
        new(
            "gpl",
            Loc.Instance["License.PlanGplTitle"],
            Loc.Instance["License.PlanGplPrice"],
            Loc.Instance["License.PlanGplDetail"],
            "\uE73E",
            null,
            IsPrivateUser && License.CommercialStatus != LicenseStatus.Active),
        new(
            "individual",
            Loc.Instance["License.PlanIndividualTitle"],
            Loc.Instance["License.PlanIndividualPrice"],
            Loc.Instance["License.PlanIndividualDetail"],
            "\uE8EC",
            CommercialLicenseTier.Individual,
            IsCommercialPlanSelected(CommercialLicenseTier.Individual)),
        new(
            "team",
            Loc.Instance["License.PlanTeamTitle"],
            Loc.Instance["License.PlanTeamPrice"],
            Loc.Instance["License.PlanTeamDetail"],
            "\uE716",
            CommercialLicenseTier.Team,
            IsCommercialPlanSelected(CommercialLicenseTier.Team)),
        new(
            "enterprise",
            Loc.Instance["License.PlanEnterpriseTitle"],
            Loc.Instance["License.PlanEnterprisePrice"],
            Loc.Instance["License.PlanEnterpriseDetail"],
            "\uE80F",
            CommercialLicenseTier.Enterprise,
            IsCommercialPlanSelected(CommercialLicenseTier.Enterprise)),
    ];

    [ObservableProperty]
    private string _licenseKeyInput = string.Empty;

    [ObservableProperty]
    private string? _licenseActivationNotice;

    /// <summary>
    /// Gets whether is private user.
    /// </summary>
    public bool IsPrivateUser => License.IsPrivateUser;
    /// <summary>
    /// Gets whether is business user.
    /// </summary>
    public bool IsBusinessUser => License.IsBusinessUser;
    /// <summary>
    /// Gets whether show plan selection.
    /// </summary>
    public bool ShowPlanSelection => License.CommercialStatus != LicenseStatus.Active;
    /// <summary>
    /// Gets whether show license activation.
    /// </summary>
    public bool ShowLicenseActivation => License.CommercialStatus != LicenseStatus.Active;
    /// <summary>
    /// Gets whether show commercial purchase.
    /// </summary>
    public bool ShowCommercialPurchase =>
        License.CommercialStatus != LicenseStatus.Active
        && (IsBusinessUser || License.HasCommercialActivation);
    /// <summary>
    /// Gets whether show commercial manage.
    /// </summary>
    public bool ShowCommercialManage => License.HasCommercialActivation;
    /// <summary>
    /// Gets whether show supporter purchase.
    /// </summary>
    public bool ShowSupporterPurchase => !License.HasSupporterLicense;
    /// <summary>
    /// Gets whether show supporter manage.
    /// </summary>
    public bool ShowSupporterManage => License.HasSupporterActivation;
    /// <summary>
    /// Gets whether show customer portal shortcut.
    /// </summary>
    public bool ShowCustomerPortalShortcut => !License.HasCommercialActivation && !License.HasSupporterActivation;
    /// <summary>
    /// Gets whether show discord section.
    /// </summary>
    public bool ShowDiscordSection => License.HasSupporterLicense;
    /// <summary>
    /// Gets whether show discord connect.
    /// </summary>
    public bool ShowDiscordConnect => ShowDiscordSection && Discord.ClaimState is SupporterDiscordClaimState.Unavailable or SupporterDiscordClaimState.Unlinked;
    /// <summary>
    /// Shows discord refresh.
    /// </summary>
    public bool ShowDiscordRefresh => ShowDiscordSection && (Discord.IsHelperUnavailable || Discord.ClaimState is SupporterDiscordClaimState.Pending or SupporterDiscordClaimState.Linked or SupporterDiscordClaimState.Failed);
    /// <summary>
    /// Gets whether show discord reconnect.
    /// </summary>
    public bool ShowDiscordReconnect => ShowDiscordSection && !Discord.IsHelperUnavailable && Discord.ClaimState is SupporterDiscordClaimState.Pending or SupporterDiscordClaimState.Linked or SupporterDiscordClaimState.Failed;
    /// <summary>
    /// Gets the commercial status title.
    /// </summary>
    public string CommercialStatusTitle => License.CommercialStatus switch
    {
        LicenseStatus.Active => Loc.Instance["License.CommercialActiveTitle"],
        LicenseStatus.Expired => Loc.Instance["License.CommercialExpiredTitle"],
        _ => Loc.Instance["License.CommercialInactiveTitle"],
    };

    /// <summary>
    /// Gets the commercial status detail.
    /// </summary>
    public string CommercialStatusDetail
    {
        get
        {
            if (License.CommercialStatus == LicenseStatus.Expired)
                return Loc.Instance["License.CommercialExpiredDetail"];

            if (License.CommercialStatus != LicenseStatus.Active)
                return Loc.Instance["License.CommercialInactiveDetail"];

            var tier = License.CommercialTierDisplayName;
            var lifetimeSuffix = License.CommercialIsLifetime ? Loc.Instance["License.LifetimeSuffix"] : string.Empty;
            return !string.IsNullOrWhiteSpace(tier)
                ? Loc.Instance.GetString("License.CommercialActiveDetailFormat", tier, lifetimeSuffix)
                : (License.CommercialIsLifetime
                    ? Loc.Instance["License.CommercialActiveLifetimeDetail"]
                    : Loc.Instance["License.CommercialActiveSubscriptionDetail"]);
        }
    }

    /// <summary>
    /// Gets the supporter status title.
    /// </summary>
    public string SupporterStatusTitle => License.SupporterStatus switch
    {
        LicenseStatus.Active => Loc.Instance["License.SupporterActiveTitle"],
        LicenseStatus.Expired => Loc.Instance["License.SupporterExpiredTitle"],
        _ => Loc.Instance["License.SupporterInactiveTitle"],
    };

    /// <summary>
    /// Gets the supporter status detail.
    /// </summary>
    public string SupporterStatusDetail => License.SupporterStatus switch
    {
        LicenseStatus.Active when !string.IsNullOrWhiteSpace(License.SupporterTierDisplayName)
            => Loc.Instance.GetString("License.SupporterActiveDetailFormat", License.SupporterTierDisplayName ?? string.Empty),
        LicenseStatus.Expired => Loc.Instance["License.SupporterExpiredDetail"],
        _ => Loc.Instance["License.SupporterInactiveDetail"],
    };

    /// <summary>
    /// Gets the discord status title.
    /// </summary>
    public string DiscordStatusTitle => Discord.ClaimState switch
    {
        _ when Discord.IsHelperUnavailable => Loc.Instance["License.DiscordServiceUnavailableTitle"],
        SupporterDiscordClaimState.Linked => Loc.Instance["License.DiscordLinkedTitle"],
        SupporterDiscordClaimState.Pending => Loc.Instance["License.DiscordPendingTitle"],
        SupporterDiscordClaimState.Failed => Loc.Instance["License.DiscordFailedTitle"],
        _ => Loc.Instance["License.DiscordUnlinkedTitle"],
    };

    /// <summary>
    /// Gets the discord status detail.
    /// </summary>
    public string DiscordStatusDetail
    {
        get
        {
            if (Discord.IsHelperUnavailable)
                return Loc.Instance["License.DiscordServiceUnavailableDetail"];

            return Discord.ClaimState switch
            {
                SupporterDiscordClaimState.Linked when !string.IsNullOrWhiteSpace(Discord.DiscordUsername) && Discord.HasLinkedRoles
                    => Loc.Instance.GetString("License.DiscordLinkedDetailFormat", Discord.DiscordUsername ?? string.Empty, Discord.LinkedRolesText),
                SupporterDiscordClaimState.Linked when !string.IsNullOrWhiteSpace(Discord.DiscordUsername)
                    => Loc.Instance.GetString("License.DiscordLinkedUserOnlyFormat", Discord.DiscordUsername ?? string.Empty),
                SupporterDiscordClaimState.Pending
                    => Loc.Instance["License.DiscordPendingDetail"],
                SupporterDiscordClaimState.Failed
                    => string.IsNullOrWhiteSpace(Discord.ErrorMessage)
                        ? Loc.Instance["License.DiscordFailedDetail"]
                        : Discord.ErrorMessage!,
                _ => Loc.Instance["License.DiscordUnlinkedDetail"],
            };
        }
    }

    /// <summary>
    /// Gets the select private user command.
    /// </summary>
    public RelayCommand SelectPrivateUserCommand { get; }
    /// <summary>
    /// Gets the select business user command.
    /// </summary>
    public RelayCommand SelectBusinessUserCommand { get; }
    /// <summary>
    /// Gets the select plan command.
    /// </summary>
    public RelayCommand<LicensePlanOption> SelectPlanCommand { get; }
    /// <summary>
    /// Gets the open url command.
    /// </summary>
    public RelayCommand<string> OpenUrlCommand { get; }
    /// <summary>
    /// Gets the open customer portal command.
    /// </summary>
    public RelayCommand OpenCustomerPortalCommand { get; }
    /// <summary>
    /// Gets the open git hub sponsors claim command.
    /// </summary>
    public RelayCommand OpenGitHubSponsorsClaimCommand { get; }
    /// <summary>
    /// Gets the activate license command.
    /// </summary>
    public AsyncRelayCommand ActivateLicenseCommand { get; }
    /// <summary>
    /// Gets the refresh commercial license command.
    /// </summary>
    public AsyncRelayCommand RefreshCommercialLicenseCommand { get; }
    /// <summary>
    /// Gets the refresh supporter license command.
    /// </summary>
    public AsyncRelayCommand RefreshSupporterLicenseCommand { get; }
    /// <summary>
    /// Gets the deactivate commercial license command.
    /// </summary>
    public AsyncRelayCommand DeactivateCommercialLicenseCommand { get; }
    /// <summary>
    /// Gets the deactivate supporter license command.
    /// </summary>
    public AsyncRelayCommand DeactivateSupporterLicenseCommand { get; }
    /// <summary>
    /// Gets the connect discord command.
    /// </summary>
    public AsyncRelayCommand ConnectDiscordCommand { get; }
    /// <summary>
    /// Gets the reconnect discord command.
    /// </summary>
    public AsyncRelayCommand ReconnectDiscordCommand { get; }
    /// <summary>
    /// Gets the refresh discord status command.
    /// </summary>
    public AsyncRelayCommand RefreshDiscordStatusCommand { get; }

    /// <summary>
    /// Performs initialize asynchronously.
    /// </summary>
    public async Task InitializeAsync()
    {
        await License.ValidateAllIfNeededAsync();
        await Discord.RefreshStatusIfNeededAsync(License);
        RefreshComputedProperties();
    }

    partial void OnLicenseKeyInputChanged(string value) =>
        ActivateLicenseCommand.NotifyCanExecuteChanged();

    private bool CanActivateLicense() =>
        !string.IsNullOrWhiteSpace(LicenseKeyInput) && !License.IsLicenseActivating;

    private bool CanManageCommercialLicense() =>
        License.HasCommercialActivation && !License.IsCommercialRefreshing;

    private bool CanManageSupporterLicense() =>
        License.HasSupporterActivation && !License.IsSupporterRefreshing;

    private bool CanConnectDiscord() =>
        License.HasSupporterLicense && !Discord.IsWorking;

    private bool CanReconnectDiscord() =>
        License.HasSupporterLicense && !Discord.IsWorking;

    private bool CanRefreshDiscord() =>
        License.HasSupporterLicense && !Discord.IsWorking;

    private bool IsCommercialPlanSelected(CommercialLicenseTier tier) =>
        License.CommercialStatus == LicenseStatus.Active
            ? License.CommercialTier == tier
            : IsBusinessUser && _selectedCommercialPlanTier == tier;

    private void SelectPlan(LicensePlanOption? option)
    {
        if (option is null)
            return;

        if (option.CommercialTier is { } tier)
        {
            _selectedCommercialPlanTier = tier;
            License.SetUserType(LicenseUserType.Business);
        }
        else
        {
            License.SetUserType(LicenseUserType.PrivateUser);
        }

        RefreshComputedProperties();
    }

    private async Task ActivateLicenseAsync()
    {
        LicenseActivationNotice = null;
        _activationNoticeKind = null;

        var entitlement = await License.ActivateAnyLicenseKeyAsync(LicenseKeyInput.Trim());
        if (entitlement is not null)
        {
            if (entitlement.CommercialTier is { } tier)
            {
                _selectedCommercialPlanTier = tier;
                License.SetUserType(LicenseUserType.Business);
            }

            LicenseKeyInput = string.Empty;
            _activationNoticeKind = entitlement.Kind;
            LicenseActivationNotice = ActivationSuccessText(entitlement);
        }

        RefreshComputedProperties();
    }

    private async Task DeactivateCommercialLicenseAsync()
    {
        await License.DeactivateCommercialLicenseAsync();
        ClearStaleActivationNotice();
        RefreshComputedProperties();
    }

    private async Task DeactivateSupporterLicenseAsync()
    {
        await License.DeactivateSupporterLicenseAsync();
        if (!License.HasSupporterActivation)
            Discord.HandleSupporterEntitlementRemoved();

        ClearStaleActivationNotice();
        RefreshComputedProperties();
    }

    private async Task ConnectDiscordAsync()
    {
        var url = await Discord.CreateClaimSessionAsync(License);
        if (url is not null)
            OpenUrl(url.ToString());
        RefreshComputedProperties();
    }

    private async Task ReconnectDiscordAsync()
    {
        var url = await Discord.ReconnectAsync(License);
        if (url is not null)
            OpenUrl(url.ToString());
        RefreshComputedProperties();
    }

    private async Task RefreshDiscordStatusAsync()
    {
        await Discord.RefreshClaimStatusAsync(License);
        RefreshComputedProperties();
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Loc.Instance.GetString("App.ErrorFormat", ex.Message),
                Loc.Instance["App.ErrorTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ActivateLicenseCommand.NotifyCanExecuteChanged();
        RefreshCommercialLicenseCommand.NotifyCanExecuteChanged();
        RefreshSupporterLicenseCommand.NotifyCanExecuteChanged();
        DeactivateCommercialLicenseCommand.NotifyCanExecuteChanged();
        DeactivateSupporterLicenseCommand.NotifyCanExecuteChanged();
        ConnectDiscordCommand.NotifyCanExecuteChanged();
        ReconnectDiscordCommand.NotifyCanExecuteChanged();
        RefreshDiscordStatusCommand.NotifyCanExecuteChanged();
        ClearStaleActivationNotice();
        RefreshComputedProperties();
    }

    private void ClearStaleActivationNotice()
    {
        if (LicenseActivationNotice is null)
            return;

        var shouldClear = _activationNoticeKind switch
        {
            ActivatedLicenseEntitlementKind.Commercial => !License.HasCommercialActivation,
            ActivatedLicenseEntitlementKind.Supporter => !License.HasSupporterActivation,
            _ => !License.HasCommercialActivation && !License.HasSupporterActivation,
        };

        if (!shouldClear)
            return;

        _activationNoticeKind = null;
        LicenseActivationNotice = null;
    }

    private void RefreshComputedProperties()
    {
        OnPropertyChanged(nameof(IsPrivateUser));
        OnPropertyChanged(nameof(IsBusinessUser));
        OnPropertyChanged(nameof(PlanOptions));
        OnPropertyChanged(nameof(ShowPlanSelection));
        OnPropertyChanged(nameof(ShowLicenseActivation));
        OnPropertyChanged(nameof(ShowCommercialPurchase));
        OnPropertyChanged(nameof(ShowCommercialManage));
        OnPropertyChanged(nameof(ShowSupporterPurchase));
        OnPropertyChanged(nameof(ShowSupporterManage));
        OnPropertyChanged(nameof(ShowCustomerPortalShortcut));
        OnPropertyChanged(nameof(ShowDiscordSection));
        OnPropertyChanged(nameof(ShowDiscordConnect));
        OnPropertyChanged(nameof(ShowDiscordRefresh));
        OnPropertyChanged(nameof(ShowDiscordReconnect));
        OnPropertyChanged(nameof(CommercialStatusTitle));
        OnPropertyChanged(nameof(CommercialStatusDetail));
        OnPropertyChanged(nameof(SupporterStatusTitle));
        OnPropertyChanged(nameof(SupporterStatusDetail));
        OnPropertyChanged(nameof(DiscordStatusTitle));
        OnPropertyChanged(nameof(DiscordStatusDetail));
    }

    private static string ActivationSuccessText(ActivatedLicenseEntitlement entitlement) =>
        entitlement.Kind switch
        {
            ActivatedLicenseEntitlementKind.Commercial when entitlement.CommercialTier is { } tier =>
                Loc.Instance.GetString(
                    "License.ActivationDetectedCommercialFormat",
                    CommercialTierText(tier),
                    entitlement.IsLifetime ? Loc.Instance["License.LifetimeSuffix"] : string.Empty),
            ActivatedLicenseEntitlementKind.Supporter when entitlement.SupporterTier is { } tier =>
                Loc.Instance.GetString("License.ActivationDetectedSupporterFormat", SupporterTierText(tier)),
            _ => Loc.Instance["License.ActivationDetectedGeneric"],
        };

    private static string CommercialTierText(CommercialLicenseTier tier) =>
        tier switch
        {
            CommercialLicenseTier.Individual => Loc.Instance["License.TierIndividualName"],
            CommercialLicenseTier.Team => Loc.Instance["License.TierTeamName"],
            CommercialLicenseTier.Enterprise => Loc.Instance["License.TierEnterpriseName"],
            _ => tier.ToString(),
        };

    private static string SupporterTierText(SupporterTier tier) =>
        tier switch
        {
            SupporterTier.Bronze => Loc.Instance["License.SupporterBronzeName"],
            SupporterTier.Silver => Loc.Instance["License.SupporterSilverName"],
            SupporterTier.Gold => Loc.Instance["License.SupporterGoldName"],
            _ => tier.ToString(),
        };
}

/// <summary>
/// Represents license plan option data.
/// </summary>
/// <param name="Id">Id supplied to the member.</param>
/// <param name="Title">Title supplied to the member.</param>
/// <param name="Price">Price supplied to the member.</param>
/// <param name="Detail">Detail supplied to the member.</param>
/// <param name="IconGlyph">Icon glyph supplied to the member.</param>
/// <param name="CommercialTier">Commercial tier supplied to the member.</param>
/// <param name="IsSelected">Is selected supplied to the member.</param>
public sealed record LicensePlanOption(
    string Id,
    string Title,
    string Price,
    string Detail,
    string IconGlyph,
    CommercialLicenseTier? CommercialTier,
    bool IsSelected);

/// <summary>
/// Represents license purchase option data.
/// </summary>
/// <param name="Title">Title supplied to the member.</param>
/// <param name="Price">Price supplied to the member.</param>
/// <param name="Detail">Detail supplied to the member.</param>
/// <param name="Url">Url supplied to the member.</param>
public sealed record LicensePurchaseOption(string Title, string Price, string Detail, string Url);
