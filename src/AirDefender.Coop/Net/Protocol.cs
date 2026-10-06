namespace AirDefenderCoop.Net
{
    /// <summary>Wire protocol version. Bump whenever any message layout changes.</summary>
    public static class Protocol
    {
        public const int Version = 2;
    }

    public enum MsgType : ushort
    {
        // Session
        Hello = 1,
        Welcome = 2,
        Reject = 3,
        Ping = 4,
        Pong = 5,
        Bye = 6,
        PlayerList = 7,

        // World bootstrap
        WorldBegin = 20,
        WorldChunk = 21,
        WorldEnd = 22,
        WorldLoaded = 23,
        ReturnToMenu = 24,
        WorldRequest = 25,
        ClientContacts = 26,

        // Live replication (host -> client)
        TimeSync = 40,
        EntitySpawn = 41,
        EntityDespawn = 42,
        EntityStates = 43,
        RadarHits = 44,
        TrackTable = 45,
        GlobalState = 46,
        GameEvent = 47,
        Digest = 48,
        TrackRemoved = 49,
        Classification = 50,
        EntityFields = 51,
        CustomGlobal = 52,

        // Client -> host
        Command = 100,
        CommandResult = 101,
        RouteCall = 102,

        // Both directions
        PartnerCursor = 120,
        PingMarker = 121,
        Chat = 122,
    }
}
