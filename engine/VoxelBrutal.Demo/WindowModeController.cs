using Brutal.GlfwApi;
using Brutal.Numerics;

namespace VoxelBrutal.Demo;

/// <summary>Borderless desktop-sized popup, without a display mode switch or exclusive monitor ownership.</summary>
internal sealed class WindowModeController
{
    private readonly GlfwWindow window;
    private int2 windowedPosition, windowedSize;
    private bool initialized;
    public bool Borderless { get; private set; }

    public WindowModeController(GlfwWindow window)
    {
        this.window = window;
        windowedPosition = window.Position;
        windowedSize = window.Size;
    }

    public void Apply(bool borderless)
    {
        if (initialized && Borderless == borderless) return;
        if (borderless)
        {
            if (!initialized || !Borderless) { windowedPosition = window.Position; windowedSize = window.Size; }
            GlfwMonitor monitor = BestMonitor();
            window.SetAttribute(GlfwWindowAttribute.Decorated, 0);
            window.SetAttribute(GlfwWindowAttribute.Resizable, 0);
            window.Position = monitor.Position;
            window.Size = monitor.Size;
        }
        else
        {
            window.SetAttribute(GlfwWindowAttribute.Decorated, 1);
            window.SetAttribute(GlfwWindowAttribute.Resizable, 1);
            window.Size = windowedSize;
            window.Position = windowedPosition;
        }
        initialized = true;
        Borderless = borderless;
    }

    private GlfwMonitor BestMonitor()
    {
        GlfwMonitor best = Glfw.PrimaryMonitor;
        long bestArea = -1;
        int2 pos = window.Position, size = window.Size;
        foreach (GlfwMonitor monitor in Glfw.GetMonitors())
        {
            int2 mp = monitor.Position, ms = monitor.Size;
            long width = Math.Max(0L, Math.Min((long)pos.X + size.X, (long)mp.X + ms.X) - Math.Max(pos.X, mp.X));
            long height = Math.Max(0L, Math.Min((long)pos.Y + size.Y, (long)mp.Y + ms.Y) - Math.Max(pos.Y, mp.Y));
            long area = width * height;
            if (area > bestArea) { best = monitor; bestArea = area; }
        }
        return best;
    }
}
