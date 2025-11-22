using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherBridge.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("AetherBridge Settings###ConfigWindow")
    {
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(350, 200);
        SizeCondition = ImGuiCond.Always;

        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.TextUnformatted("Server Settings");
        ImGui.Separator();
        ImGui.Spacing();

        // Server port configuration
        var serverPort = configuration.ServerPort;
        if (ImGui.InputInt("Server Port", ref serverPort))
        {
            if (serverPort >= 1024 && serverPort <= 65535)
            {
                configuration.ServerPort = serverPort;
                configuration.Save();
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Port for HTTP server (1024-65535)");
        }

        // Auto-start server
        var autoStart = configuration.AutoStartServer;
        if (ImGui.Checkbox("Auto-start server on login", ref autoStart))
        {
            configuration.AutoStartServer = autoStart;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextWrapped("Note: Restart the server after changing port settings.");
    }
}
