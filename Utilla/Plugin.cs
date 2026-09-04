using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Utilla.Behaviours;

namespace Utilla;

[BepInPlugin(Constants.Guid, Constants.Name, Constants.Version)]
internal class Plugin : BaseUnityPlugin
{
    public new static ManualLogSource Logger;

    public Plugin()
    {
        Logger = base.Logger;

        DontDestroyOnLoad(this);

        Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, Constants.Guid);
        Events.Events.GameInitialized += OnGameInitialized;
    }

    public void OnGameInitialized(object sender, EventArgs args)
    {
        Logger.LogInfo(
                $"Utilla v{Constants.Version}, presented to you by: legoandmars, developer9998, Seralyth Software, and ZlothY.");

        DontDestroyOnLoad(new GameObject($"{Constants.Name} {Constants.Version}", typeof(UtillaNetworkController),
                typeof(GamemodeManager)));
    }
}