namespace TypeWhisper.Plugin.ParakeetCtc;

// FluidAudio 0.15.5 default vocabulary-size and adaptive-CBW settings.
public static class CtcBiasPolicy
{
    public static float MinimumSimilarity(int termCount) => termCount > 100 ? .60f : termCount > 10 ? .55f : .52f;
    public static double Bonus(int tokenCount) => tokenCount <= 3 ? 4.5 : 4.5 * (1 + Math.Log2(tokenCount / 3d) * .3);
    public static bool Accept(double original, double preferred, int tokenCount) => tokenCount > 0
        && double.IsFinite(original) && double.IsFinite(preferred) && preferred + Bonus(tokenCount) > original;
}
