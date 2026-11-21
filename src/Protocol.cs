using System;
using System.Text;

namespace OpenDEDHub
{
    internal static class Protocol
    {
        public const int BaudRate = 115200;
        public const int CharsPerLine = 24;
        public const int DEDLines = 5;
        public const int PayloadLen = CharsPerLine * DEDLines; // 120 bytes
        public const int MaxCommandLen = PayloadLen + 4;       // 124 bytes (#|d + 120 + \n)
        public const int IDReplyLen = 6;

        public const string DcsMulticastIp = "239.255.50.10";
        public const int DcsMulticastPort = 5010;

        public static byte[] ComposeTextCommand(char code, string content = "")
        {
            return Encoding.ASCII.GetBytes($"#|{code}{content}\n");
        }

        public static byte[] ComposeDisplayCommand(byte[] payload120)
        {
            byte[] packet = new byte[MaxCommandLen];
            packet[0] = (byte)'#';
            packet[1] = (byte)'|';
            packet[2] = (byte)'d';
            Buffer.BlockCopy(payload120, 0, packet, 3, Math.Min(payload120.Length, PayloadLen));
            packet[MaxCommandLen - 1] = (byte)'\n';
            return packet;
        }

        public static bool TryParseIdentification(byte[] reply, int count, out byte hwRev, out byte fwMaj, out byte fwMin)
        {
            hwRev = fwMaj = fwMin = 0;
            if (reply == null || count < IDReplyLen) return false;
            if ((char)reply[0] != 'I') return false;

            int model = ((int)reply[1] << 1) + (int)reply[2];
            if (model != 1) return false; // 1 = Simgears DED

            hwRev = reply[3];
            fwMaj = reply[4];
            fwMin = reply[5];
            return true;
        }

        public static byte[] ProcessDedLines(string[] dedLines, string[] invertLines)
        {
            byte[] output = new byte[PayloadLen];
            for (int i = 0; i < output.Length; i++) output[i] = 32;

            if (dedLines == null || invertLines == null || dedLines.Length < DEDLines || invertLines.Length < DEDLines)
                return output;

            for (int row = 0; row < DEDLines; row++)
            {
                byte[] encodedRow = ConvertLineToCustomFont(dedLines[row], invertLines[row]);
                if (encodedRow != null)
                {
                    Buffer.BlockCopy(encodedRow, 0, output, row * CharsPerLine, CharsPerLine);
                }
            }
            return output;
        }

        private static byte[] ConvertLineToCustomFont(string inputLine, string invertedChars)
        {
            if (string.IsNullOrEmpty(inputLine)) return null;

            byte[] array = new byte[CharsPerLine];
            for (int i = 0; i < array.Length; i++) array[i] = 32;

            int len = Math.Min(inputLine.Length, CharsPerLine);
            for (int i = 0; i < len; i++)
            {
                int ch = (int)inputLine[i];
                bool isInverted = (invertedChars != null && i < invertedChars.Length && invertedChars[i] == '\u0002');

                if (!isInverted)
                {
                    array[i] = ch switch
                    {
                        0 => 32,
                        1 => 64,
                        2 => 42,
                        3 => 95,
                        94 => 94,
                        126 => 124,
                        _ => (byte)inputLine[i]
                    };
                }
                else
                {
                    array[i] = ch switch
                    {
                        0 => 32,
                        1 => 192,
                        2 => 170,
                        3 => 223,
                        94 => 222,
                        126 => 252,
                        > 64 and < 91 => (byte)(ch + 32),
                        < 128 => (byte)(inputLine[i] + '\u0080'),
                        _ => 177
                    };
                }
            }
            return array;
        }
    }
}
