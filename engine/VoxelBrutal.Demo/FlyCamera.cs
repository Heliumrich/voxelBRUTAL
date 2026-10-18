using System.Numerics;
using Brutal.GlfwApi;
using VoxelBrutal.Core.Lod;

namespace VoxelBrutal.Demo;

internal sealed class FlyCamera
{
    public WorldPosition Position { get; private set; }
    public FlyCamera(WorldPosition position) => Position = position;
    public float Yaw { get; set; } = -MathF.PI / 2;
    public float Pitch { get; set; } = -0.38f;
    private bool looking;
    private double lastX, lastY;

    public Vector3 Forward => Vector3.Normalize(new(MathF.Cos(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), MathF.Sin(Yaw) * MathF.Cos(Pitch)));

    public void Update(GlfwWindow window, float delta)
    {
        bool mouse = window.GetMouseButton(GlfwMouseButton.Right) == GlfwButtonAction.Press;
        if (mouse != looking)
        {
            looking = mouse;
            window.SetInputMode(GlfwInputMode.Cursor, mouse ? Glfw.Constants.GLFW_CURSOR_DISABLED : Glfw.Constants.GLFW_CURSOR_NORMAL);
            lastX = window.CursorPos.X; lastY = window.CursorPos.Y;
        }
        if (mouse)
        {
            var cursor = window.CursorPos;
            Yaw += (float)(cursor.X - lastX) * 0.002f;
            Pitch = Math.Clamp(Pitch - (float)(cursor.Y - lastY) * 0.002f, -1.55f, 1.55f);
            lastX = cursor.X; lastY = cursor.Y;
        }
        float turn = delta * 1.5f;
        if (Down(GlfwKey.Left)) Yaw -= turn;
        if (Down(GlfwKey.Right)) Yaw += turn;
        if (Down(GlfwKey.Up)) Pitch = Math.Min(1.55f, Pitch + turn);
        if (Down(GlfwKey.Down)) Pitch = Math.Max(-1.55f, Pitch - turn);
        Vector3 movement = Vector3.Zero;
        Vector3 forward = Forward, right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        if (Down(GlfwKey.W)) movement += forward;
        if (Down(GlfwKey.S)) movement -= forward;
        if (Down(GlfwKey.D)) movement += right;
        if (Down(GlfwKey.A)) movement -= right;
        if (Down(GlfwKey.Space)) movement.Y++;
        if (Down(GlfwKey.LeftControl)) movement.Y--;
        if (movement.LengthSquared() > 0)
            Move(Vector3.Normalize(movement) * delta * (Down(GlfwKey.LeftShift) ? 400 : 80));
        bool Down(GlfwKey key) => window.GetKey(key) != GlfwKeyAction.Release;
    }

    public void Move(Vector3 displacement)
    {
        double x = Position.OffsetX + displacement.X, y = Position.OffsetY + displacement.Y, z = Position.OffsetZ + displacement.Z;
        long ix = (long)Math.Floor(x), iy = (long)Math.Floor(y), iz = (long)Math.Floor(z);
        Position = new(checked(Position.X + ix), checked(Position.Y + iy), checked(Position.Z + iz), x - ix, y - iy, z - iz);
    }

    public Matrix4x4 ViewProjection(float aspect)
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(Vector3.Zero, Forward, Vector3.UnitY);
        // Infinite reverse-Z: near/distance, clear0, GREATER compare. Translation stays in integer space.
        const float near = 0.2f;
        float f = 1 / MathF.Tan(70 * MathF.PI / 360);
        var projection = new Matrix4x4(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, 0, -1, 0, 0, near, 0);
        return view * projection;
    }
}
