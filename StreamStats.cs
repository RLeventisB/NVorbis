namespace NVorbis
{
    public class StreamStats
    {
        private int _sampleRate;
        private readonly int[] _packetBits = new int[2], _packetSamples = new int[2];
        private int _packetIndex, _packetCount;
        private long _totalSamples, _audioBits, _headerBits, _containerBits, _wasteBits;
        private readonly object _lock = new object();
        /// <summary>
        /// Gets the calculated bit rate of audio stream data for the everything decoded so far.
        /// </summary>
        public int EffectiveBitRate
        {
            get
            {
                long samples, bits;
                lock (_lock)
                {
                    samples = _totalSamples;
                    bits = _audioBits + _headerBits + _containerBits + _wasteBits;
                }
                if (samples > 0)
                {
                    return (int)((double)bits / samples * _sampleRate);
                }
                return 0;
            }
        }
        /// <summary>
        /// Gets the calculated bit rate per second of audio for the last two packets.
        /// </summary>
        public int InstantBitRate
        {
            get
            {
                int samples, bits;
                lock (_lock)
                {
                    bits = _packetBits[0] + _packetBits[1];
                    samples = _packetSamples[0] + _packetSamples[1];
                }
                if (samples > 0)
                {
                    return (int)((double)bits / samples * _sampleRate);
                }
                return 0;
            }
        }

        /// <summary>
        /// Gets the number of framing bits used by the container.
        /// </summary>
        public long ContainerBits => _containerBits;
        /// <summary>
        /// Gets the number of bits read that do not contribute to the output audio.  Does not include framing bits from the container.
        /// </summary>
        public long OverheadBits => _headerBits;
        /// <summary>
        /// Gets the number of bits read that contribute to the output audio.
        /// </summary>
        public long AudioBits => _audioBits;
        /// <summary>
        /// Gets the number of bits skipped.
        /// </summary>
        public long WasteBits => _wasteBits;
        /// <summary>
        /// Gets the number of packets read.
        /// </summary>
        public int PacketCount => _packetCount;
        /// <summary>
        /// Resets the counters for bit rate and bits.
        /// </summary>
        public void ResetStats()
        {
            lock (_lock)
            {
                _packetBits[0] = _packetBits[1] = 0;
                _packetSamples[0] = _packetSamples[1] = 0;
                _packetIndex = 0;
                _packetCount = 0;
                _audioBits = 0;
                _totalSamples = 0;
                _headerBits = 0;
                _containerBits = 0;
                _wasteBits = 0;
            }
        }

        internal void SetSampleRate(int sampleRate)
        {
            lock (_lock)
            {
                _sampleRate = sampleRate;

                ResetStats();
            }
        }

        internal void AddPacket(int samples, int bits, int waste, int container)
        {
            lock (_lock)
            {
                if (samples >= 0)
                {
                    // audio packet
                    _audioBits += bits;
                    _wasteBits += waste;
                    _containerBits += container;
                    _totalSamples += samples;
                    _packetBits[_packetIndex] = bits + waste;
                    _packetSamples[_packetIndex] = samples;

                    if (++_packetIndex == 2)
                    {
                        _packetIndex = 0;
                    }
                }
                else
                {
                    // header packet
                    _headerBits += bits;
                    _wasteBits += waste;
                    _containerBits += container;
                }
            }
        }
    }
}
