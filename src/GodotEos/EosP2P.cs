using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;

namespace GodotEos;

/// <summary>
/// Packets between players on one socket, through the SDK's peer-to-peer connections (direct when the networks allow it,
/// through Epic's relays when not). Players are addressed by their product user ids, as text. Nobody gets a connection
/// unless <see cref="Accepts"/> says so (for a game: members of its lobby); packets from anyone else are dropped.
/// Every method is safe from any thread: packets queue here and go on the SDK's thread.
/// </summary>
public sealed class EosP2P : IDisposable
{
    /// <summary>The largest packet that goes in one piece.</summary>
    public const int MaxPacketSize = P2PInterface.MAX_PACKET_SIZE;

    private readonly EosPlatform _platform;
    private readonly SocketId _socket;
    private readonly string _socketName;
    private readonly ConcurrentQueue<Outgoing> _outgoing = new();
    private readonly ConcurrentQueue<(string Peer, byte Channel, byte[] Data)> _incoming = new();
    private readonly ConcurrentQueue<string> _closed = new();
    private readonly ConcurrentQueue<Outgoing> _held = new();
    private readonly ConcurrentDictionary<string, byte> _open = new();       // peers this side has sent to or accepted

    // The SDK's thread only.
    private readonly Dictionary<string, ProductUserId> _ids = new();
    private byte[] _buffer = new byte[MaxPacketSize];
    private ulong _requests, _closings;

    private readonly record struct Outgoing(string Peer, byte Channel, byte[] Data, bool Reliable);

    /// <param name="socketName">The game's socket (up to 32 letters and digits): packets on other sockets are not this game's.</param>
    public EosP2P(EosPlatform platform, string socketName)
    {
        _platform = platform;
        _socketName = socketName;
        _socket = new SocketId { SocketName = socketName };
        _platform.SignedIn += Listen;
        _platform.Ticked += Pump;
        _platform.Renewed += () =>
        {
            while (_held.TryDequeue(out var item))
                _outgoing.Enqueue(item);
        };
        _platform.Stopping += (platform, user) => Unlisten(platform);
        if (_platform.IsSignedIn)
            _platform.Post(platform => Listen(platform, _platform.User!));
    }

    /// <summary>
    /// Whether a player may connect (asked on the SDK's thread, when one asks to, and for each packet from a peer this
    /// side never sent to). Nobody, until the game says.
    /// </summary>
    public Func<string, bool> Accepts { get; set; } = _ => false;

    /// <summary>Sends to <paramref name="peer"/> on <paramref name="channel"/>: reliable packets arrive in order; others may not arrive.</summary>
    public void Send(string peer, byte channel, byte[] data, bool reliable)
    {
        if (data.Length > MaxPacketSize)
            throw new ArgumentException($"a packet of {data.Length} bytes is over {MaxPacketSize}");
        _open[peer] = 0;
        _outgoing.Enqueue(new Outgoing(peer, channel, data, reliable));
    }

    /// <summary>The next packet that has arrived from an accepted peer, if any.</summary>
    public bool TryReceive([NotNullWhen(true)] out string? peer, out byte channel, [NotNullWhen(true)] out byte[]? data)
    {
        if (_incoming.TryDequeue(out var item))
        {
            (peer, channel, data) = item;
            return true;
        }
        (peer, channel, data) = (null, 0, null);
        return false;
    }

    /// <summary>A peer whose connection closed (it left, or it couldn't be reached).</summary>
    public bool TryTakeClosed([NotNullWhen(true)] out string? peer) => _closed.TryDequeue(out peer);

    /// <summary>Closes the connection with <paramref name="peer"/>.</summary>
    public void Close(string peer)
    {
        _open.TryRemove(peer, out _);
        _platform.Post(platform =>
        {
            if (_platform.User is not { } user)
                return;
            var close = new CloseConnectionOptions { LocalUserId = user, RemoteUserId = Id(peer), SocketId = _socket };
            platform.GetP2PInterface().CloseConnection(ref close);
        });
    }

    public void Dispose()
    {
        _platform.SignedIn -= Listen;
        _platform.Ticked -= Pump;
        _platform.Post(Unlisten);
    }

    // ------------------------------------------------------------------ the SDK's thread

    private ProductUserId Id(string peer)
    {
        if (!_ids.TryGetValue(peer, out var id))
            _ids[peer] = id = ProductUserId.FromString(peer);
        return id;
    }

    private bool Allowed(string peer)
    {
        if (_open.ContainsKey(peer))
            return true;
        try { return Accepts(peer); }
        catch (Exception e)
        {
            _platform.Say($"P2P: deciding on {peer}: {e.Message}");
            return false;
        }
    }

    private void Listen(Epic.OnlineServices.Platform.PlatformInterface platform, ProductUserId user)
    {
        if (_requests != 0)
            return;
        var p2p = platform.GetP2PInterface();
        var requests = new AddNotifyPeerConnectionRequestOptions { LocalUserId = user, SocketId = _socket };
        _requests = p2p.AddNotifyPeerConnectionRequest(ref requests, null, (ref OnIncomingConnectionRequestInfo info) =>
        {
            if (info.SocketId?.SocketName != _socketName || info.RemoteUserId?.ToString() is not { } peer)
                return;
            if (!Allowed(peer))
            {
                _platform.Say($"P2P: turned away {peer}");
                return;
            }
            _open[peer] = 0;
            var accept = new AcceptConnectionOptions { LocalUserId = info.LocalUserId, RemoteUserId = info.RemoteUserId, SocketId = _socket };
            p2p.AcceptConnection(ref accept);
        });
        var closed = new AddNotifyPeerConnectionClosedOptions { LocalUserId = user, SocketId = _socket };
        _closings = p2p.AddNotifyPeerConnectionClosed(ref closed, null, (ref OnRemoteConnectionClosedInfo info) =>
        {
            if (info.RemoteUserId?.ToString() is { } peer && _open.TryRemove(peer, out _))
                _closed.Enqueue(peer);
        });
    }

    private void Unlisten(Epic.OnlineServices.Platform.PlatformInterface platform)
    {
        var p2p = platform.GetP2PInterface();
        if (_requests != 0)
            p2p.RemoveNotifyPeerConnectionRequest(_requests);
        if (_closings != 0)
            p2p.RemoveNotifyPeerConnectionClosed(_closings);
        _requests = _closings = 0;
        if (_platform.User is { } user)
        {
            var close = new CloseConnectionsOptions { LocalUserId = user, SocketId = _socket };
            p2p.CloseConnections(ref close);
        }
    }

    private void Pump(Epic.OnlineServices.Platform.PlatformInterface platform, ProductUserId user)
    {
        var p2p = platform.GetP2PInterface();
        while (_outgoing.TryDequeue(out var item))
        {
            var send = new SendPacketOptions
            {
                LocalUserId = user,
                RemoteUserId = Id(item.Peer),
                SocketId = _socket,
                Channel = item.Channel,
                Data = new ArraySegment<byte>(item.Data),
                AllowDelayedDelivery = true,
                Reliability = item.Reliable ? PacketReliability.ReliableOrdered : PacketReliability.UnreliableUnordered,
            };
            var result = p2p.SendPacket(ref send);
            if (result is Result.InvalidAuth or Result.AuthExpired)
            {
                // The sign-in ran out: keep the reliable packets for when it is renewed (the peer isn't lost for this).
                if (item.Reliable)
                    _held.Enqueue(item);
                _ = _platform.SignInAgainAsync();
            }
            else if (result != Result.Success)
            {
                _platform.Say($"P2P: sending to {item.Peer} failed: {result}");
                if (_open.TryRemove(item.Peer, out _))
                    _closed.Enqueue(item.Peer);
            }
        }
        var size = new GetNextReceivedPacketSizeOptions { LocalUserId = user };
        while (p2p.GetNextReceivedPacketSize(ref size, out uint bytes) == Result.Success)
        {
            if (bytes > _buffer.Length)
                _buffer = new byte[bytes];
            var receive = new ReceivePacketOptions { LocalUserId = user, MaxDataSizeBytes = (uint)_buffer.Length };
            var from = new ProductUserId();
            var socket = new SocketId();
            if (p2p.ReceivePacket(ref receive, ref from, ref socket, out byte channel, new ArraySegment<byte>(_buffer), out uint written) != Result.Success)
                break;
            if (socket.SocketName != _socketName || from?.ToString() is not { } peer || !Allowed(peer))
                continue;
            _open[peer] = 0;
            _incoming.Enqueue((peer, channel, _buffer.AsSpan(0, (int)written).ToArray()));
        }
    }
}
