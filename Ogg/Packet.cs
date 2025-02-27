﻿using System;
using System.Collections.Generic;

namespace NVorbis.Ogg
{
    public class Packet
    {
        /// <summary>
        /// Gets the number of container overhead bits associated with this packet.
        /// </summary>
        public int ContainerOverheadBits { get; set; }

        /// <summary>
        /// Gets the granule position of the packet, if known.
        /// </summary>
        public long? GranulePosition { get; set; }

        /// <summary>
        /// Gets whether this packet occurs immediately following a loss of sync in the stream.
        /// </summary>
        public bool IsResync
        {
            get => GetFlag(PacketFlags.IsResync);
            set => SetFlag(PacketFlags.IsResync, value);
        }

        /// <summary>
        /// Gets whether this packet did not read its full data.
        /// </summary>
        public bool IsShort
        {
            get => GetFlag(PacketFlags.IsShort);
            private set => SetFlag(PacketFlags.IsShort, value);
        }

        /// <summary>
        /// Gets whether the packet is the last packet of the stream.
        /// </summary>
        public bool IsEndOfStream
        {
            get => GetFlag(PacketFlags.IsEndOfStream);
            set => SetFlag(PacketFlags.IsEndOfStream, value);
        }

        /// <summary>
        /// Gets the number of bits read from the packet.
        /// </summary>
        public int BitsRead => _readBits;

        /// <summary>
        /// Gets the number of bits left in the packet.
        /// </summary>
        public int BitsRemaining => TotalBits - _readBits;
        public bool GetFlag(PacketFlags flag) => _packetFlags.HasFlag(flag);
        public void SetFlag(PacketFlags flag, bool value)
        {
            if (value)
            {
                _packetFlags |= flag;
            }
            else
            {
                _packetFlags &= ~flag;
            }
        }
        private readonly IReadOnlyList<int> _dataParts;
        private readonly PacketProvider _packetReader;                    // IntPtr
        ulong _bitBucket;
        int _dataCount, _dataIndex, _dataOfs, _bitCount, _readBits;
        byte _overflowBits;
        PacketFlags _packetFlags;
        Memory<byte> _data;
        internal Packet(IReadOnlyList<int> dataParts, PacketProvider packetReader, Memory<byte> initialData)
        {
            _dataParts = dataParts;
            _packetReader = packetReader;
            _data = initialData;
        }
        /// <summary>
        /// Gets the total number of bits in the packet.
        /// </summary>

        public int TotalBits => (_dataCount + _data.Length) * 8;
        public int ReadNextByte()
        {
            if (_dataIndex == _dataParts.Count) return -1;

            var b = _data.Span[_dataOfs];

            if (++_dataOfs == _data.Length)
            {
                _dataOfs = 0;
                _dataCount += _data.Length;
                if (++_dataIndex < _dataParts.Count)
                {
                    _data = _packetReader.GetPacketData(_dataParts[_dataIndex]);
                }
                else
                {
                    _data = Memory<byte>.Empty;
                }
            }

            return b;
        }
        /// <summary>
        /// Resets the read buffers to the beginning of the packet.
        /// </summary>
        public void Reset()
        {
            _dataIndex = 0;
            _dataOfs = 0;
            if (_dataParts.Count > 0)
            {
                _data = _packetReader.GetPacketData(_dataParts[0]);
            }
            _bitBucket = 0;
            _bitCount = 0;
            _overflowBits = 0;
            _readBits = 0;
        }
        /// <summary>
        /// Frees the buffers and caching for the packet instance.
        /// </summary>
        public void Done()
        {
            _packetReader?.InvalidatePacketCache(this);
        }
        public ulong ReadBits(int count)
        {
            var value = TryPeekBits(count, out _);

            SkipBits(count);

            return value;
        }
        /// <summary>
        /// Advances the read position by the the specified number of bits.
        /// </summary>
        /// <param name="count">The number of bits to skip reading.</param>
        public void SkipBits(int count)
        {
            if (count > 0)
            {
                if (_bitCount > count)
                {
                    // we still have bits left over...
                    if (count > 63)
                    {
                        _bitBucket = 0;
                    }
                    else
                    {
                        _bitBucket >>= count;
                    }
                    if (_bitCount > 64)
                    {
                        var overflowCount = _bitCount - 64;
                        _bitBucket |= (ulong)_overflowBits << (_bitCount - count - overflowCount);

                        if (overflowCount > count)
                        {
                            // ugh, we have to keep bits in overflow
                            _overflowBits >>= count;
                        }
                    }

                    _bitCount -= count;
                    _readBits += count;
                }
                else if (_bitCount == count)
                {
                    _bitBucket = 0UL;
                    _bitCount = 0;
                    _readBits += count;
                }
                else //  _bitCount < count
                {
                    // we have to move more bits than we have available...
                    count -= _bitCount;
                    _readBits += _bitCount;
                    _bitCount = 0;
                    _bitBucket = 0;

                    while (count > 8)
                    {
                        if (ReadNextByte() == -1)
                        {
                            count = 0;
                            IsShort = true;
                            break;
                        }
                        count -= 8;
                        _readBits += 8;
                    }

                    if (count > 0)
                    {
                        var temp = ReadNextByte();
                        if (temp == -1)
                        {
                            IsShort = true;
                        }
                        else
                        {
                            _bitBucket = (ulong)(temp >> count);
                            _bitCount = 8 - count;
                            _readBits += count;
                        }
                    }
                }
            }
        }
        /// <summary>
        /// Attempts to read the specified number of bits from the packet.  Does not advance the read position.
        /// </summary>
        /// <param name="count">The number of bits to read.</param>
        /// <param name="bitsRead">Outputs the actual number of bits read.</param>
        /// <returns>The value of the bits read.</returns>
        public ulong TryPeekBits(int count, out int bitsRead)
        {
            if (count == 0)
            {
                bitsRead = 0;
                return 0UL;
            }

            ulong value;
            while (_bitCount < count)
            {
                var val = ReadNextByte();
                if (val == -1)
                {
                    bitsRead = _bitCount;
                    value = _bitBucket;
                    return value;
                }
                _bitBucket = (ulong)(val & 0xFF) << _bitCount | _bitBucket;
                _bitCount += 8;

                if (_bitCount > 64)
                {
                    _overflowBits = (byte)(val >> (72 - _bitCount));
                }
            }

            value = _bitBucket;

            if (count < 64)
            {
                value &= (1UL << count) - 1;
            }

            bitsRead = count;
            return value;
        }
    }
    /// <summary>
    /// Defines flags to apply to the current packet
    /// </summary>
    [Flags]
    public enum PacketFlags : byte
    {
        /// <summary>
        /// Packet is first since reader had to resync with stream.
        /// </summary>
        IsResync = 0x01,
        /// <summary>
        /// Packet is the last in the logical stream.
        /// </summary>
        IsEndOfStream = 0x02,
        /// <summary>
        /// Packet does not have all its data available.
        /// </summary>
        IsShort = 0x04,
        /// <summary>
        /// Flag for use by inheritors.
        /// </summary>
        User0 = 0x08,
        /// <summary>
        /// Flag for use by inheritors.
        /// </summary>
        User1 = 0x10,
        /// <summary>
        /// Flag for use by inheritors.
        /// </summary>
        User2 = 0x20,
        /// <summary>
        /// Flag for use by inheritors.
        /// </summary>
        User3 = 0x40,
        /// <summary>
        /// Flag for use by inheritors.
        /// </summary>
        User4 = 0x80,
    }
}