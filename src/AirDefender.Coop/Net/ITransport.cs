using System.Collections.Generic;

namespace AirDefenderCoop.Net
{
    /// <summary>One received message and the peer it came from.</summary>
    public struct NetMessage
    {
        public ulong From;
        public byte[] Data;
    }

    /// <summary>
    /// A link to the other players. A host transport talks to every connected client; a client
    /// transport talks only to the host (<see cref="ServerPeer"/>). Peers are identified by a
    /// transport-specific id (Steam id, or a connection number for TCP). Implementations buffer
    /// incoming messages until <see cref="Poll"/> is called from the Unity main thread.
    /// </summary>
    public interface ITransport
    {
        string Describe { get; }

        /// <summary>Client: true once the host is reachable (TCP connected / Steam peer known).</summary>
        bool HasPeer { get; }

        /// <summary>Client: the host's peer id. Unused on a host.</summary>
        ulong ServerPeer { get; }

        void Send(ulong peer, byte[] data, int offset, int count, bool reliable);

        /// <summary>Moves all received messages into <paramref name="into"/>.</summary>
        void Poll(List<NetMessage> into);

        /// <summary>Client: a reason once the link to the host has failed, otherwise null.</summary>
        string FailureReason { get; }

        /// <summary>Host: moves the peers whose links failed since the last call into <paramref name="into"/>.</summary>
        void TakeFailedPeers(List<KeyValuePair<ulong, string>> into);

        /// <summary>Drops one peer (a host keeps listening for others).</summary>
        void DropPeer(ulong peer);

        void Close();
    }
}
