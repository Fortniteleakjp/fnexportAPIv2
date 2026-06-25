using System.Runtime.InteropServices;

namespace RADADecoder
{
    /// <summary>
    /// Standard 44-byte PCM WAVE header (RIFF/fmt /data).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct WaveHeader
    {
        public fixed byte riff_tag[4];
        public int riff_length;
        public fixed byte wave_tag[4];
        public fixed byte fmt_tag[4];
        public int fmt_length;
        public short audio_format;
        public short num_channels;
        public int sample_rate;
        public int byte_rate;
        public short block_align;
        public short bits_per_sample;
        public fixed byte data_tag[4];
        public int data_length;
    }
}
