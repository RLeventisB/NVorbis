using System;
using System.IO;
using System.Text;
using NVorbis.Contracts;
using NVorbis.Ogg;

namespace NVorbis
{
    /// <summary>
    /// Implements a stream decoder for Vorbis data.
    /// </summary>
    public sealed class StreamDecoder
    {
        private PacketProvider _packetProvider;
        private readonly StreamStats _stats;
        private byte _channels;
        private int _sampleRate, _block0Size, _block1Size, _modeFieldBits, _prevPacketStart, _prevPacketEnd, _prevPacketStop;
        private Mode[] _modes;
        private string _vendor;
        private string[] _comments;
        private TagData _tags;
        private long _currentPosition;
        private bool _hasPosition, _eosFound;
        private float[][] _nextPacketBuf, _prevPacketBuf;
        /// <summary>
        /// Creates a new instance of <see cref="StreamDecoder"/>.
        /// </summary>
        /// <param name="packetProvider">A <see cref="PacketProvider"/> instance for the decoder to read from.</param>
        public StreamDecoder(PacketProvider packetProvider)
        {
            _packetProvider = packetProvider ?? throw new ArgumentNullException(nameof(packetProvider));

            _stats = new StreamStats();

            _currentPosition = 0L;

            var packet = _packetProvider.PeekNextPacket();
            if (!ProcessHeaderPackets(packet))
            {
                _packetProvider = null;
                packet.Reset();

                throw GetInvalidStreamException(packet);
            }
        }
        private static Exception GetInvalidStreamException(Packet packet)
        {
            try
            {
                // let's give our caller some helpful hints about what they've encountered...
                var header = packet.ReadBits(64);
                if (header == 0x646165487375704ful)
                {
                    return new ArgumentException("Found OPUS bitstream.");
                }
                else if ((header & 0xFF) == 0x7F)
                {
                    return new ArgumentException("Found FLAC bitstream.");
                }
                else if (header == 0x2020207865657053ul)
                {
                    return new ArgumentException("Found Speex bitstream.");
                }
                else if (header == 0x0064616568736966ul)
                {
                    // ugh...  we need to add support for this in the container reader
                    return new ArgumentException("Found Skeleton metadata bitstream.");
                }
                else if ((header & 0xFFFFFFFFFFFF00ul) == 0x61726f65687400ul)
                {
                    return new ArgumentException("Found Theora bitsream.");
                }
                return new ArgumentException("Could not find Vorbis data to decode.");
            }
            finally
            {
                packet.Reset();
            }
        }

        #region Init

        private bool ProcessHeaderPackets(Packet packet)
        {
            if (!ProcessHeaderPacket(packet, LoadStreamHeader, _ => _packetProvider.GetNextPacket().Done()))
            {
                return false;
            }

            if (!ProcessHeaderPacket(_packetProvider.GetNextPacket(), LoadComments, pkt => pkt.Done()))
            {
                return false;
            }

            if (!ProcessHeaderPacket(_packetProvider.GetNextPacket(), LoadBooks, pkt => pkt.Done()))
            {
                return false;
            }

            _currentPosition = 0;
            ResetDecoder();
            return true;
        }

        private static bool ProcessHeaderPacket(Packet packet, Func<Packet, bool> processAction, Action<Packet> doneAction)
        {
            if (packet != null)
            {
                try
                {
                    return processAction(packet);
                }
                finally
                {
                    doneAction(packet);
                }
            }
            return false;
        }

        static private readonly byte[] PacketSignatureStream = { 0x01, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73, 0x00, 0x00, 0x00, 0x00 };
        static private readonly byte[] PacketSignatureComments = { 0x03, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73 };
        static private readonly byte[] PacketSignatureBooks = { 0x05, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73 };

        static private bool ValidateHeader(Packet packet, byte[] expected)
        {
            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] != packet.ReadBits(8))
                {
                    return false;
                }
            }
            return true;
        }
        static private string ReadString(Packet packet)
        {
            var len = (int)packet.ReadBits(32);

            if (len == 0)
            {
                return string.Empty;
            }

            var buf = new byte[len];
            var cnt = packet.Read(buf, 0, len);
            if (cnt < len)
            {
                throw new InvalidDataException("Could not read full string!");
            }
            return Encoding.UTF8.GetString(buf);
        }
        private bool LoadStreamHeader(Packet packet)
        {
            if (!ValidateHeader(packet, PacketSignatureStream))
            {
                return false;
            }

            _channels = (byte)packet.ReadBits(8);
            _sampleRate = (int)packet.ReadBits(32);
            UpperBitrate = (int)packet.ReadBits(32);
            NominalBitrate = (int)packet.ReadBits(32);
            LowerBitrate = (int)packet.ReadBits(32);

            _block0Size = 1 << (int)packet.ReadBits(4);
            _block1Size = 1 << (int)packet.ReadBits(4);

            if (NominalBitrate == 0 && UpperBitrate > 0 && LowerBitrate > 0)
            {
                NominalBitrate = (UpperBitrate + LowerBitrate) / 2;
            }

            _stats.SetSampleRate(_sampleRate);
            _stats.AddPacket(-1, packet.BitsRead, packet.BitsRemaining, packet.ContainerOverheadBits);

            return true;
        }

        private bool LoadComments(Packet packet)
        {
            if (!ValidateHeader(packet, PacketSignatureComments))
            {
                return false;
            }

            _vendor = ReadString(packet);

            _comments = new string[packet.ReadBits(32)];
            for (var i = 0; i < _comments.Length; i++)
            {
                _comments[i] = ReadString(packet);
            }

            _stats.AddPacket(-1, packet.BitsRead, packet.BitsRemaining, packet.ContainerOverheadBits);

            return true;
        }
        private bool LoadBooks(Packet packet)
        {
            if (!ValidateHeader(packet, PacketSignatureBooks))
            {
                return false;
            }

            var huffman = new Huffman();

            // read the books
            var books = new Codebook[packet.ReadBits(8) + 1];
            for (var i = 0; i < books.Length; i++)
            {
                books[i] = new Codebook();
                books[i].Init(packet, huffman);
            }

            // Vorbis never used this feature, so we just skip the appropriate number of bits
            var times = (int)packet.ReadBits(6) + 1;
            packet.SkipBits(16 * times);

            // read the floors
            var floors = new IFloor[packet.ReadBits(6) + 1];
            for (var i = 0; i < floors.Length; i++)
            {
                floors[i] = Factory.CreateFloor(packet);
                floors[i].Init(packet, _channels, _block0Size, _block1Size, books);
            }

            // read the residues
            var residues = new IResidue[packet.ReadBits(6) + 1];
            for (var i = 0; i < residues.Length; i++)
            {
                residues[i] = Factory.CreateResidue(packet);
                residues[i].Init(packet, _channels, books);
            }

            // read the mappings
            var mappings = new Mapping[packet.ReadBits(6) + 1];
            for (var i = 0; i < mappings.Length; i++)
            {
                mappings[i] = new Mapping();
                packet.SkipBits(16);
                mappings[i].Init(packet, _channels, floors, residues, new Mdct());
            }

            // read the modes
            _modes = new Mode[packet.ReadBits(6) + 1];
            for (var i = 0; i < _modes.Length; i++)
            {
                _modes[i] = new Mode();
                _modes[i].Init(packet, _channels, _block0Size, _block1Size, mappings);
            }

            // verify the closing bit
            if (!packet.ReadBit()) throw new InvalidDataException("Book packet did not end on correct bit!");

            // save off the number of bits to read to determine packet mode
            _modeFieldBits = Utils.Ilog(_modes.Length - 1);

            _stats.AddPacket(-1, packet.BitsRead, packet.BitsRemaining, packet.ContainerOverheadBits);

            return true;
        }

        #endregion

        #region State Change

        private void ResetDecoder()
        {
            _prevPacketBuf = null;
            _prevPacketStart = 0;
            _prevPacketEnd = 0;
            _prevPacketStop = 0;
            _nextPacketBuf = null;
            _eosFound = false;
            _hasPosition = false;
        }

        #endregion

        #region Decoding

        /// <summary>
        /// Reads samples into the specified buffer.
        /// </summary>
        /// <param name="buffer">The buffer to read the samples into.</param>
        /// <param name="offset">The index to start reading samples into the buffer.</param>
        /// <param name="count">The number of samples that should be read into the buffer.  Must be a multiple of <see cref="Channels"/>.</param>
        /// <returns>The number of samples read into the buffer.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the buffer is too small or <paramref name="offset"/> is less than zero.</exception>
        /// <remarks>The data populated into <paramref name="buffer"/> is interleaved by channel in normal PCM fashion: Left, Right, Left, Right, Left, Right</remarks>
        public int Read(Span<float> buffer, int offset, int count)
        {
            // save off value to track when we're done with the request
            var idx = offset;
            var tgt = offset + count;

            // try to fill the buffer; drain the last buffer if EOS, resync, bad packet, or parameter change
            while (idx < tgt)
            {
                // if we don't have any more valid data in the current packet, read in the next packet
                if (_prevPacketStart == _prevPacketEnd)
                {
                    if (_eosFound)
                    {
                        _nextPacketBuf = null;
                        _prevPacketBuf = null;

                        // no more samples, so just return
                        break;
                    }

                    if (!ReadNextPacket((idx - offset) / _channels, out var samplePosition))
                    {
                        // drain the current packet (the windowing will fade it out)
                        _prevPacketEnd = _prevPacketStop;
                    }

                    // if we need to pick up a position, and the packet had one, apply the position now
                    if (samplePosition.HasValue && !_hasPosition)
                    {
                        _hasPosition = true;
                        _currentPosition = samplePosition.Value - (_prevPacketEnd - _prevPacketStart) - (idx - offset) / _channels;
                    }
                }

                // we read out the valid samples from the previous packet
                var copyLen = Math.Min((tgt - idx) / _channels, _prevPacketEnd - _prevPacketStart);
                if (copyLen > 0)
                {
                    int cnt = copyLen;
                    for (; cnt > 0; _prevPacketStart++, cnt--)
                    {
                        for (var ch = 0; ch < _channels; ch++)
                        {
                            buffer[idx++] = Utils.ClipValue(_prevPacketBuf[ch][_prevPacketStart]);
                        }
                    }
                }
            }

            // update the count of floats written
            count = idx - offset;

            // update the position
            _currentPosition += count / _channels;

            // return count of floats written
            return count;
        }
        private bool ReadNextPacket(int bufferedSamples, out long? samplePosition)
        {
            // decode the next packet now so we can start overlapping with it
            var curPacket = DecodeNextPacket(out var startIndex, out var validLen, out var totalLen, out var isEndOfStream, out samplePosition, out var bitsRead, out var bitsRemaining, out var containerOverheadBits);
            _eosFound |= isEndOfStream;
            if (curPacket == null)
            {
                _stats.AddPacket(0, bitsRead, bitsRemaining, containerOverheadBits);
                return false;
            }

            // if we get a max sample position, back off our valid length to match
            if (samplePosition.HasValue && isEndOfStream)
            {
                var actualEnd = _currentPosition + bufferedSamples + validLen - startIndex;
                var diff = (int)(samplePosition.Value - actualEnd);
                if (diff < 0)
                {
                    validLen += diff;
                }
            }

            // start overlapping (if we don't have an previous packet data, just loop and the previous packet logic will handle things appropriately)
            if (_prevPacketEnd > 0)
            {
                // overlap the first samples in the packet with the previous packet, then loop
                int prevStart = _prevPacketStart, nextStart = startIndex, prevLen = _prevPacketStop;
                for (; prevStart < prevLen; prevStart++, nextStart++)
                {
                    for (var c = 0; c < _channels; c++)
                    {
                        curPacket[c][nextStart] += _prevPacketBuf[c][prevStart];
                    }
                }
                _prevPacketStart = startIndex;
            }
            else if (_prevPacketBuf == null)
            {
                // first packet, so it doesn't have any good data before the valid length
                _prevPacketStart = validLen;
            }

            // update stats
            _stats.AddPacket(validLen - _prevPacketStart, bitsRead, bitsRemaining, containerOverheadBits);

            // keep the old buffer so the GC doesn't have to reallocate every packet
            _nextPacketBuf = _prevPacketBuf;

            // save off our current packet's data for the next pass
            _prevPacketEnd = validLen;
            _prevPacketStop = totalLen;
            _prevPacketBuf = curPacket;
            return true;
        }
        private float[][] DecodeNextPacket(out int packetStartindex, out int packetValidLength, out int packetTotalLength, out bool isEndOfStream, out long? samplePosition, out int bitsRead, out int bitsRemaining, out int containerOverheadBits)
        {
            Packet packet = _packetProvider.GetNextPacket();
            if (packet == null)
            {
                // no packet? we're at the end of the stream
                isEndOfStream = true;
            }
            else
            {
                // if the packet is flagged as the end of the stream, we can safely mark _eosFound
                isEndOfStream = packet.IsEndOfStream;

                // resync... that means we've probably lost some data; pick up a new position
                if (packet.IsResync)
                {
                    _hasPosition = false;
                }

                // grab the container overhead now, since the read won't affect it
                containerOverheadBits = packet.ContainerOverheadBits;

                // make sure the packet starts with a 0 bit as per the spec
                if (!packet.ReadBit())
                {
                    // if we get here, we should have a good packet; decode it and add it to the buffer
                    var mode = _modes[(int)packet.ReadBits(_modeFieldBits)];
                    if (_nextPacketBuf == null)
                    {
                        _nextPacketBuf = new float[_channels][];
                        for (var i = 0; i < _channels; i++)
                        {
                            _nextPacketBuf[i] = new float[_block1Size];
                        }
                    }
                    if (mode.Decode(packet, _nextPacketBuf, out packetStartindex, out packetValidLength, out packetTotalLength))
                    {
                        // per the spec, do not decode more samples than the last granulePosition
                        samplePosition = packet.GranulePosition;
                        bitsRead = packet.BitsRead;
                        bitsRemaining = packet.BitsRemaining;
                        packet.Done();
                        return _nextPacketBuf;
                    }
                }
            }
            packetStartindex = 0;
            packetValidLength = 0;
            packetTotalLength = 0;
            samplePosition = null;
            bitsRead = 0;
            bitsRemaining = 0;
            containerOverheadBits = 0;
            return null;
        }

        #endregion

        #region Seeking

        /// <summary>
        /// Seeks the stream by the specified duration.
        /// </summary>
        /// <param name="timePosition">The relative time to seek to.</param>
        /// <param name="seekOrigin">The reference point used to obtain the new position.</param>
        public void SeekTo(TimeSpan timePosition, SeekOrigin seekOrigin = SeekOrigin.Begin)
        {
            SeekTo((long)(SampleRate * timePosition.TotalSeconds), seekOrigin);
        }

        /// <summary>
        /// Seeks the stream by the specified sample count.
        /// </summary>
        /// <param name="samplePosition">The relative sample position to seek to.</param>
        /// <param name="seekOrigin">The reference point used to obtain the new position.</param>
        public void SeekTo(long samplePosition, SeekOrigin seekOrigin = SeekOrigin.Begin)
        {
            if (_packetProvider == null) throw new ObjectDisposedException(nameof(StreamDecoder));

            switch (seekOrigin)
            {
                case SeekOrigin.Begin:
                    // no-op
                    break;
                case SeekOrigin.Current:
                    samplePosition = SamplePosition - samplePosition;
                    break;
                case SeekOrigin.End:
                    samplePosition = TotalSamples - samplePosition;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(seekOrigin));
            }

            if (samplePosition < 0) throw new ArgumentOutOfRangeException(nameof(samplePosition));

            int rollForward;
            if (samplePosition == 0)
            {
                // short circuit for the looping case...
                _packetProvider.SeekTo(0, 0, GetPacketGranules);
                rollForward = 0;
            }
            else
            {
                // seek the stream to the correct position
                var pos = _packetProvider.SeekTo(samplePosition, 1, GetPacketGranules);
                rollForward = (int)(samplePosition - pos);
            }

            // clear out old data
            ResetDecoder();
            _hasPosition = true;

            // read the pre-roll packet
            if (!ReadNextPacket(0, out _))
            {
                // we'll use this to force ReadSamples to fail to read
                _eosFound = true;
                if (_packetProvider.GetGranuleCount() != samplePosition)
                {
                    throw new InvalidOperationException("Could not read pre-roll packet!  Try seeking again prior to reading more samples.");
                }
                _prevPacketStart = _prevPacketStop;
                _currentPosition = samplePosition;
                return;
            }

            // read the actual packet
            if (!ReadNextPacket(0, out _))
            {
                ResetDecoder();
                // we'll use this to force ReadSamples to fail to read
                _eosFound = true;
                throw new InvalidOperationException("Could not read pre-roll packet!  Try seeking again prior to reading more samples.");
            }

            // adjust our indexes to match what we want
            _prevPacketStart += rollForward;
            _currentPosition = samplePosition;
        }

        private int GetPacketGranules(Packet curPacket, bool isLastInPage)
        {
            // if it's a resync, there's not any audio data to return
            if (curPacket.IsResync) return 0;

            // if it's not an audio packet, there's no audio data (seems obvious, though...)
            if (curPacket.ReadBit()) return 0;

            // OK, let's ask the appropriate mode how long this packet actually is

            // first we need to know which mode...
            var modeIdx = (int)curPacket.ReadBits(_modeFieldBits);

            // if we got an invalid mode value, we can't decode any audio data anyway...
            if (modeIdx < 0 || modeIdx >= _modes.Length) return 0;

            return _modes[modeIdx].GetPacketSampleCount(curPacket, isLastInPage);
        }

        #endregion

        /// <summary>
        /// Cleans up this instance.
        /// </summary>
        public void Dispose()
        {
            (_packetProvider as IDisposable)?.Dispose();
            _packetProvider = null;
        }

        #region Properties

        /// <summary>
        /// Gets the number of channels in the stream.
        /// </summary>
        public int Channels => _channels;

        /// <summary>
        /// Gets the sample rate of the stream.
        /// </summary>
        public int SampleRate => _sampleRate;

        /// <summary>
        /// Gets the upper bitrate limit for the stream, if specified.
        /// </summary>
        public int UpperBitrate { get; private set; }

        /// <summary>
        /// Gets the nominal bitrate of the stream, if specified.  May be calculated from <see cref="LowerBitrate"/> and <see cref="UpperBitrate"/>.
        /// </summary>
        public int NominalBitrate { get; private set; }

        /// <summary>
        /// Gets the lower bitrate limit for the stream, if specified.
        /// </summary>
        public int LowerBitrate { get; private set; }

        /// <summary>
        /// Gets the tag data from the stream's header.
        /// </summary>
        public TagData Tags => _tags ??= new TagData(_vendor, _comments);

        /// <summary>
        /// Gets the total duration of the decoded stream.
        /// </summary>
        public TimeSpan TotalTime => TimeSpan.FromSeconds((double)TotalSamples / _sampleRate);

        /// <summary>
        /// Gets the total number of samples in the decoded stream.
        /// </summary>
        public long TotalSamples => _packetProvider?.GetGranuleCount() ?? throw new ObjectDisposedException(nameof(StreamDecoder));

        /// <summary>
        /// Gets or sets the current time position of the stream.
        /// </summary>
        public TimeSpan TimePosition
        {
            get => TimeSpan.FromSeconds((double)_currentPosition / _sampleRate);
            set => SeekTo(value);
        }

        /// <summary>
        /// Gets or sets the current sample position of the stream.
        /// </summary>
        public long SamplePosition
        {
            get => _currentPosition;
            set => SeekTo(value);
        }

        /// <summary>
        /// Gets whether the decoder has reached the end of the stream.
        /// </summary>
        public bool IsEndOfStream => _eosFound && _prevPacketBuf == null;

        /// <summary>
        /// Gets the <see cref="StreamStats"/> instance for this stream.
        /// </summary>
        public StreamStats Stats => _stats;

        #endregion
    }
}
