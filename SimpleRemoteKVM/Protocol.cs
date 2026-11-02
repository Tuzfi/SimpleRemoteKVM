using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SimpleRemoteKVM
{
    /// <summary>
    /// Package type identifiers extracted from MouseWithoutBorders.PackageType enum.
    /// </summary>
    public enum PackageType : byte
    {
        heartbeat = 20,
        machineswitched = 77, // Extraído do MouseWithoutBorders.PackageType: sinaliza alternância mútua de controle
        keybd = 122,
        mouse = 123,
        handshake = 126,
        handshakeack = 127
    }

    /// <summary>
    /// Mouse event payload corresponding to MouseWithoutBorders.MOUSEDATA (16 bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEDATA
    {
        public int x;
        public int y;
        public int wDelta;
        public int dwFlags;

        public override string ToString()
        {
            return string.Format("MOUSEDATA [X={0}, Y={1}, Delta={2}, Flags=0x{3:X}]", x, y, wDelta, dwFlags);
        }
    }

    /// <summary>
    /// Keyboard event payload corresponding to MouseWithoutBorders.KEYBDDATA (8 bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDDATA
    {
        public int wVk;
        public int dwFlags;

        public override string ToString()
        {
            return string.Format("KEYBDDATA [Vk={0} (0x{0:X2}), Flags=0x{1:X}]", wVk, dwFlags);
        }
    }

    /// <summary>
    /// Binary transmission packet corresponding to MouseWithoutBorders.DATA.
    /// Compact 37-byte deterministic wire layout for didactic LAN streaming.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DATA
    {
        public const int PACKET_SIZE = 37;

        public PackageType Type;    // 1 byte
        public int Id;              // 4 bytes
        public long Timestamp;      // 8 bytes (UTC Ticks)
        public MOUSEDATA Md;        // 16 bytes
        public KEYBDDATA Kd;        // 8 bytes

        public byte[] ToBytes()
        {
            byte[] buffer = new byte[PACKET_SIZE];
            using (MemoryStream ms = new MemoryStream(buffer))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)Type);
                bw.Write(Id);
                bw.Write(Timestamp);
                
                // MOUSEDATA
                bw.Write(Md.x);
                bw.Write(Md.y);
                bw.Write(Md.wDelta);
                bw.Write(Md.dwFlags);

                // KEYBDDATA
                bw.Write(Kd.wVk);
                bw.Write(Kd.dwFlags);
            }
            return buffer;
        }

        public static DATA FromBytes(byte[] buffer)
        {
            if (buffer == null || buffer.Length < PACKET_SIZE)
            {
                throw new ArgumentException("Buffer is smaller than DATA.PACKET_SIZE (" + PACKET_SIZE + " bytes).");
            }

            DATA data = new DATA();
            using (MemoryStream ms = new MemoryStream(buffer))
            using (BinaryReader br = new BinaryReader(ms))
            {
                data.Type = (PackageType)br.ReadByte();
                data.Id = br.ReadInt32();
                data.Timestamp = br.ReadInt64();

                data.Md = new MOUSEDATA
                {
                    x = br.ReadInt32(),
                    y = br.ReadInt32(),
                    wDelta = br.ReadInt32(),
                    dwFlags = br.ReadInt32()
                };

                data.Kd = new KEYBDDATA
                {
                    wVk = br.ReadInt32(),
                    dwFlags = br.ReadInt32()
                };
            }
            return data;
        }

        public override string ToString()
        {
            return string.Format("DATA [Type={0}, Id={1}, Time={2:HH:mm:ss.fff}] -> {3}",
                Type,
                Id,
                new DateTime(Timestamp, DateTimeKind.Utc).ToLocalTime(),
                Type == PackageType.mouse ? Md.ToString() : (Type == PackageType.keybd ? Kd.ToString() : "Control"));
        }
    }
}
