namespace TnmsSoundPlayer.Core;

/// <summary>
/// How a voice packet names its speaker. Both modes send the same audio through the same client
/// decoder; they differ only in which field of CSVCMsg_VoiceData identifies who is talking, and the
/// client reads <c>entity</c> in preference to the (deprecated) player-slot field when both are set.
/// </summary>
internal enum SpeakerMode
{
    /// <summary>
    /// Attribute voice to a player slot, which means parking a bot in one so the slot resolves to
    /// somebody. Costs one of the server's 64 slots, and is the only mode that puts a row on the
    /// scoreboard — so it is also the only one where the speaker has a visible name and where a
    /// player's own client-side mute applies to it.
    /// </summary>
    Bot = 0,

    /// <summary>
    /// Attribute voice to an entity index instead. No player has to exist behind it, so no slot is
    /// consumed and no bot is created. There is no scoreboard row, hence no name and no client-side
    /// mute; mute server-side with <see cref="Shared.ISoundPlayerSession.SetHearing" /> and
    /// <see cref="Shared.ISoundPlayerSession.SetPlayerVolume" />, which drop the recipient before a
    /// packet is ever sent.
    /// </summary>
    Entity = 1,
}
