using Sharp.Shared.Objects;

namespace TnmsSoundPlayer.Shared;

/// <summary>
/// Describes which clients receive a playback. Interpreted by the implementation;
/// use the factory members to construct instances.
/// </summary>
public abstract record SoundRecipients
{
    /// <summary>Every connected client, evaluated live — clients joining mid-playback are included.</summary>
    public static SoundRecipients All { get; } = new AllRecipients();

    /// <summary>A single client.</summary>
    public static SoundRecipients Single(IGameClient client) => new SingleRecipient(client);

    /// <summary>
    /// A fixed set of clients, snapshotted at call time. The implementation tracks them by slot,
    /// so clients who disconnect simply stop receiving; nobody is added later.
    /// </summary>
    public static SoundRecipients Of(IEnumerable<IGameClient> clients) => new SnapshotRecipients(clients.ToArray());

    /// <summary>A predicate re-evaluated live against all connected clients while the playback runs.</summary>
    public static SoundRecipients Where(Func<IGameClient, bool> predicate) => new PredicateRecipients(predicate);

    public sealed record AllRecipients : SoundRecipients;

    public sealed record SingleRecipient(IGameClient Client) : SoundRecipients;

    public sealed record SnapshotRecipients(IReadOnlyList<IGameClient> Clients) : SoundRecipients;

    public sealed record PredicateRecipients(Func<IGameClient, bool> Predicate) : SoundRecipients;
}
