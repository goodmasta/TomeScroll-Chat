using Dalamud.Plugin.Services;

namespace CustomChat.Services;

/// <summary>
/// Sends a friend request via the "/friendlist add &lt;t&gt;" text command. There's no by-name
/// native entry point for this (confirmed via reflection across every friend-list-related agent/
/// proxy - see the memory notes from that investigation), and "/friendlist add "Name@World""
/// turned out not to be valid syntax either - the game rejected it with "invalid argument, please
/// specify a valid placeholder": the command only accepts a target *placeholder* like &lt;t&gt;, not
/// a literal name. So this finds the player by name+world in the current zone and targets them
/// first, then sends the placeholder command - same real-world constraint the game's own
/// right-click "Request as Friend" has (the player has to actually be rendered nearby), there's no
/// way around that from a chat message alone.
/// </summary>
public sealed class FriendRequestService
{
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ChatSendService chatSendService;

    public FriendRequestService(IObjectTable objectTable, ITargetManager targetManager, ChatSendService chatSendService)
    {
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.chatSendService = chatSendService;
    }

    /// <returns>False if the player isn't currently visible/targetable nearby - the caller should
    /// tell the user that, rather than implying the request was sent.</returns>
    public bool TrySend(string name, string world)
    {
        var obj = NearbyPlayerLookup.Find(objectTable, name, world);
        if (obj == null)
            return false;

        // Restored right after sending - targeting them is only a means to give the command
        // something to point "<t>" at, not something the player asked to change.
        var previousTarget = targetManager.Target;
        targetManager.Target = obj;
        chatSendService.Send(string.Empty, "/friendlist add <t>");
        targetManager.Target = previousTarget;
        return true;
    }
}
