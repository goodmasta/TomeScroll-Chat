using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services.CrossDc;

/// <summary>One decrypted 1:1 cross-DC chat message, in-memory only for now (not persisted to disk the
/// way native chat history is - a later increment can fold this into <c>ChatHistoryService</c> if this
/// feature graduates beyond Settings into an actual chat surface). <see cref="SenderUserId"/> is whoever
/// actually sent it (this identity's own <see cref="CrossDcRelayService.UserId"/> for an outgoing one).</summary>
public sealed record CrossDcChatMessage(string SenderUserId, string Text, DateTimeOffset Timestamp, bool IsOutgoing);

/// <summary>Snapshot of one group this identity belongs to - the public, read-only view of
/// <see cref="RelayIdentityService"/>'s own mutable <see cref="GroupState"/>, deliberately not exposing
/// <see cref="GroupState.KeyBase64"/>/<see cref="GroupState.Epoch"/> (crypto material, nobody outside
/// <see cref="CrossDcRelayService"/> needs it). <see cref="OwnerId"/> empty means "not known yet" - see
/// <see cref="CrossDcRelayService.Groups"/>'s own doc comment for why that can happen.</summary>
public sealed record CrossDcGroupInfo(string Id, string Name, string OwnerId, IReadOnlyList<string> Members, IReadOnlyList<string> ModeratorIds);

/// <summary>
/// Owns the cross-DC relay connection lifecycle - resolves which server to use from
/// <see cref="Configuration.CrossDcRelayMode"/>, connects, completes the Ed25519 challenge/response
/// handshake (matching TomeScrollRelay's own <c>RelayHandshake</c>), and keeps a background receive
/// loop running while connected. Also the client side of the relay's admin tooling (claiming admin
/// rights, pulling server logs, reading the connected-client count) and 1:1 pairing (creating/redeeming
/// invite codes, tracking who this identity has paired with) - each just a request/response pair (or, for
/// pairing, an occasional unsolicited push - see <see cref="DispatchFrame"/>'s <c>paired</c> case) over
/// the same socket, not a whole feature area of its own. End-to-end encrypted 1:1 messaging (see
/// <see cref="SendChatMessageAsync"/>) is also owned here; <c>Plugin</c> bridges it into an actual tab via
/// <see cref="ContactAdded"/>/<see cref="MessageAppended"/>.
///
/// <para>Entirely inert while <see cref="Configuration.CrossDcRelayMode"/> is
/// <see cref="RelayMode.Disabled"/> - <see cref="RelayIdentityService"/> (which is what actually
/// generates/persists the identity keypair) is only ever constructed the first time it's actually
/// needed, not eagerly in this type's own constructor, per the explicit "nothing happens at all while
/// the feature is off" requirement.</para>
///
/// <para><see cref="Reconcile"/> is the only connection entry point - call it once at plugin startup and
/// again every time Settings &gt; Cross-DC changes anything (mode, self-hosted URL). It figures out on
/// its own whether that means connecting, disconnecting, or switching servers, and is a no-op if the
/// live connection already matches what the config now says.</para>
///
/// <para><b>No automatic reconnect yet</b> - if the connection drops (network blip, relay restart),
/// <see cref="IsConnected"/> goes false and stays false until something calls <see cref="Reconcile"/>
/// again (e.g. the player revisiting Settings). Deliberately deferred rather than built speculatively:
/// this is still identity/connection-level scope, not yet anything that depends on staying connected
/// unattended - proper reconnect-with-backoff belongs with whatever increment actually needs it
/// (message send/receive).</para>
/// </summary>
public sealed class CrossDcRelayService : IDisposable
{
    private readonly string configDirectory;
    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly Func<string?> getLocalPlayerName;
    private readonly object gate = new();

    // Serializes entire admin-tooling request/response *cycles*, not just the send - a second call
    // blocks until the first one's response (success or error) actually arrives. The relay's error
    // frames carry no correlation ID, so this is what makes attributing one to the right request
    // (AdminError vs LogsError vs StatsError) unambiguous: with this in place, at most one of these
    // requests is ever outstanding at a time, so there's nothing to disambiguate in the first place -
    // simpler and more robust than trying to track/queue multiple in-flight requests.
    private readonly SemaphoreSlim requestLock = new(1, 1);

    private RelayIdentityService? identity;
    private CancellationTokenSource? runCts;
    private Task? runTask;
    private string? connectedUrl;
    private ClientWebSocket? socket;

    private enum PendingAction
    {
        None, ClaimAdmin, GetLogs, GetStats, CreateInvite, RedeemInvite, Unpair, BlockUser, UnblockUser,
        CreateGroup, CreateGroupInvite, RedeemGroupInvite, GetGroupKeyDirectory, SetGroupMemberKey, GetGroupMemberKey,
        PromoteModerator, DemoteModerator, TransferGroupOwnership, KickGroupMember, LeaveGroup,
    }
    private PendingAction pendingAction = PendingAction.None;
    private TaskCompletionSource<bool>? pendingCompletion;

    // In-memory only, see CrossDcChatMessage's own doc comment. Keyed by the *other* party's userId.
    private readonly Dictionary<string, List<CrossDcChatMessage>> messagesByContact = new();

    // Same, keyed by groupId instead of a contact's userId.
    private readonly Dictionary<string, List<CrossDcChatMessage>> messagesByGroup = new();

    // The result of the most recent getGroupKeyDirectory call - read immediately after GetGroupKeyDirectoryAsync
    // returns true, never held onto longer than that single caller's use of it (requestLock guarantees
    // nothing else can be mid-flight and overwrite it in between, same reasoning as every other *Error
    // field's freshness contract).
    private Dictionary<string, string> lastGroupKeyDirectory = new();

    /// <summary>Fired for every contact this identity has ever paired with on the current relay, once on
    /// each fresh connect (so a tab exists for each of them even if the player never opens Settings) and
    /// again the moment a brand-new pairing completes live. <c>Plugin</c> subscribes to this to create/
    /// find the matching tab (<see cref="Services.TabManager.GetOrCreateCrossDcTab"/>) - deliberately
    /// fires for *already-known* contacts too on every reconnect rather than only new ones, so a tab the
    /// player closed comes back the same self-healing way a whisper tab does.</summary>
    public event Action<string>? ContactAdded;

    /// <summary>Fired every time a message (incoming or outgoing) is added to <see cref="GetMessages"/>'s
    /// backing store - <c>Plugin</c> subscribes to this to push it into <c>TabMessageBuffer</c>/
    /// <c>ChatHistoryService</c> the same way <c>ChatCaptureService.MessageRouted</c> does for native
    /// chat, so the cross-DC tab actually shows it live instead of only on next open.</summary>
    public event Action<string, CrossDcChatMessage>? MessageAppended;

    /// <summary>Fired when a 1:1 contact stops being one - this identity unpaired, blocked them (which
    /// always unpairs first server-side), or observed the other side doing either of those live.
    /// <c>Plugin</c> uses this to close the matching tab, same as <see cref="GroupLeft"/> does for groups.</summary>
    public event Action<string>? ContactRemoved;

    /// <summary>Fired for every group this identity currently belongs to, once on each fresh connect
    /// (self-healing tabs, same reasoning as <see cref="ContactAdded"/>) and again the moment a brand-new
    /// <c>createGroup</c>/<c>redeemGroupInvite</c> succeeds.</summary>
    public event Action<string>? GroupJoined;

    /// <summary>Fired when this identity stops being a member of a group it previously tracked - it left
    /// voluntarily, was kicked, or (as the group's last member) deleted it by leaving. <c>Plugin</c> uses
    /// this to close/remove the matching tab.</summary>
    public event Action<string>? GroupLeft;

    /// <summary>The group-chat mirror of <see cref="MessageAppended"/>.</summary>
    public event Action<string, CrossDcChatMessage>? GroupMessageAppended;

    public CrossDcRelayService(string configDirectory, Configuration configuration, IPluginLog log, Func<string?> getLocalPlayerName)
    {
        this.configDirectory = configDirectory;
        this.configuration = configuration;
        this.log = log;
        this.getLocalPlayerName = getLocalPlayerName;
    }

    public bool IsConnected { get; private set; }

    /// <summary>This installation's relay user ID, once the handshake completes - null while
    /// disconnected/connecting.</summary>
    public string? UserId { get; private set; }

    /// <summary>Set on the most recent connection failure, for Settings to surface - cleared the moment
    /// a connection actually succeeds.</summary>
    public string? LastError { get; private set; }

    /// <summary>True once admin rights are established on the *current* connection - either freshly via
    /// <c>claimAdmin</c> this session, or recalled from a previous successful claim on this same relay
    /// URL (see <see cref="RelayIdentityService.IsKnownAdmin"/>; the relay's own grant is durable, so a
    /// fresh reconnect doesn't need the bootstrap key again once it's already been used once).</summary>
    public bool IsAdmin { get; private set; }

    /// <summary>Reason the most recent <c>claimAdmin</c> attempt failed - null if none yet, or the last
    /// one succeeded.</summary>
    public string? AdminError { get; private set; }

    /// <summary>Most recent <c>getLogs</c> result, oldest first (matches the relay's own ordering) -
    /// empty until the first successful fetch.</summary>
    public IReadOnlyList<string> Logs { get; private set; } = Array.Empty<string>();

    /// <summary>Reason the most recent <c>getLogs</c> attempt failed (e.g. not an admin) - null if none
    /// yet, or the last one succeeded.</summary>
    public string? LogsError { get; private set; }

    /// <summary>Most recent <c>getStats</c> result - how many clients are connected to *this* relay
    /// instance (see TomeScrollRelay's own caveat: per-instance, not a global total once there's more
    /// than one). Null until the first successful fetch.</summary>
    public int? ConnectedClients { get; private set; }

    /// <summary>Reason the most recent <c>getStats</c> attempt failed - null if none yet, or the last
    /// one succeeded.</summary>
    public string? StatsError { get; private set; }

    /// <summary>Most recently created invite code, good for 10 minutes and single-use (see
    /// TomeScrollRelay's own <c>PairingCodeRegistry</c>) - null until the first successful
    /// <see cref="CreateInviteAsync"/>.</summary>
    public string? InviteCode { get; private set; }

    /// <summary>Reason the most recent <c>createInvite</c> attempt failed - e.g. the per-user pending-
    /// invite cap. Null if none yet, or the last one succeeded.</summary>
    public string? InviteError { get; private set; }

    /// <summary>Every user ID this identity has ever successfully paired with on the *current* relay
    /// (empty while disconnected) - refreshed on connect and every time a new pairing completes, whether
    /// this client redeemed a code itself or someone else redeemed one of its invites.</summary>
    public IReadOnlyList<string> Contacts { get; private set; } = Array.Empty<string>();

    /// <summary>Reason the most recent <c>redeemInvite</c> attempt failed (invalid/expired code, blocked,
    /// etc.) - null if none yet, or the last one succeeded.</summary>
    public string? PairError { get; private set; }

    /// <summary>Every user ID this identity has ever blocked on the *current* relay (empty while
    /// disconnected) - see <see cref="RelayIdentityService.GetBlocked"/>'s own doc comment for why this
    /// can only ever reflect blocks this exact client performed, not a server-side query.</summary>
    public IReadOnlyList<string> Blocked { get; private set; } = Array.Empty<string>();

    /// <summary>Reason the most recent <c>unpair</c>/<c>blockUser</c>/<c>unblockUser</c> attempt failed -
    /// shared across the three, same reasoning as <see cref="GroupError"/>.</summary>
    public string? RelationshipError { get; private set; }

    /// <summary>Every group this identity currently belongs to on the *current* relay (empty while
    /// disconnected) - refreshed on connect and after every group membership/role change, whether this
    /// client caused it or just observed a push about it. <see cref="CrossDcGroupInfo.OwnerId"/> is empty
    /// ("unknown") until this client has actually been told who it is - creating the group (this client is
    /// the owner), or a live <c>groupOwnershipTransferred</c> - since the relay has no "get group info"
    /// query to otherwise learn it from (see <see cref="RefreshGroupsSnapshot"/>'s own doc comment).</summary>
    public IReadOnlyList<CrossDcGroupInfo> Groups { get; private set; } = Array.Empty<CrossDcGroupInfo>();

    /// <summary>Most recently created group-invite code (see <see cref="CreateGroupInviteAsync"/>) - same
    /// shape/lifetime as <see cref="InviteCode"/>, just for a group instead of a 1:1 pairing.</summary>
    public string? GroupInviteCode { get; private set; }

    /// <summary>Reason the most recent group-related action failed - shared across every group action
    /// (create/invite/join/promote/demote/transfer/kick/leave) rather than one field each, since Settings
    /// only ever has one such action in flight at a time and reads this immediately after awaiting it.</summary>
    public string? GroupError { get; private set; }

    /// <summary>Reconciles the live connection against the current config. Safe to call any time,
    /// including repeatedly with nothing changed (a no-op in that case).</summary>
    public void Reconcile()
    {
        var targetUrl = ResolveUrl();

        lock (gate)
        {
            if (targetUrl == connectedUrl && (targetUrl == null || runTask != null))
                return;

            StopLocked();

            if (targetUrl == null)
                return;

            identity ??= new RelayIdentityService(configDirectory, log);
            connectedUrl = targetUrl;
            runCts = new CancellationTokenSource();
            runTask = Task.Run(() => RunAsync(targetUrl, identity, runCts.Token));
        }
    }

    /// <summary>Sends <c>claimAdmin</c> with the given bootstrap key - the relay's own admin tooling
    /// (see TomeScrollRelay's <c>AdminBootstrap</c>) prints a fresh single-use key to its own log on
    /// every startup. Result also surfaces as <see cref="IsAdmin"/>/<see cref="AdminError"/> for Settings
    /// to poll across frames, since it's typically called fire-and-forget from a button click.</summary>
    public Task<bool> ClaimAdminAsync(string key, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.ClaimAdmin, new RelayClaimAdminRequest("claimAdmin", key), cancellationToken);

    /// <summary>Sends <c>getLogs</c> - admin-only server-side, so this only ever succeeds after
    /// <see cref="IsAdmin"/> is true. Result also surfaces as <see cref="Logs"/>/<see cref="LogsError"/>.</summary>
    public Task<bool> RequestLogsAsync(int lines, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.GetLogs, new RelayGetLogsRequest("getLogs", lines), cancellationToken);

    /// <summary>Sends <c>getStats</c> - admin-only server-side. Result also surfaces as
    /// <see cref="ConnectedClients"/>/<see cref="StatsError"/>.</summary>
    public Task<bool> RequestStatsAsync(CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.GetStats, new RelayGetStatsRequest("getStats"), cancellationToken);

    /// <summary>Sends <c>createInvite</c> - result also surfaces as <see cref="InviteCode"/>/
    /// <see cref="InviteError"/>. The code is meant to be shared entirely out-of-band (voice, Discord,
    /// whatever) with whoever should redeem it - never sent through this relay or native game chat, per
    /// the design decision that excluded in-game chat from the invite flow entirely.</summary>
    public Task<bool> CreateInviteAsync(CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.CreateInvite, new RelayCreateInviteRequest("createInvite"), cancellationToken);

    /// <summary>Sends <c>redeemInvite</c> with a code someone else created and shared out-of-band.
    /// Result also surfaces as <see cref="Contacts"/> (gains the new pairing)/<see cref="PairError"/>.</summary>
    public Task<bool> RedeemInviteAsync(string code, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.RedeemInvite, new RelayRedeemInviteRequest("redeemInvite", code), cancellationToken);

    /// <summary>Sends <c>unpair</c> - either side of a 1:1 pairing can end it unilaterally, no
    /// confirmation needed from the other side. Result surfaces as <see cref="Contacts"/> (loses the
    /// pairing)/<see cref="RelationshipError"/>; <see cref="ContactRemoved"/> fires either way (this
    /// identity's own unpair, or the other side unpairing/blocking - both arrive as the same <c>unpaired</c>
    /// frame, see <see cref="DispatchFrame"/>).</summary>
    public Task<bool> UnpairAsync(string contactUserId, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.Unpair, new RelayUnpairRequest("unpair", contactUserId), cancellationToken);

    /// <summary>Sends <c>blockUser</c> - always unpairs first if they were paired (matching the relay's
    /// own behavior, see TomeScrollRelay's <c>RelayConnectionHandler.BlockUserAsync</c>), and stops them
    /// from pairing with this identity again via a fresh invite code. Result surfaces as
    /// <see cref="Blocked"/> (gains the block)/<see cref="Contacts"/> (loses the pairing, if there was
    /// one)/<see cref="RelationshipError"/>.</summary>
    public Task<bool> BlockUserAsync(string contactUserId, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.BlockUser, new RelayBlockUserRequest("blockUser", contactUserId), cancellationToken);

    /// <summary>Sends <c>unblockUser</c> - only lifts the block, doesn't restore any pairing that existed
    /// before it (a fresh invite code is needed to pair again, same as pairing from scratch). Result
    /// surfaces as <see cref="Blocked"/> (loses the block)/<see cref="RelationshipError"/>.</summary>
    public Task<bool> UnblockUserAsync(string contactUserId, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.UnblockUser, new RelayUnblockUserRequest("unblockUser", contactUserId), cancellationToken);

    /// <summary>Re-sends this identity's current character name (and X25519 public key) to
    /// <paramref name="contactUserId"/> - the manual "refresh name" action, for when it's changed since
    /// the last announcement (a character rename, or simply logging in as someone else) and the automatic
    /// paths (right after pairing, or a send discovering no key yet) haven't fired again on their own.
    /// Best-effort/fire-and-forget, same as every other keyAnnounce - there's nothing to await a result
    /// of, the contact's tab/messages just start showing the new name once it arrives.</summary>
    public Task RefreshMyNameAsync(string contactUserId, CancellationToken cancellationToken = default) =>
        SendKeyAnnounceAsync(contactUserId, cancellationToken);

    /// <summary>Sends <c>createGroup</c> - this identity becomes the new group's owner. Generates and
    /// keeps the group's symmetric chat key entirely locally (no relay round-trip needed for the creator's
    /// own copy - only *other* members' copies ever need sealing/distribution, see
    /// <see cref="DistributeGroupKeyToMemberAsync"/>). Result surfaces as <see cref="Groups"/> (gains the
    /// new group)/<see cref="GroupError"/>.</summary>
    public Task<bool> CreateGroupAsync(string name, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.CreateGroup, new RelayCreateGroupRequest("createGroup", name, identity?.EncryptionPublicKeyBase64), cancellationToken);

    /// <summary>Sends <c>createGroupInvite</c> - owner-only server-side. Result surfaces as
    /// <see cref="GroupInviteCode"/>/<see cref="GroupError"/>.</summary>
    public Task<bool> CreateGroupInviteAsync(string groupId, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.CreateGroupInvite, new RelayCreateGroupInviteRequest("createGroupInvite", groupId), cancellationToken);

    /// <summary>Sends <c>redeemGroupInvite</c> with a code the group's owner created and shared
    /// out-of-band. Result surfaces as <see cref="Groups"/> (gains the new membership)/
    /// <see cref="GroupError"/> - this identity's own copy of the group's key isn't included in the join
    /// response (the relay only ever hands out sealed keys via <c>getGroupMemberKey</c>/
    /// <c>groupKeyRotated</c>, see <see cref="ApplySealedGroupKey"/>), so the group's messages may briefly
    /// show as undecryptable until an online owner/moderator seals one for this identity.</summary>
    public Task<bool> RedeemGroupInviteAsync(string code, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.RedeemGroupInvite, new RelayRedeemGroupInviteRequest("redeemGroupInvite", code, identity?.EncryptionPublicKeyBase64), cancellationToken);

    /// <summary>Sends <c>getGroupMemberKey</c> - fetches (and unseals/caches, see
    /// <see cref="ApplySealedGroupKey"/>) this identity's own current sealed copy of the group's key. Any
    /// member can call this any time; mainly useful right after joining, or on reconnect for a group this
    /// identity still has no key for.</summary>
    public Task<bool> GetGroupMemberKeyAsync(string groupId, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.GetGroupMemberKey, new RelayGetGroupMemberKeyRequest("getGroupMemberKey", groupId), cancellationToken);

    /// <summary>Sends <c>getGroupKeyDirectory</c> - any member can fetch the public-key directory. Result
    /// is stashed in <see cref="lastGroupKeyDirectory"/> for whichever internal caller (
    /// <see cref="DistributeGroupKeyToMemberAsync"/>/<see cref="RekeyAndDistributeAsync"/>) just awaited
    /// this, per the field's own doc comment.</summary>
    private Task<bool> GetGroupKeyDirectoryAsync(string groupId, CancellationToken cancellationToken) =>
        SendAndAwaitAsync(PendingAction.GetGroupKeyDirectory, new RelayGetGroupKeyDirectoryRequest("getGroupKeyDirectory", groupId), cancellationToken);

    /// <summary>Sends <c>setGroupMemberKey</c> - owner/moderator-only server-side. Internal: always called
    /// as part of <see cref="DistributeGroupKeyToMemberAsync"/>/<see cref="RekeyAndDistributeAsync"/>,
    /// never directly from UI (there's nothing meaningful for a person to type in for a "sealed key").</summary>
    private Task<bool> SetGroupMemberKeyAsync(string groupId, string userId, string sealedKeyBase64, long epoch, CancellationToken cancellationToken) =>
        SendAndAwaitAsync(PendingAction.SetGroupMemberKey, new RelaySetGroupMemberKeyRequest("setGroupMemberKey", groupId, userId, sealedKeyBase64, epoch), cancellationToken);

    /// <summary>Sends <c>promoteModerator</c> - owner-only server-side, capped at 10 moderators per group
    /// (see TomeScrollRelay's own <c>GroupRegistry</c>). Result surfaces as an updated
    /// <see cref="Groups"/> entry's <see cref="CrossDcGroupInfo.ModeratorIds"/>/<see cref="GroupError"/>.</summary>
    public Task<bool> PromoteModeratorAsync(string groupId, string userId, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.PromoteModerator, new RelayPromoteModeratorRequest("promoteModerator", groupId, userId), cancellationToken);

    /// <summary>Sends <c>demoteModerator</c> - owner-only server-side.</summary>
    public Task<bool> DemoteModeratorAsync(string groupId, string userId, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.DemoteModerator, new RelayDemoteModeratorRequest("demoteModerator", groupId, userId), cancellationToken);

    /// <summary>Sends <c>transferGroupOwnership</c> - owner-only server-side, target must already be a
    /// member. The outgoing owner keeps their membership, just becomes a regular member (moderator status
    /// isn't automatically granted in return - see TomeScrollRelay's own <c>GroupRegistry.SetOwnerAsync</c>).</summary>
    public Task<bool> TransferGroupOwnershipAsync(string groupId, string newOwnerId, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.TransferGroupOwnership, new RelayTransferGroupOwnershipRequest("transferGroupOwnership", groupId, newOwnerId), cancellationToken);

    /// <summary>Sends <c>kickGroupMember</c> - owner or moderator only server-side (a moderator can only
    /// kick regular members, not the owner or another moderator). On success, rotates the group's key and
    /// redistributes it to every remaining member so the kicked member loses read access to future
    /// messages - see <see cref="RekeyAndDistributeAsync"/>, triggered from the <c>groupMemberKicked</c>
    /// case in <see cref="DispatchFrame"/> once this identity observes its own action's broadcast.</summary>
    public Task<bool> KickGroupMemberAsync(string groupId, string userId, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.KickGroupMember, new RelayKickGroupMemberRequest("kickGroupMember", groupId, userId), cancellationToken);

    /// <summary>Sends <c>leaveGroup</c> - voluntary departure. If this identity owns the group and isn't
    /// its last member, the relay rejects this (see <see cref="GroupError"/>) until ownership is
    /// transferred first; if it *is* the last member, leaving deletes the group entirely. An online
    /// owner/moderator who observes this identity's departure (the <c>groupMemberLeft</c> push) is who
    /// actually performs the resulting key rotation - this identity isn't a member any more to do it
    /// itself.</summary>
    public Task<bool> LeaveGroupAsync(string groupId, CancellationToken cancellationToken = default) =>
        SendAndAwaitAsync(PendingAction.LeaveGroup, new RelayLeaveGroupRequest("leaveGroup", groupId), cancellationToken);

    /// <summary>Message history with <paramref name="contactUserId"/>, oldest first - empty if there's
    /// been no exchange with them yet this session (not persisted, see <see cref="CrossDcChatMessage"/>'s
    /// doc comment).</summary>
    public IReadOnlyList<CrossDcChatMessage> GetMessages(string contactUserId) =>
        messagesByContact.TryGetValue(contactUserId, out var messages) ? messages : Array.Empty<CrossDcChatMessage>();

    /// <summary>True once <paramref name="contactUserId"/>'s X25519 public key has been received (an
    /// automatic <c>keyAnnounce</c> sent right after pairing - see the <c>paired</c> case in
    /// <see cref="DispatchFrame"/>), so <see cref="SendChatMessageAsync"/> can actually encrypt something
    /// for them. False doesn't mean anything's wrong - the announcement may simply not have arrived yet
    /// (it's an ordinary relay message, so it's offline-queued same as anything else if they were
    /// offline when this identity first paired with them).</summary>
    public bool HasKeyFor(string contactUserId) =>
        connectedUrl != null && identity?.GetPeerPublicKey(connectedUrl, contactUserId) != null;

    /// <summary>The group-chat mirror of <see cref="HasKeyFor"/> - true once this identity has unsealed
    /// at least one copy of <paramref name="groupId"/>'s current key (see <see cref="ApplySealedGroupKey"/>).</summary>
    public bool HasGroupKey(string groupId) =>
        connectedUrl != null && identity?.GetGroup(connectedUrl, groupId)?.KeyBase64 != null;

    /// <summary>The contact's character name (as announced via <c>keyAnnounce</c> - see
    /// <see cref="SendKeyAnnounceAsync"/>/the <c>keyAnnounce</c> case in <see cref="HandleIncomingPayload"/>),
    /// or the raw relay userId if no announcement carrying a name has arrived yet - always something
    /// non-empty either way, so callers never need their own fallback.</summary>
    public string GetDisplayName(string contactUserId) =>
        (connectedUrl != null ? identity?.GetPeerDisplayName(connectedUrl, contactUserId) : null) ?? contactUserId;

    /// <summary>Encrypts and sends a chat message to an already-paired contact - false (and no local
    /// history entry added) if there's no live connection, no key for them yet (see
    /// <see cref="HasKeyFor"/>), or the send itself fails. Unlike the admin-tooling requests, there's no
    /// relay-level response to a <c>send</c> to wait for, so this returns as soon as the frame is on the
    /// wire, not once the peer has received it.</summary>
    public async Task<bool> SendChatMessageAsync(string contactUserId, string text, CancellationToken cancellationToken = default)
    {
        if (connectedUrl == null || identity == null || UserId == null)
            return false;

        var peerKey = identity.GetPeerPublicKey(connectedUrl, contactUserId);
        if (peerKey == null)
        {
            // No key yet - re-announce ours in case the original announcement never arrived (e.g. their
            // queue was full at the time), rather than leaving both sides stuck with no way to recover.
            _ = SendKeyAnnounceAsync(contactUserId, cancellationToken);
            return false;
        }

        var nonce = RandomNumberGenerator.GetBytes(24); // XChaCha20-Poly1305's nonce size
        var plaintext = Encoding.UTF8.GetBytes(text);
        var ciphertext = identity.EncryptFor(contactUserId, peerKey, UserId, nonce, plaintext);
        if (ciphertext == null)
            return false;

        var envelope = new ChatMessageEnvelope("chat", Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext));
        var payload = JsonSerializer.Serialize(envelope, RelayProtocolJson.Options);
        if (!await SendRawAsync(new RelaySendRequest("send", contactUserId, payload), cancellationToken).ConfigureAwait(false))
            return false;

        AppendMessage(contactUserId, new CrossDcChatMessage(UserId, text, DateTimeOffset.UtcNow, IsOutgoing: true));
        return true;
    }

    /// <summary>Best-effort, fire-and-forget - failure just means the contact won't get this identity's
    /// key yet; <see cref="SendChatMessageAsync"/> retries it automatically the next time it's needed.</summary>
    private async Task SendKeyAnnounceAsync(string contactUserId, CancellationToken cancellationToken)
    {
        if (identity == null)
            return;

        var envelope = new ChatKeyAnnounceEnvelope("keyAnnounce", identity.EncryptionPublicKeyBase64, getLocalPlayerName());
        var payload = JsonSerializer.Serialize(envelope, RelayProtocolJson.Options);
        await SendRawAsync(new RelaySendRequest("send", contactUserId, payload), cancellationToken).ConfigureAwait(false);
    }

    private void AppendMessage(string contactUserId, CrossDcChatMessage message)
    {
        if (!messagesByContact.TryGetValue(contactUserId, out var messages))
            messagesByContact[contactUserId] = messages = new List<CrossDcChatMessage>();

        messages.Add(message);
        MessageAppended?.Invoke(contactUserId, message);
    }

    /// <summary>Drops <paramref name="contactUserId"/> as a contact (and their in-memory message history)
    /// and fires <see cref="ContactRemoved"/> - shared by the <c>unpaired</c> and <c>userBlocked</c> cases
    /// in <see cref="DispatchFrame"/>, since blocking always unpairs too.</summary>
    private void RemoveContactLocal(string contactUserId)
    {
        if (connectedUrl == null || identity == null)
            return;

        identity.RemoveContact(connectedUrl, contactUserId);
        Contacts = identity.GetContacts(connectedUrl).ToArray();
        messagesByContact.Remove(contactUserId);
        ContactRemoved?.Invoke(contactUserId);
    }

    /// <summary>Message history with <paramref name="groupId"/>, oldest first - the group-chat mirror of
    /// <see cref="GetMessages"/>.</summary>
    public IReadOnlyList<CrossDcChatMessage> GetGroupMessages(string groupId) =>
        messagesByGroup.TryGetValue(groupId, out var messages) ? messages : Array.Empty<CrossDcChatMessage>();

    /// <summary>Encrypts and sends a chat message to a group this identity has a current key for - false
    /// (and no local history entry added) if there's no live connection, no group key yet (see
    /// <see cref="Groups"/>'s doc comment on why that can briefly happen right after joining), or the send
    /// itself fails.</summary>
    public async Task<bool> SendGroupMessageAsync(string groupId, string text, CancellationToken cancellationToken = default)
    {
        if (connectedUrl == null || identity == null || UserId == null)
            return false;

        var state = identity.GetGroup(connectedUrl, groupId);
        if (state?.KeyBase64 == null)
            return false;

        var nonce = RandomNumberGenerator.GetBytes(24);
        var plaintext = Encoding.UTF8.GetBytes(text);
        var ciphertext = identity.EncryptGroupMessage(Convert.FromBase64String(state.KeyBase64), groupId, state.Epoch, UserId, nonce, plaintext);
        if (ciphertext == null)
            return false;

        var envelope = new GroupChatMessageEnvelope("chat", Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), state.Epoch);
        var payload = JsonSerializer.Serialize(envelope, RelayProtocolJson.Options);
        if (!await SendRawAsync(new RelaySendGroupRequest("sendGroup", groupId, payload), cancellationToken).ConfigureAwait(false))
            return false;

        AppendGroupMessage(groupId, new CrossDcChatMessage(UserId, text, DateTimeOffset.UtcNow, IsOutgoing: true));
        return true;
    }

    private void AppendGroupMessage(string groupId, CrossDcChatMessage message)
    {
        if (!messagesByGroup.TryGetValue(groupId, out var messages))
            messagesByGroup[groupId] = messages = new List<CrossDcChatMessage>();

        messages.Add(message);
        GroupMessageAppended?.Invoke(groupId, message);
    }

    private void RefreshGroupsSnapshot()
    {
        if (connectedUrl == null || identity == null)
        {
            Groups = Array.Empty<CrossDcGroupInfo>();
            return;
        }

        Groups = identity.GetGroups(connectedUrl)
            .Select(kvp => new CrossDcGroupInfo(kvp.Key, kvp.Value.Name, kvp.Value.OwnerId, kvp.Value.Members.ToArray(), kvp.Value.ModeratorIds.ToArray()))
            .ToArray();
    }

    /// <summary>Seals the group's *current* (unrotated) key for one newly-joined member and distributes it
    /// via <c>setGroupMemberKey</c> - called when this identity (an owner or moderator) observes a live
    /// <c>groupMemberJoined</c> push while online. No rotation needed for an admission, only for a
    /// departure - see <see cref="RekeyAndDistributeAsync"/>'s own doc comment for why the two differ.
    /// Best-effort: silently gives up if the key directory fetch fails or doesn't (yet) have the new
    /// member's public key on file.</summary>
    private async Task DistributeGroupKeyToMemberAsync(string groupId, string memberId, byte[] groupKey, long epoch, CancellationToken cancellationToken)
    {
        if (identity == null)
            return;

        if (!await GetGroupKeyDirectoryAsync(groupId, cancellationToken).ConfigureAwait(false))
            return;

        if (!lastGroupKeyDirectory.TryGetValue(memberId, out var memberPublicKey))
            return;

        var sealedKey = identity.SealGroupKeyFor(memberPublicKey, groupId, epoch, memberId, groupKey);
        if (sealedKey != null)
            await SetGroupMemberKeyAsync(groupId, memberId, sealedKey, epoch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Generates a brand-new group key at the next epoch and seals/distributes it to every
    /// *remaining* member (this identity's own copy is updated directly, no relay round-trip needed) -
    /// the security-critical step after a kick or voluntary departure: unlike admitting a new member (see
    /// <see cref="DistributeGroupKeyToMemberAsync"/>), someone who's no longer in the group must lose the
    /// ability to decrypt *future* messages, which only rotating the key actually achieves (they still
    /// have the old epoch's key, but every member has since moved on to the new one). Called by whoever
    /// performed the kick (reacting to their own action's broadcast) or, for a voluntary leave, by an
    /// online owner/moderator reacting to the <c>groupMemberLeft</c> push - see both call sites in
    /// <see cref="DispatchFrame"/>. Best-effort per member: a member whose public key isn't on file yet is
    /// simply skipped, same as <see cref="DistributeGroupKeyToMemberAsync"/>.</summary>
    private async Task RekeyAndDistributeAsync(string groupId, CancellationToken cancellationToken)
    {
        if (identity == null || connectedUrl == null || UserId == null)
            return;

        var state = identity.GetGroup(connectedUrl, groupId);
        if (state == null)
            return;

        if (!await GetGroupKeyDirectoryAsync(groupId, cancellationToken).ConfigureAwait(false))
            return;

        var newKey = RelayIdentityService.GenerateGroupKey();
        var newEpoch = state.Epoch + 1;

        foreach (var memberId in state.Members)
        {
            if (memberId == UserId)
                continue;

            if (!lastGroupKeyDirectory.TryGetValue(memberId, out var memberPublicKey))
                continue;

            var sealedKey = identity.SealGroupKeyFor(memberPublicKey, groupId, newEpoch, memberId, newKey);
            if (sealedKey != null)
                await SetGroupMemberKeyAsync(groupId, memberId, sealedKey, newEpoch, cancellationToken).ConfigureAwait(false);
        }

        state.KeyBase64 = Convert.ToBase64String(newKey);
        state.Epoch = newEpoch;
        identity.PersistGroups();
        log.Info("TomeScrollChat: rotated cross-DC group {GroupId} to epoch {Epoch}", groupId, newEpoch);
    }

    /// <summary>Unseals and caches this identity's own copy of a group's key - shared by the
    /// <c>groupMemberKey</c> (a fetch this identity requested) and <c>groupKeyRotated</c> (an unsolicited
    /// live push) cases in <see cref="DispatchFrame"/>. A no-op if this would go *backwards* (an
    /// out-of-order delivery of an older epoch than what's already cached), since the newer key is
    /// strictly more useful and downgrading would only lose the ability to read newer messages.</summary>
    private void ApplySealedGroupKey(string groupId, string sealedKeyBase64, long epoch)
    {
        if (identity == null || connectedUrl == null || UserId == null)
            return;

        var state = identity.GetGroup(connectedUrl, groupId);
        if (state == null)
            return;

        if (state.KeyBase64 != null && epoch <= state.Epoch)
            return;

        var unsealed = identity.UnsealGroupKey(sealedKeyBase64, groupId, epoch, UserId);
        if (unsealed == null)
        {
            log.Warning("TomeScrollChat: failed to unseal cross-DC group {GroupId}'s key at epoch {Epoch}", groupId, epoch);
            return;
        }

        state.KeyBase64 = Convert.ToBase64String(unsealed);
        state.Epoch = epoch;
        identity.PersistGroups();
        log.Info("TomeScrollChat: cross-DC group {GroupId} key ready (epoch {Epoch})", groupId, epoch);
    }

    private void UpdateGroupRole(string groupId, string userId, bool isModerator)
    {
        if (connectedUrl == null || identity == null)
            return;

        var state = identity.GetGroup(connectedUrl, groupId);
        if (state == null)
            return;

        if (isModerator)
        {
            if (!state.ModeratorIds.Contains(userId))
                state.ModeratorIds.Add(userId);
        }
        else
        {
            state.ModeratorIds.Remove(userId);
        }

        identity.PersistGroups();
        RefreshGroupsSnapshot();
    }

    /// <summary>Someone else joined a group this identity is in (unsolicited push). If this identity is
    /// the owner or a moderator, seals/distributes the group's current key to them - see
    /// <see cref="DistributeGroupKeyToMemberAsync"/>.</summary>
    private void HandleGroupMemberJoined(string groupId, string newMemberId)
    {
        if (connectedUrl == null || identity == null)
            return;

        var state = identity.GetGroup(connectedUrl, groupId);
        if (state == null)
            return;

        if (!state.Members.Contains(newMemberId))
        {
            state.Members.Add(newMemberId);
            identity.PersistGroups();
        }

        RefreshGroupsSnapshot();
        log.Info("TomeScrollChat: {UserId} joined cross-DC group {GroupId}", newMemberId, groupId);

        var myUserId = UserId;
        if (myUserId != null && state.KeyBase64 != null && (state.OwnerId == myUserId || state.ModeratorIds.Contains(myUserId)))
            _ = DistributeGroupKeyToMemberAsync(groupId, newMemberId, Convert.FromBase64String(state.KeyBase64), state.Epoch, CancellationToken.None);
    }

    /// <summary>Parses the client-level envelope carried inside a relay <c>groupMessage</c>'s opaque
    /// <c>payload</c> (see <see cref="GroupChatMessageEnvelope"/>) and decrypts it - the group-chat mirror
    /// of <see cref="HandleIncomingPayload"/>'s <c>chat</c> case.</summary>
    private void HandleIncomingGroupPayload(string groupId, string fromUserId, string payloadJson)
    {
        if (connectedUrl == null || identity == null)
            return;

        var state = identity.GetGroup(connectedUrl, groupId);
        if (state?.KeyBase64 == null)
        {
            log.Warning("TomeScrollChat: received a cross-DC group message for {GroupId} but have no key yet - dropping", groupId);
            return;
        }

        var envelope = TryDeserialize<GroupChatMessageEnvelope>(payloadJson);
        if (envelope is not { Nonce.Length: > 0, Ciphertext.Length: > 0 })
            return;

        if (envelope.Epoch != state.Epoch)
        {
            log.Warning("TomeScrollChat: cross-DC group {GroupId} message epoch {MessageEpoch} doesn't match this identity's current key epoch {KeyEpoch} - dropping", groupId, envelope.Epoch, state.Epoch);
            return;
        }

        var plaintext = identity.DecryptGroupMessage(Convert.FromBase64String(state.KeyBase64), groupId, envelope.Epoch, fromUserId, Convert.FromBase64String(envelope.Nonce), Convert.FromBase64String(envelope.Ciphertext));
        if (plaintext == null)
        {
            log.Warning("TomeScrollChat: failed to decrypt a cross-DC group message for {GroupId} from {From} - dropping", groupId, fromUserId);
            return;
        }

        AppendGroupMessage(groupId, new CrossDcChatMessage(fromUserId, Encoding.UTF8.GetString(plaintext), DateTimeOffset.UtcNow, IsOutgoing: false));
    }

    /// <summary>Clears <see cref="IsAdmin"/> and the locally-cached "admin on this URL" fact (see
    /// <see cref="RelayIdentityService.ForgetAdmin"/>), for when the relay's own admin record no longer
    /// agrees with what this client remembers (e.g. the relay's Redis was flushed independently of
    /// anything this client did) - lets the player re-claim with a fresh bootstrap key instead of being
    /// stuck with a client that insists it's admin while the server keeps saying otherwise. A no-op
    /// while disconnected, since there's no URL to forget.</summary>
    public void ForgetAdminStatus()
    {
        if (connectedUrl != null)
            identity?.ForgetAdmin(connectedUrl);

        IsAdmin = false;
        AdminError = null;
    }

    /// <summary>Sends one request and waits for its matching response to actually arrive (success or
    /// error) before returning - see the <see cref="requestLock"/> field doc comment for why. False if
    /// there's no live connection to send on, the send itself failed, or the connection dropped before a
    /// response arrived.</summary>
    private async Task<bool> SendAndAwaitAsync<T>(PendingAction action, T message, CancellationToken cancellationToken)
    {
        var ws = socket;
        if (ws is not { State: WebSocketState.Open })
            return false;

        await requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            pendingAction = action;
            pendingCompletion = completion;
            await RelaySocketIo.SendAsync(ws, message, cancellationToken).ConfigureAwait(false);

            await using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
                return await completion.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to send a cross-DC relay {Action} request", action);
            return false;
        }
        finally
        {
            pendingAction = PendingAction.None;
            pendingCompletion = null;
            requestLock.Release();
        }
    }

    /// <summary>Sends a frame with no relay-level response to wait for (<c>send</c> is fire-and-forget
    /// at the protocol level - see TomeScrollRelay's own <c>RouteAsync</c>, which never replies to the
    /// sender). Still goes through <see cref="requestLock"/>, just for the duration of the write itself
    /// rather than a full cycle - <see cref="ClientWebSocket"/> only tolerates one send in flight at a
    /// time, and this lock is already what serializes every other send against that same constraint.</summary>
    private async Task<bool> SendRawAsync<T>(T message, CancellationToken cancellationToken)
    {
        var ws = socket;
        if (ws is not { State: WebSocketState.Open })
            return false;

        await requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RelaySocketIo.SendAsync(ws, message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to send a cross-DC relay message");
            return false;
        }
        finally
        {
            requestLock.Release();
        }
    }

    private string? ResolveUrl() => configuration.CrossDcRelayMode switch
    {
        RelayMode.Managed => ManagedRelayEndpoint.GetUrl(),
        RelayMode.SelfHosted when IsValidRelayUrl(configuration.CrossDcRelaySelfHostedUrl) => configuration.CrossDcRelaySelfHostedUrl,
        _ => null,
    };

    private static bool IsValidRelayUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "wss" || uri.Scheme == "ws");

    private async Task RunAsync(string url, RelayIdentityService identity, CancellationToken cancellationToken)
    {
        using var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

            var userId = await HandshakeAsync(ws, identity, cancellationToken).ConfigureAwait(false);
            if (userId == null)
            {
                log.Warning("TomeScrollChat: cross-DC relay handshake failed ({Url})", url);
                SetDisconnected("Handshake failed");
                return;
            }

            socket = ws;
            IsConnected = true;
            UserId = userId;
            LastError = null;
            if (identity.IsKnownAdmin(url))
            {
                IsAdmin = true;
                log.Info("TomeScrollChat: cross-DC relay admin rights recalled for {UserId} ({Url})", userId, url);
            }
            Contacts = identity.GetContacts(url).ToArray();
            foreach (var contactUserId in Contacts)
                ContactAdded?.Invoke(contactUserId);

            Blocked = identity.GetBlocked(url).ToArray();

            RefreshGroupsSnapshot();
            foreach (var (groupId, state) in identity.GetGroups(url).ToArray())
            {
                GroupJoined?.Invoke(groupId);
                // Retry in case this identity joined (or missed a rotation) while nobody who could seal a
                // key was online - best-effort, matches every other group notification's contract.
                if (state.KeyBase64 == null)
                    _ = GetGroupMemberKeyAsync(groupId, cancellationToken);
            }

            log.Info("TomeScrollChat: connected to cross-DC relay as {UserId} ({Url})", userId, url);

            while (!cancellationToken.IsCancellationRequested)
            {
                var raw = await RelaySocketIo.ReceiveTextAsync(ws, cancellationToken).ConfigureAwait(false);
                if (raw == null)
                    break;

                DispatchFrame(raw);
            }
        }
        catch (OperationCanceledException)
        {
            // Reconcile() (or Dispose) tearing this connection down on purpose - not a failure.
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: cross-DC relay connection failed ({Url})", url);
            SetDisconnected(ex.Message);
            return;
        }

        SetDisconnected(null);
    }

    /// <summary>Sends the Ed25519 public key + a signature over the relay's nonce, matching
    /// TomeScrollRelay's <c>RelayHandshake.TryAuthenticateAsync</c> exactly (standard base64 both
    /// directions, same as that server expects). Returns the assigned user ID, or null on any
    /// malformed/unexpected response.</summary>
    private static async Task<string?> HandshakeAsync(ClientWebSocket ws, RelayIdentityService identity, CancellationToken cancellationToken)
    {
        var challengeRaw = await RelaySocketIo.ReceiveTextAsync(ws, cancellationToken).ConfigureAwait(false);
        if (challengeRaw == null)
            return null;

        var challenge = JsonSerializer.Deserialize<RelayChallengeMessage>(challengeRaw, RelayProtocolJson.Options);
        if (challenge is not { Type: "challenge", Nonce.Length: > 0 })
            return null;

        var nonce = Convert.FromBase64String(challenge.Nonce);
        var signature = identity.Sign(nonce);
        var hello = new RelayHelloMessage(identity.SigningPublicKeyBase64, Convert.ToBase64String(signature));
        await RelaySocketIo.SendAsync(ws, hello, cancellationToken).ConfigureAwait(false);

        var connectedRaw = await RelaySocketIo.ReceiveTextAsync(ws, cancellationToken).ConfigureAwait(false);
        if (connectedRaw == null)
            return null;

        var connected = JsonSerializer.Deserialize<RelayConnectedMessage>(connectedRaw, RelayProtocolJson.Options);
        return connected is { Type: "connected", Id.Length: > 0 } ? connected.Id : null;
    }

    /// <summary>Handles <c>adminGranted</c>/<c>logs</c>/<c>stats</c>/<c>invite</c>/<c>paired</c>/
    /// <c>error</c>; everything else (actual pairwise messages, groups - not built yet) is just logged
    /// by type for now, proving the receive loop works end to end without anywhere real to route those
    /// frames to yet.</summary>
    private void DispatchFrame(string rawJson)
    {
        string? type;
        try
        {
            type = JsonSerializer.Deserialize<RelayTypeOnly>(rawJson, RelayProtocolJson.Options)?.Type;
        }
        catch (JsonException)
        {
            log.Warning("TomeScrollChat: cross-DC relay sent an unparseable frame");
            return;
        }

        switch (type)
        {
            case "adminGranted":
                IsAdmin = true;
                AdminError = null;
                if (connectedUrl != null)
                    identity?.MarkAdmin(connectedUrl);
                log.Info("TomeScrollChat: cross-DC relay admin claim succeeded");
                pendingCompletion?.TrySetResult(true);
                break;

            case "logs":
                var logsMessage = TryDeserialize<RelayLogsMessage>(rawJson);
                Logs = logsMessage?.Lines ?? Array.Empty<string>();
                LogsError = null;
                pendingCompletion?.TrySetResult(true);
                break;

            case "stats":
                var statsMessage = TryDeserialize<RelayStatsMessage>(rawJson);
                ConnectedClients = statsMessage?.ConnectedClients;
                StatsError = null;
                pendingCompletion?.TrySetResult(true);
                break;

            case "invite":
                var inviteMessage = TryDeserialize<RelayInviteMessage>(rawJson);
                InviteCode = inviteMessage?.Code;
                InviteError = null;
                pendingCompletion?.TrySetResult(true);
                break;

            case "paired":
                // Unlike the other cases above, this can arrive *unsolicited* - the inviter side of a
                // pairing gets this pushed to them (live, or queued for when they next connect) the
                // moment someone else redeems their code, without having sent anything themselves. Only
                // touch pendingCompletion when we're the one who actually sent redeemInvite and is still
                // waiting on it; otherwise just record the new contact and leave whatever else might
                // genuinely be pending (e.g. an unrelated getStats) alone.
                var pairedMessage = TryDeserialize<RelayPairedMessage>(rawJson);
                if (pairedMessage?.With is { Length: > 0 } withId)
                {
                    if (connectedUrl != null)
                        identity?.AddContact(connectedUrl, withId);
                    Contacts = connectedUrl != null ? identity?.GetContacts(connectedUrl).ToArray() ?? Contacts : Contacts;
                    ContactAdded?.Invoke(withId);
                    log.Info("TomeScrollChat: cross-DC relay paired with {With}", withId);
                    // Announce this identity's encryption key to the new contact right away, so a
                    // message can actually be sent to them without the player needing to do anything -
                    // best-effort/fire-and-forget, same as the notification that triggered this.
                    _ = SendKeyAnnounceAsync(withId, CancellationToken.None);
                }
                PairError = null;
                if (pendingAction == PendingAction.RedeemInvite)
                    pendingCompletion?.TrySetResult(true);
                break;

            case "unpaired":
                // Same dual solicited/unsolicited shape as "paired" - this identity's own unpair request
                // completing, or the other side unpairing/blocking, pushed live (best-effort, no offline
                // queue for this one - see TomeScrollRelay's own UnpairAsync doc comment).
                var unpairedMessage = TryDeserialize<RelayUnpairedMessage>(rawJson);
                if (unpairedMessage?.With is { Length: > 0 } unpairedWith)
                    RemoveContactLocal(unpairedWith);
                RelationshipError = null;
                if (pendingAction == PendingAction.Unpair)
                    pendingCompletion?.TrySetResult(true);
                break;

            case "userBlocked":
                var blockedMessage = TryDeserialize<RelayUserBlockedMessage>(rawJson);
                if (blockedMessage?.UserId is { Length: > 0 } blockedUserId && connectedUrl != null && identity != null)
                {
                    identity.AddBlocked(connectedUrl, blockedUserId);
                    Blocked = identity.GetBlocked(connectedUrl).ToArray();
                    // The relay always unpairs on block too (see BlockUserAsync's own doc comment) - no
                    // separate "unpaired" frame arrives for the blocker's own side, just this one.
                    RemoveContactLocal(blockedUserId);
                }
                RelationshipError = null;
                if (pendingAction == PendingAction.BlockUser)
                    pendingCompletion?.TrySetResult(true);
                break;

            case "userUnblocked":
                var unblockedMessage = TryDeserialize<RelayUserUnblockedMessage>(rawJson);
                if (unblockedMessage?.UserId is { Length: > 0 } unblockedUserId && connectedUrl != null && identity != null)
                {
                    identity.RemoveBlocked(connectedUrl, unblockedUserId);
                    Blocked = identity.GetBlocked(connectedUrl).ToArray();
                }
                RelationshipError = null;
                if (pendingAction == PendingAction.UnblockUser)
                    pendingCompletion?.TrySetResult(true);
                break;

            case "message":
                var incoming = TryDeserialize<RelayMessageEnvelope>(rawJson);
                if (incoming is { From.Length: > 0, Payload.Length: > 0 })
                    HandleIncomingPayload(incoming.From, incoming.Payload);
                break;

            case "groupCreated":
                var createdMessage = TryDeserialize<RelayGroupCreatedMessage>(rawJson);
                if (createdMessage is { GroupId.Length: > 0, Name.Length: > 0 } && connectedUrl != null && identity != null && UserId != null)
                {
                    var groupKey = RelayIdentityService.GenerateGroupKey();
                    var state = new GroupState
                    {
                        Name = createdMessage.Name,
                        OwnerId = UserId,
                        Members = new List<string> { UserId },
                        ModeratorIds = new List<string>(),
                        KeyBase64 = Convert.ToBase64String(groupKey),
                        Epoch = 1,
                    };
                    identity.UpsertGroup(connectedUrl, createdMessage.GroupId, state);
                    RefreshGroupsSnapshot();
                    GroupJoined?.Invoke(createdMessage.GroupId);
                    log.Info("TomeScrollChat: created cross-DC group {GroupId} ({Name})", createdMessage.GroupId, createdMessage.Name);
                }
                GroupError = null;
                pendingCompletion?.TrySetResult(true);
                break;

            case "groupInvite":
                var groupInviteMessage = TryDeserialize<RelayGroupInviteMessage>(rawJson);
                GroupInviteCode = groupInviteMessage?.Code;
                GroupError = null;
                pendingCompletion?.TrySetResult(true);
                break;

            case "joinedGroup":
                var joinedMessage = TryDeserialize<RelayJoinedGroupMessage>(rawJson);
                if (joinedMessage is { GroupId.Length: > 0, Name.Length: > 0 } && connectedUrl != null && identity != null)
                {
                    var state = identity.GetGroup(connectedUrl, joinedMessage.GroupId) ?? new GroupState();
                    state.Name = joinedMessage.Name;
                    state.Members = (joinedMessage.Members ?? Array.Empty<string>()).ToList();
                    identity.UpsertGroup(connectedUrl, joinedMessage.GroupId, state);
                    RefreshGroupsSnapshot();
                    GroupJoined?.Invoke(joinedMessage.GroupId);
                    log.Info("TomeScrollChat: joined cross-DC group {GroupId} ({Name}, {Count} member(s))", joinedMessage.GroupId, joinedMessage.Name, state.Members.Count);
                    // Nobody's necessarily sealed a key for this identity yet - best-effort fetch right
                    // away in case an online owner/moderator already reacted to the groupMemberJoined push.
                    _ = GetGroupMemberKeyAsync(joinedMessage.GroupId, CancellationToken.None);
                }
                GroupError = null;
                if (pendingAction == PendingAction.RedeemGroupInvite)
                    pendingCompletion?.TrySetResult(true);
                break;

            case "groupMemberJoined":
                var memberJoinedMessage = TryDeserialize<RelayGroupMemberJoinedMessage>(rawJson);
                if (memberJoinedMessage is { GroupId.Length: > 0, UserId.Length: > 0 })
                    HandleGroupMemberJoined(memberJoinedMessage.GroupId, memberJoinedMessage.UserId);
                break;

            case "groupKeyDirectory":
                var directoryMessage = TryDeserialize<RelayGroupKeyDirectoryMessage>(rawJson);
                lastGroupKeyDirectory = directoryMessage?.Members ?? new Dictionary<string, string>();
                GroupError = null;
                pendingCompletion?.TrySetResult(true);
                break;

            case "groupMemberKeySet":
                GroupError = null;
                pendingCompletion?.TrySetResult(true);
                break;

            case "groupKeyRotated":
                var rotatedMessage = TryDeserialize<RelayGroupKeyRotatedMessage>(rawJson);
                if (rotatedMessage is { GroupId.Length: > 0, SealedKey.Length: > 0, Epoch: { } rotatedEpoch })
                    ApplySealedGroupKey(rotatedMessage.GroupId, rotatedMessage.SealedKey, rotatedEpoch);
                break;

            case "groupMemberKey":
                var memberKeyMessage = TryDeserialize<RelayGroupMemberKeyMessage>(rawJson);
                if (memberKeyMessage is { GroupId.Length: > 0, SealedKey.Length: > 0, Epoch: { } memberEpoch })
                    ApplySealedGroupKey(memberKeyMessage.GroupId, memberKeyMessage.SealedKey, memberEpoch);
                GroupError = null;
                pendingCompletion?.TrySetResult(true);
                break;

            case "groupMessage":
                var groupMessageEnvelope = TryDeserialize<RelayGroupMessageEnvelope>(rawJson);
                if (groupMessageEnvelope is { GroupId.Length: > 0, From.Length: > 0, Payload.Length: > 0 })
                    HandleIncomingGroupPayload(groupMessageEnvelope.GroupId, groupMessageEnvelope.From, groupMessageEnvelope.Payload);
                break;

            case "moderatorPromoted":
                var promotedMessage = TryDeserialize<RelayModeratorPromotedMessage>(rawJson);
                if (promotedMessage is { GroupId.Length: > 0, UserId.Length: > 0 })
                    UpdateGroupRole(promotedMessage.GroupId, promotedMessage.UserId, isModerator: true);
                GroupError = null;
                if (pendingAction == PendingAction.PromoteModerator)
                    pendingCompletion?.TrySetResult(true);
                break;

            case "moderatorDemoted":
                var demotedMessage = TryDeserialize<RelayModeratorDemotedMessage>(rawJson);
                if (demotedMessage is { GroupId.Length: > 0, UserId.Length: > 0 })
                    UpdateGroupRole(demotedMessage.GroupId, demotedMessage.UserId, isModerator: false);
                GroupError = null;
                if (pendingAction == PendingAction.DemoteModerator)
                    pendingCompletion?.TrySetResult(true);
                break;

            case "groupOwnershipTransferred":
                var transferredMessage = TryDeserialize<RelayGroupOwnershipTransferredMessage>(rawJson);
                if (transferredMessage is { GroupId.Length: > 0, NewOwnerId.Length: > 0 } && connectedUrl != null && identity != null)
                {
                    var state = identity.GetGroup(connectedUrl, transferredMessage.GroupId);
                    if (state != null)
                    {
                        state.OwnerId = transferredMessage.NewOwnerId;
                        // Owner status supersedes moderator - matches TomeScrollRelay's own GroupRegistry.SetOwnerAsync.
                        state.ModeratorIds.Remove(transferredMessage.NewOwnerId);
                        identity.PersistGroups();
                        RefreshGroupsSnapshot();
                    }
                }
                GroupError = null;
                if (pendingAction == PendingAction.TransferGroupOwnership)
                    pendingCompletion?.TrySetResult(true);
                break;

            case "groupMemberKicked":
                var kickedMessage = TryDeserialize<RelayGroupMemberKickedMessage>(rawJson);
                if (kickedMessage is { GroupId.Length: > 0, UserId.Length: > 0 })
                {
                    if (kickedMessage.UserId == UserId)
                    {
                        if (connectedUrl != null)
                            identity?.RemoveGroup(connectedUrl, kickedMessage.GroupId);
                        RefreshGroupsSnapshot();
                        GroupLeft?.Invoke(kickedMessage.GroupId);
                        log.Info("TomeScrollChat: kicked from cross-DC group {GroupId}", kickedMessage.GroupId);
                    }
                    else if (connectedUrl != null && identity != null)
                    {
                        var state = identity.GetGroup(connectedUrl, kickedMessage.GroupId);
                        if (state != null)
                        {
                            state.Members.Remove(kickedMessage.UserId);
                            state.ModeratorIds.Remove(kickedMessage.UserId);
                            identity.PersistGroups();
                            RefreshGroupsSnapshot();

                            // Our own kick request completes via observing this same broadcast (the relay
                            // never sends the kicker a separate direct ack - see KickGroupMemberAsync's
                            // doc comment) - and it's specifically the kicker's job to rotate the key now.
                            if (pendingAction == PendingAction.KickGroupMember)
                                _ = RekeyAndDistributeAsync(kickedMessage.GroupId, CancellationToken.None);
                        }
                    }
                }
                GroupError = null;
                if (pendingAction == PendingAction.KickGroupMember)
                    pendingCompletion?.TrySetResult(true);
                break;

            case "leftGroup":
                var leftMessage = TryDeserialize<RelayLeftGroupMessage>(rawJson);
                if (leftMessage is { GroupId.Length: > 0 } && connectedUrl != null)
                {
                    identity?.RemoveGroup(connectedUrl, leftMessage.GroupId);
                    RefreshGroupsSnapshot();
                    GroupLeft?.Invoke(leftMessage.GroupId);
                    log.Info("TomeScrollChat: left cross-DC group {GroupId}", leftMessage.GroupId);
                }
                GroupError = null;
                if (pendingAction == PendingAction.LeaveGroup)
                    pendingCompletion?.TrySetResult(true);
                break;

            case "groupMemberLeft":
                var memberLeftMessage = TryDeserialize<RelayGroupMemberLeftMessage>(rawJson);
                if (memberLeftMessage is { GroupId.Length: > 0, UserId.Length: > 0 } && connectedUrl != null && identity != null)
                {
                    var state = identity.GetGroup(connectedUrl, memberLeftMessage.GroupId);
                    if (state != null)
                    {
                        state.Members.Remove(memberLeftMessage.UserId);
                        state.ModeratorIds.Remove(memberLeftMessage.UserId);
                        identity.PersistGroups();
                        RefreshGroupsSnapshot();
                        log.Info("TomeScrollChat: {UserId} left cross-DC group {GroupId}", memberLeftMessage.UserId, memberLeftMessage.GroupId);

                        var myUserId = UserId;
                        if (myUserId != null && (state.OwnerId == myUserId || state.ModeratorIds.Contains(myUserId)))
                            _ = RekeyAndDistributeAsync(memberLeftMessage.GroupId, CancellationToken.None);
                    }
                }
                break;

            case "error":
                var reason = TryDeserialize<RelayErrorMessage>(rawJson)?.Reason ?? "Unknown error";
                switch (pendingAction)
                {
                    case PendingAction.ClaimAdmin:
                        AdminError = reason;
                        break;
                    case PendingAction.GetLogs:
                        LogsError = reason;
                        break;
                    case PendingAction.GetStats:
                        StatsError = reason;
                        break;
                    case PendingAction.CreateInvite:
                        InviteError = reason;
                        break;
                    case PendingAction.RedeemInvite:
                        PairError = reason;
                        break;
                    case PendingAction.Unpair:
                    case PendingAction.BlockUser:
                    case PendingAction.UnblockUser:
                        RelationshipError = reason;
                        break;
                    case PendingAction.CreateGroup:
                    case PendingAction.CreateGroupInvite:
                    case PendingAction.RedeemGroupInvite:
                    case PendingAction.GetGroupKeyDirectory:
                    case PendingAction.SetGroupMemberKey:
                    case PendingAction.GetGroupMemberKey:
                    case PendingAction.PromoteModerator:
                    case PendingAction.DemoteModerator:
                    case PendingAction.TransferGroupOwnership:
                    case PendingAction.KickGroupMember:
                    case PendingAction.LeaveGroup:
                        GroupError = reason;
                        break;
                    default:
                        log.Debug("TomeScrollChat: cross-DC relay error with no pending request to attribute it to: {Reason}", reason);
                        break;
                }
                pendingCompletion?.TrySetResult(false);
                break;

            default:
                log.Debug("TomeScrollChat: cross-DC relay frame received (type={Type})", type ?? "(none)");
                break;
        }
    }

    /// <summary>Parses the client-level envelope carried inside a relay <c>message</c>'s opaque
    /// <c>payload</c> (see <see cref="ChatKeyAnnounceEnvelope"/>/<see cref="ChatMessageEnvelope"/>'s doc
    /// comment) and handles the two kinds this client understands so far. Anything else is logged and
    /// dropped - not an error, just not something built yet (groups).</summary>
    private void HandleIncomingPayload(string fromUserId, string payloadJson)
    {
        string? innerType;
        try
        {
            innerType = JsonSerializer.Deserialize<RelayTypeOnly>(payloadJson, RelayProtocolJson.Options)?.Type;
        }
        catch (JsonException)
        {
            log.Warning("TomeScrollChat: cross-DC relay message from {From} had an unparseable payload", fromUserId);
            return;
        }

        switch (innerType)
        {
            case "keyAnnounce":
                var announce = TryDeserialize<ChatKeyAnnounceEnvelope>(payloadJson);
                if (announce is { PublicKey.Length: > 0 } && connectedUrl != null)
                {
                    identity?.SetPeerPublicKey(connectedUrl, fromUserId, announce.PublicKey);
                    log.Info("TomeScrollChat: received a cross-DC key announcement from {From}", fromUserId);

                    // A display name riding along with this announcement (see SendKeyAnnounceAsync) is
                    // new information about a contact that may already have a tab named after their raw
                    // userId (created before this arrived) - re-raise ContactAdded so Plugin's
                    // GetOrCreateCrossDcTab gets a chance to upgrade that placeholder name now.
                    if (!string.IsNullOrEmpty(announce.DisplayName))
                    {
                        identity?.SetPeerDisplayName(connectedUrl, fromUserId, announce.DisplayName);
                        ContactAdded?.Invoke(fromUserId);
                    }
                }
                break;

            case "chat":
                var chat = TryDeserialize<ChatMessageEnvelope>(payloadJson);
                if (chat is not { Nonce.Length: > 0, Ciphertext.Length: > 0 } || connectedUrl == null || identity == null || UserId == null)
                    break;

                var peerKey = identity.GetPeerPublicKey(connectedUrl, fromUserId);
                if (peerKey == null)
                {
                    log.Warning("TomeScrollChat: received a chat message from {From} but have no key for them yet - dropping", fromUserId);
                    break;
                }

                var plaintext = identity.DecryptFrom(fromUserId, peerKey, UserId, Convert.FromBase64String(chat.Nonce), Convert.FromBase64String(chat.Ciphertext));
                if (plaintext == null)
                {
                    log.Warning("TomeScrollChat: failed to decrypt a cross-DC message from {From} - dropping", fromUserId);
                    break;
                }

                AppendMessage(fromUserId, new CrossDcChatMessage(fromUserId, Encoding.UTF8.GetString(plaintext), DateTimeOffset.UtcNow, IsOutgoing: false));
                break;

            default:
                log.Debug("TomeScrollChat: cross-DC relay message from {From} had an unrecognized payload type {Type}", fromUserId, innerType ?? "(none)");
                break;
        }
    }

    private T? TryDeserialize<T>(string rawJson) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(rawJson, RelayProtocolJson.Options);
        }
        catch (JsonException ex)
        {
            log.Warning(ex, "TomeScrollChat: cross-DC relay sent a malformed {Type} frame", typeof(T).Name);
            return null;
        }
    }

    private void SetDisconnected(string? error)
    {
        lock (gate)
        {
            socket = null;
            runTask = null;
            connectedUrl = null;
        }

        IsConnected = false;
        UserId = null;
        IsAdmin = false;
        // Unblocks a SendAndAwaitAsync call that was waiting on a reply that will now never come - it
        // returns false, same as an explicit error would.
        pendingCompletion?.TrySetResult(false);
        if (error != null)
            LastError = error;
    }

    /// <summary>Caller must already hold <see cref="gate"/>.</summary>
    private void StopLocked()
    {
        runCts?.Cancel();
        runCts?.Dispose();
        runCts = null;
        runTask = null;
        connectedUrl = null;
        socket = null;
    }

    public void Dispose()
    {
        lock (gate)
            StopLocked();

        identity?.Dispose();
        requestLock.Dispose();
    }
}
