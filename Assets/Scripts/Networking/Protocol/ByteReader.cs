using System;
using System.Text;

namespace JumpNowBro.Networking
{
    /// Big-endian reader over (untrusted) wire bytes. Every read is bounds-checked and returns false on a
    /// short buffer instead of throwing, so a truncated/malformed datagram can't crash the receive path.
    /// On a false return the read is all-or-nothing — Position only advances on success.
    public ref struct ByteReader
    {
        /// Hard cap for discovery game names and HELLO/WELCOME display names; reject before allocating.
        public const int MaxStringLength = 256;

        readonly ReadOnlySpan<byte> buf;

        public ByteReader(ReadOnlySpan<byte> buffer) { buf = buffer; Position = 0; }

        public int Position { get; private set; }
        public int Remaining => buf.Length - Position;

        public bool TryReadByte(out byte v)
        {
            if (Remaining < 1) { v = 0; return false; }
            v = buf[Position++];
            return true;
        }

        public bool TryReadUShort(out ushort v)
        {
            if (Remaining < 2) { v = 0; return false; }
            v = (ushort)((buf[Position] << 8) | buf[Position + 1]);
            Position += 2;
            return true;
        }

        public bool TryReadUInt(out uint v)
        {
            if (Remaining < 4) { v = 0; return false; }
            v = ((uint)buf[Position] << 24) | ((uint)buf[Position + 1] << 16) | ((uint)buf[Position + 2] << 8) | buf[Position + 3];
            Position += 4;
            return true;
        }

        public bool TryReadInt(out int v)
        {
            if (!TryReadUInt(out uint u)) { v = 0; return false; }
            v = (int)u;
            return true;
        }

        public bool TryReadSByte(out sbyte v)
        {
            if (!TryReadByte(out byte b)) { v = 0; return false; }
            v = (sbyte)b;
            return true;
        }

        public bool TryReadFloat(out float v)
        {
            if (!TryReadUInt(out uint u)) { v = 0; return false; }
            v = BitConverter.Int32BitsToSingle((int)u);
            return true;
        }

        public bool TryReadBytes(int count, out ReadOnlySpan<byte> v)
        {
            if (count < 0 || Remaining < count) { v = default; return false; }
            v = buf.Slice(Position, count);
            Position += count;
            return true;
        }

        public bool TryReadString(out string v)
        {
            v = null;
            if (Remaining < 2) return false;
            int len = (buf[Position] << 8) | buf[Position + 1];
            if (len > MaxStringLength) return false;                  // anti-OOM: reject an absurd length prefix outright
            if (Remaining < 2 + len) return false;                    // all-or-nothing: don't consume the prefix
            v = Encoding.UTF8.GetString(buf.Slice(Position + 2, len));
            Position += 2 + len;
            return true;
        }
    }
}
