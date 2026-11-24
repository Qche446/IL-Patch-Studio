using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using CilCode = Mono.Cecil.Cil.Code;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CilFlow = Mono.Cecil.Cil.FlowControl;

namespace ILPatchStudio;

internal sealed class AssemblyView : IDisposable
{
    private readonly ModuleDefinition _module;
    private readonly PEFile _image;
    private readonly CSharpDecompiler _decompiler;
    private readonly object _sync = new();
    private readonly UniversalAssemblyResolver _resolver;

    public AssemblyView(string path, IEnumerable<string> dependencies, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Path = System.IO.Path.GetFullPath(path);
        _module = ModuleDefinition.ReadModule(Path, new ReaderParameters { InMemory = true });
        try
        {
            _image = new PEFile(Path, System.Reflection.PortableExecutable.PEStreamOptions.PrefetchEntireImage);
            try
            {
                var settings = new DecompilerSettings { UseDebugSymbols = false };
                _resolver = new UniversalAssemblyResolver(Path, false, _image.DetectTargetFrameworkId());
                foreach (var directory in dependencies)
                    _resolver.AddSearchDirectory(directory);

                cancellationToken.ThrowIfCancellationRequested();
                _decompiler = new CSharpDecompiler(_image, _resolver, settings);
                Methods = _module.GetTypes()
                    .SelectMany(type => type.Methods)
                    .Where(method => method.HasBody)
                    .Select(method => new MethodEntry(method))
                    .OrderBy(method => method.TypeName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(method => method.MethodName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                References = _image.AssemblyReferences.Select(reference =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return ResolveReference(reference);
                    })
                    .OrderBy(reference => reference.State)
                    .ThenBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                _image.Dispose();
                throw;
            }
        }
        catch
        {
            _module.Dispose();
            throw;
        }
    }

    public string Path { get; }
    public MethodEntry[] Methods { get; }
    public AssemblyDependency[] References { get; }

    private AssemblyDependency ResolveReference(AssemblyReference reference)
    {
        string? path = null;
        try
        {
            path = _resolver.FindAssemblyFile(reference);
            if (path == null)
                return new(reference.Name, reference.FullName, null, null, DependencyState.Missing);

            var actual = System.Reflection.AssemblyName.GetAssemblyName(path);
            var expected = new System.Reflection.AssemblyName(reference.FullName);
            var state = !string.Equals(actual.Name, expected.Name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actual.CultureName ?? "", expected.CultureName ?? "", StringComparison.OrdinalIgnoreCase) ||
                !(actual.GetPublicKeyToken() ?? []).AsSpan().SequenceEqual(expected.GetPublicKeyToken() ?? [])
                ? DependencyState.IdentityMismatch
                : actual.Version != expected.Version ? DependencyState.VersionMismatch : DependencyState.Match;
            return new(reference.Name, reference.FullName, path, actual.FullName, state);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException
            or ArgumentException or System.Security.SecurityException)
        {
            return new(reference.Name, reference.FullName, path, null, DependencyState.Unreadable, error.Message);
        }
    }

    public (SourceView? Source, InstructionRow[] Rows, string? DecompileError) ReadMethod(MethodEntry entry, CancellationToken cancellationToken)
    {
        // ILSpy 的实例在同一时间只处理一个方法。
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = entry.Method.Body.Instructions.Select(instruction => new InstructionRow(instruction)).ToArray();
            try
            {
                return (Decompile(entry, rows, cancellationToken), rows, null);
            }
            catch (Exception error) when (error is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return (null, rows, error.Message);
            }
        }
    }

    private SourceView Decompile(MethodEntry entry, InstructionRow[] rows, CancellationToken cancellationToken)
    {
        _decompiler.CancellationToken = cancellationToken;
        int token = entry.Method.MetadataToken.ToInt32();
        var syntax = _decompiler.Decompile(MetadataTokens.EntityHandle(token));
        using var output = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
        var writer = TokenWriter.CreateWriterThatSetsLocationsInAST(output, "    ");
        syntax.AcceptVisitor(new CSharpOutputVisitor(writer, FormattingOptionsFactory.CreateAllman()));
        string text = output.ToString();

        var lineStarts = new List<int> { 0 };
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
                lineStarts.Add(index + 1);
        }

        var offsets = rows.Select(row => row.Instruction.Offset).ToArray();
        var mappings = new List<SourceSelection>();
        var statements = new List<StatementSelection>();
        var localNames = new Dictionary<int, HashSet<string>>();
        var incoming = rows.SelectMany(row => row.Instruction.Operand switch
        {
            Instruction target => new[] { target },
            Instruction[] targets => targets,
            _ => []
        }).ToHashSet();
        foreach (var region in entry.Method.Body.ExceptionHandlers)
        {
            foreach (var boundary in new[] { region.TryStart, region.TryEnd, region.HandlerStart, region.HandlerEnd, region.FilterStart })
            {
                if (boundary != null)
                    incoming.Add(boundary);
            }
        }

        foreach (var node in syntax.Descendants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is ExpressionStatement or VariableDeclarationStatement)
            {
                int firstCharacter = TextIndex(node.StartLocation, lineStarts, text.Length);
                int lastCharacter = TextIndex(node.EndLocation, lineStarts, text.Length);
                if (firstCharacter >= 0 && lastCharacter > firstCharacter &&
                    StatementRange(node, rows, offsets, incoming, token) is { } range)
                    statements.Add(new StatementSelection(firstCharacter, lastCharacter, range));
            }

            PatchLiteral? literal = null;
            AstNode displayed;
            AstNode origin = node;
            var resolved = node is MemberReferenceExpression or IdentifierExpression ? node.GetResolveResult() : null;
            Func<InstructionRow, bool> matches;
            bool requireConfirmation = false;
            switch (node)
            {
                case PrimitiveExpression primitive when PatchLiteral.FromSource(primitive.Value) is { } value:
                    literal = value;
                    displayed = primitive;
                    matches = row => row.Value == value.Cil;
                    break;
                case MemberReferenceExpression or IdentifierExpression
                    when resolved?.IsCompileTimeConstant == true && PatchLiteral.FromSource(resolved.ConstantValue) is { } constant:
                    literal = constant;
                    displayed = DisplayName(node);
                    matches = row => row.Value == constant.Cil;
                    break;
                case InvocationExpression invocation:
                    displayed = DisplayName(invocation.Target);
                    matches = row => (row.Instruction.OpCode.Code is CilCode.Call or CilCode.Callvirt) &&
                        MatchesMember(row.Instruction.Operand, invocation.GetSymbol());
                    break;
                case ObjectCreateExpression creation:
                    displayed = creation.Type;
                    matches = row => row.Instruction.OpCode.Code == CilCode.Newobj &&
                        MatchesMember(row.Instruction.Operand, creation.GetSymbol());
                    break;
                case VariableInitializer initializer when initializer.GetILVariable() is { Kind: VariableKind.Local, Index: int slot }:
                    RememberLocal(localNames, slot, initializer.Name);
                    displayed = initializer.NameToken;
                    matches = row => PatchDraft.StoreSlot(row.Instruction) == slot;
                    break;
                case IdentifierExpression local when local.GetILVariable() is { Kind: VariableKind.Local, Index: int slot }:
                    RememberLocal(localNames, slot, local.Identifier);
                    displayed = local.IdentifierToken;
                    bool writes = node.Parent is AssignmentExpression localAssignment && localAssignment.Left == node;
                    if (writes)
                        origin = node.Parent!;
                    matches = row => writes
                        ? PatchDraft.StoreSlot(row.Instruction) == slot
                        : PatchDraft.LoadSlot(row.Instruction) == slot;
                    break;
                case IfElseStatement conditional:
                    displayed = conditional.IfToken;
                    origin = conditional.Condition;
                    requireConfirmation = true;
                    matches = row => row.Instruction.OpCode.FlowControl == CilFlow.Cond_Branch &&
                        row.Instruction.OpCode.Code != CilCode.Switch;
                    break;
                case MemberReferenceExpression member when member.GetSymbol() is IField:
                case IdentifierExpression identifier when identifier.GetSymbol() is IField:
                    displayed = DisplayName(node);
                    bool simpleWrite = node.Parent is AssignmentExpression { Operator: AssignmentOperatorType.Assign } assignment &&
                        assignment.Left == node;
                    if (simpleWrite)
                        origin = node.Parent!;
                    matches = row => row.Instruction.Operand is FieldReference field && MatchesMember(field, node.GetSymbol()) &&
                        (simpleWrite ? row.Instruction.OpCode.Code is CilCode.Stfld or CilCode.Stsfld
                            : row.Instruction.OpCode.Code is CilCode.Ldfld or CilCode.Ldsfld or CilCode.Stfld or CilCode.Stsfld);
                    break;
                default:
                    continue;
            }

            int start = TextIndex(displayed.StartLocation, lineStarts, text.Length);
            int end = TextIndex(displayed.EndLocation, lineStarts, text.Length);
            if (start < 0 || end <= start)
                continue;

            AstNode? parent = origin;
            for (int depth = 0; depth <= 3 && parent != null && parent is not EntityDeclaration; depth++, parent = parent.Parent)
            {
                var roots = parent.Annotations.OfType<ILInstruction>();
                if (depth != 0 || requireConfirmation)
                    roots = roots.Concat(parent.Descendants
                        .Where(child => !requireConfirmation || child is not Statement)
                        .SelectMany(child => child.Annotations.OfType<ILInstruction>()))
                        .SelectMany(instruction => instruction.Descendants.Prepend(instruction));

                var found = new HashSet<InstructionRow>();
                foreach (var range in roots.Where(instruction => OwnsRange(instruction, token)).SelectMany(instruction => instruction.ILRanges))
                {
                    int index = Array.BinarySearch(offsets, range.Start);
                    if (index < 0)
                        index = ~index;
                    for (; index < offsets.Length && (offsets[index] < range.End ||
                        requireConfirmation && offsets[index] == range.End); index++)
                    {
                        if (matches(rows[index]))
                            found.Add(rows[index]);
                    }
                }
                var candidates = found.OrderBy(row => row.Instruction.Offset).ToArray();
                if (candidates.Length == 0)
                    continue;

                // 祖先节点覆盖多个表达式，扩大搜索范围后交给开发者确认。
                mappings.Add(new SourceSelection(start, end, candidates, !requireConfirmation && depth == 0 && candidates.Length == 1, literal));
                break;
            }
        }

        return new SourceView(text, mappings.ToArray(), statements.ToArray(),
            localNames.ToDictionary(pair => pair.Key, pair => string.Join(" / ", pair.Value.Order())),
            syntax.Descendants.OfType<Comment>().Count(comment =>
                comment.Content.Contains("Unknown result type", StringComparison.Ordinal)));
    }

    private static InstructionRow[]? StatementRange(AstNode node, InstructionRow[] rows, int[] offsets,
        HashSet<Instruction> incoming, int token)
    {
        var instructions = node.Descendants.Prepend(node).SelectMany(child => child.Annotations.OfType<ILInstruction>());
        var covered = new HashSet<int>();
        foreach (var il in instructions.SelectMany(instruction => instruction.Descendants.Prepend(instruction)))
        {
            if (!OwnsRange(il, token))
                continue;
            foreach (var range in il.ILRanges)
            {
                int index = Array.BinarySearch(offsets, range.Start);
                if (index < 0)
                    index = ~index;
                for (; index < offsets.Length && offsets[index] < range.End; index++)
                    covered.Add(index);
            }
        }

        if (covered.Count == 0)
            return null;

        int start = covered.Min();
        int end = covered.Max();

        // 有内部跳转或多个入口的语句无法用一对首尾注入点准确表示。
        for (int index = start; index <= end; index++)
        {
            var instruction = rows[index].Instruction;
            if ((!covered.Contains(index) && instruction.OpCode.Code != CilCode.Nop) ||
                (index > start && incoming.Contains(instruction)) ||
                instruction.OpCode.FlowControl is CilFlow.Branch or CilFlow.Cond_Branch or CilFlow.Return or CilFlow.Throw ||
                instruction.OpCode.Code is CilCode.Tail or CilCode.Jmp)
                return null;
        }

        // 反编译器可能省去语句末尾丢弃调用结果的 pop。
        if (rows[end].Instruction.OpCode.FlowControl == CilFlow.Call && end + 1 < rows.Length &&
            rows[end + 1].Instruction.OpCode.Code == CilCode.Pop && !incoming.Contains(rows[end + 1].Instruction))
            end++;
        return rows[start..(end + 1)];
    }

    private static void RememberLocal(Dictionary<int, HashSet<string>> names, int index, string name)
    {
        if (!names.TryGetValue(index, out var values))
            names.Add(index, values = []);
        values.Add(name);
    }

    private static AstNode DisplayName(AstNode node) => node switch
    {
        MemberReferenceExpression member => member.MemberNameToken,
        IdentifierExpression identifier => identifier.IdentifierToken,
        _ => node
    };

    private static bool MatchesMember(object operand, ISymbol? symbol)
    {
        if (operand is not MemberReference member || symbol is not IMember expected)
            return false;

        string declaringType = member.DeclaringType.GetElementType().FullName.Replace('/', '+');
        return member.Name == expected.Name &&
            declaringType == expected.DeclaringTypeDefinition?.ReflectionName &&
            (member is not MethodReference method || expected is not IMethod expectedMethod ||
                method.Parameters.Count == expectedMethod.Parameters.Count);
    }
    private static bool OwnsRange(ILInstruction instruction, int token)
    {
        var function = instruction.Ancestors.OfType<ILFunction>().FirstOrDefault();
        var physicalMethod = function?.MoveNextMethod ?? function?.Method;
        return physicalMethod != null && MetadataTokens.GetToken(physicalMethod.MetadataToken) == token;
    }

    private static int TextIndex(TextLocation location, List<int> lineStarts, int textLength)
    {
        if (location.Line <= 0 || location.Line > lineStarts.Count || location.Column <= 0)
            return -1;
        int index = lineStarts[location.Line - 1] + location.Column - 1;
        int nextLine = location.Line == lineStarts.Count ? textLength + 1 : lineStarts[location.Line];
        return index < nextLine ? index : -1;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _image.Dispose();
            _module.Dispose();
        }
    }
}

internal sealed record MethodEntry(MethodDefinition Method)
{
    public string MethodName => Method.Name;
    public string TypeName => Method.DeclaringType.FullName;
    public string ShortTypeName => TypeName[(TypeName.LastIndexOf('.') + 1)..].Replace('/', '.');
    public string Signature { get; } = Method.FullName;
}

internal enum DependencyState { Missing, Unreadable, IdentityMismatch, VersionMismatch, Match }

internal sealed record AssemblyDependency(string Name, string Requested, string? Path, string? Found,
    DependencyState State, string? Error = null);

internal sealed record SourceView(string Text, SourceSelection[] Selections, StatementSelection[] Statements,
    IReadOnlyDictionary<int, string> LocalNames, int UnknownTypes);

internal sealed record StatementSelection(int Start, int End, InstructionRow[] Instructions);

internal sealed record SourceSelection(int Start, int End, InstructionRow[] Candidates, bool Exact, PatchLiteral? Literal);

internal sealed class InstructionRow(Instruction instruction) : INotifyPropertyChanged
{
    private bool _isCandidate;

    public Instruction Instruction { get; } = instruction;
    public CilValue? Value { get; } = CilValue.Read(instruction);
    public string Text { get; } = instruction.ToString();
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsCandidate
    {
        get => _isCandidate;
        set
        {
            if (_isCandidate == value)
                return;
            _isCandidate = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCandidate)));
        }
    }
}
