namespace ZArchiveSharp.Pipeline;

/// <summary>
/// One batch request: items, target directory, mode, source-retention flag,
/// collision policy and worker count. A UI-facing model that expands to
/// <see cref="ZarPipelineOptions"/>.
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
    public ZarPipelineOptions ToPipelineOptions()
    {
        return new ZarPipelineOptions
        {
            MaxDegreeOfParallelism = MaxWorkers,
            CollisionPolicy = Policy,
            DeleteSourceOnSuccess = !KeepOriginals,
        };
    }
}
