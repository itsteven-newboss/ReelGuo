namespace ReelGuo.Data.Catalog;

/// <summary>
/// Which episodes cost coins. A series has a per-episode price (0 = free series) and a number of free leading
/// episodes; an episode can also be marked free on its own (a promotional episode). With monetization switched off
/// everything is free.
/// </summary>
public static class Pricing
{
    public const int MaxEpisodePrice = 10_000;
    public const int DefaultFreeEpisodes = 3;

    /// <summary>Coins needed to watch the episode, or 0 when it is free for everyone.</summary>
    public static int EpisodePrice(bool monetization, int seriesPrice, int freeEpisodes, int episodeNumber, bool episodeIsFree) =>
        !monetization || seriesPrice <= 0 || episodeIsFree || episodeNumber <= freeEpisodes ? 0 : seriesPrice;
}
