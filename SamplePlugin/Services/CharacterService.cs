using Dalamud.Plugin.Services;
using AetherBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace AetherBridge.Services;

/// <summary>
/// Service for tracking and managing poseable characters in the game world
/// </summary>
public class CharacterService : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly PoseService poseService;

    private readonly Dictionary<ulong, PoseableCharacter> characters = new();
    private readonly object lockObject = new();

    public CharacterService(IObjectTable objectTable, IClientState clientState, IFramework framework, IPluginLog log, PoseService poseService)
    {
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.framework = framework;
        this.log = log;
        this.poseService = poseService;

        // Update character list on every frame
        framework.Update += OnFrameworkUpdate;

        log.Information("CharacterService initialized");
    }

    /// <summary>
    /// Get all currently tracked poseable characters
    /// </summary>
    public IReadOnlyList<PoseableCharacter> GetPoseableCharacters()
    {
        lock (lockObject)
        {
            return characters.Values.ToList();
        }
    }

    /// <summary>
    /// Get a specific character by ID
    /// </summary>
    public PoseableCharacter? GetCharacter(ulong objectId)
    {
        lock (lockObject)
        {
            return characters.TryGetValue(objectId, out var character) ? character : null;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!clientState.IsLoggedIn)
        {
            lock (lockObject)
            {
                characters.Clear();
            }
            return;
        }

        UpdateCharacterList();
    }

    private void UpdateCharacterList()
    {
        lock (lockObject)
        {
            var currentObjectIds = new HashSet<ulong>();

            foreach (var obj in objectTable)
            {
                if (obj == null || obj.ObjectKind != ObjectKind.Player)
                    continue;

                var character = obj as ICharacter;
                if (character == null)
                    continue;

                var objectId = obj.GameObjectId;
                currentObjectIds.Add(objectId);

                if (!characters.ContainsKey(objectId))
                {
                    // New character found
                    var poseableChar = CreatePoseableCharacter(character);

                    // Only add if pose data is available (filters out duplicates in GPose)
                    if (!string.IsNullOrEmpty(poseableChar.PoseDataJson))
                    {
                        characters[objectId] = poseableChar;
                    }
                }
                else
                {
                    // Update existing character
                    UpdatePoseableCharacter(characters[objectId], character);
                }
            }

            // Remove characters that are no longer in the object table
            var toRemove = characters
                .Where(kvp => !currentObjectIds.Contains(kvp.Key))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                characters.Remove(key);
            }
        }
    }

    private PoseableCharacter CreatePoseableCharacter(ICharacter character)
    {
        var poseableChar = new PoseableCharacter
        {
            ObjectId = character.GameObjectId,
            Name = character.Name.ToString(),
            LastUpdated = DateTime.UtcNow
        };

        // Fetch initial pose data from Brio if available
        UpdatePoseData(poseableChar);

        return poseableChar;
    }

    private void UpdatePoseableCharacter(PoseableCharacter poseableChar, ICharacter character)
    {
        poseableChar.LastUpdated = DateTime.UtcNow;

        // Update pose data from Brio
        UpdatePoseData(poseableChar);
    }

    private void UpdatePoseData(PoseableCharacter poseableChar)
    {
        if (!poseService.IsBrioAvailable)
            return;

        try
        {
            // Get pose JSON from Brio
            var poseJson = poseService.GetPoseJson(poseableChar.ObjectId);
            poseableChar.PoseDataJson = poseJson;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Error updating pose data for character {poseableChar.Name}");
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        log.Information("CharacterService disposed");
    }
}
