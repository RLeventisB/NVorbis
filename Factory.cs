using System;
using NVorbis.Ogg;

namespace NVorbis.Contracts
{
    public static class Factory
    {
        public static IFloor CreateFloor(Packet packet)
        {
            var type = (int)packet.ReadBits(16);
            switch (type)
            {
                case 0: return new Floor0();
                case 1: return new Floor1();
                default: throw new System.IO.InvalidDataException("Invalid floor type!");
            }
        }
        public static IResidue CreateResidue(Packet packet)
        {
            var type = (int)packet.ReadBits(16);
            switch (type)
            {
                case 0: return new Residue0();
                case 1: return new Residue1();
                case 2: return new Residue2();
                default: throw new System.IO.InvalidDataException("Invalid residue type!");
            }
        }
    }
}
