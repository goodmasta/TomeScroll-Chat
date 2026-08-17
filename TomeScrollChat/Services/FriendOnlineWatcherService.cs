using System;
using System.Linq;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services;

/// <summary>
/// Watches whichever friends <see cref="Configuration.FriendOnlineNotifyAll"/>/
/// <see cref="Configuration.FriendOnlineNotifyKeys"/> select (see <see cref="FriendListService.IsOnline"/>
/// for the underlying data source) and pops a <see cref="NotificationService"/> toast the moment one's
/// online/offline state flips - entirely inert while <see cref="Configuration.FriendOnlineNotifyEnabled"/>
/// is off.
///
/// <para>Checked every <see cref="CheckInterval"/> via <see cref="IFramework.Update"/> (not every single
/// frame - online status changes rarely enough that a few seconds of latency is unnoticeable, and
/// re-reading the whole friend list 60 times a second for "Watch all friends" would be wasteful).
/// <b>2026-08-17, per explicit user request</b>: the very first check after this service starts (or
/// after a friend first becomes watchable) treats an unseen key as *offline* rather than recording a
/// silent baseline - so a friend who's already online the first time they're checked immediately
/// fires a notification, rather than only for a state observed to change afterward (the original
/// design deliberately avoided that "spam on startup" case, but in practice a plugin reload - very
/// common during dev testing - wipes the baseline every time, which made it look like notifications
/// just didn't work at all).</para>
///
/// <para><b>Opens the native Friend List addon once, on login</b> - <b>confirmed live (2026-08-17)</b>
/// that the native list UI only fully materializes/populates every entry (all 45 friends, not just a
/// partial batch) after a real layout pass, which requires it to actually have been shown at least
/// once. An earlier version also forced it invisible immediately after opening (to avoid it being
/// visually intrusive) - <b>removed the same day</b> once that was found to be exactly what was
/// starving it of that layout pass, undercounting friends (45 -&gt; 19). A second earlier version
/// force-reopened it every time the player closed it via <c>AddonEvent.PostHide</c>, on the theory
/// that it needed to *stay* open continuously for status to keep refreshing - <b>also removed the same
/// day</b>, once the player confirmed live that closing it after that one initial population still
/// left status updates working fine (they're driven by <see cref="FriendListService.RequestRefresh"/>
/// on its own timer, once the underlying entries are loaded - see <see cref="CheckFriends"/>). The
/// addon is simply left alone now once opened - the player can close it and it stays closed;
/// <c>/tomescroll friends</c> (<see cref="ShowOnScreen"/>) is the only way to bring it back afterward.
/// <b>Also fixed 2026-08-17</b>: that same periodic <c>RequestRefresh</c> visibly disrupted the
/// player's own interaction with the native window while it was open (the list reloading/resetting
/// mid-use) - <see cref="CheckFriends"/> now skips issuing it entirely whenever either addon is
/// currently visible, resuming automatically once both are closed again.</para>
/// </summary>
public sealed unsafe class FriendOnlineWatcherService : IDisposable
{
    private const string FriendListAddonName = "FriendList";

    /// <summary>"FriendList" is only the inner list/filter panel - the surrounding window chrome and
    /// tab switcher (Party Members/Friend List/Blacklist/Player Search) is a *separate* addon,
    /// <c>AddonSocial</c> (confirmed via the metadata tool - it owns
    /// <c>PartyMembersRadioButton</c>/<c>FriendListRadioButton</c>/<c>BlacklistRadioButton</c>/
    /// <c>PlayerSearchRadioButton</c>). Reported live: hiding only "FriendList" left "Social" (the
    /// outer window) visible and untouched. Both are now managed together as a pair - hiding one
    /// without the other only ever hides half of what the player actually sees when they open this
    /// from the game's own Social icon.</summary>
    private static readonly string[] ManagedAddonNames = { FriendListAddonName, "Social" };

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FriendListShowDelay = TimeSpan.FromSeconds(3);

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly FriendListService friendListService;
    private readonly NotificationService notificationService;

    private readonly Dictionary<string, bool> lastKnownOnline = new();
    private DateTime lastCheck = DateTime.MinValue;
    private DateTime? pendingShowAt;

    public FriendOnlineWatcherService(IFramework framework, IClientState clientState, IGameGui gameGui, IPluginLog log, Configuration configuration, FriendListService friendListService, NotificationService notificationService)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.gameGui = gameGui;
        this.log = log;
        this.configuration = configuration;
        this.friendListService = friendListService;
        this.notificationService = notificationService;

        framework.Update += OnFrameworkUpdate;
        clientState.Login += OnLogin;

        // Covers a dev-reload (or any plugin load) happening while already logged in - IClientState.Login
        // only fires for a login that happens *after* this service starts watching for it.
        if (clientState.IsLoggedIn)
            ScheduleOpen(FriendListShowDelay);
    }

    private void OnLogin() => ScheduleOpen(FriendListShowDelay);

    private void ScheduleOpen(TimeSpan delay)
    {
        if (!configuration.FriendOnlineNotifyEnabled)
            return;

        pendingShowAt = DateTime.UtcNow + delay;
    }

    /// <summary>"/tomescroll friends" - brings the Friend List (and the surrounding Social window it
    /// lives in) to the front, whether that means un-hiding an already-open-but-closed instance or
    /// creating it fresh - just calls the same <see cref="OpenAddons"/> the login-driven auto-open
    /// uses, since <c>ShowAddon()</c> already handles both cases in one call.</summary>
    public void ShowOnScreen() => OpenAddons();

    private bool wasEnabled;

    private void OnFrameworkUpdate(IFramework _)
    {
        // The feature just got turned off (in Settings) - restore normal native behaviour instead of
        // leaving the addon permanently open off-screen with nothing left to bring it back or ever
        // hide it again.
        if (wasEnabled && !configuration.FriendOnlineNotifyEnabled)
        {
            try
            {
                AgentFriendlist.Instance()->HideAddon();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "TomeScrollChat: failed to close the Friend List window after disabling the feature");
            }
        }
        wasEnabled = configuration.FriendOnlineNotifyEnabled;

        HandlePendingOpen();

        if (!configuration.FriendOnlineNotifyEnabled)
            return;

        if (DateTime.UtcNow - lastCheck < CheckInterval)
            return;
        lastCheck = DateTime.UtcNow;

        CheckFriends();
    }

    private void HandlePendingOpen()
    {
        if (pendingShowAt is not { } showAt || DateTime.UtcNow < showAt)
            return;

        pendingShowAt = null;
        OpenAddons();
    }

    /// <summary><c>AgentFriendlist.ShowAddon()</c> only ever creates/opens "FriendList" - never
    /// "Social" alongside it (confirmed live: it could make "FriendList" visible but never "Social",
    /// because "Social" simply didn't exist as a loaded addon yet unless the player had opened it
    /// manually at least once). There's no strongly-typed <c>AgentSocial</c> class in
    /// FFXIVClientStructs, but <c>AgentId.Social</c> does exist as its own entry (confirmed via the
    /// metadata tool) - <c>AgentModule.GetAgentByInternalId</c> returns the generic
    /// <c>AgentInterface*</c> for it, which still exposes <c>ShowAddon()</c>/<c>HideAddon()</c> same as
    /// every other agent. Calling both here is what lets this reliably bring back "Social" too, not
    /// just "FriendList" - and <c>ShowAddon()</c> itself already handles "doesn't exist yet" (creates
    /// it) and "exists but closed" (shows it again) in one call, so this same method works for both
    /// the initial login-driven open (<see cref="HandlePendingOpen"/>) and a manual re-open
    /// (<see cref="ShowOnScreen"/>) after the player closes it via its own close button.</summary>
    private void OpenAddons()
    {
        try
        {
            AgentFriendlist.Instance()->ShowAddon();

            var socialAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Social);
            if (socialAgent != null)
                socialAgent->ShowAddon();

            log.Info("TomeScrollChat: opened the Friend List and Social addons");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to open the Friend List window");
        }
    }

    /// <summary>Issues a fresh <see cref="FriendListService.RequestRefresh"/> for the *next* cycle to
    /// read, every <see cref="CheckInterval"/> - keeping the Friend List addon open on its own wasn't
    /// enough to keep status fresh (confirmed live 2026-08-17: status stayed stuck until the player
    /// manually opened the window), so this issues the request directly instead of relying on that as
    /// a side effect. The addon is still kept open (now visibly, see this class's own doc comment)
    /// since a real layout pass also turned out to matter for the *list itself* being fully populated.
    /// <para><b>Every live native read in this method - <see cref="FriendListService.RefreshFriendKeyCache"/>
    /// and each <see cref="FriendListService.IsOnline"/> call in the loop below - happens *before*
    /// <see cref="FriendListService.RequestRefresh"/>, never after.</b> This was fixed twice in a row
    /// the same day at two different read sites (first <c>GetAllFriends</c> for "Watch all", then
    /// <c>IsOnline</c> itself) before generalizing to this ordering rule: issuing a request appears to
    /// put the native friend list through a brief invalid/rebuilding window, and *any* live read
    /// landing in that window - not just one specific call - comes back empty/null. Rather than keep
    /// chasing individual read sites, <c>RequestRefresh</c> is now called dead last, so every read this
    /// cycle uses data that's had a full <see cref="CheckInterval"/> to settle from the *previous*
    /// cycle's request, and this cycle's own request is only ever read by the *next* cycle.</para></summary>
    private void CheckFriends()
    {
        friendListService.RefreshFriendKeyCache();

        var watchedKeys = configuration.FriendOnlineNotifyAll
            ? friendListService.GetCachedFriendKeys()
            : configuration.FriendOnlineNotifyKeys;

        foreach (var key in watchedKeys)
        {
            var at = key.IndexOf('@');
            if (at <= 0)
                continue;

            var name = key[..at];
            var world = key[(at + 1)..];
            var isOnline = friendListService.IsOnline(name, world);
            if (isOnline == null)
                continue;

            // Fixed 2026-08-17, per explicit user request: this used to treat the very first
            // observation of any key as a silent baseline (recording whatever the real state was
            // without notifying) - specifically so an already-online friend at startup didn't
            // immediately spam a notification. Reported live as "no notification ever comes" often
            // enough (a plugin reload, extremely common during dev testing, wipes lastKnownOnline
            // entirely, so the very next real transition after any reload kept landing on this silent
            // branch) that the user asked to flip the default instead: an unseen key is now treated as
            // *offline* rather than "unknown", so a friend who's already online the first time they're
            // checked immediately fires "is now online" - trades the original "don't spam on startup"
            // goal for "never silently miss a change", which is what was actually wanted.
            var wasOnline = lastKnownOnline.GetValueOrDefault(key, false);
            if (wasOnline != isOnline.Value)
            {
                log.Info("TomeScrollChat: friend-watch detected a change for {Key}: {Was} -> {Now}", key, wasOnline, isOnline.Value);
                notificationService.Show(
                    isOnline.Value ? $"{name} is now online." : $"{name} has gone offline.",
                    NotificationSeverity.Info);
            }

            lastKnownOnline[key] = isOnline.Value;
        }

        // Fixed 2026-08-17, per explicit user request: skipped entirely while the player has
        // FriendList/Social actually open - reported live that this periodic RequestData() call was
        // visibly disrupting their own interaction with the native window (the list reloading/resetting
        // out from under them while they were trying to use it). While it's open, the player can just
        // look at the native list directly for up-to-date status anyway, so there's nothing lost by not
        // polling in the background during that window - polling resumes automatically the moment
        // either addon closes again.
        if (!IsFriendListWindowVisible())
            friendListService.RequestRefresh();
    }

    private bool IsFriendListWindowVisible()
    {
        foreach (var name in ManagedAddonNames)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
            if (addon != null && addon->IsVisible)
                return true;
        }

        return false;
    }

    /// <summary>"/tomescroll frienddebug" - lets testing this feature not depend on catching a real
    /// friend actually logging in/out, or needing a second account: (1) dumps every friend list entry
    /// this plugin can currently see, with its resolved online/offline state, to <c>/xllog</c> - compare
    /// this against what the native Friend List UI shows the same friends as, to check the read side
    /// independently of any transition; (2) unconditionally shows a notification for every currently-
    /// watched friend's *current* state (not gated on it having changed, unlike <see cref="CheckFriends"/>'s
    /// normal path) - confirms the notification pipeline itself end to end without waiting for one.</summary>
    public void DebugCheckAndNotify()
    {
        var allFriends = friendListService.GetAllFriends();
        log.Info("TomeScrollChat: friend list debug dump ({Count} entries)", allFriends.Count);
        foreach (var (key, _) in allFriends)
        {
            var at = key.IndexOf('@');
            if (at <= 0)
                continue;

            var isOnline = friendListService.IsOnline(key[..at], key[(at + 1)..]);
            log.Info("TomeScrollChat:   {Key} - {Status}", key, isOnline switch
            {
                true => "online",
                false => "offline",
                null => "unknown (lookup failed)",
            });
        }

        var watchedKeys = configuration.FriendOnlineNotifyAll
            ? allFriends.Select(f => f.Key).ToList()
            : configuration.FriendOnlineNotifyKeys.ToList();

        if (watchedKeys.Count == 0)
        {
            notificationService.Show("Friend debug: no friends are currently watched (check Settings > Players).", NotificationSeverity.Warning);
            return;
        }

        foreach (var key in watchedKeys)
        {
            var at = key.IndexOf('@');
            if (at <= 0)
                continue;

            var name = key[..at];
            var isOnline = friendListService.IsOnline(name, key[(at + 1)..]);
            notificationService.Show(
                isOnline switch
                {
                    true => $"Friend debug: {name} is online.",
                    false => $"Friend debug: {name} is offline.",
                    null => $"Friend debug: {name} - lookup failed (not a friend, or friend list data not loaded yet).",
                },
                NotificationSeverity.Info);
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.Login -= OnLogin;
    }
}
