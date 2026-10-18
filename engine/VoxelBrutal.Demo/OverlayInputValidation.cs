using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace VoxelBrutal.Demo;

/// <summary>Drives Dear ImGui's native event queue; it never assigns the frame-rate setting directly.</summary>
internal static class OverlayInputValidation
{
    public static void Run(ImGuiOverlay overlay, DisplaySettings settings, Action renderFrame)
    {
        ArgumentNullException.ThrowIfNull(renderFrame);
        var pointer = new float2(0, 0);
        void Frame(Action<ImGuiIOPtr>? events = null)
        {
            overlay.ValidationInput = io =>
            {
                io.AddFocusEvent(true);
                io.AddMouseViewportEvent(ImGui.GetMainViewport().ID);
                io.AddMousePosEvent(pointer.X, pointer.Y);
                events?.Invoke(io);
            };
            renderFrame();
        }

        void TypeFrameRate(string text, int expected)
        {
            pointer = overlay.FrameRateControlCenter;
            Require(pointer.X > 0 && pointer.Y > 0, "Frame-rate slider has not been laid out.");
            Frame(io => { io.AddKeyEvent(ImGuiKey.Mod_Ctrl, true); io.AddKeyEvent(ImGuiKey.LeftCtrl, true); });
            Frame(io => io.AddMouseButtonEvent(0, true));
            Frame(io => io.AddMouseButtonEvent(0, false));
            Frame(io => io.AddKeyEvent(ImGuiKey.A, true));
            Frame(io => { io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.Mod_Ctrl, false); io.AddKeyEvent(ImGuiKey.LeftCtrl, false); });
            Frame(io => io.AddInputCharactersUTF8(text));
            Frame(io => io.AddKeyEvent(ImGuiKey.Enter, true));
            Frame(io => io.AddKeyEvent(ImGuiKey.Enter, false));
            Frame();
            Require(settings.FrameRateLimit == expected,
                $"Native Ctrl+click slider input '{text}' produced {settings.FrameRateLimit}, expected {expected}.");
            Console.WriteLine($"PASS UI native Ctrl+click frame rate '{text}' => {settings.FrameRateLimit}");
        }

        try
        {
            overlay.SettingsOpen = true;
            Frame(); Frame(); Frame();
            Require(overlay.WantsCaptureMouse && overlay.WantsCaptureKeyboard, "Open settings must capture game controls.");
            TypeFrameRate("144", 144);
            TypeFrameRate("1", 30);
            TypeFrameRate("999", 360);
            TypeFrameRate("60", 60);
            Frame(io => io.AddKeyEvent(ImGuiKey.F1, true));
            Require(!overlay.SettingsOpen, "F1 must close the settings window through the native key queue.");
            Frame(io => io.AddKeyEvent(ImGuiKey.F1, false));
            Frame(io => io.AddKeyEvent(ImGuiKey.F1, true));
            Require(overlay.SettingsOpen, "F1 must reopen the settings window through the native key queue.");
            Frame(io => io.AddKeyEvent(ImGuiKey.F1, false));
            Console.WriteLine("PASS UI F1 settings toggle and gameplay input capture");
        }
        finally
        {
            overlay.ValidationInput = null;
            var io = ImGui.GetIO();
            io.AddMouseButtonEvent(0, false);
            io.AddKeyEvent(ImGuiKey.Mod_Ctrl, false); io.AddKeyEvent(ImGuiKey.LeftCtrl, false);
            io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.Enter, false); io.AddKeyEvent(ImGuiKey.F1, false);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
