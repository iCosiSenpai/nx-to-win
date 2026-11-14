using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace NxToWin;

public sealed class LocExtension(string text) : MarkupExtension
{
    private static readonly ResourceManager English = new("NxToWin.Strings", typeof(LocExtension).Assembly);

    internal static string Get(string value) => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en"
        ? English.GetString(value, CultureInfo.InvariantCulture) ?? value
        : value;

    public override object ProvideValue(IServiceProvider serviceProvider) => Get(text);
}
