#nullable enable
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Player;
using UnityEngine;

namespace PreLuna.FoamGunCustom;


[BepInIncompatibility("local.betterfoamgun")]
[BepInPlugin(Id, "PreLuna FoamGunCustom", "1.0.1")]
public sealed class Plugin : BasePlugin
{
    public const string Id = "PreLuna.FoamGunCustom";
    internal static Plugin Instance = null!;
    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<float> ShotInterval = null!;
    internal static ConfigEntry<float> LaunchForce = null!;
    internal static ConfigEntry<float> PelletSpread = null!;
    internal static ConfigEntry<float> AmmoCost = null!;
    private Harmony? _harmony;

    public override void Load()
    {
        Instance = this;
        Enabled = Config.Bind("General", "Enabled", true,
            "Enable automatic C-Foam Launcher for the local player. Restart after changing.");
        ShotInterval = Config.Bind("Firing", "ShotInterval", 0.08f,
            new ConfigDescription("Seconds between pellets; frame scheduling can increase the actual interval.",
                new AcceptableValueRange<float>(0.02f, 2f)));
        LaunchForce = Config.Bind("Firing", "LaunchForce", 32f,
            new ConfigDescription("Launch force for each full-pressure pellet. Does not change glue strength.",
                new AcceptableValueRange<float>(1f, 100f)));
        PelletSpread = Config.Bind("Firing", "PelletSpread", 1f,
            new ConfigDescription("Actual pellet spread for each shot; independent of crosshair size.",
                new AcceptableValueRange<float>(1f, 45f)));
        AmmoCost = Config.Bind("Ammo", "CostPerPellet", 0.5f,
            new ConfigDescription("Base tool-resource cost per pellet, before GlueEfficiency. Preserves existing resources and resource capacity.",
                new AcceptableValueRange<float>(0.01f, 50f)));
        _harmony = new Harmony(Id);
        try
        {
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"Loaded: local C-Foam automatic fire, interval={Interval}, force={Force}, spread={Spread}, base cost={Cost}. Glue strength unchanged.");
        }
        catch
        {
            _harmony.UnpatchSelf();
            throw;
        }
    }

    public override bool Unload()
    {
        // Runtime unloading while the tool is held would need state restoration.
        // Restarting the game gives a clean transition to the original controls.
        return false;
    }

    internal static bool Handles(GlueGun gun)
        => Enabled.Value && gun.Owner != null && gun.Owner.IsLocallyOwned;

    internal static float Interval
        => float.IsFinite(ShotInterval.Value) ? Mathf.Clamp(ShotInterval.Value, 0.02f, 2f) : 0.08f;
    internal static float Force
        => float.IsFinite(LaunchForce.Value) ? Mathf.Clamp(LaunchForce.Value, 1f, 100f) : 32f;
    internal static float Spread
        => float.IsFinite(PelletSpread.Value) ? Mathf.Clamp(PelletSpread.Value, 1f, 45f) : 1f;
    internal static float Cost
        => float.IsFinite(AmmoCost.Value) ? Mathf.Clamp(AmmoCost.Value, 0.01f, 50f) : 0.5f;
}

internal sealed class GunState
{
    internal readonly ShotScheduler Scheduler = new();
    internal bool Initialized;
    internal bool Faulted;
    internal bool? Ready;
    internal bool Activated;
}

internal static class AutomaticFire
{
    // Il2Cpp can supply different managed wrappers for the same native instance.
    private static readonly Dictionary<IntPtr, GunState> States = new();

    internal static GunState State(GlueGun gun)
    {
        if (!States.TryGetValue(gun.Pointer, out var state))
            States.Add(gun.Pointer, state = new GunState());
        return state;
    }

    internal static void Forget(IntPtr pointer) => States.Remove(pointer);

    internal static void Stop(GlueGun gun)
    {
        if (gun.m_fireRoutine != null)
        {
            gun.StopCoroutine(gun.m_fireRoutine);
            gun.m_fireRoutine = null;
        }
        gun.m_firing = false;
        gun.m_firingCurrentShotCount = 0;
        gun.m_firePressedLast = false;
        gun.m_firePressureMet = false;
        gun.m_maxPressureMet = false;
        gun.m_pressure = 0;
        gun.m_reCharging = false;
        gun.m_rechargeTimer = 0;
    }

    internal static void Update(GlueGun gun, ref PlayerInventoryBase.pSimpleItemSyncData syncData)
    {
        var state = State(gun);
        if (!state.Initialized)
        {
            Stop(gun);
            state.Initialized = true;
        }

        // These flags represent charging/recharging in the original GlueGun's
        // ReceiveClassItemSync, not the individual networked projectiles.
        syncData.inFireMode = false;
        syncData.inCooldown = false;
        gun.m_pressure = 0;
        gun.m_firePressedLast = false;
        gun.m_reCharging = false;

        var owner = gun.Owner;
        bool permitted = !state.Faulted && gun.m_isWielded && gun.IsEnabled &&
            owner.Alive && owner.IsEnabled && !owner.IsBeingDestroyed &&
            !owner.IsBeingDespawned && owner.Inventory != null &&
            owner.Inventory.AllowedToWieldItem && !owner.Inventory.InElevatorAnims &&
            GameStateManager.CurrentStateName == eGameStateName.InLevel &&
            InputMapper.PlayerHasFocus && Time.timeScale > 0;
        // m_fireButtonDown was sampled by the native Update using FireButton,
        // which respects input binding, the owner's input filter, and item busy state.
        bool held = permitted && gun.m_fireButtonDown;
        if (!held) state.Activated = false;
        var storage = PlayerBackpackManager.GetBackpack(owner.Owner)?.AmmoStorage;
        var ammo = storage?.GetInventorySlotAmmo(AmmoType.Class);
        if (storage != null && ammo != null && ammo.CostOfBullet != Plugin.Cost)
        {
            // Native Setup recalculates bullet capacity and UI conversion factors.
            // A max-cap argument of -1 retains the current resource capacity;
            // Setup does not refill or overwrite AmmoInPack (verified in revision 34873).
            storage.SetupAmmoType(AmmoType.Class, Plugin.Cost, ammo.BulletClipSize, -1f);
            storage.UpdateSlotAmmoUI(ammo, 0);
        }
        double cost = ammo == null ? double.PositiveInfinity : ShotScheduler.CostPerShot(
            ammo.CostOfBullet, AgentModifierManager.ApplyModifier(owner, AgentModifier.GlueEfficiency, 1f));
        double available = ammo?.AmmoInPack ?? 0;
        bool ready = permitted && ShotScheduler.CanAfford(available, cost);

        if (storage != null)
        {
            state.Scheduler.TryFire(Time.time, Plugin.Interval, held, available, cost,
                () => Emit(gun, state), value => storage.UpdateAmmoInPack(AmmoType.Class, -(float)value));
        }
        if (!held) gun.m_firing = false;

        // Replace the charging indicator with a simple ready/empty lamp. The
        // original Update still sends item status; projectile replication is native.
        if (state.Ready != ready)
        {
            gun.SetLampColor(ready ? gun.m_colMeterYellow : gun.m_colMeterRed, false);
            if (gun.m_progressBar != null) gun.m_progressBar.SetProgress(ready ? 1f : 0f);
            state.Ready = ready;
        }
        if (GuiManager.CrosshairLayer != null)
            GuiManager.CrosshairLayer.SetChargeUpVisibleAndProgress(false, 0);
    }

    private static void Emit(GlueGun gun, GunState state)
    {
        float originalMin = gun.m_forceSizeMin, originalMax = gun.m_forceSizeMax;
        float originalSpreadMin = gun.m_spreadMin, originalSpreadMax = gun.m_spreadMax;
        try
        {
            // FireBurst notifies tool-activation listeners once per use. Retain
            // that notification on the first emitted pellet of each held press.
            if (!state.Activated)
            {
                GlobalCallbacks.OnPlayerActivatedTool?.Invoke(gun.Owner, gun.ItemDataBlock.inventorySlot);
                state.Activated = true;
            }
            // Keep full-pressure firing, overriding only this shot's force and
            // actual spread. Restore both ranges after the native firing call.
            gun.m_forceSizeMin = gun.m_forceSizeMax = Plugin.Force;
            gun.m_spreadMin = gun.m_spreadMax = Plugin.Spread;
            gun.m_firing = true;
            gun.FireSingle(1f, true);
        }
        finally
        {
            gun.m_forceSizeMin = originalMin;
            gun.m_forceSizeMax = originalMax;
            gun.m_spreadMin = originalSpreadMin;
            gun.m_spreadMax = originalSpreadMax;
            gun.m_firing = false;
        }
    }

    internal static void Fail(GlueGun gun, Exception error)
    {
        var state = State(gun);
        if (!state.Faulted)
            Plugin.Instance.Log.LogError($"Automatic C-Foam stopped for this instance after an error. Restart the level/game before retrying. {error}");
        state.Faulted = true;
        Stop(gun);
    }
}

[HarmonyPatch(typeof(GlueGun), nameof(GlueGun.Updatepressure))]
internal static class PressurePatch
{
    private static bool Prefix(GlueGun __instance, ref PlayerInventoryBase.pSimpleItemSyncData __0)
    {
        if (!Plugin.Handles(__instance)) return true;
        try { AutomaticFire.Update(__instance, ref __0); }
        catch (Exception error) { AutomaticFire.Fail(__instance, error); }
        // Never fall through to the native release-to-fire burst after a failure.
        return false;
    }
}

[HarmonyPatch(typeof(GlueGun), nameof(GlueGun.UpdateRecharging))]
internal static class RechargePatch
{
    private static bool Prefix(GlueGun __instance, ref PlayerInventoryBase.pSimpleItemSyncData __0)
    {
        if (!Plugin.Handles(__instance)) return true;
        __instance.m_reCharging = false;
        __instance.m_rechargeTimer = 0;
        __0.inCooldown = false;
        return false;
    }
}

[HarmonyPatch(typeof(GlueGun), nameof(GlueGun.OnUnWield))]
internal static class UnwieldPatch
{
    private static void Prefix(GlueGun __instance)
    {
        if (!Plugin.Handles(__instance)) return;
        AutomaticFire.Stop(__instance);
        AutomaticFire.State(__instance).Ready = null;
        AutomaticFire.State(__instance).Activated = false;
    }
}

[HarmonyPatch(typeof(Item), nameof(Item.OnDespawn))]
internal static class DespawnPatch
{
    private static void Prefix(Item __instance) => AutomaticFire.Forget(__instance.Pointer);
}
