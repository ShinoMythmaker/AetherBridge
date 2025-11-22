using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace AetherBridge.Services;

/// <summary>
/// HTTP server that exposes poseable character data to external tools like Blender
/// </summary>
public class BridgeServer : IDisposable
{
    private readonly CharacterService characterService;
    private readonly PoseService poseService;
    private readonly SkeletonService skeletonService;
    private readonly IPluginLog log;
    private readonly Configuration configuration;

    private HttpListener? httpListener;
    private CancellationTokenSource? cancellationTokenSource;
    private Task? listenerTask;
    private readonly List<StreamClient> streamClients = new();
    private readonly object streamClientsLock = new();

    public bool IsRunning => httpListener?.IsListening ?? false;

    public BridgeServer(CharacterService characterService, PoseService poseService, SkeletonService skeletonService, Configuration configuration, IPluginLog log)
    {
        this.characterService = characterService;
        this.poseService = poseService;
        this.skeletonService = skeletonService;
        this.configuration = configuration;
        this.log = log;
    }

    /// <summary>
    /// Start the HTTP server
    /// </summary>
    public void Start()
    {
        if (IsRunning)
        {
            log.Warning("Bridge server is already running");
            return;
        }

        try
        {
            var port = configuration.ServerPort;
            httpListener = new HttpListener();
            httpListener.Prefixes.Add($"http://localhost:{port}/");
            httpListener.Start();

            cancellationTokenSource = new CancellationTokenSource();
            listenerTask = Task.Run(() => ListenAsync(cancellationTokenSource.Token), cancellationTokenSource.Token);

            log.Information($"Bridge server started on port {port}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to start bridge server");
            Stop();
        }
    }

    /// <summary>
    /// Stop the HTTP server
    /// </summary>
    public void Stop()
    {
        try
        {
            cancellationTokenSource?.Cancel();
            httpListener?.Stop();
            httpListener?.Close();
            listenerTask?.Wait(TimeSpan.FromSeconds(2));

            log.Information("Bridge server stopped");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error stopping bridge server");
        }
        finally
        {
            cancellationTokenSource?.Dispose();
            cancellationTokenSource = null;
            httpListener = null;
            listenerTask = null;
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && httpListener != null && httpListener.IsListening)
        {
            try
            {
                var context = await httpListener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context), cancellationToken);
            }
            catch (HttpListenerException)
            {
                // Server stopped
                break;
            }
            catch (Exception ex)
            {
                log.Error(ex, "Error in listener loop");
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            // Enable CORS for Blender plugin
            response.AddHeader("Access-Control-Allow-Origin", "*");
            response.AddHeader("Access-Control-Allow-Methods", "GET, POST, PUT, OPTIONS");
            response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            var path = request.Url?.AbsolutePath ?? "/";

            switch (path)
            {
                case "/":
                case "/status":
                    HandleStatusRequest(response);
                    break;
                case "/characters":
                    if (request.HttpMethod == "GET")
                        HandleCharactersRequest(response);
                    else
                        SendError(response, 405, "Method not allowed");
                    break;
                case "/stream":
                    if (request.HttpMethod == "GET")
                        HandleStreamRequest(context);
                    else
                        SendError(response, 405, "Method not allowed");
                    break;
                default:
                    // Check if it's a character-specific endpoint
                    if (path.StartsWith("/character/"))
                    {
                        HandleCharacterEndpoint(request, response, path);
                    }
                    else
                    {
                        SendError(response, 404, "Not found");
                    }
                    break;
            }

            response.Close();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error handling request");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch { }
        }
    }

    private void HandleStatusRequest(HttpListenerResponse response)
    {
        var status = new
        {
            status = "online",
            version = "1.0.0",
            brioAvailable = poseService.IsBrioAvailable,
            timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(status);
        var bytes = Encoding.UTF8.GetBytes(json);

        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        response.StatusCode = 200;
        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private void HandleCharactersRequest(HttpListenerResponse response)
    {
        var characters = characterService.GetPoseableCharacters();

        var json = JsonSerializer.Serialize(characters, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var bytes = Encoding.UTF8.GetBytes(json);

        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        response.StatusCode = 200;
        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private void HandleStreamRequest(HttpListenerContext context)
    {
        var response = context.Response;

        try
        {
            response.ContentType = "text/event-stream";
            response.AddHeader("Cache-Control", "no-cache");
            response.AddHeader("Connection", "keep-alive");
            response.StatusCode = 200;

            var client = new StreamClient(context);

            lock (streamClientsLock)
            {
                streamClients.Add(client);
            }

            log.Information($"Stream client connected. Total clients: {streamClients.Count}");

            // Start sending updates on a background task
            Task.Run(async () =>
            {
                try
                {
                    while (!client.IsClosed)
                    {
                        var characters = characterService.GetPoseableCharacters();
                        var json = JsonSerializer.Serialize(characters, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                        });

                        var message = $"data: {json}\n\n";
                        await client.SendAsync(message);

                        await Task.Delay(33); // ~30 FPS
                    }
                }
                catch (Exception ex)
                {
                    log.Debug(ex, "Stream client disconnected");
                }
                finally
                {
                    lock (streamClientsLock)
                    {
                        streamClients.Remove(client);
                    }
                    client.Dispose();
                    log.Information($"Stream client removed. Total clients: {streamClients.Count}");
                }
            });
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error setting up stream");
        }
    }

    private void HandleCharacterEndpoint(HttpListenerRequest request, HttpListenerResponse response, string path)
    {
        // Extract object ID from path like /character/123456789/pose
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !ulong.TryParse(parts[1], out var objectId))
        {
            SendError(response, 400, "Invalid character ID");
            return;
        }

        var action = parts.Length > 2 ? parts[2] : "";

        switch (action)
        {
            case "pose":
                if (request.HttpMethod == "GET")
                    HandleGetPose(response, objectId);
                else if (request.HttpMethod == "POST" || request.HttpMethod == "PUT")
                    HandleSetPose(request, response, objectId);
                else
                    SendError(response, 405, "Method not allowed");
                break;

            case "bones":
                if (request.HttpMethod == "POST" || request.HttpMethod == "PUT")
                    HandleSetBones(request, response, objectId);
                else
                    SendError(response, 405, "Method not allowed");
                break;

            case "transform":
                if (request.HttpMethod == "GET")
                    HandleGetTransform(response, objectId);
                else if (request.HttpMethod == "POST" || request.HttpMethod == "PUT")
                    HandleSetTransform(request, response, objectId);
                else
                    SendError(response, 405, "Method not allowed");
                break;

            default:
                SendError(response, 404, "Unknown action");
                break;
        }
    }

    private void HandleGetPose(HttpListenerResponse response, ulong objectId)
    {
        var poseJson = poseService.GetPoseJson(objectId);
        if (poseJson == null)
        {
            SendError(response, 404, "Pose data not available");
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(poseJson);
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        response.StatusCode = 200;
        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private void HandleSetPose(HttpListenerRequest request, HttpListenerResponse response, ulong objectId)
    {
        try
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            var poseJson = reader.ReadToEnd();

            log.Information($"[DEBUG] Received pose data for object {objectId}");
            log.Information($"[DEBUG] Pose JSON length: {poseJson.Length} characters");
            log.Information($"[DEBUG] Pose JSON preview (first 500 chars): {(poseJson.Length > 500 ? poseJson.Substring(0, 500) : poseJson)}");

            var success = poseService.SetPoseJson(objectId, poseJson, false);

            log.Information($"[DEBUG] SetPoseJson result: {success}");

            var result = new { success, objectId };
            var json = JsonSerializer.Serialize(result);
            var bytes = Encoding.UTF8.GetBytes(json);

            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            response.StatusCode = success ? 200 : 400;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error setting pose");
            SendError(response, 500, "Failed to set pose");
        }
    }

    private void HandleSetBones(HttpListenerRequest request, HttpListenerResponse response, ulong objectId)
    {
        try
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            var bonesJson = reader.ReadToEnd();

            // Check if direct method is requested via query parameter
            var useDirect = request.QueryString["method"] == "direct";

            log.Information($"[DEBUG] Received bones data for object {objectId}, length: {bonesJson.Length}, method: {(useDirect ? "direct" : "brio")}");
            log.Information($"[DEBUG] Raw bones JSON: {(bonesJson.Length > 1000 ? bonesJson.Substring(0, 1000) : bonesJson)}");

            // Parse the incoming bone data
            var bonesData = JsonSerializer.Deserialize<Dictionary<string, BoneTransformData>>(bonesJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (bonesData == null || bonesData.Count == 0)
            {
                SendError(response, 400, "Invalid or empty bone data");
                return;
            }

            log.Information($"[DEBUG] Parsed {bonesData.Count} bones: {string.Join(", ", bonesData.Keys)}");

            bool success;

            if (useDirect)
            {
                // Direct skeleton manipulation - bypasses Brio
                var boneTransforms = new Dictionary<string, BoneTransform>();

                foreach (var kvp in bonesData)
                {
                    boneTransforms[kvp.Key] = new BoneTransform
                    {
                        Position = kvp.Value.Position?.ToVector3(),
                        Rotation = kvp.Value.Rotation?.ToQuaternion(),
                        Scale = kvp.Value.Scale?.ToVector3()
                    };
                }

                success = skeletonService.SetBoneTransforms(objectId, boneTransforms);
                log.Information($"[DEBUG] Direct skeleton manipulation result: {success}");
            }
            else
            {
                // Build a minimal pose JSON with only the specified bones
                var poseData = BuildMinimalPoseJson(bonesData);

                log.Information($"[DEBUG] Built pose JSON, length: {poseData.Length}");
                log.Information($"[DEBUG] Pose JSON preview: {(poseData.Length > 500 ? poseData.Substring(0, 500) : poseData)}");

                // Apply the pose via Brio
                success = poseService.SetPoseJson(objectId, poseData, false);
            }

            var result = new { success, objectId, bonesUpdated = bonesData.Count, method = useDirect ? "direct" : "brio" };
            var json = JsonSerializer.Serialize(result);
            var bytes = Encoding.UTF8.GetBytes(json);

            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            response.StatusCode = success ? 200 : 400;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error setting bones");
            SendError(response, 500, "Failed to set bones");
        }
    }

    private string BuildMinimalPoseJson(Dictionary<string, BoneTransformData> bones)
    {
        var boneEntries = new List<string>();

        foreach (var kvp in bones)
        {
            var boneName = kvp.Key;
            var transform = kvp.Value;

            var posStr = transform.Position != null
                ? $"{transform.Position.X:F6}, {transform.Position.Y:F6}, {transform.Position.Z:F6}"
                : "0.000000, 0.000000, 0.000000";

            var rotStr = transform.Rotation != null
                ? $"{transform.Rotation.X:F6}, {transform.Rotation.Y:F6}, {transform.Rotation.Z:F6}, {transform.Rotation.W:F6}"
                : "0.000000, 0.000000, 0.000000, 1.000000";

            var scaleStr = transform.Scale != null
                ? $"{transform.Scale.X:F8}, {transform.Scale.Y:F8}, {transform.Scale.Z:F8}"
                : "1.00000000, 1.00000000, 1.00000000";

            var boneEntry = $"\"{boneName}\": {{\"Position\": \"{posStr}\", \"Rotation\": \"{rotStr}\", \"Scale\": \"{scaleStr}\"}}";
            boneEntries.Add(boneEntry);

            log.Debug($"[DEBUG] Bone {boneName}: Pos={posStr}, Rot={rotStr}, Scale={scaleStr}");
        }

        var bonesJson = string.Join(", ", boneEntries);

        return $"{{\"FileExtension\": \".pose\", \"TypeName\": \"Aetherblend Pose\", \"FileVersion\": 2, \"Bones\": {{{bonesJson}}}}}";
    }

    private void HandleGetTransform(HttpListenerResponse response, ulong objectId)
    {
        var transform = poseService.GetTransform(objectId);
        if (transform == null)
        {
            SendError(response, 404, "Transform data not available");
            return;
        }

        var result = new
        {
            objectId,
            position = transform.Value.Position,
            rotation = transform.Value.Rotation,
            scale = transform.Value.Scale
        };

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        response.StatusCode = 200;
        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private void HandleSetTransform(HttpListenerRequest request, HttpListenerResponse response, ulong objectId)
    {
        try
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            var json = reader.ReadToEnd();

            var data = JsonSerializer.Deserialize<TransformData>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (data == null)
            {
                SendError(response, 400, "Invalid transform data");
                return;
            }

            var success = poseService.SetTransform(objectId, data.Position, data.Rotation, data.Scale, data.Additive);

            var result = new { success, objectId };
            var resultJson = JsonSerializer.Serialize(result);
            var bytes = Encoding.UTF8.GetBytes(resultJson);

            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            response.StatusCode = success ? 200 : 400;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error setting transform");
            SendError(response, 500, "Failed to set transform");
        }
    }

    private void SendError(HttpListenerResponse response, int statusCode, string message)
    {
        var error = new { error = message, statusCode };
        var json = JsonSerializer.Serialize(error);
        var bytes = Encoding.UTF8.GetBytes(json);

        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        response.StatusCode = statusCode;
        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private class TransformData
    {
        public Vector3? Position { get; set; }
        public Quaternion? Rotation { get; set; }
        public Vector3? Scale { get; set; }
        public bool Additive { get; set; } = false;
    }

    private class BoneTransformData
    {
        public Vec3Data? Position { get; set; }
        public QuatData? Rotation { get; set; }
        public Vec3Data? Scale { get; set; }
    }

    private class Vec3Data
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public Vector3 ToVector3() => new Vector3(X, Y, Z);
    }

    private class QuatData
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; }

        public Quaternion ToQuaternion() => new Quaternion(X, Y, Z, W);
    }

    private class StreamClient : IDisposable
    {
        private readonly HttpListenerContext context;
        private bool closed;

        public bool IsClosed => closed;

        public StreamClient(HttpListenerContext context)
        {
            this.context = context;
        }

        public async Task SendAsync(string message)
        {
            if (closed) return;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                await context.Response.OutputStream.FlushAsync();
            }
            catch
            {
                closed = true;
                throw;
            }
        }

        public void Dispose()
        {
            closed = true;
            try
            {
                context.Response.Close();
            }
            catch { }
        }
    }

    public void Dispose()
    {
        lock (streamClientsLock)
        {
            foreach (var client in streamClients)
            {
                client.Dispose();
            }
            streamClients.Clear();
        }

        Stop();
    }
}
