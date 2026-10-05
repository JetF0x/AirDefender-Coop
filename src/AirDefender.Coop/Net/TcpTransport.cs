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
    /// Length-prefixed TCP link. Used for local two-instance testing and direct-IP play.
    /// "Unreliable" sends are delivered reliably, which is harmless on a TCP link.
    /// </summary>
    public sealed class TcpTransport : ITransport
    {
        private const int MaxFrame = 64 * 1024 * 1024;

        private readonly ConcurrentQueue<byte[]> _inbox = new ConcurrentQueue<byte[]>();
        private readonly object _sendLock = new object();
        private readonly string _describe;
        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;
        private Thread _thread;
        private volatile bool _closed;
        private volatile string _failure;

        private TcpTransport(string describe) { _describe = describe; }

        public string Describe => _describe;
        public bool HasPeer => _stream != null && _failure == null;
        public string FailureReason => _failure;

        public static TcpTransport Listen(int port)
        {
            var t = new TcpTransport($"TCP host :{port}");
            t._listener = new TcpListener(IPAddress.Any, port);
            t._listener.Start();
            t._thread = new Thread(t.AcceptLoop) { IsBackground = true, Name = "coop-tcp-accept" };
            t._thread.Start();
            return t;
        }

        public static TcpTransport Connect(string host, int port)
        {
            var t = new TcpTransport($"TCP {host}:{port}");
            t._thread = new Thread(() => t.ConnectLoop(host, port)) { IsBackground = true, Name = "coop-tcp-connect" };
            t._thread.Start();
            return t;
        }

        private void AcceptLoop()
        {
            while (!_closed)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { if (_closed) return; continue; }
                if (_stream != null)
                {
                    // Only one partner at a time.
                    try { c.Close(); } catch { }
                    continue;
                }
                Attach(c);
                ReadLoop(c);
            }
        }

        private void ConnectLoop(string host, int port)
        {
            try
            {
                var c = new TcpClient();
                c.Connect(host, port);
                Attach(c);
                ReadLoop(c);
            }
            catch (Exception e)
            {
                if (!_closed) _failure = "connect failed: " + e.Message;
            }
        }

        private void Attach(TcpClient c)
        {
            c.NoDelay = true;
            lock (_sendLock)
            {
                _client = c;
                _stream = c.GetStream();
            }
            _failure = null;
        }

        private void ReadLoop(TcpClient c)
        {
            var s = c.GetStream();
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
                    _inbox.Enqueue(msg);
                }
            }
            catch (Exception e)
            {
                if (_closed) return;
                lock (_sendLock)
                {
                    if (_client == c) { _stream = null; _client = null; }
                }
                try { c.Close(); } catch { }
                _failure = "connection lost: " + e.Message;
            }
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

        public void Send(byte[] data, int offset, int count, bool reliable)
        {
            lock (_sendLock)
            {
                var s = _stream;
                if (s == null) return;
                try
                {
                    var hdr = new byte[] { (byte)count, (byte)(count >> 8), (byte)(count >> 16), (byte)(count >> 24) };
                    s.Write(hdr, 0, 4);
                    s.Write(data, offset, count);
                }
                catch (Exception e)
                {
                    _failure = "send failed: " + e.Message;
                }
            }
        }

        public void Poll(List<byte[]> into)
        {
            while (_inbox.TryDequeue(out var m)) into.Add(m);
        }

        public void DropPeer()
        {
            lock (_sendLock)
            {
                try { _client?.Close(); } catch { }
                _client = null;
                _stream = null;
            }
            // A listening host can accept the next partner.
            if (_listener != null) _failure = null;
        }

        public void Close()
        {
            _closed = true;
            DropPeer();
            try { _listener?.Stop(); } catch { }
        }
    }
}
