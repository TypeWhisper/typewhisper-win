namespace TypeWhisper.Presentation;

/// <summary>Chooses the initial surface without changing later hotkey/tray activations.</summary>
public enum StartupPresentation
{
    /// <summary>Keep windows hidden and expose the tray/hotkeys.</summary>
    Tray,
    /// <summary>Open the unfinished setup wizard.</summary>
    Setup,
    /// <summary>Dispatch an explicit navigation or error request.</summary>
    RequestedDestination
}

/// <summary>Ordinary starts are quiet once setup has been completed.</summary>
public static class StartupPresentationPolicy
{
    /// <summary>Explicit navigation, files and account callbacks retain their requested behavior.</summary>
    public static StartupPresentation Resolve(ApplicationActivationRequest request, bool setupCompleted)
    {
        if (request.Route is not null || request.Files.Count > 0 || request.AccountCallback is not null || request.Error is not null)
            return StartupPresentation.RequestedDestination;
        return setupCompleted ? StartupPresentation.Tray : StartupPresentation.Setup;
    }
}
