namespace RaCMAN.App;

/// <summary>
/// The question asked when per-user settings are ticked on for a signed-in user who has never
/// been set up: copy the current settings into their profile, or start from the defaults. It is
/// the only time it is asked; a user who signs in later with no profile starts from the defaults.
/// </summary>
public sealed record ProfileQuestion(uint UserId, string Who);
