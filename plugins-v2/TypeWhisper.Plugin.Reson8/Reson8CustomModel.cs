namespace TypeWhisper.Plugin.Reson8;

/// <summary>
/// Represents reson8 custom model data.
/// </summary>
/// <param name="Id">Id supplied to the member.</param>
/// <param name="Name">Name supplied to the member.</param>
/// <param name="Description">Description supplied to the member.</param>
/// <param name="PhraseCount">Phrase count supplied to the member.</param>
public sealed record Reson8CustomModel(
    string Id,
    string Name,
    string? Description,
    int? PhraseCount);
