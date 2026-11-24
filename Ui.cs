using System.Globalization;
using System.Windows.Markup;

namespace ILPatchStudio;

internal static class Ui
{
    public static bool English { get; private set; }
    public static void Configure(string language) => English =
        language == "en-US" || language != "zh-Hans" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "zh";
    public static string Text(string chinese, string english) => English ? english : chinese;
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocalTextExtension : MarkupExtension
{
    public string Zh { get; set; } = "";
    public string En { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Ui.Text(Zh, En);
}