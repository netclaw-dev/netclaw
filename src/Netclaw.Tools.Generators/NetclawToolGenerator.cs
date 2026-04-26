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
        foreach (var namedArg in attr.NamedArguments)
        {
            if (namedArg.Key == "Grant" && namedArg.Value.Value is string g)
                grant = g;
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

            var paramDescription = "";
            foreach (var paramAttr in param.GetAttributes())
            {
                if (paramAttr.AttributeClass?.Name == "DescriptionAttribute" &&
                    paramAttr.ConstructorArguments.Length > 0 &&
                    paramAttr.ConstructorArguments[0].Value is string desc)
                {
                    paramDescription = desc;
                    break;
                }
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

        return new ToolModel(
            classNamespace,
            classSymbol.Name,
            name,
            description,
            grant,
            paramsType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            parameters.ToImmutableArray());
    }

    private static string GetJsonType(ITypeSymbol type)
    {
        // Unwrap Nullable<T>
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];

        // Unwrap nullable reference annotation
        if (type.NullableAnnotation == NullableAnnotation.Annotated && type.OriginalDefinition is INamedTypeSymbol)
            type = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);

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
                _ => "string" // fallback
            }
        };
    }

    private static void GenerateSource(SourceProductionContext spc, ToolModel model)
    {
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
        sb.AppendLine("]");

        sb.AppendLine("        }");
        sb.AppendLine("        \"\"\").RootElement.Clone();");
        sb.AppendLine();

        // -- INetclawTool properties --
        sb.AppendLine($"    public override string Name => \"{EscapeJson(model.ToolName)}\";");
        sb.AppendLine($"    public override string Description => \"{EscapeJson(model.ToolDescription)}\";");
        sb.AppendLine($"    public override string GrantCategory => \"{EscapeJson(model.Grant)}\";");
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
                if (p.IsNullable)
                    sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetNullableInt(arguments, \"{p.Name}\");");
                else if (p.IsRequired)
                {
                    sb.AppendLine($"        var __{p.Name}_raw = Netclaw.Tools.ToolArgumentHelper.GetNullableInt(arguments, \"{p.Name}\");");
                    sb.AppendLine($"        if (__{p.Name}_raw is null)");
                    sb.AppendLine($"            throw new System.ArgumentException(\"Required parameter '{p.Name}' is missing.\");");
                    sb.AppendLine($"        var __{p.Name} = __{p.Name}_raw.Value;");
                }
                else
                    sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetNullableInt(arguments, \"{p.Name}\") ?? 0;");
            }
            else if (p.JsonType == "number")
            {
                if (p.IsNullable)
                    sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetNullableDouble(arguments, \"{p.Name}\");");
                else if (p.IsRequired)
                {
                    sb.AppendLine($"        var __{p.Name}_raw = Netclaw.Tools.ToolArgumentHelper.GetNullableDouble(arguments, \"{p.Name}\");");
                    sb.AppendLine($"        if (__{p.Name}_raw is null)");
                    sb.AppendLine($"            throw new System.ArgumentException(\"Required parameter '{p.Name}' is missing.\");");
                    sb.AppendLine($"        var __{p.Name} = __{p.Name}_raw.Value;");
                }
                else
                    sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetNullableDouble(arguments, \"{p.Name}\") ?? 0.0;");
            }
            else if (p.JsonType == "boolean")
            {
                if (p.IsNullable)
                    sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetNullableBool(arguments, \"{p.Name}\");");
                else if (p.IsRequired)
                {
                    sb.AppendLine($"        var __{p.Name}_raw = Netclaw.Tools.ToolArgumentHelper.GetNullableBool(arguments, \"{p.Name}\");");
                    sb.AppendLine($"        if (__{p.Name}_raw is null)");
                    sb.AppendLine($"            throw new System.ArgumentException(\"Required parameter '{p.Name}' is missing.\");");
                    sb.AppendLine($"        var __{p.Name} = __{p.Name}_raw.Value;");
                }
                else
                    sb.AppendLine($"        var __{p.Name} = Netclaw.Tools.ToolArgumentHelper.GetNullableBool(arguments, \"{p.Name}\") ?? false;");
            }
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
        string grant, string paramsTypeName, ImmutableArray<ToolParameter> parameters)
    {
        Namespace = ns;
        ClassName = className;
        ToolName = toolName;
        ToolDescription = toolDescription;
        Grant = grant;
        ParamsTypeName = paramsTypeName;
        Parameters = parameters;
    }

    public string? Namespace { get; }
    public string ClassName { get; }
    public string ToolName { get; }
    public string ToolDescription { get; }
    public string Grant { get; }
    public string ParamsTypeName { get; }
    public ImmutableArray<ToolParameter> Parameters { get; }
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
