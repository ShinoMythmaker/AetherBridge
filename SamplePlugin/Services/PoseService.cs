using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System;
using System.Numerics;

namespace AetherBridge.Services;

/// <summary>
/// Service for reading and writing character pose data using Brio's IPC
/// </summary>
public class PoseService : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;

    // Brio IPC subscribers
    private ICallGateSubscriber<(int, int)>? brioVersionIpc;
    private ICallGateSubscriber<IGameObject, string>? getPoseJsonIpc;
    private ICallGateSubscriber<IGameObject, string, bool, bool>? setPoseJsonIpc;
    private ICallGateSubscriber<IGameObject, Vector3?, Quaternion?, Vector3?, bool, bool>? setTransformIpc;
    private ICallGateSubscriber<IGameObject, (Vector3?, Quaternion?, Vector3?)>? getTransformIpc;

    private bool brioAvailable = false;

    public bool IsBrioAvailable => brioAvailable;

    public PoseService(IDalamudPluginInterface pluginInterface, IPluginLog log, IObjectTable objectTable, IFramework framework)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.objectTable = objectTable;
        this.framework = framework;

        InitializeBrioIpc();
    }

    private void InitializeBrioIpc()
    {
        try
        {
            // Initialize Brio IPC subscribers
            brioVersionIpc = pluginInterface.GetIpcSubscriber<(int, int)>("Brio.ApiVersion");
            getPoseJsonIpc = pluginInterface.GetIpcSubscriber<IGameObject, string>("Brio.Actor.Pose.GetPoseAsJson");
            setPoseJsonIpc = pluginInterface.GetIpcSubscriber<IGameObject, string, bool, bool>("Brio.Actor.Pose.LoadFromJson");
            setTransformIpc = pluginInterface.GetIpcSubscriber<IGameObject, Vector3?, Quaternion?, Vector3?, bool, bool>("Brio.Actor.SetModelTransform");
            getTransformIpc = pluginInterface.GetIpcSubscriber<IGameObject, (Vector3?, Quaternion?, Vector3?)>("Brio.Actor.GetModelTransform");

            // Check if Brio is available
            try
            {
                var version = brioVersionIpc.InvokeFunc();
                brioAvailable = true;
                log.Information($"Brio detected - API Version: {version.Item1}.{version.Item2}");
            }
            catch
            {
                brioAvailable = false;
                log.Warning("Brio not detected - pose manipulation features will be limited");
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to initialize Brio IPC");
            brioAvailable = false;
        }
    }

    /// <summary>
    /// Get the pose data for a character as JSON
    /// </summary>
    public string? GetPoseJson(ulong objectId)
    {
        if (!brioAvailable || getPoseJsonIpc == null)
        {
            log.Warning("Brio is not available for getting pose data");
            return null;
        }

        try
        {
            string? result = null;
            framework.RunOnFrameworkThread(() =>
            {
                var gameObject = GetGameObject(objectId);
                if (gameObject != null)
                {
                    result = getPoseJsonIpc.InvokeFunc(gameObject);
                }
                else
                {
                    log.Warning($"GameObject not found for ID: {objectId}");
                }
            }).Wait();
            return result;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to get pose JSON for object {objectId}");
            return null;
        }
    }

    /// <summary>
    /// Set the pose data for a character from JSON
    /// </summary>
    public bool SetPoseJson(ulong objectId, string poseJson, bool isCmpFormat = false)
    {
        if (!brioAvailable || setPoseJsonIpc == null)
        {
            log.Warning("Brio is not available for setting pose data");
            return false;
        }

        try
        {
            bool result = false;
            framework.RunOnFrameworkThread(() =>
            {
                var gameObject = GetGameObject(objectId);
                if (gameObject != null)
                {
                    log.Information($"[DEBUG] Calling Brio SetPose for object {objectId}, isCmpFormat: {isCmpFormat}");
                    result = setPoseJsonIpc.InvokeFunc(gameObject, poseJson, isCmpFormat);
                    log.Information($"[DEBUG] Brio SetPose result: {result}");
                }
                else
                {
                    log.Warning($"GameObject not found for ID: {objectId}");
                }
            }).Wait();
            return result;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to set pose JSON for object {objectId}");
            return false;
        }
    }

    /// <summary>
    /// Set the transform (position, rotation, scale) for a character
    /// </summary>
    public bool SetTransform(ulong objectId, Vector3? position, Quaternion? rotation, Vector3? scale, bool additive = false)
    {
        if (!brioAvailable || setTransformIpc == null)
        {
            log.Warning("Brio is not available for setting transform");
            return false;
        }

        try
        {
            bool result = false;
            framework.RunOnFrameworkThread(() =>
            {
                var gameObject = GetGameObject(objectId);
                if (gameObject != null)
                {
                    result = setTransformIpc.InvokeFunc(gameObject, position, rotation, scale, additive);
                    log.Debug($"Set transform for object {objectId}: {result}");
                }
                else
                {
                    log.Warning($"GameObject not found for ID: {objectId}");
                }
            }).Wait();
            return result;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to set transform for object {objectId}");
            return false;
        }
    }

    /// <summary>
    /// Get the transform (position, rotation, scale) for a character
    /// </summary>
    public (Vector3? Position, Quaternion? Rotation, Vector3? Scale)? GetTransform(ulong objectId)
    {
        if (!brioAvailable || getTransformIpc == null)
        {
            log.Warning("Brio is not available for getting transform");
            return null;
        }

        try
        {
            (Vector3? Position, Quaternion? Rotation, Vector3? Scale)? result = null;
            framework.RunOnFrameworkThread(() =>
            {
                var gameObject = GetGameObject(objectId);
                if (gameObject != null)
                {
                    result = getTransformIpc.InvokeFunc(gameObject);
                }
                else
                {
                    log.Warning($"GameObject not found for ID: {objectId}");
                }
            }).Wait();
            return result;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to get transform for object {objectId}");
            return null;
        }
    }

    private IGameObject? GetGameObject(ulong objectId)
    {
        foreach (var obj in objectTable)
        {
            if (obj != null && obj.GameObjectId == objectId)
                return obj;
        }
        return null;
    }

    public void Dispose()
    {
        // IPC subscribers don't need explicit disposal
        log.Information("PoseService disposed");
    }
}
