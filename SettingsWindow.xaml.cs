using System.Globalization;
using System.Windows;
using static ILPatchStudio.Ui;

namespace ILPatchStudio;

public partial class SettingsWindow : Window
{
    private static readonly string[] Languages = ["system", "zh-Hans", "en-US"];
    internal string SelectedLanguage => Languages[LanguageChoice.SelectedIndex];
    internal double CodeFontSize { get; private set; }
    internal bool RestoreLastSession => RestoreSession.IsChecked == true;
    internal bool ResetPreferences { get; private set; }

    internal SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        LanguageChoice.ItemsSource = new[] { Text("跟随系统", "Follow system"), "简体中文", "English" };
        FolderLabel.Text = Text("数据目录", "Data folder") + Environment.NewLine + SettingsStore.DataDirectory;
        ShowValues(settings);
    }

    private void ShowValues(AppSettings settings)
    {
        LanguageChoice.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, settings.Language));
        SizeInput.Text = settings.CodeFontSize.ToString("0.#", CultureInfo.CurrentCulture);
        RestoreSession.IsChecked = settings.RestoreLastSession;
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        ResetPreferences = true;
        ShowValues(new AppSettings());
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(SizeInput.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var size) ||
            !double.IsFinite(size) || size is < 9 or > 30)
        {
            MessageBox.Show(this, Text("请输入 9 到 30 之间的字号。", "Enter a font size from 9 to 30."));
            return;
        }
        CodeFontSize = size;
        DialogResult = true;
    }
}