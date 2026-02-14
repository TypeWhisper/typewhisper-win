using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface IProfileService
{
    IReadOnlyList<Profile> Profiles { get; }
    event Action? ProfilesChanged;

    void AddProfile(Profile profile);
    void UpdateProfile(Profile profile);
    void DeleteProfile(string id);
    Profile? MatchProfile(string? processName, string? url);
}
