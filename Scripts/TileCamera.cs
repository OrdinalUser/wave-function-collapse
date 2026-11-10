using Godot;

public partial class TileCamera : Camera3D
{
    [Export] float CameraSpeed = 100f;
    [Export] float ZoomSpeed = 1f;
    [Export] float MinZoom = 0.25f;
    [Export] float MaxZoom = 5f;
    [Export] Wfc Board; // Generator to frame on start and when F is pressed
    [Export] float FrameMargin = 0.05f; // Extra space around the board, fraction of its size
    [Export] float Distance = 500f; // How far back along the view direction the camera sits
    private float defaultSize;
    private float zoom = 1f;

    public override void _Ready()
    {
        defaultSize = Size;
        Position = new Vector3(Position.Y, Position.Y, Position.Y);
        // Deferred so the generator has loaded its config and the viewport has its final size
        if (Board != null) CallDeferred(MethodName.FrameBoard);
    }

    public void FrameBoard()
    {
        Aabb bounds = Board.GetWorldBounds();
        Vector3 center = bounds.GetCenter();

        // Project every corner onto the camera's right/up axes to get the board's extent on screen
        float halfWidth = 0, halfHeight = 0;
        for (int i = 0; i < 8; i++)
        {
            Vector3 offset = bounds.GetEndpoint(i) - center;
            halfWidth = Mathf.Max(halfWidth, Mathf.Abs(offset.Dot(Basis.X)));
            halfHeight = Mathf.Max(halfHeight, Mathf.Abs(offset.Dot(Basis.Y)));
        }

        // Orthographic Size is the visible height (or width with KeepWidth), so fit whichever side is tighter
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        float aspect = viewport.X / viewport.Y;
        float fitSize = KeepAspect == KeepAspectEnum.Height
            ? Mathf.Max(halfHeight * 2, halfWidth * 2 / aspect)
            : Mathf.Max(halfWidth * 2, halfHeight * 2 * aspect);

        Position = center + Basis.Z * Distance;
        defaultSize = fitSize * (1 + FrameMargin * 2);
        zoom = 1f;
        Size = defaultSize;
    }

    public override void _Process(double delta)
    {
        if (Board != null && Input.IsKeyPressed(Key.F))
        {
            FrameBoard();
        }
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
