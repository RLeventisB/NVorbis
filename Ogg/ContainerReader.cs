﻿using System;
using System.Collections.Generic;
using System.IO;

namespace NVorbis.Ogg
{
    public delegate bool NewStreamHandler(PacketProvider packetProvider);
    public sealed class ContainerReader
    {
        private PageReader _reader;
        private readonly List<WeakReference<PacketProvider>> _packetProviders;
        private bool _foundStream;

        /// <summary>
        /// Gets or sets the callback to invoke when a new stream is encountered in the container.
        /// </summary>
        public NewStreamHandler NewStreamCallback;

        /// <summary>
        /// Returns a list of streams available from this container.
        /// </summary>
        public IReadOnlyList<PacketProvider> GetStreams()
        {
            var list = new List<PacketProvider>(_packetProviders.Count);
            for (var i = 0; i < _packetProviders.Count; i++)
            {
                if (_packetProviders[i].TryGetTarget(out var pp))
                {
                    list.Add(pp);
                }
                else
                {
                    list.RemoveAt(i);
                    --i;
                }
            }
            return list;
        }

        /// <summary>
        /// Gets whether the underlying stream can seek.
        /// </summary>
        public bool CanSeek { get; }
        /// <summary>
        /// Gets the number of bits in the container that are not associated with a logical stream.
        /// </summary>
        public long WasteBits => _reader.WasteBits;

        /// <summary>
        /// Gets the number of bits in the container that are strictly for framing of logical streams.
        /// </summary>
        public long ContainerBits => _reader.ContainerBits;

        /// <summary>
        /// Creates a new instance of <see cref="ContainerReader"/>.
        /// </summary>
        /// <param name="stream">The <see cref="Stream"/> to read.</param>
        /// <param name="closeOnDispose"><c>True</c> to close the stream when disposed, otherwise <c>false</c>.</param>
        /// <exception cref="ArgumentException"><paramref name="stream"/>'s <see cref="Stream.CanSeek"/> is <c>False</c>.</exception>
        public ContainerReader(Stream stream, bool closeOnDispose)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            _packetProviders = new List<WeakReference<PacketProvider>>();

            _reader = new PageReader(stream, closeOnDispose, ProcessNewStream);
        }

        /// <summary>
        /// Attempts to initialize the container.
        /// </summary>
        /// <returns><see langword="true"/> if successful, otherwise <see langword="false"/>.</returns>
        public bool TryInit()
        {
            return FindNextStream();
        }

        /// <summary>
        /// Finds the next new stream in the container.
        /// </summary>
        /// <returns><c>True</c> if a new stream was found, otherwise <c>False</c>.</returns>
        public bool FindNextStream()
        {
            _foundStream = false;
            while (_reader.ReadNextPage())
            {
                if (_foundStream)
                {
                    return true;
                }
            }
            return false;
        }

        private bool ProcessNewStream(PacketProvider packetProvider)
        {
            if (NewStreamCallback?.Invoke(packetProvider) ?? true)
            {
                _packetProviders.Add(new WeakReference<PacketProvider>(packetProvider));
                _foundStream = true;
                return true;
            }
            return false;
        }
        /// <summary>
        /// Cleans up
        /// </summary>
        public void Dispose()
        {
            _reader?.Dispose();
            _reader = null;
        }
    }
}
