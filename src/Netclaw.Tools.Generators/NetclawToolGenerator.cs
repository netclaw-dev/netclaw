// -----------------------------------------------------------------------
// <copyright file="NetclawToolGenerator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Netclaw.Tools.Generators;

[Generator]
public sealed class NetclawToolGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "Netclaw.Tools.NetclawToolAttribute";
    private const string VariantAttributeFullName = "Netclaw.Tools.ToolArgumentVariantAttribute";
    private const string BaseClassPrefix = "Netclaw.Tools.NetclawTool<";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var toolClasses = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractToolModel(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        context.RegisterSourceOutput(toolClasses, static (spc, model) => GenerateSource(spc, model));
    }

    private static ToolModel? ExtractToolModel(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var classSymbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var attr = ctx.Attributes[0];

        // Read attribute values
        var name = attr.ConstructorArguments[0].Value as string;
        var description = attr.ConstructorArguments[1].Value as string;
        if (name is null || description is null)
            return null;

        var grant = "default";
        var liveness = "Opaque";
        foreach (var namedArg in attr.NamedArguments)
        {
            if (namedArg.Key == "Grant" && namedArg.Value.Value is string g)
                grant = g;
            if (namedArg.Key == "Liveness")
                liveness = GetEnumMemberName(namedArg.Value, liveness);
        }

        // Find the TParams type from the base class
        INamedTypeSymbol? paramsType = null;
        var baseType = classSymbol.BaseType;
        while (baseType is not null)
        {
            if (baseType.IsGenericType &&
                baseType.OriginalDefinition.ToDisplayString().StartsWith("Netclaw.Tools.NetclawTool<"))
            {
                paramsType = baseType.TypeArguments[0] as INamedTypeSymbol;
                break;
            }
            baseType = baseType.BaseType;
        }

        if (paramsType is null)
            return null;

        // Extract constructor parameters from the params record
        var primaryCtor = paramsType.InstanceConstructors
            .FirstOrDefault(c => c.Parameters.Length > 0 && !c.IsImplicitlyDeclared)
            ?? paramsType.InstanceConstructors
            .FirstOrDefault(c => c.Parameters.Length > 0);

        if (primaryCtor is null)
            return null;

        var parameters = new List<ToolParameter>();
        foreach (var param in primaryCtor.Parameters)
        {
            ct.ThrowIfCancellationRequested();

            var paramDescription = ReadDescription(param.GetAttributes());
            if (paramDescription.Length == 0)
            {
                var property = paramsType.GetMembers(param.Name)
                    .OfType<IPropertySymbol>()
                    .FirstOrDefault();
                if (property is not null)
                    paramDescription = ReadDescription(property.GetAttributes());
            }

            var isNullable = param.Type.NullableAnnotation == NullableAnnotation.Annotated;
            var hasDefault = param.HasExplicitDefaultValue;
            var isRequired = !isNullable && !hasDefault;

            var jsonType = GetJsonType(param.Type);

            parameters.Add(new ToolParameter(
                param.Name,
                paramDescription,
                jsonType,
                isRequired,
                isNullable,
                param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        }

        var classNamespace = classSymbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : classSymbol.ContainingNamespace.ToDisplayString();

        var variants = ExtractVariants(classSymbol, parameters, out var variantError);

        return new ToolModel(
            classNamespace,
            classSymbol.Name,
            name,
            description,
            grant,
            liveness,
            paramsType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            [.. parameters],
            variants,
            variantError);
    }

    private static string ReadDescription(ImmutableArray<AttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass?.Name == "DescriptionAttribute"
                && attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is string description)
            {
                return description;
            }
        }

        return string.Empty;
    }

    private static ImmutableArray<ToolVariant> ExtractVariants(
        INamedTypeSymbol classSymbol,
        IReadOnlyList<ToolParameter> parameters,
        out string? error)
    {
        error = null;
        var attributes = classSymbol.GetAttributes()
            .Where(static attribute => attribute.AttributeClass?.ToDisplayString() == VariantAttributeFullName)
            .ToArray();
        if (attributes.Length == 0)
            return [];

        var parameterNames = new HashSet<string>(
            parameters.Select(static parameter => parameter.Name),
            System.StringComparer.Ordinal);
        var variants = new List<ToolVariant>(attributes.Length);
        string? sharedDiscriminator = null;
        var values = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var attribute in attributes)
        {
            if (attribute.ConstructorArguments.Length != 2
                || attribute.ConstructorArguments[0].Value is not string discriminator
                || attribute.ConstructorArguments[1].Value is not string value
                || string.IsNullOrWhiteSpace(discriminator)
                || string.IsNullOrWhiteSpace(value))
            {
                error = "the discriminator parameter and value must be non-empty strings";
                return [];
            }

            if (!parameterNames.Contains(discriminator))
            {
                error = $"the discriminator parameter '{discriminator}' does not exist";
                return [];
            }

            var discriminatorParameter = parameters.First(parameter => parameter.Name == discriminator);
            if (discriminatorParameter.JsonType != "string")
            {
                error = $"the discriminator parameter '{discriminator}' must be a string";
                return [];
            }

            sharedDiscriminator ??= discriminator;
            if (!string.Equals(sharedDiscriminator, discriminator, System.StringComparison.Ordinal))
            {
                error = "all variants must use the same discriminator parameter";
                return [];
            }

            if (!values.Add(value))
            {
                error = $"the discriminator value '{value}' is duplicated";
                return [];
            }

            if (!TryReadStringArray(attribute, "Required", out var required)
                || !TryReadStringArray(attribute, "Forbidden", out var forbidden))
            {
                error = $"variant '{value}' contains a null parameter name";
                return [];
            }

            if (required.Any(name => !parameterNames.Contains(name))
                || forbidden.Any(name => !parameterNames.Contains(name)))
            {
                error = $"variant '{value}' references an unknown parameter";
                return [];
            }

            if (required.Contains(discriminator, System.StringComparer.Ordinal)
                || forbidden.Contains(discriminator, System.StringComparer.Ordinal)
                || required.Distinct(System.StringComparer.Ordinal).Count() != required.Length
                || forbidden.Distinct(System.StringComparer.Ordinal).Count() != forbidden.Length
                || required.Intersect(forbidden, System.StringComparer.Ordinal).Any())
            {
                error = $"variant '{value}' has conflicting required or forbidden parameters";
                return [];
            }

            variants.Add(new ToolVariant(discriminator, value, required, forbidden));
        }

        return [.. variants];
    }

    private static bool TryReadStringArray(
        AttributeData attribute,
        string name,
        out ImmutableArray<string> values)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key != name || argument.Value.Kind != TypedConstantKind.Array)
                continue;

            var result = ImmutableArray.CreateBuilder<string>(argument.Value.Values.Length);
            foreach (var value in argument.Value.Values)
            {
                if (value.Value is not string parameterName)
                {
                    values = [];
                    return false;
                }

                result.Add(parameterName);
            }

            values = result.MoveToImmutable();
            return true;
        }

        values = [];
        return true;
    }

    private static string GetJsonType(ITypeSymbol type)
    {
        // Unwrap Nullable<T>
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];

        // Unwrap nullable reference annotation
        if (type.NullableAnnotation == NullableAnnotation.Annotated && type.OriginalDefinition is INamedTypeSymbol)
            type = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);

        if (IsStringSequence(type))
            return "array";

        return type.SpecialType switch
        {
            SpecialType.System_String => "string",
            SpecialType.System_Int32 => "integer",
            SpecialType.System_Int64 => "integer",
            SpecialType.System_Single => "number",
            SpecialType.System_Double => "number",
            SpecialType.System_Boolean => "boolean",
            _ => type.Name switch
            {
                "Int16" => "integer",
                "UInt16" => "integer",
                "UInt32" => "integer",
                "UInt64" => "integer",
                "IReadOnlyDictionary" when IsStringDictionary(type) => "object",
                "IDictionary" when IsStringDictionary(type) => "object",
                "Dictionary" when IsStringDictionary(type) => "object",
                _ => "string" // fallback
            }
        };
    }

    private static bool IsStringDictionary(ITypeSymbol type)
        => type is INamedTypeSymbol { TypeArguments.Length: 2 } named
           && named.TypeArguments[0].SpecialType == SpecialType.System_String
           && named.TypeArguments[1].SpecialType == SpecialType.System_String;

    private static bool IsStringSequence(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
            return array.ElementType.SpecialType == SpecialType.System_String;

        return type is INamedTypeSymbol { TypeArguments.Length: 1 } named
               && named.TypeArguments[0].SpecialType == SpecialType.System_String
               && named.Name is "IEnumerable" or "IReadOnlyCollection" or "IReadOnlyList" or "ICollection" or "IList" or "List";
    }

    private static string GetEnumMemberName(TypedConstant value, string defaultName)
    {
        if (value.Kind != TypedConstantKind.Enum ||
            value.Type is not INamedTypeSymbol enumType ||
            value.Value is null)
        {
            return defaultName;
        }

        foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
        {
            if (member.HasConstantValue && Equals(member.ConstantValue, value.Value))
                return member.Name;
        }

        return $"__Unknown_{value.Value}";
    }

    private static void GenerateSource(SourceProductionContext spc, ToolModel model)
    {
        if (model.VariantError is not null)
        {
            var error = $"#error NETCLAWTOOL001 Tool '{model.ToolName}' has an invalid conditional variant: {model.VariantError}";
            spc.AddSource(
                $"{model.ClassName}.variant-error.g.cs",
                SourceText.From(error, Encoding.UTF8));
            return;
        }

        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using Microsoft.Extensions.AI;");
        sb.AppendLine();

        if (model.Namespace is not null)
        {
            sb.AppendLine($"namespace {model.Namespace};");
            sb.AppendLine();
        }

        sb.AppendLine($"partial class {model.ClassName}");
        sb.AppendLine("{");

        // -- Static schema field --
        sb.AppendLine("    private static readonly JsonElement _generatedSchema =");
        sb.AppendLine("        JsonDocument.Parse(\"\"\"");
        sb.AppendLine("        {");
        sb.AppendLine("            \"type\": \"object\",");
        sb.AppendLine("            \"properties\": {");

        for (var i = 0; i < model.Parameters.Length; i++)
        {
            var p = model.Parameters[i];
            sb.AppendLine($"                \"{p.Name}\": {{");
            sb.AppendLine($"                    \"type\": \"{p.JsonType}\",");
            if (p.JsonType == "object")
                sb.AppendLine("                    \"additionalProperties\": { \"type\": \"string\" },");
            if (p.JsonType == "array")
                sb.AppendLine("                    \"items\": { \"type\": \"string\" },");
            sb.AppendLine($"                    \"description\": \"{EscapeJson(p.Description)}\"");
            sb.AppendLine("                },");
        }

        // Meta properties injected after user-defined parameters
        sb.AppendLine("                \"_rationale\": {");
        sb.AppendLine("                    \"type\": \"string\",");
        sb.AppendLine("                    \"description\": \"State your intent for this tool call in one sentence — what are you trying to accomplish and why?\"");
        sb.AppendLine("                },");
        sb.AppendLine("                \"_timeout_seconds\": {");
        sb.AppendLine("                    \"type\": \"integer\",");
        sb.AppendLine("                    \"description\": \"Requested timeout in seconds. Only set when the default is insufficient.\"");
        sb.AppendLine("                },");
        sb.AppendLine("                \"_background\": {");
        sb.AppendLine("                    \"type\": \"boolean\",");
        sb.AppendLine("                    \"description\": \"Set to true to run this tool in the background and receive results later.\"");
        sb.AppendLine("                }");

        sb.AppendLine("            },");

        // Required array — user-required params plus _rationale
        var required = model.Parameters.Where(p => p.IsRequired).ToArray();
        var requiredNames = required.Select(p => $"\"{p.Name}\"").Append("\"_rationale\"");
        sb.Append("            \"required\": [");
        sb.Append(string.Join(", ", requiredNames));
        sb.Append("]");

        if (model.Variants.Length > 0)
        {
            sb.AppendLine(",");
            sb.AppendLine("            \"additionalProperties\": false,");
            sb.AppendLine("            \"oneOf\": [");
            for (var index = 0; index < model.Variants.Length; index++)
            {
                var variant = model.Variants[index];
                sb.AppendLine("                {");
                sb.AppendLine($"                    \"properties\": {{ \"{variant.DiscriminatorParameter}\": {{ \"enum\": [\"{EscapeJson(variant.DiscriminatorValue)}\"] }} }},");
                var branchRequired = variant.Required
                    .Prepend(variant.DiscriminatorParameter)
                    .Select(static name => $"\"{name}\"");
                sb.AppendLine($"                    \"required\": [{string.Join(", ", branchRequired)}]{(variant.Forbidden.Length > 0 ? "," : string.Empty)}");
                if (variant.Forbidden.Length > 0)
                {
                    var forbidden = variant.Forbidden
                        .Select(static name => $"{{ \"required\": [\"{name}\"] }}");
                    sb.AppendLine($"                    \"not\": {{ \"anyOf\": [{string.Join(", ", forbidden)}] }}");
                }
                sb.AppendLine(index == model.Variants.Length - 1 ? "                }" : "                },");
            }
            sb.AppendLine("            ]");
        }
        else
        {
            sb.AppendLine();
        }

        sb.AppendLine("        }");
        sb.AppendLine("        \"\"\").RootElement.Clone();");
        sb.AppendLine();

        // -- INetclawTool properties --
        sb.AppendLine($"    public override string Name => \"{EscapeJson(model.ToolName)}\";");
        // LlmFacingName goes through LlmFacingToolName.FromCanonical at type
        // init so any future attribute name containing '/' (or other
        // disallowed chars) fails loudly the first time the tool is
        // referenced, rather than at the Anthropic API boundary on the
        // user's first call. First-party tool names today have no '/'
        // so this round-trips unchanged.
        sb.AppendLine($"    private static readonly Netclaw.Tools.LlmFacingToolName _generatedLlmFacingName = Netclaw.Tools.LlmFacingToolName.FromCanonical(\"{EscapeJson(model.ToolName)}\");");
        sb.AppendLine("    public override Netclaw.Tools.LlmFacingToolName LlmFacingName => _generatedLlmFacingName;");
        sb.AppendLine($"    public override string Description => \"{EscapeJson(model.ToolDescription)}\";");
        sb.AppendLine($"    public override string GrantCategory => \"{EscapeJson(model.Grant)}\";");
        sb.AppendLine($"    public override Netclaw.Tools.ToolLivenessMode LivenessMode => Netclaw.Tools.ToolLivenessMode.{model.Liveness};");
        sb.AppendLine("    public override JsonElement ParameterSchema => _generatedSchema;");
        sb.AppendLine();

        // -- ParseArguments --
        sb.AppendLine($"    public override {model.ParamsTypeName} ParseArguments(System.Collections.Generic.IDictionary<string, object?> arguments)");
        sb.AppendLine("    {");

        foreach (var p in model.Parameters)
        {
            if (p.JsonType == "string")
            {
                sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetString(arguments, \"{p.Name}\");");
                if (p.IsRequired)
                {
                    sb.AppendLine($"        if (__{p.Name} is null)");
                    sb.AppendLine($"            throw new System.ArgumentException(\"Required parameter '{p.Name}' is missing.\");");
                }
            }
            else if (p.JsonType == "integer")
            {
                // Strict variants throw on present-but-invalid values, so the
                // null-coalesce arms below only apply a default for a genuinely
                // absent parameter (tool-arg-validation spec).
                if (p.IsNullable)
                    sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetIntStrict(arguments, \"{p.Name}\");");
                else if (p.IsRequired)
                {
                    sb.AppendLine($"        var __{p.Name}_raw = Netclaw.Tools.ToolArgumentHelper.GetIntStrict(arguments, \"{p.Name}\");");
                    sb.AppendLine($"        if (__{p.Name}_raw is null)");
                    sb.AppendLine($"            throw new System.ArgumentException(\"Required parameter '{p.Name}' is missing.\");");
                    sb.AppendLine($"        var __{p.Name} = __{p.Name}_raw.Value;");
                }
                else
                    sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetIntStrict(arguments, \"{p.Name}\") ?? 0;");
            }
            else if (p.JsonType == "number")
            {
                if (p.IsNullable)
                    sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetDoubleStrict(arguments, \"{p.Name}\");");
                else if (p.IsRequired)
                {
                    sb.AppendLine($"        var __{p.Name}_raw = Netclaw.Tools.ToolArgumentHelper.GetDoubleStrict(arguments, \"{p.Name}\");");
                    sb.AppendLine($"        if (__{p.Name}_raw is null)");
                    sb.AppendLine($"            throw new System.ArgumentException(\"Required parameter '{p.Name}' is missing.\");");
                    sb.AppendLine($"        var __{p.Name} = __{p.Name}_raw.Value;");
                }
                else
                    sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetDoubleStrict(arguments, \"{p.Name}\") ?? 0.0;");
            }
            else if (p.JsonType == "boolean")
            {
                if (p.IsNullable)
                    sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetBoolStrict(arguments, \"{p.Name}\");");
                else if (p.IsRequired)
                {
                    sb.AppendLine($"        var __{p.Name}_raw = Netclaw.Tools.ToolArgumentHelper.GetBoolStrict(arguments, \"{p.Name}\");");
                    sb.AppendLine($"        if (__{p.Name}_raw is null)");
                    sb.AppendLine($"            throw new System.ArgumentException(\"Required parameter '{p.Name}' is missing.\");");
                    sb.AppendLine($"        var __{p.Name} = __{p.Name}_raw.Value;");
                }
                else
                    sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetBoolStrict(arguments, \"{p.Name}\") ?? false;");
            }
            else if (p.JsonType == "object")
            {
                sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetStringDictionary(arguments, \"{p.Name}\");");
                if (p.IsRequired)
                {
                    sb.AppendLine($"        if (__{p.Name} is null)");
                    sb.AppendLine($"            throw new System.ArgumentException(\"Required parameter '{p.Name}' is missing.\");");
                }
            }
            else if (p.JsonType == "array")
            {
                sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetStringArray(arguments, \"{p.Name}\");");
                if (p.IsRequired)
                {
                    sb.AppendLine($"        if (__{p.Name} is null)");
                    sb.AppendLine($"            throw new System.ArgumentException(\"Required parameter '{p.Name}' is missing.\");");
                }
            }
        }

        if (model.Variants.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("        static int __ArgumentValueCount(System.Collections.Generic.IDictionary<string, object?> source, string parameter)");
            sb.AppendLine("        {");
            sb.AppendLine("            var normalized = Netclaw.Tools.ToolArgumentHelper.NormalizeKey(parameter);");
            sb.AppendLine("            var count = 0;");
            sb.AppendLine("            foreach (var pair in source)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (!string.Equals(Netclaw.Tools.ToolArgumentHelper.NormalizeKey(pair.Key), normalized, System.StringComparison.OrdinalIgnoreCase))");
            sb.AppendLine("                    continue;");
            sb.AppendLine("                if (pair.Value is not null and not JsonElement { ValueKind: JsonValueKind.Null })");
            sb.AppendLine("                    count++;");
            sb.AppendLine("            }");
            sb.AppendLine("            return count;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        var __variantMatches = 0;");
            foreach (var variant in model.Variants)
            {
                sb.AppendLine($"        if (__ArgumentValueCount(arguments, \"{variant.DiscriminatorParameter}\") == 1");
                sb.AppendLine($"            && string.Equals(__{variant.DiscriminatorParameter}, \"{EscapeJson(variant.DiscriminatorValue)}\", System.StringComparison.OrdinalIgnoreCase)");
                foreach (var requiredParameter in variant.Required)
                    sb.AppendLine($"            && __ArgumentValueCount(arguments, \"{requiredParameter}\") == 1");
                foreach (var forbiddenParameter in variant.Forbidden)
                    sb.AppendLine($"            && __ArgumentValueCount(arguments, \"{forbiddenParameter}\") == 0");
                sb.AppendLine("           )");
                sb.AppendLine("            __variantMatches++;");
            }
            sb.AppendLine("        if (__variantMatches != 1)");
            sb.AppendLine($"            throw new System.ArgumentException(\"Arguments for tool '{EscapeJson(model.ToolName)}' must match exactly one declared variant. The tool was NOT executed.\");");
        }

        sb.AppendLine();
        sb.Append($"        return new {model.ParamsTypeName}(");
        sb.Append(string.Join(", ", model.Parameters.Select(p => $"__{p.Name}")));
        sb.AppendLine(");");
        sb.AppendLine("    }");

        sb.AppendLine("}");

        spc.AddSource($"{model.ClassName}.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

internal sealed class ToolModel
{
    public ToolModel(string? ns, string className, string toolName, string toolDescription,
        string grant, string liveness, string paramsTypeName, ImmutableArray<ToolParameter> parameters,
        ImmutableArray<ToolVariant> variants, string? variantError)
    {
        Namespace = ns;
        ClassName = className;
        ToolName = toolName;
        ToolDescription = toolDescription;
        Grant = grant;
        Liveness = liveness;
        ParamsTypeName = paramsTypeName;
        Parameters = parameters;
        Variants = variants;
        VariantError = variantError;
    }

    public string? Namespace { get; }
    public string ClassName { get; }
    public string ToolName { get; }
    public string ToolDescription { get; }
    public string Grant { get; }
    public string Liveness { get; }
    public string ParamsTypeName { get; }
    public ImmutableArray<ToolParameter> Parameters { get; }
    public ImmutableArray<ToolVariant> Variants { get; }
    public string? VariantError { get; }
}

internal sealed class ToolVariant
{
    public ToolVariant(
        string discriminatorParameter,
        string discriminatorValue,
        ImmutableArray<string> required,
        ImmutableArray<string> forbidden)
    {
        DiscriminatorParameter = discriminatorParameter;
        DiscriminatorValue = discriminatorValue;
        Required = required;
        Forbidden = forbidden;
    }

    public string DiscriminatorParameter { get; }
    public string DiscriminatorValue { get; }
    public ImmutableArray<string> Required { get; }
    public ImmutableArray<string> Forbidden { get; }
}

internal sealed class ToolParameter
{
    public ToolParameter(string name, string description, string jsonType,
        bool isRequired, bool isNullable, string clrTypeName)
    {
        Name = name;
        Description = description;
        JsonType = jsonType;
        IsRequired = isRequired;
        IsNullable = isNullable;
        ClrTypeName = clrTypeName;
    }

    public string Name { get; }
    public string Description { get; }
    public string JsonType { get; }
    public bool IsRequired { get; }
    public bool IsNullable { get; }
    public string ClrTypeName { get; }
}
