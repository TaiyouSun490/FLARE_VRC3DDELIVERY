namespace AvatarCatalog.Remote
{
    // Shared wire-data safety ceilings, not a claim of acceptable rendering performance.
    // Keep exporter and Udon decoder in sync. Changing these requires rebuilding both.
    public static class Rac2Capacity
    {
        public const int MaxVertices = 250000;
        public const int MaxIndices = 1500000;
        public const int MaxBytes = 134217728;
    }
}
