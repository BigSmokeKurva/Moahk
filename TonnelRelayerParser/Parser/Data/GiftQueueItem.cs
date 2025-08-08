namespace Moahk.Parser.Data;

internal record GiftQueueItem
{
    public required string Name { get; init; }
    public required string Model { get; init; }
    public required double ModelPercent { get; init; }
    public required string Backdrop { get; init; }
    public required double BackdropPercent { get; init; }
    public required string CacheKey { get; init; }
}