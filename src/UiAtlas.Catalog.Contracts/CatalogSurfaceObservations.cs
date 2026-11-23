namespace UiAtlas.Catalog;

public sealed record CatalogSceneRect(int X, int Y, int Width, int Height);
public sealed record CatalogSceneControl(string Id, CatalogSceneRect[] VisibleRegions, bool UsePixelCrop);
public sealed record CatalogSurfaceFrame(string Surface, string Variant, string VariantName, string SourceRef,
    string Bundle, string BundleHash, long Frame, long WindowHandle, CatalogSceneRect Bounds, CatalogSceneControl[] Controls, bool ClearView = false, string? Exclusion = null);
public sealed record CatalogSurfaceObservations(string MapId, string Revision, string Generation, CatalogSurfaceFrame[] Frames);
