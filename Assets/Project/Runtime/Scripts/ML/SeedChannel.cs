using System;
using Unity.MLAgents.SideChannels;
using UnityEngine;

/// <summary>
/// One-way SideChannel that receives an integer seed from Python and applies it
/// to UnityEngine.Random before each episode begins.
///
/// Seeding is inherently process-global (there is only one UnityEngine.Random state), so
/// exactly one instance must ever be registered with SideChannelManager even when multiple
/// BlackOutEpisodeCoordinator instances share a scene (multi-arena training builds) — a
/// second RegisterSideChannel call with the same ChannelId throws. TryRegister/TryUnregister
/// make that safe: only the first coordinator to call TryRegister actually registers: the
/// rest get 'false' back and skip registration/unregistration entirely.
/// </summary>
public class SeedChannel : SideChannel
{
    static readonly Guid kChannelId = new Guid("7a8b9c0d-1e2f-3a4b-5c6d-7e8f9a0b1c2d");
    static bool s_registered;

    public SeedChannel()
    {
        ChannelId = kChannelId;
    }

    /// <summary>Registers this channel iff no SeedChannel is registered yet. Returns whether
    /// this call actually registered it (the caller only owns unregistration if true).</summary>
    public static bool TryRegister(SeedChannel channel)
    {
        if (s_registered) return false;
        SideChannelManager.RegisterSideChannel(channel);
        s_registered = true;
        return true;
    }

    /// <summary>Unregisters this channel iff the caller was the one that registered it.</summary>
    public static void TryUnregister(SeedChannel channel, bool owned)
    {
        if (!owned) return;
        SideChannelManager.UnregisterSideChannel(channel);
        s_registered = false;
    }

    protected override void OnMessageReceived(IncomingMessage msg)
    {
        int seed = msg.ReadInt32();
        UnityEngine.Random.InitState(seed);
    }
}
