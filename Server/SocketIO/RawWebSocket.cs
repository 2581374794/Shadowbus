using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace Shadowbus.Server.SocketIO
{
    /// <summary>
    /// Minimal RFC6455 framing used after the HTTP upgrade. Unity's Mono
    /// runtime does not implement HttpListener.AcceptWebSocketAsync, so the
    /// Socket.IO transport owns the WebSocket framing directly.
    /// </summary>
    internal sealed class RawWebSocket : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly object _sendLock = new object();
        private int _closed;

        public RawWebSocket(TcpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _stream = client.GetStream();
        }

        public bool IsOpen => Volatile.Read(ref _closed) == 0 && _client.Connected;

        public bool TryReceive(out bool isBinary, out byte[] payload)
        {
            isBinary = false;
            payload = null;
            using (var message = new MemoryStream())
            {
                bool firstFrame = true;
                while (true)
                {
                    int first = _stream.ReadByte();
                    if (first < 0)
                        return false;
                    int second = ReadRequiredByte();
                    bool fin = (first & 0x80) != 0;
                    int opcode = first & 0x0F;
                    bool masked = (second & 0x80) != 0;
                    ulong length = (ulong)(second & 0x7F);

                    if (length == 126)
                        length = ReadUInt16NetworkOrder();
                    else if (length == 127)
                        length = ReadUInt64NetworkOrder();
                    if (length > 16UL * 1024UL * 1024UL)
                        throw new InvalidDataException("WebSocket frame exceeds 16 MB");

                    byte[] mask = masked ? ReadBytes(4) : null;
                    byte[] frame = ReadBytes((int)length);
                    if (masked)
                    {
                        for (int i = 0; i < frame.Length; i++)
                            frame[i] = (byte)(frame[i] ^ mask[i % 4]);
                    }

                    if (opcode == 0x8)
                    {
                        SendControl(0x8, frame);
                        Close();
                        return false;
                    }
                    if (opcode == 0x9)
                    {
                        SendControl(0xA, frame);
                        continue;
                    }
                    if (opcode == 0xA)
                        continue;

                    if (firstFrame)
                    {
                        isBinary = opcode == 0x2;
                        firstFrame = false;
                    }
                    message.Write(frame, 0, frame.Length);
                    if (fin)
                    {
                        payload = message.ToArray();
                        return true;
                    }
                }
            }
        }

        public void SendText(byte[] payload)
        {
            SendFrame(0x1, payload);
        }

        public void SendBinary(byte[] payload)
        {
            SendFrame(0x2, payload);
        }

        private void SendControl(byte opcode, byte[] payload)
        {
            try
            {
                SendFrame(opcode, payload);
            }
            catch
            {
                Close();
            }
        }

        private void SendFrame(byte opcode, byte[] payload)
        {
            if (!IsOpen)
                return;
            payload = payload ?? Array.Empty<byte>();
            lock (_sendLock)
            {
                _stream.WriteByte((byte)(0x80 | (opcode & 0x0F)));
                if (payload.Length < 126)
                {
                    _stream.WriteByte((byte)payload.Length);
                }
                else if (payload.Length <= ushort.MaxValue)
                {
                    _stream.WriteByte(126);
                    WriteUInt16NetworkOrder((ushort)payload.Length);
                }
                else
                {
                    _stream.WriteByte(127);
                    WriteUInt64NetworkOrder((ulong)payload.Length);
                }
                if (payload.Length > 0)
                    _stream.Write(payload, 0, payload.Length);
                _stream.Flush();
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            try
            {
                _client.Close();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Close();
            _stream.Dispose();
        }

        private int ReadRequiredByte()
        {
            int value = _stream.ReadByte();
            if (value < 0)
                throw new EndOfStreamException("Unexpected WebSocket EOF");
            return value;
        }

        private byte[] ReadBytes(int count)
        {
            byte[] data = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = _stream.Read(data, offset, count - offset);
                if (read <= 0)
                    throw new EndOfStreamException("Unexpected WebSocket EOF");
                offset += read;
            }
            return data;
        }

        private ulong ReadUInt16NetworkOrder()
        {
            byte[] data = ReadBytes(2);
            return (ulong)((data[0] << 8) | data[1]);
        }

        private ulong ReadUInt64NetworkOrder()
        {
            byte[] data = ReadBytes(8);
            ulong value = 0;
            for (int i = 0; i < data.Length; i++)
                value = (value << 8) | data[i];
            return value;
        }

        private void WriteUInt16NetworkOrder(ushort value)
        {
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        private void WriteUInt64NetworkOrder(ulong value)
        {
            for (int i = 7; i >= 0; i--)
                _stream.WriteByte((byte)(value >> (8 * i)));
        }
    }
}
