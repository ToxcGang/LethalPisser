using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LethalPisser;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class LethalPisserPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.lethalpisser.plugin";
    public const string PluginName = "LethalPisser";
    public const string PluginVersion = "1.0.0";

    private const string RequestMessageName = "LethalPisser.RequestPeeState";
    private const string StateMessageName = "LethalPisser.PeeState";
    private const float ChargerShockCooldown = 0.85f;
    private const int ChargerShockDamage = 35;

    private static ManualLogSource? logger;

    private readonly Dictionary<ulong, PeeStream> activeStreams = new Dictionary<ulong, PeeStream>();
    private readonly List<ulong> staleStreamIds = new List<ulong>();

    private InputAction? peeAction;
    private NetworkManager? registeredNetworkManager;
    private bool localPlayerIsPeeing;
    private float nextLocalChargerShockTime;

    private void Awake()
    {
        logger = Logger;
        peeAction = new InputAction("LethalPisser.Pee", InputActionType.Button);
        peeAction.AddBinding("<Keyboard>/p");
        peeAction.AddBinding("<Gamepad>/dpad/up");
        peeAction.Enable();

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded for Lethal Company v81.");
    }

    private void Update()
    {
        EnsureNetworkHandlers();
        UpdateLocalPeeInput();
        CleanupInvalidStreams();
    }

    private void OnDestroy()
    {
        StopLocalPeeing();
        UnregisterNetworkHandlers();
        peeAction?.Disable();
        peeAction?.Dispose();
        peeAction = null;

        foreach (PeeStream stream in activeStreams.Values)
        {
            if (stream != null)
            {
                Destroy(stream.gameObject);
            }
        }

        activeStreams.Clear();
    }

    private void UpdateLocalPeeInput()
    {
        PlayerControllerB? localPlayer = GetLocalPlayer();
        if (!CanPlayerPee(localPlayer))
        {
            if (localPlayerIsPeeing)
            {
                StopLocalPeeing();
            }

            return;
        }

        bool inputHeld = IsPeeInputHeld();
        if (inputHeld == localPlayerIsPeeing)
        {
            if (localPlayerIsPeeing && localPlayer != null)
            {
                SetPeeState(localPlayer.playerClientId, isPeeing: true);
            }

            return;
        }

        if (inputHeld)
        {
            StartLocalPeeing(localPlayer!);
        }
        else
        {
            StopLocalPeeing();
        }
    }

    private static PlayerControllerB? GetLocalPlayer()
    {
        if (GameNetworkManager.Instance != null && GameNetworkManager.Instance.localPlayerController != null)
        {
            return GameNetworkManager.Instance.localPlayerController;
        }

        return StartOfRound.Instance != null ? StartOfRound.Instance.localPlayerController : null;
    }

    private static bool CanPlayerPee(PlayerControllerB? player)
    {
        if (player == null || !player.isPlayerControlled || player.isPlayerDead)
        {
            return false;
        }

        if (player.isTypingChat || player.inTerminalMenu)
        {
            return false;
        }

        return player.quickMenuManager == null || !player.quickMenuManager.isMenuOpen;
    }

    private bool IsPeeInputHeld()
    {
        try
        {
            if (peeAction != null)
            {
                return peeAction.IsPressed();
            }
        }
        catch (Exception exception)
        {
            logger?.LogDebug($"InputSystem read failed; falling back to legacy keyboard input. {exception.Message}");
        }

        return Input.GetKey(KeyCode.P);
    }

    private void StartLocalPeeing(PlayerControllerB localPlayer)
    {
        localPlayerIsPeeing = true;
        SetPeeState(localPlayer.playerClientId, isPeeing: true);
        SendPeeState(localPlayer.playerClientId, isPeeing: true);
    }

    private void StopLocalPeeing()
    {
        if (!localPlayerIsPeeing)
        {
            return;
        }

        localPlayerIsPeeing = false;
        PlayerControllerB? localPlayer = GetLocalPlayer();
        if (localPlayer == null)
        {
            return;
        }

        SetPeeState(localPlayer.playerClientId, isPeeing: false);
        SendPeeState(localPlayer.playerClientId, isPeeing: false);
    }

    private void EnsureNetworkHandlers()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.CustomMessagingManager == null)
        {
            return;
        }

        if (ReferenceEquals(registeredNetworkManager, networkManager))
        {
            return;
        }

        UnregisterNetworkHandlers();

        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(RequestMessageName, HandleRequestMessage);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(StateMessageName, HandleStateMessage);
        registeredNetworkManager = networkManager;
    }

    private void UnregisterNetworkHandlers()
    {
        if (registeredNetworkManager == null || registeredNetworkManager.CustomMessagingManager == null)
        {
            registeredNetworkManager = null;
            return;
        }

        registeredNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(RequestMessageName);
        registeredNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(StateMessageName);
        registeredNetworkManager = null;
    }

    private void SendPeeState(ulong playerClientId, bool isPeeing)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.CustomMessagingManager == null || !networkManager.IsListening)
        {
            return;
        }

        if (networkManager.IsServer)
        {
            BroadcastPeeState(playerClientId, isPeeing);
            return;
        }

        using FastBufferWriter writer = CreateStateWriter(playerClientId, isPeeing);
        networkManager.CustomMessagingManager.SendNamedMessage(
            RequestMessageName,
            NetworkManager.ServerClientId,
            writer,
            NetworkDelivery.ReliableSequenced);
    }

    private void HandleRequestMessage(ulong senderClientId, FastBufferReader reader)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsServer || !TryReadState(reader, out ulong playerClientId, out bool isPeeing))
        {
            return;
        }

        PlayerControllerB? senderPlayer = FindPlayerByPlayerClientId(playerClientId);
        if (senderPlayer == null || senderPlayer.actualClientId != senderClientId)
        {
            return;
        }

        SetPeeState(playerClientId, isPeeing);
        BroadcastPeeState(playerClientId, isPeeing);
    }

    private void HandleStateMessage(ulong senderClientId, FastBufferReader reader)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager != null && !networkManager.IsServer && senderClientId != NetworkManager.ServerClientId)
        {
            return;
        }

        if (!TryReadState(reader, out ulong playerClientId, out bool isPeeing))
        {
            return;
        }

        SetPeeState(playerClientId, isPeeing);
    }

    private void BroadcastPeeState(ulong playerClientId, bool isPeeing)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.CustomMessagingManager == null || !networkManager.IsServer || !networkManager.IsListening)
        {
            return;
        }

        using FastBufferWriter writer = CreateStateWriter(playerClientId, isPeeing);
        networkManager.CustomMessagingManager.SendNamedMessageToAll(
            StateMessageName,
            writer,
            NetworkDelivery.ReliableSequenced);
    }

    private static FastBufferWriter CreateStateWriter(ulong playerClientId, bool isPeeing)
    {
        FastBufferWriter writer = new FastBufferWriter(16, Allocator.Temp);
        writer.WriteValueSafe(playerClientId);
        writer.WriteValueSafe(isPeeing);
        return writer;
    }

    private static bool TryReadState(FastBufferReader reader, out ulong playerClientId, out bool isPeeing)
    {
        playerClientId = 0;
        isPeeing = false;

        try
        {
            reader.ReadValueSafe(out playerClientId);
            reader.ReadValueSafe(out isPeeing);
            return true;
        }
        catch (Exception exception)
        {
            logger?.LogWarning($"Failed to read pee state message: {exception.Message}");
            return false;
        }
    }

    private void SetPeeState(ulong playerClientId, bool isPeeing)
    {
        if (!isPeeing)
        {
            StopStream(playerClientId);
            return;
        }

        PlayerControllerB? player = FindPlayerByPlayerClientId(playerClientId);
        if (!CanRenderPeeStream(player))
        {
            DestroyStream(playerClientId);
            return;
        }

        if (activeStreams.TryGetValue(playerClientId, out PeeStream stream) && stream != null)
        {
            stream.Bind(player!, OnStreamHitCharger);
            return;
        }

        activeStreams[playerClientId] = PeeStream.Create(player!, OnStreamHitCharger);
    }

    private void StopStream(ulong playerClientId)
    {
        if (activeStreams.TryGetValue(playerClientId, out PeeStream stream) && stream != null)
        {
            stream.StopFlow();
        }
    }

    private void OnStreamHitCharger(PlayerControllerB streamPlayer, ItemCharger charger, Vector3 hitPoint)
    {
        PlayerControllerB? localPlayer = GetLocalPlayer();
        if (localPlayer == null || streamPlayer != localPlayer || localPlayer.isPlayerDead)
        {
            return;
        }

        if (Time.time < nextLocalChargerShockTime)
        {
            return;
        }

        nextLocalChargerShockTime = Time.time + ChargerShockCooldown;
        PlayLocalChargerZap(charger);
        charger.PlayChargeItemEffectServerRpc((int)localPlayer.playerClientId);

        Vector3 shockForce = (localPlayer.transform.position - hitPoint).normalized * 10f + Vector3.up * 4f;
        localPlayer.DamagePlayer(
            ChargerShockDamage,
            hasDamageSFX: true,
            callRPC: true,
            causeOfDeath: CauseOfDeath.Electrocution,
            deathAnimation: 0,
            fallDamage: false,
            force: shockForce);
    }

    private static void PlayLocalChargerZap(ItemCharger charger)
    {
        if (charger.zapAudio != null)
        {
            charger.zapAudio.Play();
        }

        if (charger.chargeStationAnimator != null)
        {
            charger.chargeStationAnimator.SetTrigger("zap");
        }
    }

    private static bool CanRenderPeeStream(PlayerControllerB? player)
    {
        return player != null && player.isPlayerControlled && !player.isPlayerDead && player.gameObject.activeInHierarchy;
    }

    private static PlayerControllerB? FindPlayerByPlayerClientId(ulong playerClientId)
    {
        PlayerControllerB[]? players = StartOfRound.Instance != null ? StartOfRound.Instance.allPlayerScripts : null;
        if (players == null)
        {
            return null;
        }

        for (int i = 0; i < players.Length; i++)
        {
            PlayerControllerB player = players[i];
            if (player != null && player.playerClientId == playerClientId)
            {
                return player;
            }
        }

        return null;
    }

    private void CleanupInvalidStreams()
    {
        staleStreamIds.Clear();

        foreach (KeyValuePair<ulong, PeeStream> pair in activeStreams)
        {
            if (pair.Value == null || !pair.Value.HasValidTarget)
            {
                staleStreamIds.Add(pair.Key);
            }
        }

        for (int i = 0; i < staleStreamIds.Count; i++)
        {
            DestroyStream(staleStreamIds[i]);
        }
    }

    private void DestroyStream(ulong playerClientId)
    {
        if (!activeStreams.TryGetValue(playerClientId, out PeeStream stream))
        {
            return;
        }

        activeStreams.Remove(playerClientId);
        if (stream != null)
        {
            Destroy(stream.gameObject);
        }
    }
}
