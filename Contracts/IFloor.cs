using NVorbis.Ogg;

namespace NVorbis.Contracts
{
    public interface IFloor
    {
        void Init(Packet packet, int channels, int block0Size, int block1Size, Codebook[] codebooks);
        IFloorData Unpack(Packet packet, int blockSize, int channel);
        void Apply(IFloorData floorData, int blockSize, float[] residue);
    }
}
