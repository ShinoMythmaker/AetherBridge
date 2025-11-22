using System;

namespace AetherBridge.Models;

/// <summary>
/// Represents a character that can be posed and exported to external tools like Blender
/// </summary>
[Serializable]
public class PoseableCharacter
{
    /// <summary>
    /// Unique identifier for this character
    /// </summary>
    public ulong ObjectId { get; set; }

    /// <summary>
    /// Character name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Raw pose data in JSON format (compatible with Brio/Anamnesis)
    /// </summary>
    public string? PoseDataJson { get; set; }

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
