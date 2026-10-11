using Treemap;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Screen geometry for the host pointer tests. Reads only the public frame and
/// camera, so production code carries no test-only helpers.
/// </summary>
internal static class FormGeometry
{
    /// <summary>
    /// Screen centre of a frame cell, so tests can press the cell they mean
    /// without duplicating the camera transform.
    /// </summary>
    public static Point CellCenter(TreemapForm form, int nodeId)
        => CellPoint(form, nodeId, header: false);

    /// <summary>
    /// Screen point on a folder's header strip, so tests latch the folder
    /// itself rather than the deepest child under its centre.
    /// </summary>
    public static Point CellHeaderPoint(TreemapForm form, int nodeId)
        => CellPoint(form, nodeId, header: true);

    private static Point CellPoint(TreemapForm form, int nodeId, bool header)
    {
        foreach (TweenedCell cell in form.Controller.CurrentFrame)
        {
            if (cell.NodeId != nodeId)
            {
                continue;
            }

            RectF area = header && !cell.HeaderBounds.IsEmpty ? cell.HeaderBounds : cell.Bounds;
            PointF centre = new(
                area.X + area.Width / 2,
                area.Y + area.Height / 2);
            PointF screen = form.Controller.Camera.ContentToScreen(centre);
            return new Point((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        }

        throw new InvalidOperationException($"Node {nodeId} has no cell in the current frame.");
    }
}
