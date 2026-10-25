using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Epic.OnlineServices;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.Platform;

namespace GodotEos;

/// <summary>A lobby as a search found it.</summary>
/// <param name="Owner">The owner's product user id: who to connect to.</param>
public sealed record EosLobbyInfo(string Id, string Owner, int Members, int MaxMembers, IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// The service's lobbies: a player makes one (and owns it), others find it by searching a bucket and join it. Joining is
/// what makes a player a member: a lobby tracks its members (<see cref="EosLobby"/>), so the owner can let only them
/// connect. Lobbies have no host migration: when the owner leaves, the lobby closes.
/// </summary>
public sealed class EosLobbies
{
    private readonly EosPlatform _platform;

    // The SDK's thread only.
    private readonly Dictionary<string, LobbyDetails> _found = new();
    private readonly Dictionary<string, EosLobby> _joined = new();
    private ulong _notice;

    public EosLobbies(EosPlatform platform)
    {
        _platform = platform;
        _platform.Stopping += (p, user) => Leave(p, user);
    }

    /// <summary>
    /// Makes a lobby in <paramref name="bucket"/> (e.g. the game and its network version: only lobbies in the same
    /// bucket are found) with public text <paramref name="attributes"/>; the player owns it.
    /// </summary>
    public Task<EosLobby> CreateAsync(string bucket, int maxMembers, IReadOnlyDictionary<string, string> attributes) =>
        _platform.Run<EosLobby>((platform, done) =>
        {
            Watch(platform);
            var lobbies = platform.GetLobbyInterface();
            var create = new CreateLobbyOptions
            {
                LocalUserId = _platform.User,
                MaxLobbyMembers = (uint)Math.Clamp(maxMembers, 1, LobbyInterface.MAX_LOBBY_MEMBERS),
                PermissionLevel = LobbyPermissionLevel.Publicadvertised,
                PresenceEnabled = false,
                AllowInvites = false,
                BucketId = bucket,
                DisableHostMigration = true,
                EnableRTCRoom = false,
                EnableJoinById = false,
            };
            lobbies.CreateLobby(ref create, null, (ref CreateLobbyCallbackInfo info) =>
            {
                if (info.ResultCode != Result.Success)
                {
                    EosPlatform.Fail(done, "making the lobby", info.ResultCode);
                    return;
                }
                string id = info.LobbyId;
                var lobby = new EosLobby(this, id, _platform.UserId!, true);
                lobby.Add(_platform.UserId!);
                _joined[id] = lobby;
                var modify = new UpdateLobbyModificationOptions { LocalUserId = _platform.User, LobbyId = id };
                var result = lobbies.UpdateLobbyModification(ref modify, out var modification);
                if (result != Result.Success || modification is null)
                {
                    EosPlatform.Fail(done, "listing the lobby", result);
                    return;
                }
                foreach (var (key, value) in attributes)
                {
                    var add = new LobbyModificationAddAttributeOptions
                    {
                        Attribute = new AttributeData { Key = key, Value = value },
                        Visibility = LobbyAttributeVisibility.Public,
                    };
                    modification.AddAttribute(ref add);
                }
                var update = new UpdateLobbyOptions { LobbyModificationHandle = modification };
                lobbies.UpdateLobby(ref update, null, (ref UpdateLobbyCallbackInfo updated) =>
                {
                    if (updated.ResultCode == Result.Success)
                        done.TrySetResult(lobby);
                    else
                        EosPlatform.Fail(done, "listing the lobby", updated.ResultCode);
                });
                modification.Release();
            });
        });

    /// <summary>The lobbies in <paramref name="bucket"/> whose attributes match <paramref name="where"/>, with room left.</summary>
    public Task<IReadOnlyList<EosLobbyInfo>> SearchAsync(string bucket, IReadOnlyDictionary<string, string>? where = null, int max = 50) =>
        _platform.Run<IReadOnlyList<EosLobbyInfo>>((platform, done) =>
        {
            var lobbies = platform.GetLobbyInterface();
            var create = new CreateLobbySearchOptions { MaxResults = (uint)Math.Clamp(max, 1, 200) };
            var result = lobbies.CreateLobbySearch(ref create, out var search);
            if (result != Result.Success || search is null)
            {
                EosPlatform.Fail(done, "searching lobbies", result);
                return;
            }
            void Where(string key, AttributeDataValue value, ComparisonOp op = ComparisonOp.Equal)
            {
                var parameter = new LobbySearchSetParameterOptions { Parameter = new AttributeData { Key = key, Value = value }, ComparisonOp = op };
                search.SetParameter(ref parameter);
            }
            Where(LobbyInterface.SEARCH_BUCKET_ID, bucket);
            foreach (var (key, value) in where ?? new Dictionary<string, string>())
                Where(key, value);
            var find = new LobbySearchFindOptions { LocalUserId = _platform.User };
            search.Find(ref find, null, (ref LobbySearchFindCallbackInfo info) =>
            {
                foreach (var old in _found.Values)
                    old.Release();
                _found.Clear();
                var found = new List<EosLobbyInfo>();
                if (info.ResultCode == Result.Success)
                {
                    var count = new LobbySearchGetSearchResultCountOptions();
                    for (uint i = 0, n = search.GetSearchResultCount(ref count); i < n; i++)
                    {
                        var copy = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = i };
                        if (search.CopySearchResultByIndex(ref copy, out var details) != Result.Success || details is null)
                            continue;
                        if (Describe(details) is { } lobby && lobby.Members < lobby.MaxMembers)
                        {
                            found.Add(lobby);
                            _found[lobby.Id] = details;
                        }
                        else
                            details.Release();
                    }
                }
                search.Release();
                if (info.ResultCode is Result.Success or Result.NotFound)
                    done.TrySetResult(found);
                else
                    EosPlatform.Fail(done, "searching lobbies", info.ResultCode);
            });
        });

    /// <summary>Joins a lobby the last search found.</summary>
    public Task<EosLobby> JoinAsync(EosLobbyInfo lobby) =>
        _platform.Run<EosLobby>((platform, done) =>
        {
            if (!_found.TryGetValue(lobby.Id, out var details))
            {
                done.TrySetException(new InvalidOperationException("that lobby wasn't in the last search"));
                return;
            }
            Watch(platform);
            var lobbies = platform.GetLobbyInterface();
            var join = new JoinLobbyOptions { LobbyDetailsHandle = details, LocalUserId = _platform.User, PresenceEnabled = false };
            lobbies.JoinLobby(ref join, null, (ref JoinLobbyCallbackInfo info) =>
            {
                if (info.ResultCode != Result.Success)
                {
                    EosPlatform.Fail(done, "joining the lobby", info.ResultCode);
                    return;
                }
                string id = info.LobbyId;
                var joined = new EosLobby(this, id, lobby.Owner, false);
                var copy = new CopyLobbyDetailsHandleOptions { LobbyId = id, LocalUserId = _platform.User };
                if (lobbies.CopyLobbyDetailsHandle(ref copy, out var now) == Result.Success && now is not null)
                {
                    var count = new LobbyDetailsGetMemberCountOptions();
                    for (uint i = 0, n = now.GetMemberCount(ref count); i < n; i++)
                    {
                        var member = new LobbyDetailsGetMemberByIndexOptions { MemberIndex = i };
                        if (now.GetMemberByIndex(ref member)?.ToString() is { } m)
                            joined.Add(m);
                    }
                    now.Release();
                }
                joined.Add(_platform.UserId!);
                _joined[id] = joined;
                done.TrySetResult(joined);
            });
        });

    /// <summary>Leaves (and, as the owner, closes) <paramref name="lobby"/>.</summary>
    internal Task LeaveAsync(EosLobby lobby) =>
        _platform.Run<bool>((platform, done) =>
        {
            if (!_joined.Remove(lobby.Id))
            {
                done.TrySetResult(false);
                return;
            }
            Leave(platform, lobby, ok => done.TrySetResult(ok));
        });

    // ------------------------------------------------------------------ the SDK's thread

    private void Leave(PlatformInterface platform, EosLobby lobby, Action<bool> done)
    {
        var lobbies = platform.GetLobbyInterface();
        lobby.Close();
        _platform.Begin();
        void Answered(Result result)
        {
            _platform.Answered();
            done(result == Result.Success);
        }
        if (lobby.IsOwner)
        {
            var destroy = new DestroyLobbyOptions { LocalUserId = _platform.User, LobbyId = lobby.Id };
            lobbies.DestroyLobby(ref destroy, null, (ref DestroyLobbyCallbackInfo info) => Answered(info.ResultCode));
        }
        else
        {
            var leave = new LeaveLobbyOptions { LocalUserId = _platform.User, LobbyId = lobby.Id };
            lobbies.LeaveLobby(ref leave, null, (ref LeaveLobbyCallbackInfo info) => Answered(info.ResultCode));
        }
    }

    /// <summary>The platform is stopping: leave every lobby (the platform ticks a little longer for the calls to go out).</summary>
    private void Leave(PlatformInterface platform, ProductUserId? user)
    {
        if (user is not null)
            foreach (var lobby in _joined.Values.ToList())
                Leave(platform, lobby, _ => { });
        _joined.Clear();
        foreach (var details in _found.Values)
            details.Release();
        _found.Clear();
        if (_notice != 0)
            platform.GetLobbyInterface().RemoveNotifyLobbyMemberStatusReceived(_notice);
        _notice = 0;
    }

    /// <summary>Members coming and going, in every lobby this player is in.</summary>
    private void Watch(PlatformInterface platform)
    {
        if (_notice != 0)
            return;
        var notify = new AddNotifyLobbyMemberStatusReceivedOptions();
        _notice = platform.GetLobbyInterface().AddNotifyLobbyMemberStatusReceived(ref notify, null, (ref LobbyMemberStatusReceivedCallbackInfo info) =>
        {
            if (info.LobbyId?.ToString() is not { } id || !_joined.TryGetValue(id, out var lobby) || info.TargetUserId?.ToString() is not { } member)
                return;
            _platform.Say($"Lobby {id}: {member} {info.CurrentStatus}");
            switch (info.CurrentStatus)
            {
                case LobbyMemberStatus.Joined:
                    lobby.Add(member);
                    break;
                case LobbyMemberStatus.Left:
                case LobbyMemberStatus.Disconnected:
                case LobbyMemberStatus.Kicked:
                    if (member == _platform.UserId)
                    {
                        _joined.Remove(id);
                        lobby.Close();
                    }
                    else
                        lobby.Remove(member);
                    break;
                case LobbyMemberStatus.Closed:
                    _joined.Remove(id);
                    lobby.Close();
                    break;
            }
        });
    }

    private static EosLobbyInfo? Describe(LobbyDetails details)
    {
        var options = new LobbyDetailsCopyInfoOptions();
        if (details.CopyInfo(ref options, out var info) != Result.Success || info is not { } lobby || lobby.LobbyId is null)
            return null;
        var attributes = new Dictionary<string, string>();
        var count = new LobbyDetailsGetAttributeCountOptions();
        for (uint i = 0, n = details.GetAttributeCount(ref count); i < n; i++)
        {
            var copy = new LobbyDetailsCopyAttributeByIndexOptions { AttrIndex = i };
            if (details.CopyAttributeByIndex(ref copy, out var attribute) == Result.Success && attribute?.Data is { } data
                && data.Key?.ToString() is { } key && data.Value.AsUtf8?.ToString() is { } value)
                attributes[key] = value;
        }
        int members = (int)(lobby.MaxMembers - lobby.AvailableSlots);
        return new EosLobbyInfo(lobby.LobbyId, lobby.LobbyOwnerUserId?.ToString() ?? "", members, (int)lobby.MaxMembers, attributes);
    }
}

/// <summary>
/// A lobby this player is in, with its members as they come and go. Members change on the SDK's thread; read them, and
/// take the changes, from any thread.
/// </summary>
public sealed class EosLobby
{
    private readonly EosLobbies _lobbies;
    private readonly ConcurrentDictionary<string, byte> _members = new();
    private readonly ConcurrentQueue<(string Member, bool Joined)> _changes = new();
    private volatile bool _closed;

    internal EosLobby(EosLobbies lobbies, string id, string owner, bool isOwner)
    {
        _lobbies = lobbies;
        Id = id;
        Owner = owner;
        IsOwner = isOwner;
    }

    public string Id { get; }

    /// <summary>The owner's product user id.</summary>
    public string Owner { get; }

    public bool IsOwner { get; }

    /// <summary>The owner closed it, this player was kicked, or this player left.</summary>
    public bool IsClosed => _closed;

    public IReadOnlyCollection<string> Members => (IReadOnlyCollection<string>)_members.Keys;

    public bool Has(string member) => !_closed && _members.ContainsKey(member);

    /// <summary>The next member who joined (<paramref name="joined"/>) or left, in order.</summary>
    public bool TryTakeChange([NotNullWhen(true)] out string? member, out bool joined)
    {
        if (_changes.TryDequeue(out var change))
        {
            (member, joined) = change;
            return true;
        }
        (member, joined) = (null, false);
        return false;
    }

    /// <summary>Leaves the lobby; the owner closes it.</summary>
    public Task LeaveAsync() => _lobbies.LeaveAsync(this);

    internal void Add(string member)
    {
        if (_members.TryAdd(member, 0))
            _changes.Enqueue((member, true));
    }

    internal void Remove(string member)
    {
        if (_members.TryRemove(member, out _))
            _changes.Enqueue((member, false));
    }

    internal void Close()
    {
        _closed = true;
        foreach (string member in _members.Keys)
            Remove(member);
    }
}
