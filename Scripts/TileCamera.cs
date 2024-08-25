using Godot;

public partial class TileCamera : Camera3D
{
    [Export] float CameraSpeed = 100f;
    [Export] float ZoomSpeed = 1f;
    [Export] float MinZoom = 0.25f;
    [Export] float MaxZoom = 5f;
    private float defaultSize;
    private float zoom = 1f;

    public override void _Ready()
    {
        defaultSize = Size;
        Position = new Vector3(Position.Y, Position.Y, Position.Y);
    }

    public override void _Process(double delta)
    {
        if (Input.IsKeyPressed(Key.W))
        {
            Position = Position + new Vector3((float)delta * -CameraSpeed * zoom, 0, (float)delta * -CameraSpeed * zoom);
        }
        if (Input.IsKeyPressed(Key.D))
        {
            Position = Position + new Vector3((float)delta * CameraSpeed * zoom, 0, (float)delta * -CameraSpeed * zoom);
        }
        if (Input.IsKeyPressed(Key.S))
        {
            Position = Position + new Vector3((float)delta * CameraSpeed * zoom, 0, (float)delta * CameraSpeed * zoom);
        }
        if (Input.IsKeyPressed(Key.A))
        {
            Position = Position + new Vector3((float)delta * -CameraSpeed * zoom, 0, (float)delta * CameraSpeed * zoom);
        }
        if (Input.IsKeyPressed(Key.E))
        {
            zoom = Mathf.Clamp((float)delta * -ZoomSpeed * zoom + zoom, MinZoom, MaxZoom);
            Size = defaultSize * zoom;
        }
        if (Input.IsKeyPressed(Key.Q))
        {
            zoom = Mathf.Clamp((float)delta * ZoomSpeed * zoom + zoom, MinZoom, MaxZoom);
            Size = defaultSize * zoom;
        }
    }
}
