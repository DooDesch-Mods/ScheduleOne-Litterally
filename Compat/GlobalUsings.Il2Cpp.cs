// IL2CPP backend (net6.0) global usings.
//
// Import the Il2Cpp* game namespaces globally so the rest of the source uses
// UNQUALIFIED game type names (TrashManager, TrashItem, Player, NetworkSingleton).
//
// NOTE: because UnityEngine is imported here and System is imported implicitly,
// the bare identifiers `Object` and `Random` are ambiguous - always write
// `UnityEngine.Object` / `UnityEngine.Random` (or `System.Random`) explicitly.

global using UnityEngine;
global using Il2CppScheduleOne.Trash;          // TrashManager, TrashItem, TrashGenerator
global using Il2CppScheduleOne.DevUtilities;    // NetworkSingleton<T>, Singleton<T>, PlayerSingleton<T>
global using Il2CppScheduleOne.PlayerScripts;   // Player (Player.Local)
