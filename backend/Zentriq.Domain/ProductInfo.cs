using System.Reflection;

namespace Zentriq.Domain;

/// <summary>Build facts stamped at compile time (Directory.Build.props): used for the support window of perpetual licenses.</summary>
public static class ProductInfo
{
    /// <summary>The UTC date this build was made (its release date). Perpetual licenses need support up to this date.</summary>
    public static DateTimeOffset ReleaseDate { get; } = Read();

    private static DateTimeOffset Read()
    {
        var v = typeof(ProductInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "ZentriqReleaseDate")?.Value;
        return DateTimeOffset.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d : new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
