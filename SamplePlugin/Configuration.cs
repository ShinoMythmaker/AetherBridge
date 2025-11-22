using Dalamud.Configuration;
using System;

namespace AetherBridge;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Server configuration
    public int ServerPort { get; set; } = 8765;
    public bool AutoStartServer { get; set; } = false;
    
    // Animation configuration
    public float BoneInterpolationSpeed { get; set; } = 0.3f; // 0-1, higher = snappier, lower = smoother

    // The below exist just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
