using System;
using System.IO;
using System.Text;

namespace ClipBridge
{
    internal enum ClipboardPacketType : byte
    {
        Announcement = 1,
        DataRequest = 2,
        DataResponse = 3,
        TimeSyncRequest = 4,
        TimeSyncResponse = 5,
        FileManifestRequest = 6,
        FileManifestResponse = 7,
        FileReadRequest = 8,
        FileReadResponse = 9
    }

    internal enum SharedClipboardKind : byte
    {
        None = 0,
        Text = 1,
        Image = 2,
        Files = 3
    }

    internal sealed class ClipboardPacket
    {
        public ClipboardPacketType Type;
        public byte[] Payload;
    }

    internal static class FrameProtocol
    {
        private const uint Magic = 0x434C5042; // CLPB
        private const byte Version = 4;
        private const int MaxFrameBytes = 256 * 1024 * 1024;

        public static void Write(Stream stream, ClipboardPacketType type, byte[] payload)
        {
            if (payload == null) payload = new byte[0];
            BinaryWriter w = new BinaryWriter(stream, Encoding.UTF8);
            w.Write(Magic);
            w.Write(Version);
            w.Write((byte)type);
            w.Write(payload.Length);
            w.Write(payload);
            w.Flush();
        }

        public static ClipboardPacket Read(Stream stream)
        {
            BinaryReader r = new BinaryReader(stream, Encoding.UTF8);
            uint magic = r.ReadUInt32();
            if (magic != Magic) throw new InvalidDataException("Invalid ClipBridge frame.");

            byte version = r.ReadByte();
            if (version != Version) throw new InvalidDataException("Unsupported ClipBridge protocol version.");

            ClipboardPacketType type = (ClipboardPacketType)r.ReadByte();
            int length = r.ReadInt32();
            if (length < 0 || length > MaxFrameBytes) throw new InvalidDataException("Invalid frame size.");

            byte[] payload = ReadExactly(r, length);
            return new ClipboardPacket { Type = type, Payload = payload };
        }

        private static byte[] ReadExactly(BinaryReader r, int count)
        {
            byte[] data = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int n = r.Read(data, offset, count - offset);
                if (n <= 0) throw new EndOfStreamException();
                offset += n;
            }
            return data;
        }
    }
}
