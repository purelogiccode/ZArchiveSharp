namespace ZARSharp.Pipeline;

/// <summary>
/// run (items, target, workers, mode, keep-originals, policy). Ports the
/// </summary>
public sealed record ZarBatchRequest(
    IReadOnlyList<string> Items,
    string TargetDirectory,
    ZarProcessMode Mode = ZarProcessMode.Auto,
    bool KeepOriginals = true,
    ZarCollisionPolicy Policy = ZarCollisionPolicy.Fail,
    int MaxWorkers = 4)
{
    /// <summary>Expands to <see cref="ZarPipelineOptions"/>.</summary>
    public ZarPipelineOptions ToPipelineOptions() => new()
    {
        MaxDegreeOfParallelism = MaxWorkers,
        CollisionPolicy = Policy,
        DeleteSourceOnSuccess = !KeepOriginals,
    };
}
