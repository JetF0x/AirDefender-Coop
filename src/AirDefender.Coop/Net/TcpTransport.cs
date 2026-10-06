using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace AirDefenderCoop.Net
{
    /// <summary>
    /// Length-prefixed TCP links. Used for local multi-instance testing and direct-IP play. A host
    /// accepts any number of clients, each numbered from 1; a client's only peer is the host.
    /// "Unreliable" sends are delivered reliably, which is harmless on a TCP link.
    /// </summary>
    public sealed class TcpTransport : ITransport
    {
        private const int MaxFrame = 64 * 1024 * 1024;
        private const ulong ClientServerPeer = 1;

        private sealed class Conn
        {
            public ulong Id;
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object SendLock = new object();
        }

        private readonly ConcurrentQueue<NetMessage> _inbox = new ConcurrentQueue<NetMessage>();
        private readonly ConcurrentQueue<KeyValuePair<ulong, string>> _failed = new ConcurrentQueue<KeyValuePair<ulong, string>>();
        private readonly ConcurrentDictionary<ulong, Conn> _conns = new ConcurrentDictionary<ulong, Conn>();
        private readonly string _describe;
        private readonly bool _isHost;
        private TcpListener _listener;
        private long _nextId;
        private volatile bool _closed;
        private volatile string _failure;

        private TcpTransport(string describe, bool isHost) { _describe = describe; _isHost = isHost; }

        public string Describe => _isHost ? $"{_describe} ({_conns.Count} linked)" : _describe;
        public bool HasPeer => !_isHost && _conns.ContainsKey(ClientServerPeer) && _failure == null;
        public ulong ServerPeer => _isHost ? 0 : ClientServerPeer;
        public string FailureReason => _failure;

        public static TcpTransport Listen(int port)
        {
            var t = new TcpTransport($"TCP host :{port}", true);
            t._listener = new TcpListener(IPAddress.Any, port);
            t._listener.Start();
            new Thread(t.AcceptLoop) { IsBackground = true, Name = "coop-tcp-accept" }.Start();
            return t;
        }

        public static TcpTransport Connect(string host, int port)
        {
            var t = new TcpTransport($"TCP {host}:{port}", false);
            new Thread(() => t.ConnectLoop(host, port)) { IsBackground = true, Name = "coop-tcp-connect" }.Start();
            return t;
        }

        private void AcceptLoop()
        {
            while (!_closed)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { if (_closed) return; continue; }
                var conn = Attach(c, (ulong)Interlocked.Increment(ref _nextId));
                new Thread(() => ReadLoop(conn)) { IsBackground = true, Name = "coop-tcp-read-" + conn.Id }.Start();
            }
        }

        private void ConnectLoop(string host, int port)
        {
            try
            {
                var c = new TcpClient();
                c.Connect(host, port);
                ReadLoop(Attach(c, ClientServerPeer));
            }
            catch (Exception e)
            {
                if (!_closed) _failure = "connect failed: " + e.Message;
            }
        }

        private Conn Attach(TcpClient c, ulong id)
        {
            c.NoDelay = true;
            var conn = new Conn { Id = id, Client = c, Stream = c.GetStream() };
            _conns[id] = conn;
            if (!_isHost) _failure = null;
            return conn;
        }

        private void ReadLoop(Conn conn)
        {
            var s = conn.Stream;
            var hdr = new byte[4];
            try
            {
                while (!_closed)
                {
                    ReadExact(s, hdr, 4);
                    int len = hdr[0] | (hdr[1] << 8) | (hdr[2] << 16) | (hdr[3] << 24);
                    if (len < 0 || len > MaxFrame) throw new IOException("bad frame length " + len);
                    var msg = new byte[len];
                    ReadExact(s, msg, len);
                    _inbox.Enqueue(new NetMessage { From = conn.Id, Data = msg });
                }
            }
            catch (Exception e)
            {
                if (_closed) return;
                Fail(conn, "connection lost: " + e.Message);
            }
        }

        private void Fail(Conn conn, string why)
        {
            // Only report a connection once, and only while it is still current.
            if (!_conns.TryGetValue(conn.Id, out var cur) || cur != conn || !_conns.TryRemove(conn.Id, out _)) return;
            try { conn.Client.Close(); } catch { }
            if (_isHost) _failed.Enqueue(new KeyValuePair<ulong, string>(conn.Id, why));
            else _failure = why;
        }

        private static void ReadExact(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) throw new EndOfStreamException("peer closed");
                got += n;
            }
        }

        public void Send(ulong peer, byte[] data, int offset, int count, bool reliable)
        {
            if (!_conns.TryGetValue(peer, out var conn)) return;
            lock (conn.SendLock)
            {
                try
                {
                    var hdr = new byte[] { (byte)count, (byte)(count >> 8), (byte)(count >> 16), (byte)(count >> 24) };
                    conn.Stream.Write(hdr, 0, 4);
                    conn.Stream.Write(data, offset, count);
                }
                catch (Exception e)
                {
                    Fail(conn, "send failed: " + e.Message);
                }
            }
        }

        public void Poll(List<NetMessage> into)
        {
            while (_inbox.TryDequeue(out var m)) into.Add(m);
        }

        public void TakeFailedPeers(List<KeyValuePair<ulong, string>> into)
        {
            while (_failed.TryDequeue(out var f)) into.Add(f);
        }

        public void DropPeer(ulong peer)
        {
            if (_conns.TryRemove(peer, out var conn))
            {
                try { conn.Client.Close(); } catch { }
            }
        }

        public void Close()
        {
            _closed = true;
            foreach (var id in new List<ulong>(_conns.Keys)) DropPeer(id);
            try { _listener?.Stop(); } catch { }
        }
    }
}
