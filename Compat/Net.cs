using System;
using Il2CppScheduleOne.Networking;   // Lobby (PersistentSingleton<Lobby>)

namespace Litterally.Compat
{
    /// <summary>
    /// Best-effort detection of a real multiplayer session (more than one player), used to auto-disable the
    /// local-only performance layer in MP. Uses the game's own Lobby singleton (Il2CppScheduleOne.Networking.Lobby
    /// : PersistentSingleton&lt;Lobby&gt;) which exposes IsInLobby + PlayerCount. In Schedule I single-player the host
    /// runs a local FishNet server too, so "IsServer || IsClient" is TRUE even solo - that cannot tell SP from MP.
    /// The Lobby player count is the reliable signal: a co-op session is IsInLobby with PlayerCount &gt; 1.
    ///
    /// SOURCE-CONFIRMED (decompile): `IsInLobby = LobbyID != 0` (set only by the Steam matchmaking callbacks
    /// OnLobbyCreated/OnLobbyEntered), and `PlayerCount` returns 1 when not in a lobby, else the count of non-Nil
    /// Steam members. So `IsInLobby &amp;&amp; PlayerCount &gt; 1` reliably means "a real Steam co-op lobby with more than
    /// me" - the threshold is correct. Conservative: any failure -&gt; false (treat as single-player).
    ///
    /// KNOWN GAP (documented, not yet fixed): these are 100% Steam-matchmaking-driven. The community
    /// DedicatedServerMod connects clients over a direct-UDP (FishNet Tugboat) transport WITHOUT a Steam lobby,
    /// so LobbyID stays 0 and this returns FALSE even with 2 players connected - the perf layer would NOT
    /// auto-disable there. TODO: to also cover dedicated servers, OR in a FishNet connected-client-count signal
    /// (e.g. ServerManager/ClientManager clients &gt; 1) - but that must be live-tested first, since the host's
    /// clientHost loopback in single-player could otherwise read as &gt;1 and false-positive (disabling the layer
    /// in SP, which is worse). Steam-lobby co-op - the standard co-op path - IS handled correctly today.
    /// </summary>
    internal static class Net
    {
        internal static bool IsMultiplayer()
        {
            try
            {
                Lobby lobby = PersistentSingleton<Lobby>.Instance;
                if (lobby == null)
                {
                    return false;   // no lobby manager yet -> treat as single-player
                }
                // A co-op session: actively in a lobby with more than the local player.
                return lobby.IsInLobby && lobby.PlayerCount > 1;
            }
            catch
            {
                return false;   // conservative: any failure -> assume single-player (perf layer stays on)
            }
        }
    }
}
