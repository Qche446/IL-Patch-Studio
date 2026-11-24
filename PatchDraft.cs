using System.Globalization;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static ILPatchStudio.Ui;

namespace ILPatchStudio;

internal enum PatchOperation
{
    ReplaceConstant, TransformConstant, BeforeCall, AfterCall, TransformResult,
    TransformFieldRead, TransformFieldWrite, TransformLastArgument, RedirectLocal,
    BeforeInstruction, AfterInstruction, BranchFallthrough, BranchTaken, Cursor
}

internal sealed record OperationChoice(PatchOperation Operation, string Label, string DefaultText)
{
    public bool UsesInputs => Operation is not (PatchOperation.ReplaceConstant or PatchOperation.RedirectLocal or PatchOperation.Cursor);
}

internal enum InputPassing { Value, Ref, Out, In }

internal sealed record PatchInput(string Label, string Name, string Type, string[] Loads,
    bool UsesField = false, string[]? AddressLoads = null, bool Writable = false, bool UsesTypeOperand = false,
    InputPassing Passing = InputPassing.Value)
{
    public bool Allows(InputPassing mode) => mode switch
    {
        InputPassing.Value => true,
        InputPassing.In => AddressLoads != null,
        InputPassing.Ref or InputPassing.Out => AddressLoads != null && Writable,
        _ => false
    };

    public string Parameter => (Passing == InputPassing.Value ? "" : $"{Keyword(Passing)} ") + $"{Type} {Name}";
    public IEnumerable<string> Instructions => Passing == InputPassing.Value ? Loads : AddressLoads!;
    public static string Keyword(InputPassing mode) => mode.ToString().ToLowerInvariant();
}

internal readonly record struct DraftOptions(bool SkipMissing, bool CheckDuplicates, bool CheckBoundaries, bool IncludeUsings = true);

internal sealed class PatchDraft
{
    private sealed record Atom(Func<Instruction, bool> Matches, string Predicate, bool IsMember = false);

    private readonly MethodDefinition _method;
    private readonly Instruction _target;
    private readonly PatchLiteral? _literal;
    private readonly Atom[] _pattern;
    private readonly int _targetIndex;

    private PatchDraft(MethodDefinition method, Instruction target, PatchLiteral? literal, Atom[] pattern, int targetIndex)
    {
        _method = method;
        _target = target;
        _literal = literal;
        _pattern = pattern;
        _targetIndex = targetIndex;
        ValueType = TargetType(target);
        if (target.Operand is MethodReference methodReference && methodReference.Parameters.Count > 0 &&
            methodReference.CallingConvention != MethodCallingConvention.VarArg)
            LastArgumentType = TypeName(methodReference.Parameters[^1].ParameterType);
        Operations = GetOperations();
    }

    public int Offset => _target.Offset;
    public int? WrittenSlot => StoreSlot(_target);

    public VariableDefinition[] RedirectTargets => WrittenSlot is int slot
        ? _method.Body.Variables.Where(variable => variable.Index != slot && !variable.IsPinned &&
            SameType(variable.VariableType, _method.Body.Variables[slot].VariableType)).ToArray()
        : [];

    internal IEnumerable<PatchInput> Inputs(IReadOnlyDictionary<int, string>? localNames)
    {
        string? owner = TypeName(_method.DeclaringType);
        bool instance = _method.HasThis && !_method.IsConstructor && !_method.DeclaringType.IsValueType && owner != null;
        if (instance)
            yield return new PatchInput($"self · {Text("当前实例", "Current instance")} · {owner}", "self", owner!, ["cursor.EmitLdarg(0);"]);

        foreach (var parameter in _method.Parameters)
        {
            var parameterType = parameter.ParameterType;
            bool readOnly = parameter.IsIn && !parameter.IsOut ||
                parameter.CustomAttributes.Any(attribute => attribute.AttributeType.FullName is
                    "System.Runtime.CompilerServices.IsReadOnlyAttribute" or "System.Runtime.CompilerServices.RequiresLocationAttribute");
            while (parameterType is IModifierType modifier)
            {
                readOnly |= modifier.ModifierType.FullName is "System.Runtime.CompilerServices.IsReadOnlyAttribute" or
                    "System.Runtime.InteropServices.InAttribute";
                parameterType = modifier.ElementType;
            }

            var reference = parameterType as ByReferenceType;
            var elementType = reference?.ElementType ?? parameterType;
            if (TypeName(elementType) is not { } type)
                continue;

            int index = parameter.Index + (_method.HasThis ? 1 : 0);
            string name = $"arg{parameter.Index}";
            var reads = new List<string> { $"cursor.EmitLdarg({index});" };
            if (reference != null)
                reads.Add($"cursor.Emit(OpCodes.Ldobj, typeof({type}));");

            string modifierLabel = reference == null ? "" : readOnly ? "in " : parameter.IsOut ? "out " : "ref ";
            yield return new PatchInput($"{name} · {Text("参数", "Parameter")} {parameter.Name} · {modifierLabel}{type}", name, type,
                reads.ToArray(), AddressLoads: [reference != null ? $"cursor.EmitLdarg({index});" : $"cursor.EmitLdarga({index});"],
                Writable: reference == null || !readOnly, UsesTypeOperand: reference != null);
        }

        foreach (var variable in _method.Body.Variables)
        {
            if (!variable.IsPinned && TypeName(variable.VariableType) is { } type)
            {
                string name = $"local{variable.Index}";
                string? sourceName = localNames?.GetValueOrDefault(variable.Index);
                yield return new PatchInput($"{name} · V_{variable.Index} {sourceName} · {type}", name, type,
                    [$"cursor.EmitLdloc({variable.Index});"],
                    AddressLoads: [$"cursor.EmitLdloca({variable.Index});"], Writable: true);
            }
        }

        if (owner == null)
            yield break;

        int fieldIndex = 0;
        foreach (var field in _method.DeclaringType.Fields)
        {
            if (field.IsLiteral || (!field.IsStatic && !instance) || TypeName(field.FieldType) is not { } type)
                continue;
            string name = $"field{fieldIndex++}";
            string access = field.IsStatic ? "Static" : "Instance";
            string reference = $"typeof({owner}).GetField({CodeText.Quote(field.Name)}, " +
                $"BindingFlags.{access} | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!";
            var loads = new List<string>();
            if (!field.IsStatic)
                loads.Add("cursor.EmitLdarg(0);");
            loads.Add($"cursor.Emit(OpCodes.{(field.IsStatic ? "Ldsfld" : "Ldfld")}, {reference});");
            var address = field.IsInitOnly ? null : loads.Take(loads.Count - 1)
                .Append($"cursor.Emit(OpCodes.{(field.IsStatic ? "Ldsflda" : "Ldflda")}, {reference});").ToArray();
            yield return new PatchInput($"{name} · {Text("字段", "Field")} {field.Name} · {type}", name, type,
                loads.ToArray(), true, address, Writable: address != null);
        }
    }
    public string? ValueType { get; }
    private string? LastArgumentType { get; }
    public OperationChoice[] Operations { get; }
    public int PatternLength => _pattern.Length;
    public bool HasMemberAnchor => _pattern.Any(atom => atom.IsMember);

    public static PatchDraft? Locate(MethodDefinition method, Instruction selected, PatchLiteral? literal, bool preferMember)
    {
        var instructions = method.Body.Instructions;
        int selectedIndex = instructions.IndexOf(selected);
        if (selectedIndex < 0 || Describe(selected) == null)
            return null;

        var neighbors = instructions.Select(Describe).ToArray();
        if (StoreSlot(selected) is int slot)
            neighbors[selectedIndex] = new Atom(i => StoreSlot(i) == slot, $"i.MatchStloc({slot})");
        else if (LoadSlot(selected) is int loadSlot)
            neighbors[selectedIndex] = new Atom(i => LoadSlot(i) == loadSlot, $"i.MatchLdloc({loadSlot})");
        var blockStarts = new HashSet<Instruction> { instructions[0] };
        foreach (var instruction in instructions)
        {
            if (instruction.Operand is Instruction target)
                blockStarts.Add(target);
            else if (instruction.Operand is Instruction[] targets)
                blockStarts.UnionWith(targets);

            if (instruction.Next != null && instruction.OpCode.FlowControl is
                FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw)
                blockStarts.Add(instruction.Next);
        }

        foreach (var region in method.Body.ExceptionHandlers)
        {
            foreach (var boundary in new[] { region.TryStart, region.TryEnd, region.HandlerStart, region.HandlerEnd, region.FilterStart })
            {
                if (boundary != null)
                    blockStarts.Add(boundary);
            }
        }

        var windows = new List<(int Start, Atom[] Pattern)>();
        for (int start = Math.Max(0, selectedIndex - 4); start <= selectedIndex; start++)
        {
            for (int end = start; end < Math.Min(instructions.Count, start + 5); end++)
            {
                if ((end > start && blockStarts.Contains(instructions[end])) || neighbors[end] == null)
                    break;
                if (end >= selectedIndex)
                    windows.Add((start, neighbors[start..(end + 1)].Select(atom => atom!).ToArray()));
            }
        }

        // 生成时确认当前 DLL 指向所选位置。运行时是否额外检查重复由使用者选择。
        foreach (var window in windows.OrderByDescending(window => preferMember && window.Pattern.Any(atom => atom.IsMember))
                     .ThenBy(window => window.Pattern.Length)
                     .ThenByDescending(window => window.Start))
        {
            bool duplicate = false;
            for (int start = 0; start <= instructions.Count - window.Pattern.Length; start++)
            {
                if (start == window.Start || !window.Pattern[0].Matches(instructions[start]))
                    continue;

                int step = 1;
                while (step < window.Pattern.Length && window.Pattern[step].Matches(instructions[start + step]))
                    step++;
                if (step == window.Pattern.Length)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
                return new PatchDraft(method, selected, literal, window.Pattern, selectedIndex - window.Start);
        }

        return null;
    }

    public bool TryRender(PatchOperation operation, string text, DraftOptions options,
        IReadOnlyList<PatchInput> inputs, int? redirectSlot, out string code, out string status, out string[] namespaces)
    {
        code = "";
        namespaces = [];
        var replacement = default(PatchLiteral?);
        bool before = operation is PatchOperation.BeforeCall or PatchOperation.TransformLastArgument or
            PatchOperation.TransformFieldWrite or PatchOperation.BeforeInstruction;
        bool action = operation is PatchOperation.BeforeCall or PatchOperation.AfterCall or
            PatchOperation.BeforeInstruction or PatchOperation.AfterInstruction or PatchOperation.BranchFallthrough or PatchOperation.BranchTaken;
        bool redirect = operation == PatchOperation.RedirectLocal;
        bool inject = operation is not (PatchOperation.ReplaceConstant or PatchOperation.RedirectLocal or PatchOperation.Cursor);

        if (!Operations.Any(choice => choice.Operation == operation))
        {
            status = Text("所选指令不支持这个操作。", "The selected instruction does not support this operation.");
            return false;
        }

        if (inject && inputs.Count > (action ? 16 : 15))
        {
            status = Text("委托参数超过 Action 或 Func 可容纳的上限，请减少传入变量。", "Too many delegate parameters. Select fewer variables.");
            return false;
        }

        if (redirect && !RedirectTargets.Any(variable => variable.Index == redirectSlot))
        {
            status = Text("请选择目标局部变量。", "Select a destination local variable.");
            return false;
        }

        if (operation == PatchOperation.ReplaceConstant)
        {
            replacement = text.Trim() == _literal!.Value.Text ? _literal : _literal.Value.Parse(text);
            if (replacement == null)
            {
                status = _literal.Value.Kind switch
                {
                    LiteralKind.String => Text("请输入双引号包围的字符串，例如 \"hello\\nworld\"。", "Enter a quoted string, such as \"hello\\nworld\"."),
                    LiteralKind.Char => Text("请输入单个字符或 Unicode 码元，例如 A 或 U+0041。", "Enter one character or Unicode code unit, such as A or U+0041."),
                    _ => string.Format(Text("请输入合法的 {0} 常量。", "Enter a valid {0} constant."), _literal.Value.Type)
                };
                return false;
            }
        }
        else if (inject && string.IsNullOrWhiteSpace(text))
        {
            status = action ? Text("请输入方法调用或 C# 语句。", "Enter a method call or C# statement.") : Text("请输入返回同一类型的 C# 表达式。", "Enter a C# expression that returns the same type.");
            return false;
        }

        bool customDelegate = inject && inputs.Any(input => input.Passing != InputPassing.Value);
        string returnType = action ? "void" : operation switch
        {
            PatchOperation.TransformConstant => _literal?.Type ?? "void",
            PatchOperation.TransformLastArgument => LastArgumentType ?? "void",
            _ => ValueType ?? "void"
        };

        var imports = new List<string>();
        if ((inject && !customDelegate) || options.CheckDuplicates || (inject && options.CheckBoundaries) ||
            _pattern.Any(atom => atom.Predicate.Contains("BitConverter.")))
            imports.Add("System");
        if (inject && options.CheckBoundaries)
            imports.Add("System.Linq");
        bool loadsField = inject && inputs.Any(input => input.UsesField);
        if (loadsField)
            imports.Add("System.Reflection");
        if (_pattern.Any(atom => atom.IsMember))
            imports.Add("Mono.Cecil");
        if (replacement != null || redirect || loadsField || options.CheckDuplicates ||
            inject && inputs.Any(input => input.UsesTypeOperand && input.Passing == InputPassing.Value) ||
            _pattern.Any(atom => atom.Predicate.Contains("Code.")) || (inject && options.CheckBoundaries))
            imports.Add("Mono.Cecil.Cil");
        imports.Add("MonoMod.Cil");
        namespaces = imports.ToArray();

        var draft = new StringBuilder();
        if (options.IncludeUsings)
        {
            foreach (string name in imports)
                draft.AppendLine($"using {name};");
            draft.AppendLine();
        }

        string delegateParameters = string.Join(", ", inputs.Select(input => input.Parameter));
        if (customDelegate)
        {
            if (!action)
                delegateParameters = $"{returnType} original, {delegateParameters}";
            draft.AppendLine($"private delegate {returnType} PatchDelegate({delegateParameters});");
            draft.AppendLine();
        }
        draft.AppendLine($"// {Text("目标方法", "Target method")} · {_method.FullName}");
        draft.AppendLine("private static void PatchMethod(ILContext il)");
        draft.AppendLine("{");
        draft.AppendLine("    ILCursor cursor = new(il);");

        string move = operation == PatchOperation.Cursor || redirect || replacement != null || options.CheckDuplicates ||
            (before ? _targetIndex != 0 : _targetIndex != _pattern.Length - 1)
            ? "Before"
            : before ? "AfterLabel" : "After";

        string search = options.SkipMissing ? "TryGotoNext" : "GotoNext";
        if (options.CheckDuplicates)
        {
            draft.AppendLine("    Func<Instruction, bool>[] pattern =");
            draft.AppendLine("    {");
            foreach (var atom in _pattern)
                draft.AppendLine($"        i => {atom.Predicate},");
            draft.AppendLine("    };");
            draft.AppendLine(options.SkipMissing
                ? $"    if (!cursor.{search}(MoveType.{move}, pattern))"
                : $"    cursor.{search}(MoveType.{move}, pattern);");
            if (options.SkipMissing)
                draft.AppendLine("        return;");

            draft.AppendLine("    ILCursor next = cursor.Clone();");
            draft.AppendLine("    next.Index++;");
            draft.AppendLine("    if (next.TryGotoNext(MoveType.Before, pattern))");
            draft.AppendLine($"        throw new InvalidOperationException({CodeText.Quote(_method.Name + Text("：补丁匹配到多处。", ": patch pattern matched more than once."))});");
        }
        else
        {
            AppendSearch(draft, $"cursor.{search}(MoveType.{move}", options.SkipMissing);
        }
        if (move == "Before" && _targetIndex != 0)
            draft.AppendLine($"    cursor.Index += {_targetIndex};");

        if (operation == PatchOperation.Cursor)
        {
            draft.AppendLine(Text("    // TODO 在此处发射 IL，cursor.Next 是所选指令。", "    // TODO Emit IL here. cursor.Next is the selected instruction."));
        }
        else if (redirect)
        {
            draft.AppendLine("    cursor.Next.OpCode = OpCodes.Stloc;");
            draft.AppendLine($"    cursor.Next.Operand = il.Body.Variables[{redirectSlot}];");
        }
        else if (replacement is { } value)
        {
            draft.AppendLine($"    cursor.Next.OpCode = OpCodes.{value.Cil.Opcode};");
            draft.AppendLine($"    cursor.Next.Operand = {value.Cil.CSharpOperand};");
        }
        else
        {
            if (move == "Before")
                draft.AppendLine(before ? "    cursor.MoveAfterLabels();" : "    cursor.Goto(cursor.Next, MoveType.After);");

            if (options.CheckBoundaries)
            {
                draft.AppendLine("    if (cursor.Prev?.OpCode.OpCodeType == OpCodeType.Prefix ||");
                if (!before)
                    draft.AppendLine("        cursor.Prev?.Previous?.OpCode.OpCodeType == OpCodeType.Prefix ||");
                draft.AppendLine("        il.Body.ExceptionHandlers.Any(region => cursor.Next != null &&");
                draft.AppendLine("            (region.TryStart == cursor.Next || region.TryEnd == cursor.Next ||");
                draft.AppendLine("             region.HandlerStart == cursor.Next || region.HandlerEnd == cursor.Next ||");
                draft.AppendLine("             region.FilterStart == cursor.Next)))");
                draft.AppendLine($"        throw new InvalidOperationException({CodeText.Quote(Text("注入点紧邻 IL 前缀或异常处理边界。", "The injection point touches an IL prefix or exception boundary."))});");
            }

            if (operation == PatchOperation.BranchTaken)
            {
                draft.AppendLine("    var branch = cursor.Prev;");
                draft.AppendLine("    var destination = (ILLabel)branch.Operand;");
                draft.AppendLine("    var fallthrough = il.DefineLabel(cursor.Next);");
                draft.AppendLine("    cursor.EmitBr(fallthrough);");
                draft.AppendLine("    branch.Operand = cursor.MarkLabel();");
            }

            foreach (var input in inputs)
            {
                foreach (string load in input.Instructions)
                    draft.AppendLine($"    {load}");
            }

            string inputTypes = string.Join(", ", inputs.Select(input => input.Type));
            string inputNames = string.Join(", ", inputs.Select(input => input.Name));
            if (action)
            {
                string delegateType = customDelegate ? "PatchDelegate" : inputs.Count == 0 ? "Action" : $"Action<{inputTypes}>";
                string parameters = customDelegate ? delegateParameters : inputNames;
                draft.AppendLine($"    cursor.EmitDelegate<{delegateType}>(({parameters}) =>");
                draft.AppendLine("    {");
                var statement = text.Trim();
                if (!statement.EndsWith(';') && !statement.EndsWith('}'))
                    statement += ";";
                foreach (string line in statement.Replace("\r\n", "\n").Split('\n'))
                    draft.AppendLine($"        {line}");
                draft.AppendLine("    });");
            }
            else
            {
                string parameters = customDelegate ? $"({delegateParameters})" :
                    inputs.Count == 0 ? "original" : $"(original, {inputNames})";
                string types = inputs.Count == 0 ? $"{returnType}, {returnType}" : $"{returnType}, {inputTypes}, {returnType}";
                string delegateType = customDelegate ? "PatchDelegate" : $"Func<{types}>";
                draft.AppendLine($"    cursor.EmitDelegate<{delegateType}>({parameters} => {text.Trim()});");
            }
        }

        if (operation == PatchOperation.BranchTaken)
            draft.AppendLine("    cursor.EmitBr(destination);");
        draft.AppendLine("}");
        code = draft.ToString();
        status = operation switch
        {
            PatchOperation.Cursor => Text("光标停在目标指令之前。", "The cursor is before the target instruction."),
            PatchOperation.RedirectLocal => string.Format(Text("把此处写入从 V_{0} 改到 V_{1}。", "Redirect this write from V_{0} to V_{1}."), WrittenSlot, redirectSlot),
            PatchOperation.BeforeInstruction => Text("到达此指令时执行 C# 语句，包括跳转到该指令的路径。", "Run C# when this instruction is reached, including on incoming jumps."),
            PatchOperation.AfterInstruction => Text("执行此指令后运行 C#，其他路径跳到下一条指令时直接继续。", "Run C# after this instruction. Jumps straight to the next instruction bypass it."),
            PatchOperation.BranchFallthrough => Text("仅在条件跳转未跳转、继续执行下一条指令时运行。", "Run only when the conditional branch falls through."),
            PatchOperation.BranchTaken => Text("仅在所选条件跳转发生时运行，随后继续到原目标。", "Run only when the selected branch is taken, then continue to its original target."),
            PatchOperation.ReplaceConstant => string.Format(Text("将 {0} 常量替换为 {1}。", "Replace the {0} constant with {1}."), _literal!.Value.Type, replacement!.Value.Text),
            PatchOperation.BeforeCall => Text("调用前运行 Action，保留已入栈的参数。", "Run code before the call, preserving the arguments on the stack."),
            PatchOperation.AfterCall => Text("调用后运行 Action，保留原返回值。", "Run code after the call, preserving its return value."),
            PatchOperation.TransformResult => string.Format(Text("变换调用结果（{0}），用 original 引用原值。", "Transform the {0} call result. Use original for its value."), ValueType),
            PatchOperation.TransformLastArgument => string.Format(Text("变换最后一个实参（{0}）。", "Transform the final {0} argument."), LastArgumentType),
            PatchOperation.TransformFieldRead => string.Format(Text("变换字段读取值（{0}）。", "Transform the {0} field read."), ValueType),
            PatchOperation.TransformFieldWrite => string.Format(Text("变换字段写入值（{0}）。", "Transform the {0} field value before storing it."), ValueType),
            _ => Text("变换栈顶结果。原值可用 original 引用，表达式由模组编译器检查。", "Transform the top stack value. Use original for the current value.")
        };
        return true;
    }

    private void AppendSearch(StringBuilder draft, string head, bool skipMissing)
    {
        var args = _pattern.Select(atom => "i => " + atom.Predicate).ToArray();
        string prefix = skipMissing ? "    if (!" : "    ";
        string suffix = skipMissing ? "))" : ");";
        if (args.Length == 1)
            draft.AppendLine($"{prefix}{head}, {args[0]}{suffix}");
        else
        {
            draft.AppendLine($"{prefix}{head},");
            draft.AppendLine("        " + string.Join(",\n        ", args) + suffix);
        }

        if (skipMissing)
            draft.AppendLine("        return;");
    }

    private OperationChoice[] GetOperations()
    {
        const string sampleAction = "System.Console.WriteLine(\"IL hook\")";
        var choices = new List<OperationChoice>();
        if (_literal is { } literal)
        {
            choices.Add(new(PatchOperation.ReplaceConstant, string.Format(Text("替换常量（{0}）", "Replace constant ({0})"), literal.Type), literal.Text));
            choices.Add(new(PatchOperation.TransformConstant, Text("用 C# 表达式变换", "Transform with a C# expression"), "original"));
        }
        else
        {
            Code target = _target.OpCode.Code;
            bool prefixed = _target.Previous?.OpCode.OpCodeType == OpCodeType.Prefix;
            if (RedirectTargets.Length > 0)
                choices.Add(new(PatchOperation.RedirectLocal, Text("改写到同类型局部变量", "Redirect to another local of the same type"), ""));
            if (IsConditionalBranch(_target))
            {
                choices.Add(new(PatchOperation.BranchFallthrough, Text("未跳转路径插入 C#", "Insert C# on fall-through"), sampleAction));
                if (_target.Next != null && !IsExceptionBoundary(_method.Body, _target.Next))
                    choices.Add(new(PatchOperation.BranchTaken, Text("跳转路径插入 C#", "Insert C# on taken branch"), sampleAction));
            }
            if (target is Code.Call or Code.Callvirt or Code.Newobj)
            {
                if (ValueType != null && !prefixed)
                    choices.Add(new(PatchOperation.TransformResult, Text("变换调用结果", "Transform call result"), "original"));
                if (!prefixed)
                {
                    if (LastArgumentType != null)
                        choices.Add(new(PatchOperation.TransformLastArgument, Text("变换最后一个实参", "Transform last argument"), "original"));
                    choices.Add(new(PatchOperation.BeforeCall, Text("调用前执行 C# 语句", "Run C# before call"), sampleAction));
                    choices.Add(new(PatchOperation.AfterCall, Text("调用后执行 C# 语句", "Run C# after call"), sampleAction));
                }
            }
            else if (ValueType != null)
            {
                if (target is Code.Ldfld or Code.Ldsfld)
                    choices.Add(new(PatchOperation.TransformFieldRead, Text("变换读取值", "Transform field read"), "original"));
                else if (target is Code.Stfld or Code.Stsfld && !prefixed)
                    choices.Add(new(PatchOperation.TransformFieldWrite, Text("变换写入值", "Transform field write"), "original"));
            }
        }
        if (_target.OpCode.OpCodeType != OpCodeType.Prefix &&
            _target.Previous?.OpCode.OpCodeType != OpCodeType.Prefix &&
            !IsExceptionBoundary(_method.Body, _target))
            choices.Add(new(PatchOperation.BeforeInstruction, Text("此指令前执行 C# 语句", "Run C# before instruction"), sampleAction));
        if (_target.Next != null && _target.OpCode.OpCodeType != OpCodeType.Prefix &&
            _target.Previous?.OpCode.Code != Code.Tail &&
            _target.OpCode.FlowControl is not (FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw) &&
            !IsExceptionBoundary(_method.Body, _target.Next))
            choices.Add(new(PatchOperation.AfterInstruction, Text("此指令后执行 C# 语句", "Run C# after instruction"), sampleAction));
        choices.Add(new(PatchOperation.Cursor, Text("仅生成定位草稿", "Generate cursor only"), ""));
        return choices.ToArray();
    }

    private static string? TargetType(Instruction instruction) => instruction.Operand switch
    {
        MethodReference method when instruction.OpCode.Code == Code.Newobj => TypeName(method.DeclaringType),
        MethodReference method when instruction.OpCode.Code is Code.Call or Code.Callvirt => TypeName(method.ReturnType),
        FieldReference field => TypeName(field.FieldType),
        _ => null
    };

    private static string? TypeName(TypeReference type)
    {
        if (type is ArrayType array && TypeName(array.ElementType) is { } element)
            return $"{element}[{new string(',', array.Rank - 1)}]";

        return type.MetadataType switch
        {
            MetadataType.Boolean => "bool",
            MetadataType.Char => "char",
            MetadataType.SByte => "sbyte",
            MetadataType.Byte => "byte",
            MetadataType.Int16 => "short",
            MetadataType.UInt16 => "ushort",
            MetadataType.Int32 => "int",
            MetadataType.UInt32 => "uint",
            MetadataType.Int64 => "long",
            MetadataType.UInt64 => "ulong",
            MetadataType.Single => "float",
            MetadataType.Double => "double",
            MetadataType.String => "string",
            MetadataType.Object => "object",
            MetadataType.IntPtr => "global::System.IntPtr",
            MetadataType.UIntPtr => "global::System.UIntPtr",
            MetadataType.Class or MetadataType.ValueType when !type.HasGenericParameters &&
                type.FullName.Replace('/', '.').Split('.').All(IsIdentifier)
                => "global::" + type.FullName.Replace('/', '.'),
            _ => null
        };
    }

    private static bool IsIdentifier(string text) => text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_')
        && text.All(character => char.IsLetterOrDigit(character) || character == '_');

    private static bool SameType(TypeReference left, TypeReference right)
        => left.FullName == right.FullName && left.Scope?.Name == right.Scope?.Name;

    private static bool IsExceptionBoundary(MethodBody body, Instruction instruction)
        => body.ExceptionHandlers.Any(region => region.TryStart == instruction || region.TryEnd == instruction ||
            region.HandlerStart == instruction || region.HandlerEnd == instruction || region.FilterStart == instruction);

    private static bool IsConditionalBranch(Instruction instruction)
        => instruction.OpCode.FlowControl == FlowControl.Cond_Branch && instruction.OpCode.Code != Code.Switch;

    public static int? StoreSlot(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Stloc_0 => 0, Code.Stloc_1 => 1, Code.Stloc_2 => 2, Code.Stloc_3 => 3,
        Code.Stloc or Code.Stloc_S when instruction.Operand is VariableReference local => local.Index,
        _ => null
    };

    public static int? LoadSlot(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldloc_0 => 0, Code.Ldloc_1 => 1, Code.Ldloc_2 => 2, Code.Ldloc_3 => 3,
        Code.Ldloc or Code.Ldloc_S when instruction.Operand is VariableReference local => local.Index,
        _ => null
    };

    private static Atom? Describe(Instruction instruction)
    {
        if (CilValue.Read(instruction) is { } literal)
            return new Atom(candidate => CilValue.Read(candidate) == literal, literal.Predicate);

        if (IsConditionalBranch(instruction))
        {
            Code code = LongBranch(instruction.OpCode.Code);
            return new Atom(candidate => LongBranch(candidate.OpCode.Code) == code,
                $"i.OpCode.Code is Code.{code} or Code.{code}_S");
        }

        return instruction.OpCode.Code switch
        {
            Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3
                => new Atom(candidate => candidate.OpCode.Code is Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3, "i.MatchStloc(out int _)"),
            Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3
                => new Atom(candidate => candidate.OpCode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3, "i.MatchLdloc(out int _)"),
            Code.Ldarg or Code.Ldarg_S or Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3
                => new Atom(candidate => Argument(candidate) == Argument(instruction), $"i.MatchLdarg({Argument(instruction)})"),
            Code.Call or Code.Callvirt or Code.Newobj when instruction.Operand is MethodReference member
                => Member(instruction.OpCode.Code, member.FullName, "MethodReference"),
            Code.Ldfld or Code.Ldsfld or Code.Stfld or Code.Stsfld when instruction.Operand is FieldReference member
                => Member(instruction.OpCode.Code, member.FullName, "FieldReference"),
            Code.Add or Code.Sub or Code.Mul or Code.Div or Code.Dup or Code.Conv_R4 or Code.Conv_I4 or
                Code.Ceq or Code.Clt or Code.Cgt or Code.Pop or Code.Ret or Code.Throw or Code.Ldnull
                => new Atom(candidate => candidate.OpCode.Code == instruction.OpCode.Code, $"i.OpCode.Code == Code.{instruction.OpCode.Code}"),
            _ => null
        };
    }

    private static Code LongBranch(Code code) => code switch
    {
        Code.Brfalse_S => Code.Brfalse, Code.Brtrue_S => Code.Brtrue,
        Code.Beq_S => Code.Beq, Code.Bne_Un_S => Code.Bne_Un,
        Code.Bge_S => Code.Bge, Code.Bge_Un_S => Code.Bge_Un,
        Code.Bgt_S => Code.Bgt, Code.Bgt_Un_S => Code.Bgt_Un,
        Code.Ble_S => Code.Ble, Code.Ble_Un_S => Code.Ble_Un,
        Code.Blt_S => Code.Blt, Code.Blt_Un_S => Code.Blt_Un,
        _ => code
    };

    private static Atom Member(Code code, string signature, string type)
        => new(candidate => candidate.OpCode.Code == code && candidate.Operand is MemberReference member && member.FullName == signature,
            $"i.OpCode.Code == Code.{code} && i.Operand is {type} member && member.FullName == {CodeText.Quote(signature)}", true);

    private static int Argument(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldarg_0 => 0,
        Code.Ldarg_1 => 1,
        Code.Ldarg_2 => 2,
        Code.Ldarg_3 => 3,
        Code.Ldarg or Code.Ldarg_S when instruction.Operand is ParameterDefinition parameter
            => parameter.Index + (parameter.Method.HasThis ? 1 : 0),
        _ => -1
    };
}
