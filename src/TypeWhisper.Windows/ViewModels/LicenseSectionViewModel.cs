using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

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
        OpenUrlCommand = new RelayCommand<string>(OpenUrl);
        OpenCustomerPortalCommand = new RelayCommand(() => OpenUrl(CustomerPortalUrl));
        ActivateCommercialLicenseCommand = new AsyncRelayCommand(ActivateCommercialLicenseAsync, CanActivateCommercialLicense);
        ActivateSupporterLicenseCommand = new AsyncRelayCommand(ActivateSupporterLicenseAsync, CanActivateSupporterLicense);
        DeactivateCommercialLicenseCommand = new AsyncRelayCommand(() => License.DeactivateCommercialLicenseAsync());
        DeactivateSupporterLicenseCommand = new AsyncRelayCommand(DeactivateSupporterLicenseAsync);
        ConnectDiscordCommand = new AsyncRelayCommand(ConnectDiscordAsync, CanConnectDiscord);
        ReconnectDiscordCommand = new AsyncRelayCommand(ReconnectDiscordAsync, CanReconnectDiscord);
        RefreshDiscordStatusCommand = new AsyncRelayCommand(RefreshDiscordStatusAsync, CanRefreshDiscord);
        OpenGitHubSponsorsClaimCommand = new RelayCommand(() => OpenUrl(Discord.GitHubSponsorsUrl));

        License.PropertyChanged += OnServicePropertyChanged;
        Discord.PropertyChanged += OnServicePropertyChanged;
    }

    public LicenseService License { get; }
    public SupporterDiscordService Discord { get; }
    public IReadOnlyList<LicensePurchaseOption> MonthlyCommercialOptions { get; }
    public IReadOnlyList<LicensePurchaseOption> LifetimeCommercialOptions { get; }
    public IReadOnlyList<LicensePurchaseOption> SupporterOptions { get; }

    [ObservableProperty]
    private string _commercialLicenseKeyInput = string.Empty;

    [ObservableProperty]
    private string _supporterLicenseKeyInput = string.Empty;

    public bool IsPrivateUser => License.IsPrivateUser;
    public bool IsBusinessUser => License.IsBusinessUser;
    public bool ShowCommercialPurchase => License.CommercialStatus != LicenseStatus.Active;
    public bool ShowCommercialManage => License.CommercialStatus == LicenseStatus.Active;
    public bool ShowSupporterPurchase => !License.HasSupporterLicense;
    public bool ShowSupporterManage => License.HasSupporterLicense;
    public bool ShowDiscordSection => License.HasSupporterLicense;
    public bool ShowDiscordConnect => ShowDiscordSection && Discord.ClaimState is SupporterDiscordClaimState.Unavailable or SupporterDiscordClaimState.Unlinked;
    public bool ShowDiscordRefresh => ShowDiscordSection && (Discord.IsHelperUnavailable || Discord.ClaimState is SupporterDiscordClaimState.Pending or SupporterDiscordClaimState.Linked or SupporterDiscordClaimState.Failed);
    public bool ShowDiscordReconnect => ShowDiscordSection && !Discord.IsHelperUnavailable && Discord.ClaimState is SupporterDiscordClaimState.Pending or SupporterDiscordClaimState.Linked or SupporterDiscordClaimState.Failed;
    public string CommercialStatusTitle => License.CommercialStatus switch
    {
        LicenseStatus.Active => Loc.Instance["License.CommercialActiveTitle"],
        LicenseStatus.Expired => Loc.Instance["License.CommercialExpiredTitle"],
        _ => Loc.Instance["License.CommercialInactiveTitle"],
    };

    public string CommercialStatusDetail
    {
        get
        {
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

    public string SupporterStatusTitle => License.HasSupporterLicense
        ? Loc.Instance["License.SupporterActiveTitle"]
        : Loc.Instance["License.SupporterInactiveTitle"];

    public string SupporterStatusDetail => License.HasSupporterLicense && !string.IsNullOrWhiteSpace(License.SupporterTierDisplayName)
        ? Loc.Instance.GetString("License.SupporterActiveDetailFormat", License.SupporterTierDisplayName ?? string.Empty)
        : Loc.Instance["License.SupporterInactiveDetail"];

    public string DiscordStatusTitle => Discord.ClaimState switch
    {
        _ when Discord.IsHelperUnavailable => Loc.Instance["License.DiscordServiceUnavailableTitle"],
        SupporterDiscordClaimState.Linked => Loc.Instance["License.DiscordLinkedTitle"],
        SupporterDiscordClaimState.Pending => Loc.Instance["License.DiscordPendingTitle"],
        SupporterDiscordClaimState.Failed => Loc.Instance["License.DiscordFailedTitle"],
        _ => Loc.Instance["License.DiscordUnlinkedTitle"],
    };

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

    public RelayCommand SelectPrivateUserCommand { get; }
    public RelayCommand SelectBusinessUserCommand { get; }
    public RelayCommand<string> OpenUrlCommand { get; }
    public RelayCommand OpenCustomerPortalCommand { get; }
    public RelayCommand OpenGitHubSponsorsClaimCommand { get; }
    public AsyncRelayCommand ActivateCommercialLicenseCommand { get; }
    public AsyncRelayCommand ActivateSupporterLicenseCommand { get; }
    public AsyncRelayCommand DeactivateCommercialLicenseCommand { get; }
    public AsyncRelayCommand DeactivateSupporterLicenseCommand { get; }
    public AsyncRelayCommand ConnectDiscordCommand { get; }
    public AsyncRelayCommand ReconnectDiscordCommand { get; }
    public AsyncRelayCommand RefreshDiscordStatusCommand { get; }

    public async Task InitializeAsync()
    {
        await License.ValidateAllIfNeededAsync();
        await Discord.RefreshStatusIfNeededAsync(License);
        RefreshComputedProperties();
    }

    partial void OnCommercialLicenseKeyInputChanged(string value) =>
        ActivateCommercialLicenseCommand.NotifyCanExecuteChanged();

    partial void OnSupporterLicenseKeyInputChanged(string value) =>
        ActivateSupporterLicenseCommand.NotifyCanExecuteChanged();

    private bool CanActivateCommercialLicense() =>
        !string.IsNullOrWhiteSpace(CommercialLicenseKeyInput) && !License.IsCommercialActivating;

    private bool CanActivateSupporterLicense() =>
        !string.IsNullOrWhiteSpace(SupporterLicenseKeyInput) && !License.IsSupporterActivating;

    private bool CanConnectDiscord() =>
        License.HasSupporterLicense && !Discord.IsWorking;

    private bool CanReconnectDiscord() =>
        License.HasSupporterLicense && !Discord.IsWorking;

    private bool CanRefreshDiscord() =>
        License.HasSupporterLicense && !Discord.IsWorking;

    private async Task ActivateCommercialLicenseAsync()
    {
        await License.ActivateCommercialLicenseAsync(CommercialLicenseKeyInput.Trim());
        if (License.CommercialStatus == LicenseStatus.Active)
            CommercialLicenseKeyInput = string.Empty;
        RefreshComputedProperties();
    }

    private async Task ActivateSupporterLicenseAsync()
    {
        await License.ActivateSupporterKeyAsync(SupporterLicenseKeyInput.Trim());
        if (License.HasSupporterLicense)
            SupporterLicenseKeyInput = string.Empty;
        RefreshComputedProperties();
    }

    private async Task DeactivateSupporterLicenseAsync()
    {
        await License.DeactivateSupporterLicenseAsync();
        Discord.HandleSupporterEntitlementRemoved();
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
        ActivateCommercialLicenseCommand.NotifyCanExecuteChanged();
        ActivateSupporterLicenseCommand.NotifyCanExecuteChanged();
        ConnectDiscordCommand.NotifyCanExecuteChanged();
        ReconnectDiscordCommand.NotifyCanExecuteChanged();
        RefreshDiscordStatusCommand.NotifyCanExecuteChanged();
        RefreshComputedProperties();
    }

    private void RefreshComputedProperties()
    {
        OnPropertyChanged(nameof(IsPrivateUser));
        OnPropertyChanged(nameof(IsBusinessUser));
        OnPropertyChanged(nameof(ShowCommercialPurchase));
        OnPropertyChanged(nameof(ShowCommercialManage));
        OnPropertyChanged(nameof(ShowSupporterPurchase));
        OnPropertyChanged(nameof(ShowSupporterManage));
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
}

public sealed record LicensePurchaseOption(string Title, string Price, string Detail, string Url);
