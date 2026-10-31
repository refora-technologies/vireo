namespace Vireo.Services;

/// <summary>Where a tab stands in its life cycle, from most to least memory.</summary>
public enum TabLife
{
    /// <summary>Shown to the person now.</summary>
    Active,
    /// <summary>Hidden but fully alive.</summary>
    Background,
    /// <summary>Hidden and frozen: no CPU, memory trimmed, resumes instantly.</summary>
    Suspended,
    /// <summary>Hidden and freed: no page process at all; only the address and title are kept.</summary>
    Discarded,
}

public enum TabAction { None, Suspend, Discard }

/// <summary>What the policy needs to know about a tab to decide.</summary>
public readonly record struct TabFacts(bool IsPlayingAudio, bool IsLoading, bool HasAddress, bool IsPinned = false);

public sealed record SuspensionSettings(TimeSpan SuspendAfter, TimeSpan DiscardAfter, bool Enabled = true)
{
    public static SuspensionSettings Normal { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));

    /// <summary>For machines with little memory: freeze sooner and free sooner.</summary>
    public static SuspensionSettings LowEnd { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2));
}

/// <summary>Decides when a hidden tab is frozen and when it is freed. Pure, so it is easy to test.</summary>
public static class SuspensionPolicy
{
    public static TabAction Decide(TabLife life, TimeSpan hiddenFor, TabFacts tab, SuspensionSettings settings)
    {
        if (!settings.Enabled) return TabAction.None;
        if (life is TabLife.Active or TabLife.Discarded) return TabAction.None;
        // Never interrupt music, a page still loading, or a pinned tab.
        if (tab.IsPlayingAudio || tab.IsLoading || tab.IsPinned) return TabAction.None;

        if (tab.HasAddress && hiddenFor >= settings.DiscardAfter) return TabAction.Discard;
        if (life == TabLife.Background && hiddenFor >= settings.SuspendAfter) return TabAction.Suspend;
        return TabAction.None;
    }

    /// <summary>Low-end mode is chosen automatically on machines with 4 GB of memory or less.</summary>
    public static SuspensionSettings ForMemory(long totalBytes) =>
        totalBytes > 0 && totalBytes <= 4L * 1024 * 1024 * 1024 ? SuspensionSettings.LowEnd : SuspensionSettings.Normal;
}
