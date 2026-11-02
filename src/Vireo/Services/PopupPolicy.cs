namespace Vireo.Services;

/// <summary>What a page asking for a new window looks like.</summary>
public sealed record PopupRequest(
    bool UserInitiated,
    /// <summary>It came from a frame inside the page (an embedded player, an ad) and not from the page itself.</summary>
    bool FromFrame,
    /// <summary>That frame belongs to another site than the page.</summary>
    bool FromOtherSite,
    /// <summary>A filter list says the address is an ad or tracker.</summary>
    bool ListedAsAd);

public enum PopupVerdict { Open, BlockedNoClick, BlockedAd, BlockedChain }

/// <summary>
/// Decides whether a page may open a new window. A window the person asked for with a click is allowed, which keeps
/// sign-in and payment pop-ups working; one nobody clicked for, one aimed at an ad network, and the second and third
/// window an embedded ad opens for a single click ("pop-unders") are not.
/// </summary>
public sealed class PopupPolicy(Func<DateTime>? clock = null)
{
    /// <summary>After one window opened by another site's frame, further ones within this time are the same click being reused.</summary>
    public static readonly TimeSpan ChainWindow = TimeSpan.FromSeconds(2);

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private DateTime _lastFromOtherSite = DateTime.MinValue;

    public PopupVerdict Decide(PopupRequest request, bool blockPopups, bool blockAds)
    {
        if (blockAds && request.ListedAsAd) return PopupVerdict.BlockedAd;
        if (!blockPopups) return PopupVerdict.Open;
        if (!request.UserInitiated) return PopupVerdict.BlockedNoClick;
        if (request.FromFrame && request.FromOtherSite)
        {
            var now = _clock();
            if (now - _lastFromOtherSite < ChainWindow) return PopupVerdict.BlockedChain;
            _lastFromOtherSite = now;
        }
        return PopupVerdict.Open;
    }
}
