using System;

namespace NVorbis.Contracts.Ogg
{
    [Flags]
    public enum PageFlags
    {
        None = 0,
        ContinuesPacket = 1,
        BeginningOfStream = 2,
        EndOfStream = 4,
    }
}
