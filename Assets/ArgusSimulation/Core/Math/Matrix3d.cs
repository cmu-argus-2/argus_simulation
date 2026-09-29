namespace Argus.Simulation.Core
{
    // Row-major 3x3 matrix: element (i, j) is component j of row i.
    public readonly struct Matrix3d
    {
        public Matrix3d(Vector3d row0, Vector3d row1, Vector3d row2)
        {
            Row0 = row0;
            Row1 = row1;
            Row2 = row2;
        }

        public Vector3d Row0 { get; }
        public Vector3d Row1 { get; }
        public Vector3d Row2 { get; }

        public bool IsFinite => Row0.IsFinite && Row1.IsFinite && Row2.IsFinite;
    }
}
