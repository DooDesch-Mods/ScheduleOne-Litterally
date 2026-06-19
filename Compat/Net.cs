using System;
using Il2CppScheduleOne.Networking;   // Lobby (PersistentSingleton<Lobby>)

namespace Trashville.Compat
{
    /// <summary>
    /// Best-effort detection of a real multiplayer session (more than one player), used to auto-disable the
    /// local-only performance layer in MP. Uses the game's own Lobby singleton (Il2CppScheduleOne.Networking.Lobby
    /// : PersistentSingleton&lt;Lobby&gt;) which exposes IsInLobby + PlayerCount. In Schedule I single-player the host
    /// runs a local FishNet server too, so "IsServer || IsClient" is TRUE even solo - that cannot tell SP from MP.
    /// The Lobby player count is the reliable signal: a co-op session is IsInLobby with PlayerCount &gt; 1.
    ///
    /// Verified against the decompiled game (Lobby.IsInLobby / Lobby.PlayerCount exist and are public). The
    /// SP-vs-MP threshold (PlayerCount &gt; 1) is the assumption that still wants a quick live check with a 2-player
    /// lobby; conservative by design - if the lobby cannot be read it returns false (treat as single-player).
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
