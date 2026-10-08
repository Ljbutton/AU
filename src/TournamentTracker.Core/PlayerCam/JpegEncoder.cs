using System;

namespace TournamentTracker.PlayerCam
{
    /// <summary>
    /// A small baseline JPEG encoder (4:2:0, the standard tables), so the host's game can hand the
    /// player camera's pictures to The Button without Unity's encoder (which must run on the game's
    /// own thread). One instance per thread: it reuses its buffers.
    /// </summary>
    public sealed class JpegEncoder
    {
        private static readonly byte[] ZigZag =
        {
            0, 1, 5, 6, 14, 15, 27, 28, 2, 4, 7, 13, 16, 26, 29, 42, 3, 8, 12, 17, 25, 30, 41, 43, 9, 11, 18, 24, 31, 40, 44, 53,
            10, 19, 23, 32, 39, 45, 52, 54, 20, 22, 33, 38, 46, 51, 55, 60, 21, 34, 37, 47, 50, 56, 59, 61, 35, 36, 48, 49, 57, 58, 62, 63,
        };
        private static readonly int[] YQ =
        {
            16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
            18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
        };
        private static readonly int[] UVQ =
        {
            17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        };
        private static readonly byte[] DcLumCodes = { 0, 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
        private static readonly byte[] DcLumValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        private static readonly byte[] AcLumCodes = { 0, 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
        private static readonly byte[] AcLumValues =
        {
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
            0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
            0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
            0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
            0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
            0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
            0xf9, 0xfa,
        };
        private static readonly byte[] DcChromCodes = { 0, 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
        private static readonly byte[] DcChromValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        private static readonly byte[] AcChromCodes = { 0, 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
        private static readonly byte[] AcChromValues =
        {
            0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
            0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
            0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
            0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
            0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
            0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
            0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
            0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
            0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
            0xf9, 0xfa,
        };

        // Each value from -32767 to 32767: its size category and its bits.
        private static readonly byte[] Category = new byte[65535];
        private static readonly ushort[] Bits = new ushort[65535];
        private static readonly (ushort Code, byte Length)[] YDc = Huffman(DcLumCodes, DcLumValues);
        private static readonly (ushort Code, byte Length)[] YAc = Huffman(AcLumCodes, AcLumValues);
        private static readonly (ushort Code, byte Length)[] CDc = Huffman(DcChromCodes, DcChromValues);
        private static readonly (ushort Code, byte Length)[] CAc = Huffman(AcChromCodes, AcChromValues);

        static JpegEncoder()
        {
            int lower = 1, upper = 2;
            for (int cat = 1; cat <= 15; cat++)
            {
                for (int n = lower; n < upper; n++) { Category[32767 + n] = (byte)cat; Bits[32767 + n] = (ushort)n; }
                for (int n = -(upper - 1); n <= -lower; n++) { Category[32767 + n] = (byte)cat; Bits[32767 + n] = (ushort)(upper - 1 + n); }
                lower <<= 1;
                upper <<= 1;
            }
        }

        private static (ushort, byte)[] Huffman(byte[] counts, byte[] values)
        {
            var table = new (ushort, byte)[256];
            int code = 0, at = 0;
            for (int length = 1; length <= 16; length++)
            {
                for (int j = 0; j < counts[length]; j++) table[values[at++]] = ((ushort)code++, (byte)length);
                code <<= 1;
            }
            return table;
        }

        private readonly byte[] _yTable = new byte[64], _uvTable = new byte[64];
        private readonly float[] _yScale = new float[64], _uvScale = new float[64];
        private readonly float[] _block = new float[64];
        private readonly int[] _zig = new int[64];
        private readonly float[] _y = new float[256], _u = new float[256], _v = new float[256];
        private readonly float[] _cb = new float[64], _cr = new float[64];
        private int _quality = -1;
        private byte[] _out = new byte[256 * 1024];
        private int _length, _bitBuffer, _bitCount;

        /// <summary>
        /// Encodes 8-bit RGBA pixels (width × height, rows top first, or bottom first with
        /// <paramref name="bottomUp"/>, as Unity reads them back). Quality 1–100.
        /// </summary>
        public byte[] Encode(byte[] rgba, int width, int height, int quality = 80, bool bottomUp = false)
        {
            if (width <= 0 || height <= 0 || width > 65535 || height > 65535) throw new ArgumentOutOfRangeException(nameof(width));
            if (rgba.Length < width * height * 4) throw new ArgumentException("Not enough pixels.", nameof(rgba));
            Quality(Math.Max(1, Math.Min(100, quality)));
            _length = 0;
            _bitBuffer = 0;
            _bitCount = 0;
            Headers(width, height);

            float dcY = 0, dcU = 0, dcV = 0;
            for (int my = 0; my < height; my += 16)
                for (int mx = 0; mx < width; mx += 16)
                {
                    // The 16×16 square in Y, U and V (the edges repeat the last pixel).
                    for (int yy = 0; yy < 16; yy++)
                    {
                        int row = Math.Min(my + yy, height - 1);
                        if (bottomUp) row = height - 1 - row;
                        int rowAt = row * width * 4;
                        for (int xx = 0; xx < 16; xx++)
                        {
                            int p = rowAt + Math.Min(mx + xx, width - 1) * 4;
                            float r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
                            int k = yy * 16 + xx;
                            _y[k] = 0.299f * r + 0.587f * g + 0.114f * b - 128f;
                            _u[k] = -0.16874f * r - 0.33126f * g + 0.5f * b;
                            _v[k] = 0.5f * r - 0.41869f * g - 0.08131f * b;
                        }
                    }
                    dcY = Block(_y, 0, dcY, _yScale, YDc, YAc);
                    dcY = Block(_y, 8, dcY, _yScale, YDc, YAc);
                    dcY = Block(_y, 128, dcY, _yScale, YDc, YAc);
                    dcY = Block(_y, 136, dcY, _yScale, YDc, YAc);
                    // Colour at half the size each way.
                    for (int yy = 0; yy < 8; yy++)
                        for (int xx = 0; xx < 8; xx++)
                        {
                            int k = yy * 32 + xx * 2;
                            _cb[yy * 8 + xx] = (_u[k] + _u[k + 1] + _u[k + 16] + _u[k + 17]) * 0.25f;
                            _cr[yy * 8 + xx] = (_v[k] + _v[k + 1] + _v[k + 16] + _v[k + 17]) * 0.25f;
                        }
                    dcU = Block8(_cb, dcU, _uvScale, CDc, CAc);
                    dcV = Block8(_cr, dcV, _uvScale, CDc, CAc);
                }

            if (_bitCount > 0) Put((1 << (8 - _bitCount)) - 1, 8 - _bitCount);   // pad the last byte with ones
            Byte(0xFF); Byte(0xD9);
            var result = new byte[_length];
            Buffer.BlockCopy(_out, 0, result, 0, _length);
            return result;
        }

        private void Quality(int quality)
        {
            if (quality == _quality) return;
            _quality = quality;
            int sf = quality < 50 ? 5000 / quality : 200 - quality * 2;
            for (int i = 0; i < 64; i++)
            {
                _yTable[ZigZag[i]] = (byte)Math.Max(1, Math.Min(255, (YQ[i] * sf + 50) / 100));
                _uvTable[ZigZag[i]] = (byte)Math.Max(1, Math.Min(255, (UVQ[i] * sf + 50) / 100));
            }
            double[] aan = { 1.0, 1.387039845, 1.306562965, 1.175875602, 1.0, 0.785694958, 0.541196100, 0.275899379 };
            for (int row = 0, k = 0; row < 8; row++)
                for (int col = 0; col < 8; col++, k++)
                {
                    _yScale[k] = (float)(1.0 / (_yTable[ZigZag[k]] * aan[row] * aan[col] * 8.0));
                    _uvScale[k] = (float)(1.0 / (_uvTable[ZigZag[k]] * aan[row] * aan[col] * 8.0));
                }
        }

        private float Block(float[] plane, int at, float dc, float[] scale, (ushort, byte)[] hDc, (ushort, byte)[] hAc)
        {
            for (int y = 0; y < 8; y++)
                Array.Copy(plane, at + y * 16, _block, y * 8, 8);
            return Code(dc, scale, hDc, hAc);
        }

        private float Block8(float[] plane, float dc, float[] scale, (ushort, byte)[] hDc, (ushort, byte)[] hAc)
        {
            Array.Copy(plane, _block, 64);
            return Code(dc, scale, hDc, hAc);
        }

        /// <summary>The 8×8 block in _block: transformed, quantised and written. Returns its DC value.</summary>
        private float Code(float lastDc, float[] scale, (ushort Code, byte Length)[] hDc, (ushort Code, byte Length)[] hAc)
        {
            var d = _block;
            for (int o = 0; o < 64; o += 8) Dct(d, o, 1);
            for (int o = 0; o < 8; o++) Dct(d, o, 8);
            for (int i = 0; i < 64; i++)
            {
                float q = d[i] * scale[i];
                _zig[ZigZag[i]] = q > 0 ? (int)(q + 0.5f) : (int)(q - 0.5f);
            }
            int diff = _zig[0] - (int)lastDc;
            if (diff == 0) Put(hDc[0].Code, hDc[0].Length);
            else
            {
                int p = 32767 + diff;
                Put(hDc[Category[p]].Code, hDc[Category[p]].Length);
                Put(Bits[p], Category[p]);
            }
            int end = 63;
            while (end > 0 && _zig[end] == 0) end--;
            if (end == 0) { Put(hAc[0].Code, hAc[0].Length); return _zig[0]; }
            int i2 = 1;
            while (i2 <= end)
            {
                int start = i2;
                while (_zig[i2] == 0 && i2 <= end) i2++;
                int zeros = i2 - start;
                if (zeros >= 16)
                {
                    for (int n = 0; n < zeros >> 4; n++) Put(hAc[0xF0].Code, hAc[0xF0].Length);
                    zeros &= 0xF;
                }
                int p = 32767 + _zig[i2];
                var h = hAc[(zeros << 4) + Category[p]];
                Put(h.Code, h.Length);
                Put(Bits[p], Category[p]);
                i2++;
            }
            if (end != 63) Put(hAc[0].Code, hAc[0].Length);
            return _zig[0];
        }

        /// <summary>The AAN forward DCT on 8 values, <paramref name="step"/> apart, in place.</summary>
        private static void Dct(float[] d, int o, int step)
        {
            int i0 = o, i1 = o + step, i2 = o + 2 * step, i3 = o + 3 * step, i4 = o + 4 * step, i5 = o + 5 * step, i6 = o + 6 * step, i7 = o + 7 * step;
            float tmp0 = d[i0] + d[i7], tmp7 = d[i0] - d[i7];
            float tmp1 = d[i1] + d[i6], tmp6 = d[i1] - d[i6];
            float tmp2 = d[i2] + d[i5], tmp5 = d[i2] - d[i5];
            float tmp3 = d[i3] + d[i4], tmp4 = d[i3] - d[i4];

            float tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
            d[i0] = tmp10 + tmp11;
            d[i4] = tmp10 - tmp11;
            float z1 = (tmp12 + tmp13) * 0.707106781f;
            d[i2] = tmp13 + z1;
            d[i6] = tmp13 - z1;

            tmp10 = tmp4 + tmp5;
            tmp11 = tmp5 + tmp6;
            tmp12 = tmp6 + tmp7;
            float z5 = (tmp10 - tmp12) * 0.382683433f;
            float z2 = 0.541196100f * tmp10 + z5;
            float z4 = 1.306562965f * tmp12 + z5;
            float z3 = tmp11 * 0.707106781f;
            float z11 = tmp7 + z3, z13 = tmp7 - z3;
            d[i5] = z13 + z2;
            d[i3] = z13 - z2;
            d[i1] = z11 + z4;
            d[i7] = z11 - z4;
        }

        private void Put(int value, int length)
        {
            for (int bit = length - 1; bit >= 0; bit--)
            {
                _bitBuffer = (_bitBuffer << 1) | ((value >> bit) & 1);
                if (++_bitCount == 8)
                {
                    Byte((byte)_bitBuffer);
                    if (_bitBuffer == 0xFF) Byte(0);   // a 0xFF in the data is followed by a zero
                    _bitBuffer = 0;
                    _bitCount = 0;
                }
            }
        }

        private void Byte(byte b)
        {
            if (_length == _out.Length) Array.Resize(ref _out, _out.Length * 2);
            _out[_length++] = b;
        }

        private void Word(int w) { Byte((byte)(w >> 8)); Byte((byte)w); }

        private void Headers(int width, int height)
        {
            Word(0xFFD8);
            // JFIF
            Word(0xFFE0); Word(16);
            Byte((byte)'J'); Byte((byte)'F'); Byte((byte)'I'); Byte((byte)'F'); Byte(0);
            Byte(1); Byte(1); Byte(0); Word(1); Word(1); Byte(0); Byte(0);
            // Quantisation tables
            Word(0xFFDB); Word(132);
            Byte(0); for (int i = 0; i < 64; i++) Byte(_yTable[i]);
            Byte(1); for (int i = 0; i < 64; i++) Byte(_uvTable[i]);
            // The frame: 8 bits, three components, Y at full size, colour at half.
            Word(0xFFC0); Word(17); Byte(8); Word(height); Word(width); Byte(3);
            Byte(1); Byte(0x22); Byte(0);
            Byte(2); Byte(0x11); Byte(1);
            Byte(3); Byte(0x11); Byte(1);
            // Huffman tables
            Word(0xFFC4); Word(0x01A2);
            Table(0x00, DcLumCodes, DcLumValues);
            Table(0x10, AcLumCodes, AcLumValues);
            Table(0x01, DcChromCodes, DcChromValues);
            Table(0x11, AcChromCodes, AcChromValues);
            // Start of scan
            Word(0xFFDA); Word(12); Byte(3);
            Byte(1); Byte(0x00);
            Byte(2); Byte(0x11);
            Byte(3); Byte(0x11);
            Byte(0); Byte(0x3F); Byte(0);
        }

        private void Table(byte id, byte[] counts, byte[] values)
        {
            Byte(id);
            for (int i = 1; i <= 16; i++) Byte(counts[i]);
            foreach (var v in values) Byte(v);
        }
    }
}
