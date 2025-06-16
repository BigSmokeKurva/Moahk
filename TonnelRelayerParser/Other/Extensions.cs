namespace Moahk.Other;

public static class Extensions
{
    public static double Percentile(this IEnumerable<double> source, double percentile)
    {
        var enumerable = source as double[] ?? source.ToArray();
        if (source == null || enumerable.Length == 0)
            throw new InvalidOperationException("Нельзя вычислить процентиль пустой выборки.");
        if (percentile is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percentile), "Процентиль должен быть между 0 и 100.");

        var sorted = enumerable.OrderBy(x => x).ToArray();
        var n = sorted.Length;

        var rank = (n + 1) * percentile / 100.0;
        if (rank <= 1)
            return sorted[0];
        if (rank >= n)
            return sorted[n - 1];

        var m = (int)Math.Floor(rank);
        var d = rank - m;

        var lower = sorted[m - 1];
        var upper = sorted[m];
        return lower + d * (upper - lower);
    }
}