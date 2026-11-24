using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using static ILPatchStudio.Ui;
using Microsoft.Win32;

namespace ILPatchStudio;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly List<string> _dependencies = new();
    private AssemblyView? _assembly;
    private CancellationTokenSource? _openRequest;
    private CancellationTokenSource? _methodRequest;
    private bool _closed;
    private SourceView? _source;
    private PatchDraft? _patch;
    private PatchDraft? _statementBefore;
    private PatchDraft? _statementAfter;
    private StatementSelection? _statement;
    private bool _filtering;
    private InstructionRow[] _rows = [];
    private InstructionRow[] _highlighted = [];
    private bool _updating;
    private InstructionRow? _targetRow;
    private PatchLiteral? _selectedLiteral;
    private PatchInput[] _inputs = [];

    internal MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        ApplyPreferences();
        Closing += (_, _) => SaveSettings();
        Closed += (_, _) =>
        {
            _closed = true;
            _openRequest?.Cancel();
            _methodRequest?.Cancel();
            if (_assembly is { } assembly)
                _ = Task.Run(assembly.Dispose);
        };
    }

    internal Task OpenAssemblyAsync(string file) => OpenAssemblyAsync(file, reload: false);

    private async Task OpenAssemblyAsync(string file, bool reload, AssemblySession? relocated = null)
    {
        _openRequest?.Cancel();
        using var request = new CancellationTokenSource();
        _openRequest = request;
        Sidebar.IsEnabled = false;
        AddDependencyButton.IsEnabled = false;
        Status.Text = $"{Text("正在打开程序集", "Opening assembly")} · {file}";
        Status.ToolTip = Status.Text;
        try
        {
            string path = Path.GetFullPath(file);
            if (!reload)
                RememberAssembly();

            var session = relocated ?? _settings.Find(path);
            var dependencies = reload || _assembly == null && session == null
                ? _dependencies.ToArray()
                : session?.Dependencies.ToArray() ?? [];
            var assembly = await Task.Run(() => new AssemblyView(path, dependencies.Where(Directory.Exists), request.Token),
                request.Token);
            if (request.IsCancellationRequested || _closed)
            {
                _ = Task.Run(assembly.Dispose);
                return;
            }

            _methodRequest?.Cancel();
            if (_assembly is { } previous)
                _ = Task.Run(previous.Dispose);
            _assembly = assembly;
            _dependencies.Clear();
            _dependencies.AddRange(dependencies);
            AssemblyLabel.Text = assembly.Path;
            AssemblyLabel.ToolTip = assembly.Path;
            Status.Text = Text("Ctrl+F 搜索方法。打开后可在 C# 中选中整句插入代码。",
                "Press Ctrl+F to search. Select a full C# statement to insert code.");
            Status.ToolTip = null;

            if (!reload)
            {
                _filtering = true;
                try { SearchBox.Text = session?.Search ?? ""; }
                finally { _filtering = false; }
            }

            FilterMethods(reload: true, restoredSignature: session?.Method);
            _ = RefreshDependenciesAsync();
            _settings.LastAssembly = path;
            if (relocated != null && !string.Equals(relocated.Path, path, StringComparison.OrdinalIgnoreCase))
                _settings.Recent.RemoveAll(entry => string.Equals(entry.Path, relocated.Path, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!request.IsCancellationRequested && !_closed)
            {
                Status.Text = $"{Text("打开程序集失败", "Failed to open assembly")} · {file} · {exception.Message}";
                Status.ToolTip = Status.Text;
            }
        }
        finally
        {
            if (_openRequest == request)
            {
                _openRequest = null;
                if (!_closed)
                {
                    Sidebar.IsEnabled = true;
                    AddDependencyButton.IsEnabled = true;
                }
            }
        }
    }

    private void RememberAssembly()
    {
        if (_assembly == null)
            return;

        string path = _assembly.Path;
        var session = new AssemblySession
        {
            Path = path,
            Dependencies = _dependencies.ToList(),
            Method = (Methods.SelectedItem as MethodEntry)?.Signature,
            Search = SearchBox.Text
        };
        _settings.Recent.RemoveAll(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));
        _settings.Recent.Insert(0, session);
        if (_settings.Recent.Count > 20)
            _settings.Recent.RemoveAt(_settings.Recent.Count - 1);
    }

    private void ApplyPreferences()
    {
        double width = WindowLimit(_settings.Width, MinWidth, SystemParameters.WorkArea.Width, 1260);
        double height = WindowLimit(_settings.Height, MinHeight, SystemParameters.WorkArea.Height, 860);
        Width = width;
        Height = height;
        SidebarColumn.Width = new GridLength(Math.Max(170, Math.Min(
            FiniteOrDefault(_settings.SidebarWidth, 255), width - 660)));
        double source = Math.Clamp(FiniteOrDefault(_settings.SourceRatio, 0.51), 0.2, 0.8);
        SourceColumn.Width = new GridLength(source, GridUnitType.Star);
        IlColumn.Width = new GridLength(1 - source, GridUnitType.Star);
        double draft = Math.Clamp(FiniteOrDefault(_settings.DraftRatio, 1d / 3), 0.2, 0.7);
        SourceRow.Height = new GridLength(1 - draft, GridUnitType.Star);
        DraftRow.Height = new GridLength(draft, GridUnitType.Star);
        if (_settings.Left is double left && double.IsFinite(left) &&
            _settings.Top is double top && double.IsFinite(top))
        {
            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var titleArea = new Rect(left, top, Math.Min(width, 300), 32);
            titleArea.Intersect(desktop);
            if (!titleArea.IsEmpty && titleArea.Width >= 100 && titleArea.Height >= 20)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }
        WindowState = _settings.Maximized ? WindowState.Maximized : WindowState.Normal;
        ApplyDraftPreferences();
    }

    private static double FiniteOrDefault(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    private static double WindowLimit(double value, double minimum, double screen, double fallback)
        => Math.Clamp(FiniteOrDefault(value, fallback), minimum, Math.Max(minimum, screen));

    private void ApplyDraftPreferences()
    {
        double size = Math.Clamp(FiniteOrDefault(_settings.CodeFontSize, 13), 9, 30);
        SourceBox.FontSize = DraftBox.FontSize = Instructions.FontSize = size;
        IncludeUsings.IsChecked = _settings.IncludeUsings;
        MissingPolicy.SelectedIndex = _settings.SkipMissing ? 1 : 0;
        CheckDuplicates.IsChecked = _settings.CheckDuplicates;
        CheckBoundaries.IsChecked = _settings.CheckBoundaries;
        PreferMember.IsChecked = _settings.PreferMember;
    }

    private void SaveSettings()
    {
        RememberAssembly();
        _settings.IncludeUsings = IncludeUsings.IsChecked == true;
        _settings.SkipMissing = MissingPolicy.SelectedIndex == 1;
        _settings.CheckDuplicates = CheckDuplicates.IsChecked == true;
        _settings.CheckBoundaries = CheckBoundaries.IsChecked == true;
        _settings.PreferMember = PreferMember.IsChecked == true;
        if (IsLoaded)
        {
            Rect bounds = RestoreBounds;
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            {
                _settings.Left = bounds.Left;
                _settings.Top = bounds.Top;
                _settings.Width = bounds.Width;
                _settings.Height = bounds.Height;
            }
            _settings.SidebarWidth = SidebarColumn.ActualWidth;
            _settings.SourceRatio = Ratio(SourceColumn.ActualWidth, IlColumn.ActualWidth, 0.51);
            _settings.DraftRatio = Ratio(DraftRow.ActualHeight, SourceRow.ActualHeight, 1d / 3);
            if (WindowState != WindowState.Minimized)
                _settings.Maximized = WindowState == WindowState.Maximized;
        }
        if (!SettingsStore.TrySave(_settings, out string? error))
            MessageBox.Show(this, $"{Text("设置写入失败", "Could not save settings")} · {error}{Environment.NewLine}{SettingsStore.DataDirectory}");
    }

    private static double Ratio(double first, double second, double fallback)
        => first + second > 0 ? first / (first + second) : fallback;

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        if (dialog.ResetPreferences)
        {
            _settings.IncludeUsings = true;
            _settings.SkipMissing = _settings.CheckDuplicates = _settings.CheckBoundaries = _settings.PreferMember = false;
            _settings.Left = _settings.Top = null;
            _settings.Width = 1260;
            _settings.Height = 860;
            WindowState = WindowState.Normal;
            _settings.SidebarWidth = 255;
            _settings.SourceRatio = 0.51;
            _settings.DraftRatio = 1d / 3;
            _settings.Maximized = false;
        }
        _settings.Language = dialog.SelectedLanguage;
        _settings.CodeFontSize = dialog.CodeFontSize;
        _settings.RestoreLastSession = dialog.RestoreLastSession;
        if (dialog.ResetPreferences)
        {
            ApplyPreferences();
            UpdateLayout();
        }
        else
            SourceBox.FontSize = DraftBox.FontSize = Instructions.FontSize = _settings.CodeFontSize;
        SaveSettings();
    }

    private void OnRecent(object sender, RoutedEventArgs e)
    {
        RememberAssembly();
        var menu = new ContextMenu { PlacementTarget = (Button)sender, Placement = PlacementMode.Bottom };
        if (_settings.Recent.Count == 0)
            menu.Items.Add(new MenuItem { Header = Text("没有打开记录", "No recent files"), IsEnabled = false });

        foreach (var entry in _settings.Recent)
        {
            bool exists = File.Exists(entry.Path);
            var item = new MenuItem
            {
                Header = exists ? entry.Path : $"{entry.Path} · {Text("文件已失效，点击重新定位", "Missing · click to locate")}",
                ToolTip = entry.Path
            };
            item.Click += async (_, _) =>
            {
                if (File.Exists(entry.Path))
                    await OpenAssemblyAsync(entry.Path);
                else
                {
                    var dialog = CreateAssemblyDialog();
                    if (dialog.ShowDialog(this) == true)
                        await OpenAssemblyAsync(dialog.FileName, reload: false, relocated: entry);
                }
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    internal void ShowSettingsLoadError(string error)
    {
        Status.Text = $"{Text("无法读取设置，已使用默认值", "Unable to load settings. Defaults were used.")} · {error}";
        Status.ToolTip = Status.Text;
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        var dialog = CreateAssemblyDialog();
        if (dialog.ShowDialog(this) == true)
            await OpenAssemblyAsync(dialog.FileName);
    }

    private static OpenFileDialog CreateAssemblyDialog() => new()
    {
        Filter = Text(".NET 程序集 (*.dll;*.exe)|*.dll;*.exe|所有文件 (*.*)|*.*",
            ".NET assemblies (*.dll;*.exe)|*.dll;*.exe|All files (*.*)|*.*")
    };

    private async void OnAddDependency(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Text("选择包含依赖 DLL 的目录", "Select folders containing dependency DLLs"), Multiselect = true };
        if (dialog.ShowDialog(this) != true)
            return;

        bool changed = false;
        foreach (string folder in dialog.FolderNames)
        {
            if (_dependencies.Contains(folder, StringComparer.OrdinalIgnoreCase))
                continue;
            _dependencies.Add(folder);
            changed = true;
        }

        if (changed)
            await ReloadDependenciesAsync();
        Sidebar.SelectedItem = DependenciesTab;
    }

    private async void OnRemoveDependency(object sender, RoutedEventArgs e)
    {
        if (Dependencies.SelectedItem is TreeViewItem { Tag: string folder } && _dependencies.Remove(folder))
            await ReloadDependenciesAsync();
    }

    private void OnDependencySelected(object sender, RoutedPropertyChangedEventArgs<object> e)
        => RemoveDependency.IsEnabled = e.NewValue is TreeViewItem { Tag: string };

    private async Task ReloadDependenciesAsync()
    {
        if (_assembly != null)
        {
            RememberAssembly();
            await OpenAssemblyAsync(_assembly.Path, reload: true);
        }
        else
            await RefreshDependenciesAsync();
    }

    private int _dependencyRefresh;

    private async Task RefreshDependenciesAsync()
    {
        int refresh = ++_dependencyRefresh;
        string[] folders = _dependencies.ToArray();
        var references = _assembly?.References ?? [];
        DependenciesTab.Header = $"{Text("依赖", "Dependencies")} ({folders.Length})";
        var directories = await Task.Run(() => folders.Select(ReadDirectory).ToArray());
        if (_closed || refresh != _dependencyRefresh)
            return;

        Dependencies.Items.Clear();
        var added = new TreeViewItem { Header = Text("已添加的搜索目录", "Added search folders"), IsExpanded = true };
        Dependencies.Items.Add(added);
        if (directories.Length == 0)
            added.Items.Add(new TreeViewItem { Header = Text("当前程序集没有添加目录", "No folders added for this assembly"), IsEnabled = false });

        foreach (var (directory, files, error) in directories)
        {
            string label = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            if (string.IsNullOrEmpty(label))
                label = directory;
            if (error != null)
                label += $" · {Text("不可用", "Unavailable")}";
            var item = new TreeViewItem { Header = label, ToolTip = directory, Tag = directory };
            added.Items.Add(item);
            foreach (string file in files)
                item.Items.Add(new TreeViewItem { Header = Path.GetFileName(file), ToolTip = file });
            if (error != null)
                item.Items.Add(new TreeViewItem { Header = Text("无法读取目录", "Could not read folder"), ToolTip = error });
            else if (files.Length == 0)
                item.Items.Add(new TreeViewItem { Header = Text("此目录没有 DLL", "No DLL files in this folder"), IsEnabled = false });
        }

        if (references.Length == 0)
            return;

        int matches = references.Count(reference => reference.State == DependencyState.Match);
        var declared = new TreeViewItem
        {
            Header = string.Format(Text("直接引用 · {0}/{1} 身份一致", "Direct references · {0}/{1} exact matches"), matches, references.Length),
            IsExpanded = true
        };
        foreach (var reference in references)
        {
            string state = reference.State switch
            {
                DependencyState.Missing => Text("缺失", "Missing"),
                DependencyState.Unreadable => Text("无法读取", "Unreadable"),
                DependencyState.IdentityMismatch => Text("程序集身份不同", "Identity differs"),
                DependencyState.VersionMismatch => Text("版本不同", "Version differs"),
                _ => Text("匹配", "Match")
            };
            string requested = $"{Text("请求", "Requested")} · {reference.Requested}";
            string found = reference.Found == null ? "" : $"{Environment.NewLine}{Text("找到", "Found")} · {reference.Found}";
            string location = reference.Path == null ? "" : $"{Environment.NewLine}{reference.Path}";
            string error = reference.Error == null ? "" : $"{Environment.NewLine}{reference.Error}";
            declared.Items.Add(new TreeViewItem
            {
                Header = $"{reference.Name} · {state}",
                ToolTip = requested + found + location + error
            });
        }
        Dependencies.Items.Add(declared);
    }

    private static (string Path, string[] Files, string? Error) ReadDirectory(string path)
    {
        try
        {
            return (path, Directory.EnumerateFiles(path, "*.dll").Order(StringComparer.OrdinalIgnoreCase).ToArray(), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or System.Security.SecurityException)
        {
            return (path, [], exception.Message);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files &&
            Path.GetExtension(files[0]).Equals(".dll", StringComparison.OrdinalIgnoreCase)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files)
            await OpenAssemblyAsync(files[0]);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint != null)
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_filtering)
            FilterMethods();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control)
            return;
        Sidebar.SelectedIndex = 0;
        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            e.Handled = true;
            return;
        }
        if (Methods.Items.Count == 0 || e.Key is not (Key.Up or Key.Down or Key.Enter))
            return;

        int index = Methods.SelectedIndex;
        index = e.Key switch
        {
            Key.Up => index < 0 ? Methods.Items.Count - 1 : Math.Max(0, index - 1),
            Key.Down => Math.Min(Methods.Items.Count - 1, index + 1),
            _ => Math.Max(0, index)
        };
        Methods.SelectedIndex = index;
        Methods.ScrollIntoView(Methods.SelectedItem);
        if (e.Key == Key.Enter)
            Methods.Focus();
        e.Handled = true;
    }

    private void FilterMethods(bool reload = false, string? restoredSignature = null)
    {
        if (_assembly == null)
            return;

        string query = SearchBox.Text.Trim();
        string[] terms = query.Replace("::", " ").Split([' ', '\t', '.'], StringSplitOptions.RemoveEmptyEntries);
        var matches = query.Length == 0 ? _assembly.Methods : _assembly.Methods
            .Select(method => (Method: method, Rank: SearchRank(method, query, terms)))
            .Where(match => match.Rank >= 0)
            .OrderBy(match => match.Rank)
            .Select(match => match.Method)
            .ToArray();

        var restored = restoredSignature == null ? null : matches.FirstOrDefault(entry => entry.Signature == restoredSignature);
        var previous = reload ? null : Methods.SelectedItem as MethodEntry;
        bool keepSelection = previous != null && matches.Contains(previous);
        _filtering = true;
        try
        {
            Methods.ItemsSource = matches;
            if (keepSelection)
                Methods.SelectedItem = previous;
        }
        finally { _filtering = false; }

        MethodCount.Text = matches.Length switch
        {
            0 => Text("没有匹配的方法", "No matching methods"),
            1 => Text("1 个方法", "1 method"),
            _ => $"{matches.Length:N0} {Text("个方法", "methods")}"
        };
        if (restored != null)
        {
            Methods.SelectedItem = restored;
            Methods.ScrollIntoView(restored);
        }
        else if (keepSelection)
            Methods.ScrollIntoView(previous);
        else if (matches.Length == 1 && restoredSignature == null)
            Methods.SelectedIndex = 0;
        else
        {
            ResetMethod();
            if (restoredSignature != null)
                Status.Text = Text("上次选中的方法已找不到，请重新选择。", "The previous method could not be found. Select it again.");
        }
    }

    internal static int SearchRank(MethodEntry method, string query, string[] terms)
    {
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        string name = method.MethodName;
        if (name.Equals(query, comparison))
            return 0;
        if (terms.Length == 0)
            return -1;
        if (name.StartsWith(query, comparison))
            return 1;
        if (name.Contains(query, comparison))
            return 2;
        if (method.TypeName.Contains(query, comparison))
            return 3;
        if (terms.All(term => name.Contains(term, comparison) || method.TypeName.Contains(term, comparison)))
            return terms.Any(term => name.Equals(term, comparison)) ? 0 : 4;
        return terms.All(term => method.Signature.Contains(term, comparison)) ? 5 : -1;
    }

    private async void OnMethodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filtering)
            return;

        ResetMethod();
        if (_assembly is not { } assembly || Methods.SelectedItem is not MethodEntry entry)
            return;

        using var request = new CancellationTokenSource();
        _methodRequest = request;
        MethodLabel.Text = entry.Signature;
        MethodLabel.ToolTip = entry.Signature;
        Status.Text = Text("正在读取方法体并反编译…", "Reading and decompiling method…");
        Status.ToolTip = null;
        try
        {
            // 键盘连续切换时合并请求，减少已经过时的反编译。
            await Task.Delay(90, request.Token);
            var result = await Task.Run(() => assembly.ReadMethod(entry, request.Token), request.Token);
            if (request.IsCancellationRequested || _closed)
                return;

            _updating = true;
            try
            {
                (_source, _rows, _) = result;
                Instructions.ItemsSource = _rows;
                SourceBox.Text = _source?.Text ?? "";
                SourceBox.ScrollToHome();
            }
            finally { _updating = false; }

            if (_openRequest != null)
                return;

            if (result.DecompileError is { } error)
            {
                Status.Text = $"{Text("C# 反编译失败，仍可查看原始 IL", "C# decompilation failed. Raw IL is available")} · {error}";
                Status.ToolTip = Status.Text;
                return;
            }

            Status.Text = string.Format(Text("IL 指令 {0:N0} 条 · 可定位的 C# 选区 {1:N0} 个",
                "{0:N0} IL instructions · {1:N0} mapped C# selections"), _rows.Length, _source!.Selections.Length);
            if (_source!.UnknownTypes > 0)
                Status.Text += string.Format(Text(" · {0} 处类型不明，可在「依赖」页检查",
                    " · {0} unknown types. Check Dependencies."), _source.UnknownTypes);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!request.IsCancellationRequested && !_closed && _openRequest == null)
            {
                Status.Text = $"{Text("读取方法失败", "Failed to read method")} · {exception.Message}";
                Status.ToolTip = Status.Text;
            }
        }
        finally
        {
            if (_methodRequest == request)
                _methodRequest = null;
        }
    }

    private void ResetMethod()
    {
        _methodRequest?.Cancel();
        _updating = true;
        _source = null;
        _rows = [];
        _inputs = [];
        Highlight([]);
        MethodLabel.Text = Text("选择一个方法", "Select a method");
        MethodLabel.ToolTip = null;
        SourceBox.Clear();
        Instructions.ItemsSource = null;
        ShowPatch(null);
        _updating = false;
    }

    private void OnSourceSelected(object sender, RoutedEventArgs e)
    {
        if (_updating || _source == null)
            return;

        int start = SourceBox.SelectionStart;
        int end = start + SourceBox.SelectionLength;
        if (FindStatement(start, end) is { } statement)
        {
            _updating = true;
            try { ShowStatement(statement); }
            finally { _updating = false; }
            return;
        }
        var literals = _source.Selections.Where(literal => end == start
            ? start >= literal.Start && start < literal.End
            : start < literal.End && end > literal.Start).ToArray();
        var candidates = literals.SelectMany(literal => literal.Candidates).Distinct().ToArray();

        _updating = true;
        try
        {
            Highlight(candidates);
            bool exact = literals is [{ Exact: true }] && candidates.Length == 1;
            Instructions.SelectedItem = exact ? candidates[0] : null;
            if (candidates.Length != 0)
                Instructions.ScrollIntoView(candidates[0]);

            ShowPatch(exact ? candidates[0] : null, exact ? literals[0].Literal : null);
            if (!exact && candidates.Length != 0)
                PatchInfo.Text = string.Format(Text("找到 {0} 条候选。请在 IL 栏确认要修改的指令。", "Found {0} candidates. Choose the instruction in the IL pane."), candidates.Length);
            else if (candidates.Length == 0 && end > start)
                PatchInfo.Text = Text("选区没有可用的 IL 映射。可以在 IL 栏直接选择指令。", "No IL mapping for this selection. Select an instruction in the IL pane.");
        }
        finally
        {
            _updating = false;
        }
    }

    private StatementSelection? FindStatement(int start, int end)
    {
        if (_source == null || start == end)
            return null;

        while (start < end && char.IsWhiteSpace(_source.Text[start]))
            start++;
        while (end > start && char.IsWhiteSpace(_source.Text[end - 1]))
            end--;
        return _source.Statements.FirstOrDefault(statement => statement.Start == start &&
            (statement.End == end || (statement.End == end + 1 && _source.Text[end] == ';')));
    }

    private void ShowStatement(StatementSelection statement, bool preserveInput = false)
    {
        if (_statement == statement && !preserveInput)
            return;

        _statement = statement;
        _targetRow = null;
        _selectedLiteral = null;
        var previous = preserveInput ? (Operation.SelectedItem as OperationChoice)?.Operation : null;
        string previousText = Replacement.Text;
        var method = (MethodEntry)Methods.SelectedItem;
        bool preferMember = PreferMember.IsChecked == true;
        _statementBefore = PatchDraft.Locate(method.Method, statement.Instructions[0].Instruction, null, preferMember);
        _statementAfter = PatchDraft.Locate(method.Method, statement.Instructions[^1].Instruction, null, preferMember);

        var choices = new List<OperationChoice>();
        if (_statementBefore?.Operations.FirstOrDefault(choice => choice.Operation == PatchOperation.BeforeInstruction) is { } before)
            choices.Add(before with { Label = Text("语句前插入 C#", "Insert C# before statement") });
        if (_statementAfter?.Operations.FirstOrDefault(choice => choice.Operation == PatchOperation.AfterInstruction) is { } after)
            choices.Add(after with { Label = Text("语句后插入 C#", "Insert C# after statement") });

        Operation.ItemsSource = choices;
        Operation.IsEnabled = choices.Count > 0;
        Operation.SelectedItem = choices.FirstOrDefault(choice => choice.Operation == previous) ?? choices.FirstOrDefault();
        SelectStatementBoundary();
        SetOperationInput(previous != null ? previousText : null);
        RenderDraft();
    }

    private void SelectStatementBoundary()
    {
        if (_statement == null)
            return;

        bool after = (Operation.SelectedItem as OperationChoice)?.Operation == PatchOperation.AfterInstruction;
        _patch = Operation.SelectedItem == null ? null : after ? _statementAfter : _statementBefore;
        var row = after ? _statement.Instructions[^1] : _statement.Instructions[0];
        Highlight(_statement.Instructions);
        Instructions.SelectedItem = row;
        Instructions.ScrollIntoView(row);
    }

    private void OnInstructionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || Instructions.SelectedItem is not InstructionRow row)
            return;

        _updating = true;
        try
        {
            Highlight([row]);
            var locations = _source?.Selections.Where(literal => literal.Exact &&
                literal.Candidates[0] == row).ToArray();
            if (locations is [{ } location])
            {
                SourceBox.Select(location.Start, location.End - location.Start);
                SourceBox.ScrollToLine(SourceBox.GetLineIndexFromCharacterIndex(location.Start));
            }
            else
            {
                // 手动选择的指令可能没有唯一的反编译位置。
                SourceBox.Select(0, 0);
            }

            ShowPatch(row, locations is [{ } mapped] ? mapped.Literal : null);
        }
        finally
        {
            _updating = false;
        }
    }

    private void Highlight(InstructionRow[] rows)
    {
        foreach (var row in _highlighted)
            row.IsCandidate = false;
        foreach (var row in rows)
            row.IsCandidate = true;
        _highlighted = rows;
    }

    private void ShowPatch(InstructionRow? row, PatchLiteral? literal = null, bool preserveInput = false)
    {
        _statement = null;
        _statementBefore = _statementAfter = null;
        literal ??= PatchLiteral.FromInstruction(row?.Value);
        if (row != null && row == _targetRow && literal == _selectedLiteral && !preserveInput)
            return;

        _targetRow = row;
        _selectedLiteral = literal;
        var previous = preserveInput ? (Operation.SelectedItem as OperationChoice)?.Operation : null;
        string previousText = Replacement.Text;
        int? previousSlot = preserveInput ? (Redirect.SelectedItem as LocalChoice)?.Slot : null;
        _patch = row != null && Methods.SelectedItem is MethodEntry method
            ? PatchDraft.Locate(method.Method, row.Instruction, literal, PreferMember.IsChecked == true)
            : null;

        Operation.ItemsSource = _patch?.Operations;
        Operation.IsEnabled = _patch != null;
        if (_patch != null)
        {
            Operation.SelectedItem = _patch.Operations.FirstOrDefault(choice => choice.Operation == previous)
                ?? _patch.Operations.First();
            SetOperationInput(preserveInput && previous != null ? previousText : null, previousSlot);
        }
        else
        {
            Replacement.IsEnabled = false;
            Replacement.Clear();
            Redirect.Visibility = Visibility.Collapsed;
            InputButton.IsEnabled = false;
        }

        RenderDraft();
    }

    private void OnDraftChanged(object sender, RoutedEventArgs e)
    {
        if (_updating || (_targetRow == null && _statement == null))
            return;

        if (sender == PreferMember)
        {
            _updating = true;
            try
            {
                if (_statement != null)
                    ShowStatement(_statement, preserveInput: true);
                else
                    ShowPatch(_targetRow, _selectedLiteral, preserveInput: true);
            }
            finally { _updating = false; }
            return;
        }

        if (sender == Operation)
        {
            _updating = true;
            try
            {
                SelectStatementBoundary();
                SetOperationInput(_statement == null ? null : Replacement.Text);
            }
            finally { _updating = false; }
        }
        RenderDraft();
    }

    private void SetOperationInput(string? existingText, int? previousSlot = null)
    {
        var choice = Operation.SelectedItem as OperationChoice;
        bool redirect = choice?.Operation == PatchOperation.RedirectLocal;
        Replacement.IsEnabled = choice != null && !redirect && choice.Operation != PatchOperation.Cursor;
        Replacement.Visibility = redirect ? Visibility.Collapsed : Visibility.Visible;
        Replacement.Text = existingText ?? choice?.DefaultText ?? "";
        Redirect.Visibility = redirect ? Visibility.Visible : Visibility.Collapsed;
        InputButton.IsEnabled = choice?.UsesInputs == true && _patch?.Inputs(_source?.LocalNames).Any() == true;

        if (redirect && _patch != null)
        {
            var targets = _patch.RedirectTargets.Select(variable =>
            {
                string? sourceName = _source?.LocalNames.GetValueOrDefault(variable.Index);
                return new LocalChoice(variable.Index, $"V_{variable.Index} {sourceName} · {variable.VariableType.FullName}");
            }).ToArray();
            Redirect.ItemsSource = targets;
            Redirect.SelectedItem = targets.FirstOrDefault(target => target.Slot == previousSlot);
        }
    }

    private void OnPatchInputs(object sender, RoutedEventArgs e)
    {
        if (_patch == null)
            return;

        var dialog = new PatchInputsWindow(_patch.Inputs(_source?.LocalNames), _inputs) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _inputs = dialog.Inputs;
            RenderDraft();
        }
    }

    private sealed record LocalChoice(int Slot, string Label);

    private void RenderDraft()
    {
        if (_patch == null || Operation.SelectedItem is not OperationChoice choice)
        {
            SelectedInputs.Visibility = Visibility.Collapsed;
            RequiredNamespaces.Visibility = Visibility.Collapsed;
            DraftBox.Clear();
            CopyButton.IsEnabled = false;
            PatchInfo.Text = _statement != null
                ? Text("找不到能唯一定位这条语句边界的短模式。", "No unique pattern identifies the statement boundary.")
                : _targetRow == null
                    ? Text("在 C# 中选中整句，或选择要修改的符号和 IL 指令。", "Select a C# statement, a symbol, or an IL instruction.")
                    : $"IL_{_targetRow.Instruction.Offset:x4} · {Text("找不到能唯一定位这条指令的短模式。", "No unique short pattern identifies this instruction.")}";
            return;
        }

        var options = new DraftOptions(MissingPolicy.SelectedIndex == 1, CheckDuplicates.IsChecked == true,
            CheckBoundaries.IsChecked == true, IncludeUsings.IsChecked == true);
        var selectedInputs = choice.UsesInputs ? _inputs : [];
        SelectedInputs.Visibility = selectedInputs.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SelectedInputs.Text = $"{Text("传入变量", "Passed variables")} · {string.Join("、", selectedInputs.Select(input => input.Passing == InputPassing.Value ? input.Name : $"{PatchInput.Keyword(input.Passing)} {input.Name}"))}";
        int? slot = (Redirect.SelectedItem as LocalChoice)?.Slot;
        bool ready = _patch.TryRender(choice.Operation, Replacement.Text, options, selectedInputs, slot,
            out string code, out string message, out string[] namespaces);
        RequiredNamespaces.Visibility = ready && !options.IncludeUsings ? Visibility.Visible : Visibility.Collapsed;
        RequiredNamespaces.Text = $"{Text("所需命名空间", "Required namespaces")} · {string.Join(", ", namespaces)}";
        DraftBox.Text = code;
        CopyButton.IsEnabled = ready;
        string match = _patch.HasMemberAnchor ? Text("含成员签名", "member signature") : Text("指令形状", "instruction pattern");
        string point = _statement == null ? $"IL_{_patch.Offset:x4}" :
            $"{(choice.Operation == PatchOperation.AfterInstruction ? Text("语句后", "After statement") : Text("语句前", "Before statement"))} · IL_{_patch.Offset:x4}";
        PatchInfo.Text = string.Format(Text("{0} · 当前 DLL 唯一命中 · {1} 条指令（{2}）。{3}", "{0} · Unique in current DLL · {1} instructions ({2}). {3}"), point, _patch.PatternLength, match, message);
    }
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DraftBox.Text);
            Status.Text = Text("补丁草稿已复制。", "Patch draft copied.");
        }
        catch (ExternalException)
        {
            Status.Text = Text("剪贴板暂时不可用，请重试。", "The clipboard is temporarily unavailable. Try again.");
        }
    }
}
