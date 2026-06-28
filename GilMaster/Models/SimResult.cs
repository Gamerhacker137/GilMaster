namespace GilMaster.Models;

/// <summary>One recipe's outcome in a crafting-bot simulation run.</summary>
public sealed class SimResult
{
    public uint   ItemId   { get; init; }
    public string Name     { get; init; } = "";
    public string JobName  { get; init; } = "";
    public int    Level    { get; init; }
    public int    HqPercent { get; init; }
    public bool   Completed { get; init; }
    public int    Score    { get; init; }
    public string Outcome  { get; init; } = "";
}
