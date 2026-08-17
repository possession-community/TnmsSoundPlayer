using TnmsSoundPlayer.Shared;

namespace TnmsSoundPlayer.Core;

internal sealed class SoundPlayerSession : ISoundPlayerSession
{
    private readonly SoundPlayerCore _core;

    public string OwnerName { get; }

    internal SoundPlayerSession(string ownerName, SoundPlayerCore core)
    {
        OwnerName = ownerName;
        _core = core;
    }

    public ISoundPlayback Play(IPcmAudioStream stream, PlayOptions? options = null, ISoundPlaybackCallback? callback = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return _core.Play(OwnerName, _ => Task.FromResult(stream), stream, options, callback);
    }

    public ISoundPlayback PlayFile(string path, PlayOptions? options = null, ISoundPlaybackCallback? callback = null)
        => _core.Play(OwnerName, ct => _core.FileService.OpenFileAsync(path, ct), null, options, callback);

    public ISoundPlayback PlayUrl(string url, PlayOptions? options = null, ISoundPlaybackCallback? callback = null)
        => _core.Play(OwnerName, ct => _core.NetworkService.OpenUrlAsync(url, ct), null, options, callback);

    public IReadOnlyList<ISoundPlayback> OwnPlaybacks => _core.GetOwnedPlaybacks(OwnerName);

    public void StopAll() => _core.StopAllOwnedBy(OwnerName);
}
