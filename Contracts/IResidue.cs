using NVorbis.Ogg;

namespace NVorbis.Contracts
{
    public interface IResidue
    {
        void Init(Packet packet, int channels, Codebook[] codebooks);
        void Decode(Packet packet, bool[] doNotDecodeChannel, int blockSize, float[][] buffer);
    }
}