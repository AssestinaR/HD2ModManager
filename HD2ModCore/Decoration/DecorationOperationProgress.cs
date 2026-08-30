namespace HD2ModCore.Decoration;

// Progress contract shared by UI, CLI and Core decoration operations.
public sealed record DecorationOperationProgress(string Stage, int Completed, int Total)
{
    public double? Fraction => Total <= 0 ? null : (double)Completed / Total;
}
