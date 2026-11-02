using Vireo.Services;
using Xunit;

namespace Vireo.Tests;

public class PopupTests
{
    private static PopupRequest Click(bool frame = false, bool other = false, bool ad = false) => new(true, frame, other, ad);
    private static PopupRequest NoClick(bool frame = false, bool other = false, bool ad = false) => new(false, frame, other, ad);

    [Fact]
    public void A_window_the_person_clicked_for_opens_even_with_a_sized_sign_in_window()
    {
        var policy = new PopupPolicy();
        Assert.Equal(PopupVerdict.Open, policy.Decide(Click(), blockPopups: true, blockAds: true));
    }

    [Fact]
    public void A_window_nobody_clicked_for_is_blocked_when_popups_are_blocked()
    {
        var policy = new PopupPolicy();
        Assert.Equal(PopupVerdict.BlockedNoClick, policy.Decide(NoClick(), true, true));
        Assert.Equal(PopupVerdict.Open, policy.Decide(NoClick(), blockPopups: false, blockAds: false));
    }

    [Fact]
    public void A_listed_ad_address_is_blocked_even_when_it_was_clicked_for()
    {
        var policy = new PopupPolicy();
        Assert.Equal(PopupVerdict.BlockedAd, policy.Decide(Click(ad: true), blockPopups: false, blockAds: true));
        Assert.Equal(PopupVerdict.Open, policy.Decide(Click(ad: true), blockPopups: true, blockAds: false));
    }

    [Fact]
    public void One_click_in_another_sites_frame_opens_one_window_not_a_chain_of_them()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var policy = new PopupPolicy(() => now);
        var request = Click(frame: true, other: true);
        Assert.Equal(PopupVerdict.Open, policy.Decide(request, true, true));
        now = now.AddMilliseconds(400);
        Assert.Equal(PopupVerdict.BlockedChain, policy.Decide(request, true, true));
        now = now.AddMilliseconds(400);
        Assert.Equal(PopupVerdict.BlockedChain, policy.Decide(request, true, true));
        now = now.Add(PopupPolicy.ChainWindow).AddMilliseconds(500);
        Assert.Equal(PopupVerdict.Open, policy.Decide(request, true, true)); // a fresh click later
    }

    [Fact]
    public void The_page_s_own_windows_are_never_treated_as_a_chain()
    {
        var policy = new PopupPolicy();
        Assert.Equal(PopupVerdict.Open, policy.Decide(Click(), true, true));
        Assert.Equal(PopupVerdict.Open, policy.Decide(Click(), true, true));
        Assert.Equal(PopupVerdict.Open, policy.Decide(Click(frame: true, other: false), true, true));
        Assert.Equal(PopupVerdict.Open, policy.Decide(Click(frame: true, other: false), true, true));
    }
}
