using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetherBridge.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("AetherBridge##MainWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        // Server control section
        ImGui.TextUnformatted("AetherBridge Server");
        ImGui.Separator();

        var isRunning = plugin.BridgeServer.IsRunning;
        var statusText = isRunning ? "Running" : "Stopped";
        var statusColor = isRunning ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1);

        ImGui.TextColored(statusColor, $"Status: {statusText}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"| Port: {plugin.Configuration.ServerPort}");

        if (isRunning)
        {
            if (ImGui.Button("Stop Server"))
            {
                plugin.BridgeServer.Stop();
            }
        }
        else
        {
            if (ImGui.Button("Start Server"))
            {
                plugin.BridgeServer.Start();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Settings"))
        {
            plugin.ToggleConfigUi();
        }

        ImGui.Spacing();
        ImGui.Spacing();

        // Poseable characters section
        ImGui.TextUnformatted("Poseable Characters");
        ImGui.Separator();

        var characters = plugin.CharacterService.GetPoseableCharacters();
        ImGui.TextUnformatted($"Total characters: {characters.Count}");

        ImGui.Spacing();

        // Character list with scrollbar
        using (var child = ImRaii.Child("CharacterList", new Vector2(0, -30), true))
        {
            if (child.Success)
            {
                if (characters.Count == 0)
                {
                    ImGui.TextUnformatted("No characters found. Make sure you're logged in and near other players.");
                }
                else
                {
                    // Table header
                    if (ImGui.BeginTable("CharacterTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                    {
                        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 100);
                        ImGui.TableHeadersRow();

                        foreach (var character in characters)
                        {
                            ImGui.TableNextRow();

                            // Name
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(character.Name);

                            // Object ID
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted($"{character.ObjectId}");
                        }

                        ImGui.EndTable();
                    }
                }
            }
        }

        // Footer info
        ImGui.Separator();
        if (isRunning)
        {
            ImGui.TextUnformatted($"API Endpoint: http://localhost:{plugin.Configuration.ServerPort}/characters");
        }
        else
        {
            ImGui.TextUnformatted("Start the server to enable Blender integration");
        }
    }
}
