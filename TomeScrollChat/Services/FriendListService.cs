using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace TomeScrollChat.Services;

/// <summary>
/// Looks up whether a chat message's sender is on the local player's friends list, for the
/// configurable emoji marker prefix. The game's <see cref="InfoProxyFriendList"/> entries don't
/// carry the character's name directly (only content id / world), so rather than enumerating and
/// resolving names ourselves, this uses the game's own <c>GetEntryByName</c> lookup - which needs a
/// world *row id*, resolved via the shared <see cref="WorldIdResolver"/>.
/// </summary>
public sealed unsafe class FriendListService
{
    private readonly WorldIdResolver worldIdResolver;
    private readonly IPluginLog log;

    public FriendListService(WorldIdResolver worldIdResolver, IPluginLog log)
    {
        this.worldIdResolver = worldIdResolver;
        this.log = log;

        try
        {
            // Best-effort: the friend list's native data may only be populated once the game has
            // fetched it - this nudges that along, but if the player has opened their own friend
            // list at least once this session it's likely already loaded regardless.
            InfoProxyFriendList.Instance()->RequestData();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to request friend list data");
        }

        RefreshFriendKeyCache();
    }

    /// <summary>Same as <see cref="IsFriend"/> but from an already-combined "Name@World" key.</summary>
    public bool IsFriendKey(string key)
    {
        var at = key.IndexOf('@');
        return at > 0 && IsFriend(key[..at], key[(at + 1)..]);
    }

    private HashSet<string> friendKeyCache = new();

    /// <summary>The current <see cref="friendKeyCache"/> snapshot, for callers that need to *enumerate*
    /// every friend (not just test one key) without calling <see cref="GetAllFriends"/> - a live native
    /// read - themselves. Added 2026-08-17: <see cref="FriendOnlineWatcherService.CheckFriends"/> used
    /// to call <see cref="GetAllFriends"/> a *second* time for "Watch all friends" mode, after already
    /// calling <see cref="RequestRefresh"/> that same cycle - landing squarely in the same
    /// request-response-being-applied race this cache exists to avoid, reported live as the watch list
    /// silently reading 0 friends (and therefore never notifying about anything) despite
    /// <see cref="RefreshFriendKeyCache"/> having just populated a perfectly good 45-entry cache
    /// moments earlier in the exact same method call. Reusing that cache instead of reading live again
    /// closes this gap.</summary>
    public IReadOnlyCollection<string> GetCachedFriendKeys() => friendKeyCache;

    /// <summary><b>Fixed 2026-08-17</b>: this used to call <see cref="TryGetContentId"/> (a live
    /// native <c>GetEntryByName</c> lookup) directly, every single time - which is every single
    /// message row and every whisper tab, every single ImGui frame (so dozens of times a second).
    /// Reported live as the friend marker icon/tab indicator visibly *flickering* on and off, in
    /// sync with <see cref="FriendOnlineWatcherService"/>'s periodic <see cref="RequestRefresh"/>
    /// (every 5s) - the native friend list data most likely goes through a brief invalid/rebuilding
    /// state while that request's response is being applied, and a live per-frame read has a real
    /// chance of landing exactly in that window. Now reads a cached snapshot (<see cref="friendKeyCache"/>)
    /// instead - a few seconds of staleness on a *display-only* marker is unnoticeable, unlike a
    /// flicker.
    /// <para><b>Fixed again, same day</b>: an earlier version of this cache refreshed itself lazily
    /// on a short TTL, checked from whatever ImGui frame happened to call <see cref="IsFriend"/> right
    /// as the TTL expired - which could itself land in the same "request response being applied"
    /// window purely by bad luck (just less often than every frame). Reported live as "some friends'
    /// sidebar marker periodically reloads" - a *partial* miss (some entries missing from an
    /// otherwise non-empty snapshot) that the old empty-set guard didn't catch, since it only refused
    /// a *fully* empty refresh. Now the cache is only ever refreshed by <see cref="RefreshFriendKeyCache"/>,
    /// called from a controlled point (<see cref="FriendOnlineWatcherService"/>'s own periodic tick,
    /// *before* that tick's <see cref="RequestRefresh"/> call) instead of arbitrary frame timing - by
    /// then the *previous* cycle's request has had the entire interval between ticks to settle, so the
    /// read is of genuinely stable data rather than racing a request that was just issued.</para></summary>
    public bool IsFriend(string name, string world)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
            return false;

        return friendKeyCache.Contains($"{name}@{world}");
    }

    /// <summary>Rebuilds <see cref="friendKeyCache"/> from a fresh <see cref="GetAllFriends"/> read -
    /// see <see cref="IsFriend"/>'s doc comment for why this is only ever called from a controlled
    /// timer (<see cref="FriendOnlineWatcherService"/>), never lazily from an arbitrary ImGui frame.
    /// Guards against a transient bad read blanking/thinning an otherwise-good cache: a refresh that
    /// comes back with *fewer* entries than before is treated as suspect and skipped entirely (kept
    /// simple - a real friend removal is rare and just waits for the next successful refresh, whereas
    /// a partial native read happening at the exact moment this runs is the actual failure mode this
    /// guards against).</summary>
    public void RefreshFriendKeyCache()
    {
        var fresh = GetAllFriends().Select(f => f.Key).ToHashSet();
        if (fresh.Count < friendKeyCache.Count)
            return;

        friendKeyCache = fresh;
    }

    /// <summary>The friend list entry's content id, if this player is a friend - lets
    /// <see cref="AdventurerPlateService"/> open their Adventurer Plate by content id (works
    /// regardless of whether they're actually rendered nearby, unlike the plain object-table lookup
    /// other features here have to fall back to), same as opening a plate from the friends list UI
    /// itself doesn't require them to be nearby either.</summary>
    public bool TryGetContentId(string name, string world, out ulong contentId)
    {
        contentId = 0;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
            return false;

        try
        {
            var worldId = worldIdResolver.Resolve(world);
            if (worldId == null)
                return false;

            var proxy = InfoProxyFriendList.Instance();
            if (proxy == null)
                return false;

            var entry = proxy->GetEntryByName(name, worldId.Value);
            if (entry == null || entry->ContentId == 0)
                return false;

            contentId = entry->ContentId;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: friend list lookup failed for {Name}@{World}", name, world);
            return false;
        }
    }

    /// <summary>Whether a friend is currently online - null if they're not a friend, or the lookup
    /// otherwise failed. <c>CharacterData.State</c> (confirmed via the metadata-reading technique) is
    /// an <see cref="InfoProxyCommonList.CharacterData.OnlineStatus"/> flags value, not a plain
    /// online/offline bool.
    /// <para><b>Fixed 2026-08-17</b>: the first version checked <c>(State &amp; OnlineStatus.Offline) == 0</c>,
    /// on the assumption <c>Offline</c> was a bit that gets set - reported live as showing every friend
    /// as online, including known-offline ones. Dumped the enum's actual literal values (this project's
    /// usual metadata-reading technique doesn't print constant values by default, so a small one-off
    /// tool was written just for this) and found <c>Offline = 0</c> - the *baseline* value, not a real
    /// bit, so ANDing against it is always <c>0</c> regardless of the real state (a no-op bug, not a
    /// wrong-bit bug). There's a genuine <c>Online</c> bit instead (<c>0x800000000000</c>), which is
    /// what's actually checked now.</para></summary>
    public bool? IsOnline(string name, string world)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
            return null;

        try
        {
            var worldId = worldIdResolver.Resolve(world);
            if (worldId == null)
                return null;

            var proxy = InfoProxyFriendList.Instance();
            if (proxy == null)
                return null;

            var entry = proxy->GetEntryByName(name, worldId.Value);
            if (entry == null || entry->ContentId == 0)
                return null;

            return (entry->State & InfoProxyCommonList.CharacterData.OnlineStatus.Online) != 0;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: online-status lookup failed for {Name}@{World}", name, world);
            return null;
        }
    }

    /// <summary>Whether a friend is currently in a duty (dungeon/raid/trial/etc.) - null on the same
    /// terms as <see cref="IsOnline"/>. <c>CharacterData.State</c>'s <c>OnlineStatus.InDuty</c> bit
    /// (confirmed via the metadata-reading technique, part of the same flags enum <see cref="IsOnline"/>
    /// already reads) - added per explicit user request for a notification when a friend enters/leaves
    /// a duty, since that's specifically when a <c>/tell</c> to them stops being deliverable ("the
    /// target does not currently reside in this world"-style failure). Deliberately not
    /// <c>SharingDuty</c>/<c>SimilarDuty</c> (which describe *this player's* relationship to the
    /// friend's duty, not whether the friend themselves is in one).</summary>
    public bool? IsInDuty(string name, string world)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
            return null;

        try
        {
            var worldId = worldIdResolver.Resolve(world);
            if (worldId == null)
                return null;

            var proxy = InfoProxyFriendList.Instance();
            if (proxy == null)
                return null;

            var entry = proxy->GetEntryByName(name, worldId.Value);
            if (entry == null || entry->ContentId == 0)
                return null;

            return (entry->State & InfoProxyCommonList.CharacterData.OnlineStatus.InDuty) != 0;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: duty-status lookup failed for {Name}@{World}", name, world);
            return null;
        }
    }

    /// <summary>Whether a friend is currently flagged "in another world" - null on the same terms as
    /// <see cref="IsOnline"/>. <c>CharacterData.State</c>'s <c>OnlineStatus.AnotherWorld</c> bit
    /// (confirmed via the metadata-reading technique, part of the same flags enum <see cref="IsOnline"/>/
    /// <see cref="IsInDuty"/> already read) - this is the native Friend List's own "In Another World"
    /// status text, a *separate* condition from <see cref="IsInDuty"/> that also blocks <c>/tell</c>
    /// delivery ("the target does not currently reside in this world"). Reported live: a friend showing
    /// "In Another World" natively still read <c>IsInDuty == false</c> here, since duty and cross-world
    /// reachability are genuinely different bits - <see cref="FriendOnlineWatcherService"/> checks both.</summary>
    public bool? IsInAnotherWorld(string name, string world)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
            return null;

        try
        {
            var worldId = worldIdResolver.Resolve(world);
            if (worldId == null)
                return null;

            var proxy = InfoProxyFriendList.Instance();
            if (proxy == null)
                return null;

            var entry = proxy->GetEntryByName(name, worldId.Value);
            if (entry == null || entry->ContentId == 0)
                return null;

            return (entry->State & InfoProxyCommonList.CharacterData.OnlineStatus.AnotherWorld) != 0;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: another-world-status lookup failed for {Name}@{World}", name, world);
            return null;
        }
    }

    /// <summary>Asks the game to re-fetch friend list data from the server right now, independent of
    /// whether the native Friend List addon is open/visible - <c>InfoProxyFriendList.RequestData()</c>
    /// (confirmed via the metadata tool: returns <c>Boolean</c>, and the type carries its own
    /// <c>NextRequestId</c>/<c>CurrentRequestId</c> byte pair, implying it's safe to call repeatedly -
    /// a request already in flight just doesn't get duplicated). <see cref="FriendOnlineWatcherService"/>
    /// calls this on a timer instead of relying on keeping the addon open (see that class's own notes:
    /// keeping the addon open+invisible was <b>confirmed live not to be enough on its own</b> - a
    /// friend's status only actually updated after the player manually opened the window, meaning
    /// whatever the addon does on open to request fresh data isn't triggered just by it existing
    /// off-screen). This is that same request, issued directly instead of hoping addon-open triggers
    /// it as a side effect.</summary>
    public void RequestRefresh()
    {
        try
        {
            InfoProxyFriendList.Instance()->RequestData();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to request fresh friend list data");
        }
    }

    /// <summary>Every current friend list entry as a "Name@World" key plus a display label (also
    /// "Name@World" - kept distinct from <see cref="Key"/> only so a future caller could show
    /// something friendlier without changing the key format everything else keys off of) - used to
    /// build the friend picker in Settings &gt; Players (<see cref="Configuration.FriendOnlineNotifyKeys"/>).
    /// Entries whose world id doesn't resolve to a name (shouldn't normally happen) are skipped rather
    /// than shown with a blank world.</summary>
    public List<(string Key, string DisplayName)> GetAllFriends()
    {
        var result = new List<(string, string)>();
        try
        {
            var proxy = InfoProxyFriendList.Instance();
            if (proxy == null)
                return result;

            foreach (var entry in proxy->CharDataSpan)
            {
                if (entry.ContentId == 0)
                    continue;

                var name = entry.NameString;
                if (string.IsNullOrEmpty(name))
                    continue;

                var worldName = worldIdResolver.ResolveName(entry.HomeWorld);
                if (string.IsNullOrEmpty(worldName))
                    continue;

                var key = $"{name}@{worldName}";
                result.Add((key, key));
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to enumerate the friend list");
        }

        return result;
    }
}
