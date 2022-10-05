namespace NVorbis.Contracts
{
    public interface IFloorData
    {
        bool ExecuteChannel { get; }
        bool ForceEnergy { get; set; }
        bool ForceNoEnergy { get; set; }
    }
}
