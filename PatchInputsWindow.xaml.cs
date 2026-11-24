using System.Windows;
using static ILPatchStudio.Ui;

namespace ILPatchStudio;

public partial class PatchInputsWindow : Window
{
    private sealed record ModeChoice(InputPassing Mode, string Label);

    private sealed class InputOption
    {
        public PatchInput Input { get; }
        public ModeChoice[] Modes { get; }
        public bool Checked { get; set; }
        public InputPassing Mode { get; set; }

        public InputOption(PatchInput input, PatchInput? selected)
        {
            Input = input;
            Modes = Enum.GetValues<InputPassing>().Where(input.Allows)
                .Select(mode => new ModeChoice(mode, mode == InputPassing.Value ? Text("按值", "Value") : PatchInput.Keyword(mode)))
                .ToArray();
            Checked = selected != null;
            Mode = selected != null && input.Allows(selected.Passing) ? selected.Passing : Modes[0].Mode;
        }
    }

    private readonly InputOption[] _options;
    internal PatchInput[] Inputs { get; private set; } = [];

    internal PatchInputsWindow(IEnumerable<PatchInput> available, IEnumerable<PatchInput> selected)
    {
        InitializeComponent();
        var previous = selected.ToDictionary(input => input.Name, StringComparer.Ordinal);
        _options = available.Select(input => new InputOption(input, previous.GetValueOrDefault(input.Name))).ToArray();
        Options.ItemsSource = _options;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        Inputs = _options.Where(option => option.Checked)
            .Select(option => option.Input with { Passing = option.Mode }).ToArray();
        DialogResult = true;
    }
}