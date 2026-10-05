using System.Collections.Generic;

namespace AirDefenderCoop.Net
{
    /// <summary>
    /// A point-to-point link to the single co-op partner. Implementations buffer incoming
    /// messages until <see cref="Poll"/> is called from the Unity main thread.
    /// </summary>
    public interface ITransport
    {
        string Describe { get; }

        /// <summary>True once a peer is reachable (TCP connected / Steam peer known).</summary>
        bool HasPeer { get; }

        void Send(byte[] data, int offset, int count, bool reliable);

        /// <summary>Moves all received messages into <paramref name="into"/>.</summary>
        void Poll(List<byte[]> into);

        /// <summary>Returns a reason once the link has failed, otherwise null.</summary>
        string FailureReason { get; }

        /// <summary>Drops the current peer (a host keeps listening for the next one).</summary>
        void DropPeer();

        void Close();
    }
}
