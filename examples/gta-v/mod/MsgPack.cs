using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NpcVoice
{
    /// <summary>
    /// Just enough MessagePack for fal realtime: encode a string map, decode anything the
    /// FishTTS AudioChunk model can contain. Avoids shipping MessagePack-CSharp and its
    /// System.Memory dependencies into the scripts folder.
    /// </summary>
    internal static class MsgPack
    {
        public static byte[] EncodeStringMap(IDictionary<string, string> map)
        {
            var s = new MemoryStream();
            if (map.Count < 16) s.WriteByte((byte)(0x80 | map.Count));
            else { s.WriteByte(0xde); WriteBigEndian(s, (ulong)map.Count, 2); }

            foreach (var kv in map)
            {
                WriteString(s, kv.Key);
                WriteString(s, kv.Value);
            }
            return s.ToArray();
        }

        public static object Decode(byte[] data)
        {
            int pos = 0;
            return Read(data, ref pos);
        }

        private static void WriteString(Stream s, string value)
        {
            byte[] b = Encoding.UTF8.GetBytes(value ?? "");
            if (b.Length < 32) s.WriteByte((byte)(0xa0 | b.Length));
            else if (b.Length < 256) { s.WriteByte(0xd9); s.WriteByte((byte)b.Length); }
            else if (b.Length < 65536) { s.WriteByte(0xda); WriteBigEndian(s, (ulong)b.Length, 2); }
            else { s.WriteByte(0xdb); WriteBigEndian(s, (ulong)b.Length, 4); }
            s.Write(b, 0, b.Length);
        }

        private static void WriteBigEndian(Stream s, ulong v, int bytes)
        {
            for (int i = bytes - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
        }

        private static object Read(byte[] d, ref int p)
        {
            byte b = d[p++];
            if (b <= 0x7f) return (long)b;
            if (b >= 0xe0) return (long)(sbyte)b;
            if ((b & 0xf0) == 0x80) return ReadMap(d, ref p, b & 0x0f);
            if ((b & 0xf0) == 0x90) return ReadArray(d, ref p, b & 0x0f);
            if ((b & 0xe0) == 0xa0) return ReadString(d, ref p, b & 0x1f);

            switch (b)
            {
                case 0xc0: return null;
                case 0xc2: return false;
                case 0xc3: return true;
                case 0xc4: return ReadBytes(d, ref p, (int)ReadUInt(d, ref p, 1));
                case 0xc5: return ReadBytes(d, ref p, (int)ReadUInt(d, ref p, 2));
                case 0xc6: return ReadBytes(d, ref p, (int)ReadUInt(d, ref p, 4));
                case 0xc7: return SkipExt(d, ref p, (int)ReadUInt(d, ref p, 1));
                case 0xc8: return SkipExt(d, ref p, (int)ReadUInt(d, ref p, 2));
                case 0xc9: return SkipExt(d, ref p, (int)ReadUInt(d, ref p, 4));
                case 0xca: return (double)BitConverter.ToSingle(ReadBigEndianBytes(d, ref p, 4), 0);
                case 0xcb: return BitConverter.ToDouble(ReadBigEndianBytes(d, ref p, 8), 0);
                case 0xcc: return (long)ReadUInt(d, ref p, 1);
                case 0xcd: return (long)ReadUInt(d, ref p, 2);
                case 0xce: return (long)ReadUInt(d, ref p, 4);
                case 0xcf: return (long)ReadUInt(d, ref p, 8);
                case 0xd0: return (long)(sbyte)ReadUInt(d, ref p, 1);
                case 0xd1: return (long)(short)ReadUInt(d, ref p, 2);
                case 0xd2: return (long)(int)ReadUInt(d, ref p, 4);
                case 0xd3: return (long)ReadUInt(d, ref p, 8);
                case 0xd4: return SkipExt(d, ref p, 1);
                case 0xd5: return SkipExt(d, ref p, 2);
                case 0xd6: return SkipExt(d, ref p, 4);
                case 0xd7: return SkipExt(d, ref p, 8);
                case 0xd8: return SkipExt(d, ref p, 16);
                case 0xd9: return ReadString(d, ref p, (int)ReadUInt(d, ref p, 1));
                case 0xda: return ReadString(d, ref p, (int)ReadUInt(d, ref p, 2));
                case 0xdb: return ReadString(d, ref p, (int)ReadUInt(d, ref p, 4));
                case 0xdc: return ReadArray(d, ref p, (int)ReadUInt(d, ref p, 2));
                case 0xdd: return ReadArray(d, ref p, (int)ReadUInt(d, ref p, 4));
                case 0xde: return ReadMap(d, ref p, (int)ReadUInt(d, ref p, 2));
                case 0xdf: return ReadMap(d, ref p, (int)ReadUInt(d, ref p, 4));
                default: throw new InvalidDataException($"Unsupported msgpack type 0x{b:x2}");
            }
        }

        private static Dictionary<string, object> ReadMap(byte[] d, ref int p, int count)
        {
            var map = new Dictionary<string, object>(count);
            for (int i = 0; i < count; i++)
            {
                object key = Read(d, ref p);
                map[key as string ?? Convert.ToString(key)] = Read(d, ref p);
            }
            return map;
        }

        private static object[] ReadArray(byte[] d, ref int p, int count)
        {
            var items = new object[count];
            for (int i = 0; i < count; i++) items[i] = Read(d, ref p);
            return items;
        }

        private static string ReadString(byte[] d, ref int p, int len)
        {
            string s = Encoding.UTF8.GetString(d, p, len);
            p += len;
            return s;
        }

        private static byte[] ReadBytes(byte[] d, ref int p, int len)
        {
            var b = new byte[len];
            Buffer.BlockCopy(d, p, b, 0, len);
            p += len;
            return b;
        }

        private static object SkipExt(byte[] d, ref int p, int len)
        {
            p += 1 + len; // type byte + payload
            return null;
        }

        private static ulong ReadUInt(byte[] d, ref int p, int bytes)
        {
            ulong v = 0;
            for (int i = 0; i < bytes; i++) v = (v << 8) | d[p++];
            return v;
        }

        private static byte[] ReadBigEndianBytes(byte[] d, ref int p, int len)
        {
            byte[] b = ReadBytes(d, ref p, len);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return b;
        }
    }
}
