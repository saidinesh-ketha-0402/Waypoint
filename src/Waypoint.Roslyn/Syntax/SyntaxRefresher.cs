using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Waypoint.Core.Indexing;
using Waypoint.Core.Paths;
using Waypoint.Core.Storage;

namespace Waypoint.Roslyn.Syntax;

public sealed class SyntaxRefresher : ISyntaxRefresher
{
  private readonly SqliteConnection? _connection;

  public SyntaxRefresher()
  {
  }

  public SyntaxRefresher(SqliteConnection connection)
  {
    _connection = connection ?? throw new ArgumentNullException(nameof(connection));
  }

  private static readonly HashSet<string> DeclarationKinds = new(StringComparer.Ordinal)
  {
    "class",
    "interface",
    "struct",
    "record",
    "recordstruct",
    "enum",
    "method",
    "property",
    "field",
    "event"
  };

  private static readonly HashSet<string> TypeKinds = new(StringComparer.Ordinal)
  {
    "class",
    "interface",
    "struct",
    "record",
    "recordstruct",
    "enum"
  };

  private static readonly HashSet<string> SyntaxOwnedEdgeKinds = new(StringComparer.Ordinal)
  {
    "CONTAINS",
    "REGISTERED_AS",
    "ROUTES_TO"
  };

  private static readonly HashSet<string> SemanticEdgeKinds = new(StringComparer.Ordinal)
  {
    "CALLS",
    "IMPLEMENTS",
    "INHERITS",
    "INJECTS"
  };

  public FileRefreshResult Refresh(FileRefreshRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    cancellationToken.ThrowIfCancellationRequested();

    var repoRoot = Path.GetFullPath(request.RepoRoot);
    var relativePath = NormalizePath(request.RelativePath);
    var syntaxTree = CSharpSyntaxTree.ParseText(request.FileText, path: relativePath);
    var root = syntaxTree.GetCompilationUnitRoot(cancellationToken);
    var hasParseErrors = syntaxTree.GetDiagnostics(cancellationToken).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    var contentHash = ComputeSha256(request.FileText);
    var useOwnedConnection = _connection is null;
    var connection = _connection ?? WaypointDb.Open(WaypointPaths.ForRepo(repoRoot).DbPath);
    try
    {
    var existingDeclarations = LoadExistingDeclarations(connection, relativePath);
    var existingProject = GetExistingFileProject(connection, relativePath);

    if (hasParseErrors)
    {
      UpdateFileNode(connection, relativePath, contentHash, existingProject);
      MarkSemanticEdgesStale(connection, relativePath);
      return new FileRefreshResult(ReadFileNodes(connection, relativePath));
    }

    var declarations = CollectDeclarations(root);
    var resolution = Reconcile(existingDeclarations, declarations);
    var updatedNodes = BuildNodeUpserts(existingDeclarations, resolution, relativePath, contentHash, existingProject);
    var updatedSpans = BuildSpanUpserts(resolution, relativePath);
    var updatedEdges = BuildSyntaxEdges(root, resolution, relativePath, connection);

    using var transaction = connection.BeginTransaction();
    UpsertNodes(connection, transaction, updatedNodes);
    ReplaceFileSpans(connection, transaction, relativePath, updatedSpans);
    ReplaceSyntaxEdges(connection, transaction, relativePath, updatedEdges);
    MarkSemanticEdgesStale(connection, relativePath, transaction);
    DeleteOrphanedFileNodes(connection, transaction, relativePath, existingDeclarations, resolution);
    transaction.Commit();

    return new FileRefreshResult(ReadFileNodes(connection, relativePath));
    }
    finally
    {
      if (useOwnedConnection)
      {
        connection.Dispose();
      }
    }
  }

  private static IReadOnlyList<SyntaxDeclaration> CollectDeclarations(CompilationUnitSyntax root)
  {
    var declarations = new List<SyntaxDeclaration>();
    var nextId = 1;
    WalkMembers(root.Members, namespacePrefix: string.Empty, parentType: null, declarations, ref nextId);
    return declarations;
  }

  private static void WalkMembers(
    SyntaxList<MemberDeclarationSyntax> members,
    string namespacePrefix,
    TypeContext? parentType,
    List<SyntaxDeclaration> declarations,
    ref int nextId)
  {
    foreach (var member in members)
    {
      switch (member)
      {
        case NamespaceDeclarationSyntax namespaceDeclaration:
          WalkMembers(
            namespaceDeclaration.Members,
            CombineNamespace(namespacePrefix, namespaceDeclaration.Name.ToString()),
            parentType,
            declarations,
            ref nextId);
          break;
        case FileScopedNamespaceDeclarationSyntax fileScopedNamespace:
          WalkMembers(
            fileScopedNamespace.Members,
            CombineNamespace(namespacePrefix, fileScopedNamespace.Name.ToString()),
            parentType,
            declarations,
            ref nextId);
          break;
        case BaseTypeDeclarationSyntax typeDeclaration:
          AddTypeDeclaration(typeDeclaration, namespacePrefix, parentType, declarations, ref nextId);
          break;
      }
    }
  }

  private static void AddTypeDeclaration(
    BaseTypeDeclarationSyntax declaration,
    string namespacePrefix,
    TypeContext? parentType,
    List<SyntaxDeclaration> declarations,
    ref int nextId)
  {
    var typeName = declaration.Identifier.ValueText;
    var arity = GetGenericArity(declaration);
    var typeSegment = arity > 0 ? $"{typeName}`{arity}" : typeName;
    var typePath = parentType is null ? typeSegment : $"{parentType.TypePath}.{typeSegment}";
    var fullTypePath = string.IsNullOrWhiteSpace(namespacePrefix) ? typePath : $"{namespacePrefix}.{typePath}";
    var qualifiedName = $"T:{fullTypePath}";
    var kind = GetTypeNodeKind(declaration);
    var span = GetLineSpan(declaration);

    var typeDeclaration = new SyntaxDeclaration(
      nextId++,
      kind,
      typeName,
      qualifiedName,
      parentType?.DeclarationId,
      span.StartLine,
      span.EndLine,
      declaration);
    declarations.Add(typeDeclaration);

    var context = new TypeContext(typeDeclaration.Id, typePath, fullTypePath);
    if (declaration is TypeDeclarationSyntax nestedType)
    {
      foreach (var child in nestedType.Members)
      {
        switch (child)
        {
          case BaseTypeDeclarationSyntax nestedTypeDeclaration:
            AddTypeDeclaration(nestedTypeDeclaration, namespacePrefix, context, declarations, ref nextId);
            break;
          case MethodDeclarationSyntax method:
            AddMethodDeclaration(method, context, declarations, ref nextId);
            break;
          case ConstructorDeclarationSyntax ctor:
            AddConstructorDeclaration(ctor, context, declarations, ref nextId);
            break;
          case PropertyDeclarationSyntax property:
            AddSimpleMemberDeclaration("property", property.Identifier.ValueText, property, context, declarations, ref nextId, "P");
            break;
          case FieldDeclarationSyntax field:
            foreach (var variable in field.Declaration.Variables)
            {
              AddSimpleMemberDeclaration("field", variable.Identifier.ValueText, field, context, declarations, ref nextId, "F");
            }

            break;
          case EventDeclarationSyntax eventDeclaration:
            AddSimpleMemberDeclaration("event", eventDeclaration.Identifier.ValueText, eventDeclaration, context, declarations, ref nextId, "E");
            break;
          case EventFieldDeclarationSyntax eventField:
            foreach (var variable in eventField.Declaration.Variables)
            {
              AddSimpleMemberDeclaration("event", variable.Identifier.ValueText, eventField, context, declarations, ref nextId, "E");
            }

            break;
        }
      }
    }
  }

  private static void AddMethodDeclaration(
    MethodDeclarationSyntax method,
    TypeContext typeContext,
    List<SyntaxDeclaration> declarations,
    ref int nextId)
  {
    var parameters = method.ParameterList.Parameters.Count == 0
      ? string.Empty
      : string.Join(",", method.ParameterList.Parameters.Select(parameter => MapType(parameter.Type)));
    var qualifiedName = $"M:{typeContext.FullTypePath}.{method.Identifier.ValueText}({parameters})";
    var span = GetLineSpan(method);
    declarations.Add(new SyntaxDeclaration(
      nextId++,
      "method",
      method.Identifier.ValueText,
      qualifiedName,
      typeContext.DeclarationId,
      span.StartLine,
      span.EndLine,
      method));
  }

  private static void AddConstructorDeclaration(
    ConstructorDeclarationSyntax ctor,
    TypeContext typeContext,
    List<SyntaxDeclaration> declarations,
    ref int nextId)
  {
    var parameters = ctor.ParameterList.Parameters.Count == 0
      ? string.Empty
      : string.Join(",", ctor.ParameterList.Parameters.Select(parameter => MapType(parameter.Type)));
    var qualifiedName = $"M:{typeContext.FullTypePath}.#ctor({parameters})";
    var span = GetLineSpan(ctor);
    declarations.Add(new SyntaxDeclaration(
      nextId++,
      "method",
      ctor.Identifier.ValueText,
      qualifiedName,
      typeContext.DeclarationId,
      span.StartLine,
      span.EndLine,
      ctor));
  }

  private static void AddSimpleMemberDeclaration(
    string kind,
    string name,
    MemberDeclarationSyntax member,
    TypeContext typeContext,
    List<SyntaxDeclaration> declarations,
    ref int nextId,
    string prefix)
  {
    var qualifiedName = $"{prefix}:{typeContext.FullTypePath}.{name}";
    var span = GetLineSpan(member);
    declarations.Add(new SyntaxDeclaration(
      nextId++,
      kind,
      name,
      qualifiedName,
      typeContext.DeclarationId,
      span.StartLine,
      span.EndLine,
      member));
  }

  private static ReconcileResult Reconcile(IReadOnlyList<ExistingDeclaration> existing, IReadOnlyList<SyntaxDeclaration> declarations)
  {
    var existingByQualifiedName = existing
      .GroupBy(item => item.QualifiedName, StringComparer.Ordinal)
      .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    var existingById = existing.ToDictionary(item => item.NodeId);
    var resolvedByDeclaration = new Dictionary<int, ResolvedDeclaration>();
    var consumedIds = new HashSet<long>();

    foreach (var declaration in declarations.OrderBy(item => item.StartLine).ThenBy(item => item.EndLine))
    {
      ExistingDeclaration? matched = null;
      if (existingByQualifiedName.TryGetValue(declaration.SynthesizedQualifiedName, out var exact))
      {
        matched = exact;
      }

      string? parentQualifiedName = null;
      if (declaration.ParentDeclarationId.HasValue &&
          resolvedByDeclaration.TryGetValue(declaration.ParentDeclarationId.Value, out var parent))
      {
        parentQualifiedName = parent.FinalQualifiedName;
      }

      if (matched is null)
      {
        matched = existing
          .Where(candidate =>
            !consumedIds.Contains(candidate.NodeId) &&
            string.Equals(candidate.Kind, declaration.Kind, StringComparison.Ordinal) &&
            string.Equals(candidate.Name, declaration.Name, StringComparison.Ordinal) &&
            string.Equals(candidate.ContainingTypeQualifiedName, parentQualifiedName, StringComparison.Ordinal))
          .OrderBy(candidate => Math.Abs(candidate.StartLine - declaration.StartLine))
          .FirstOrDefault();
      }

      if (matched is null)
      {
        matched = existing
          .Where(candidate => !consumedIds.Contains(candidate.NodeId))
          .Select(candidate => new { Candidate = candidate, Score = OverlapRatio(candidate, declaration) })
          .Where(item => item.Score >= 0.50)
          .OrderByDescending(item => item.Score)
          .ThenBy(item => Math.Abs(item.Candidate.StartLine - declaration.StartLine))
          .Select(item => item.Candidate)
          .FirstOrDefault();
      }

      var finalQualifiedName = matched?.QualifiedName ?? declaration.SynthesizedQualifiedName;
      var isNewNode = matched is null;
      if (matched is not null)
      {
        consumedIds.Add(matched.NodeId);
      }

      resolvedByDeclaration[declaration.Id] = new ResolvedDeclaration(
        declaration,
        finalQualifiedName,
        matched?.NodeId,
        isNewNode,
        parentQualifiedName);
    }

    var matchedNodeIds = resolvedByDeclaration.Values
      .Where(item => item.MatchedNodeId.HasValue)
      .Select(item => item.MatchedNodeId!.Value)
      .ToHashSet();
    var unmatchedExisting = existingById.Values
      .Where(item => !matchedNodeIds.Contains(item.NodeId))
      .ToArray();

    return new ReconcileResult(resolvedByDeclaration, unmatchedExisting);
  }

  private static IReadOnlyList<NodeUpsert> BuildNodeUpserts(
    IReadOnlyList<ExistingDeclaration> existing,
    ReconcileResult resolution,
    string relativePath,
    string contentHash,
    string? existingProject)
  {
    var nodeByQualifiedName = new Dictionary<string, NodeUpsert>(StringComparer.Ordinal)
    {
      [ToFileQualifiedName(relativePath)] = new NodeUpsert("file", relativePath, ToFileQualifiedName(relativePath), existingProject, "csharp", ContentHash: contentHash)
    };
    var existingByQualifiedName = existing
      .GroupBy(item => item.QualifiedName, StringComparer.Ordinal)
      .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    foreach (var resolved in resolution.Declarations.Values)
    {
      if (nodeByQualifiedName.ContainsKey(resolved.FinalQualifiedName))
      {
        continue;
      }

      string? metaJson = null;
      if (resolved.IsNewNode)
      {
        metaJson = JsonSerializer.Serialize(new Dictionary<string, bool>
        {
          ["syntax_only"] = true
        });
      }
      else if (existingByQualifiedName.TryGetValue(resolved.FinalQualifiedName, out var existingNode))
      {
        metaJson = existingNode.MetaJson;
      }

      nodeByQualifiedName[resolved.FinalQualifiedName] = new NodeUpsert(
        resolved.Declaration.Kind,
        resolved.Declaration.Name,
        resolved.FinalQualifiedName,
        existingProject,
        "csharp",
        metaJson);
    }

    return nodeByQualifiedName.Values.ToArray();
  }

  private static IReadOnlyList<SpanUpsert> BuildSpanUpserts(ReconcileResult resolution, string relativePath)
  {
    return resolution.Declarations.Values
      .Select(item => new SpanUpsert(item.FinalQualifiedName, relativePath, item.Declaration.StartLine, item.Declaration.EndLine))
      .ToArray();
  }

  private static IReadOnlyList<EdgeUpsert> BuildSyntaxEdges(
    CompilationUnitSyntax root,
    ReconcileResult resolution,
    string relativePath,
    SqliteConnection connection)
  {
    var edges = new Dictionary<(string Source, string Destination, string Kind), EdgeUpsert>();
    var fileQualifiedName = ToFileQualifiedName(relativePath);

    foreach (var resolved in resolution.Declarations.Values)
    {
      var container = resolved.ParentQualifiedName ?? fileQualifiedName;
      AddEdge(edges, new EdgeUpsert(container, resolved.FinalQualifiedName, "CONTAINS", OriginFile: relativePath));
    }

    foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
      if (!TryResolveSyntaxRegistration(connection, invocation, out var registration))
      {
        continue;
      }

      AddEdge(edges, new EdgeUpsert(registration.ServiceQualifiedName, registration.ImplementationQualifiedName, "REGISTERED_AS", OriginFile: relativePath));
    }

    foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
    {
      if (!IsController(classDeclaration))
      {
        continue;
      }

      var classRoute = ResolveRouteTemplateFromSyntax(classDeclaration.AttributeLists);
      foreach (var method in classDeclaration.Members.OfType<MethodDeclarationSyntax>())
      {
        var resolvedMethod = resolution.Declarations.Values.FirstOrDefault(item =>
          item.Declaration.SyntaxNode is MethodDeclarationSyntax methodDeclaration &&
          ReferenceEquals(methodDeclaration, method));
        if (resolvedMethod is null)
        {
          continue;
        }

        if (!method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)))
        {
          continue;
        }

        var verbs = ResolveHttpVerbsFromSyntax(method.AttributeLists)
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();
        if (verbs.Length == 0)
        {
          continue;
        }

        var methodRoute = ResolveRouteTemplateFromSyntax(method.AttributeLists);
        foreach (var verb in verbs)
        {
          var route = BuildRoute(classRoute, methodRoute, classDeclaration.Identifier.ValueText, method.Identifier.ValueText);
          var endpointQualifiedName = $"ep:{verb} {route}";
          AddEdge(edges, new EdgeUpsert(endpointQualifiedName, resolvedMethod.FinalQualifiedName, "ROUTES_TO", OriginFile: relativePath));
        }
      }
    }

    return edges.Values.ToArray();
  }

  private static bool TryResolveSyntaxRegistration(SqliteConnection connection, InvocationExpressionSyntax invocation, out RegistrationEdge registration)
  {
    registration = default!;
    var methodName = GetInvokedMethodName(invocation);
    if (string.IsNullOrWhiteSpace(methodName))
    {
      return false;
    }

    var lifetime = ResolveLifetime(methodName);
    if (lifetime is null &&
        !string.Equals(methodName, "AddDbContext", StringComparison.Ordinal) &&
        !string.Equals(methodName, "AddHostedService", StringComparison.Ordinal))
    {
      return false;
    }

    var typeArguments = invocation.Expression switch
    {
      MemberAccessExpressionSyntax memberAccess when memberAccess.Name is GenericNameSyntax generic => generic.TypeArgumentList.Arguments.ToArray(),
      GenericNameSyntax generic => generic.TypeArgumentList.Arguments.ToArray(),
      _ => Array.Empty<TypeSyntax>()
    };

    if (string.Equals(methodName, "AddDbContext", StringComparison.Ordinal) && typeArguments.Length == 1)
    {
      var contextType = ResolveTypeBySyntax(connection, typeArguments[0]);
      registration = new RegistrationEdge(contextType, contextType, "Scoped");
      return true;
    }

    if (string.Equals(methodName, "AddHostedService", StringComparison.Ordinal) && typeArguments.Length == 1)
    {
      var serviceType = ResolveTypeBySyntax(connection, typeArguments[0]);
      registration = new RegistrationEdge(serviceType, serviceType, "Singleton");
      return true;
    }

    if (typeArguments.Length == 2)
    {
      var serviceType = ResolveTypeBySyntax(connection, typeArguments[0]);
      var implType = ResolveTypeBySyntax(connection, typeArguments[1]);
      registration = new RegistrationEdge(serviceType, implType, lifetime ?? "Scoped");
      return true;
    }

    if (typeArguments.Length == 1)
    {
      var type = ResolveTypeBySyntax(connection, typeArguments[0]);
      registration = new RegistrationEdge(type, type, lifetime ?? "Scoped");
      return true;
    }

    return false;
  }

  private static string ResolveTypeBySyntax(SqliteConnection connection, TypeSyntax typeSyntax)
  {
    var simpleName = ExtractSimpleTypeName(typeSyntax);
    if (string.IsNullOrWhiteSpace(simpleName))
    {
      return $"T:{NormalizeTypeText(typeSyntax.ToString())}";
    }

    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT qualified_name
      FROM node
      WHERE name = $name
        AND kind IN ('class','interface','struct','record','recordstruct','enum')
        AND qualified_name LIKE 'T:%'
      ORDER BY qualified_name
      LIMIT 2;
      """;
    command.Parameters.AddWithValue("$name", simpleName);
    using var reader = command.ExecuteReader();
    var candidates = new List<string>();
    while (reader.Read())
    {
      candidates.Add(reader.GetString(0));
    }

    if (candidates.Count == 1)
    {
      return candidates[0];
    }

    return $"T:{NormalizeTypeText(typeSyntax.ToString())}";
  }

  private static string ExtractSimpleTypeName(TypeSyntax typeSyntax) =>
    typeSyntax switch
    {
      IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
      QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
      AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
      GenericNameSyntax generic => generic.Identifier.ValueText,
      NullableTypeSyntax nullable => ExtractSimpleTypeName(nullable.ElementType),
      PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,
      _ => typeSyntax.ToString()
    };

  private static string NormalizeTypeText(string value) =>
    value.Replace(" ", string.Empty, StringComparison.Ordinal);

  private static string? ResolveLifetime(string methodName)
  {
    if (methodName.Contains("Scoped", StringComparison.Ordinal))
    {
      return "Scoped";
    }

    if (methodName.Contains("Singleton", StringComparison.Ordinal))
    {
      return "Singleton";
    }

    if (methodName.Contains("Transient", StringComparison.Ordinal))
    {
      return "Transient";
    }

    return null;
  }

  private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation) =>
    invocation.Expression switch
    {
      MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
      MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.ValueText,
      IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
      GenericNameSyntax generic => generic.Identifier.ValueText,
      _ => null
    };

  private static bool IsController(ClassDeclarationSyntax classDeclaration)
  {
    if (classDeclaration.Identifier.ValueText.EndsWith("Controller", StringComparison.Ordinal))
    {
      return true;
    }

    return classDeclaration.AttributeLists
      .SelectMany(list => list.Attributes)
      .Any(attribute => string.Equals(GetAttributeSimpleName(attribute.Name), "ApiController", StringComparison.Ordinal));
  }

  private static IEnumerable<string> ResolveHttpVerbsFromSyntax(SyntaxList<AttributeListSyntax> attributeLists)
  {
    foreach (var attribute in attributeLists.SelectMany(list => list.Attributes))
    {
      var simpleName = GetAttributeSimpleName(attribute.Name);
      if (simpleName.StartsWith("Http", StringComparison.Ordinal) &&
          simpleName.Length > "Http".Length)
      {
        yield return simpleName["Http".Length..].ToUpperInvariant();
        continue;
      }

      if (!string.Equals(simpleName, "AcceptVerbs", StringComparison.Ordinal))
      {
        continue;
      }

      if (attribute.ArgumentList is null)
      {
        continue;
      }

      foreach (var argument in attribute.ArgumentList.Arguments)
      {
        if (argument.Expression is LiteralExpressionSyntax literal &&
            literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
          var verb = literal.Token.ValueText;
          if (!string.IsNullOrWhiteSpace(verb))
          {
            yield return verb.ToUpperInvariant();
          }
        }
      }
    }
  }

  private static string? ResolveRouteTemplateFromSyntax(SyntaxList<AttributeListSyntax> attributeLists)
  {
    foreach (var attribute in attributeLists.SelectMany(list => list.Attributes))
    {
      var simpleName = GetAttributeSimpleName(attribute.Name);
      if (simpleName is not ("Route" or "HttpGet" or "HttpPost" or "HttpPut" or "HttpDelete" or "HttpPatch"))
      {
        continue;
      }

      if (attribute.ArgumentList?.Arguments.Count > 0 &&
          attribute.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal &&
          literal.IsKind(SyntaxKind.StringLiteralExpression))
      {
        return literal.Token.ValueText;
      }
    }

    return null;
  }

  private static string GetAttributeSimpleName(NameSyntax nameSyntax)
  {
    var simple = nameSyntax switch
    {
      IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
      QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
      AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
      GenericNameSyntax generic => generic.Identifier.ValueText,
      _ => nameSyntax.ToString()
    };

    return simple.EndsWith("Attribute", StringComparison.Ordinal)
      ? simple[..^"Attribute".Length]
      : simple;
  }

  private static string BuildRoute(string? classRoute, string? methodRoute, string className, string actionName)
  {
    string combinedRoute;
    if (!string.IsNullOrWhiteSpace(methodRoute) &&
        (methodRoute.StartsWith("/", StringComparison.Ordinal) || methodRoute.StartsWith("~/", StringComparison.Ordinal)))
    {
      combinedRoute = methodRoute.StartsWith("~/", StringComparison.Ordinal)
        ? methodRoute[1..]
        : methodRoute;
    }
    else
    {
      var classPart = classRoute ?? string.Empty;
      var methodPart = methodRoute ?? string.Empty;
      combinedRoute = string.IsNullOrWhiteSpace(classPart)
        ? methodPart
        : string.IsNullOrWhiteSpace(methodPart) ? classPart : $"{classPart.TrimEnd('/')}/{methodPart.TrimStart('/')}";
    }

    var controllerName = className.EndsWith("Controller", StringComparison.Ordinal)
      ? className[..^"Controller".Length]
      : className;
    combinedRoute = combinedRoute.Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase);
    combinedRoute = combinedRoute.Replace("[action]", actionName, StringComparison.OrdinalIgnoreCase);

    if (string.IsNullOrWhiteSpace(combinedRoute))
    {
      combinedRoute = "/";
    }
    else if (!combinedRoute.StartsWith("/", StringComparison.Ordinal))
    {
      combinedRoute = "/" + combinedRoute;
    }

    return combinedRoute.ToLowerInvariant();
  }

  private static string MapType(TypeSyntax? typeSyntax)
  {
    if (typeSyntax is null)
    {
      return "System.Object";
    }

    return typeSyntax switch
    {
      PredefinedTypeSyntax predefined => MapKeyword(predefined.Keyword.ValueText),
      NullableTypeSyntax nullable => MapType(nullable.ElementType),
      QualifiedNameSyntax qualified => $"{MapTypeNamePart(qualified.Left)}.{MapTypeNamePart(qualified.Right)}",
      AliasQualifiedNameSyntax alias => $"{alias.Alias.Identifier.ValueText}::{MapTypeNamePart(alias.Name)}",
      GenericNameSyntax generic => $"{generic.Identifier.ValueText}<{string.Join(",", generic.TypeArgumentList.Arguments.Select(MapType))}>",
      ArrayTypeSyntax array => $"{MapType(array.ElementType)}[]",
      _ => NormalizeTypeText(typeSyntax.ToString())
    };
  }

  private static string MapTypeNamePart(TypeSyntax syntax) => MapType(syntax);

  private static string MapKeyword(string keyword) =>
    keyword switch
    {
      "bool" => "System.Boolean",
      "byte" => "System.Byte",
      "char" => "System.Char",
      "decimal" => "System.Decimal",
      "double" => "System.Double",
      "float" => "System.Single",
      "int" => "System.Int32",
      "long" => "System.Int64",
      "object" => "System.Object",
      "sbyte" => "System.SByte",
      "short" => "System.Int16",
      "string" => "System.String",
      "uint" => "System.UInt32",
      "ulong" => "System.UInt64",
      "ushort" => "System.UInt16",
      _ => keyword
    };

  private static void AddEdge(IDictionary<(string Source, string Destination, string Kind), EdgeUpsert> map, EdgeUpsert edge) =>
    map[(edge.SourceQualifiedName, edge.DestinationQualifiedName, edge.Kind)] = edge;

  private static void UpsertNodes(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<NodeUpsert> nodes)
  {
    foreach (var node in nodes)
    {
      using var command = connection.CreateCommand();
      command.Transaction = transaction;
      command.CommandText = """
        INSERT INTO node(kind, name, qualified_name, project, lang, meta_json, content_hash, updated_at)
        VALUES($kind, $name, $qualified_name, $project, $lang, $meta_json, $content_hash, $updated_at)
        ON CONFLICT(qualified_name) DO UPDATE SET
          kind = excluded.kind,
          name = excluded.name,
          project = excluded.project,
          lang = excluded.lang,
          meta_json = excluded.meta_json,
          content_hash = excluded.content_hash,
          updated_at = excluded.updated_at;
        """;
      command.Parameters.AddWithValue("$kind", node.Kind);
      command.Parameters.AddWithValue("$name", node.Name);
      command.Parameters.AddWithValue("$qualified_name", node.QualifiedName);
      command.Parameters.AddWithValue("$project", (object?)node.Project ?? DBNull.Value);
      command.Parameters.AddWithValue("$lang", (object?)node.Lang ?? DBNull.Value);
      command.Parameters.AddWithValue("$meta_json", (object?)node.MetaJson ?? DBNull.Value);
      command.Parameters.AddWithValue("$content_hash", (object?)node.ContentHash ?? DBNull.Value);
      command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
      command.ExecuteNonQuery();
    }
  }

  private static void ReplaceFileSpans(SqliteConnection connection, SqliteTransaction transaction, string relativePath, IReadOnlyList<SpanUpsert> spans)
  {
    using (var deleteCommand = connection.CreateCommand())
    {
      deleteCommand.Transaction = transaction;
      deleteCommand.CommandText = "DELETE FROM span WHERE file_path = $file_path;";
      deleteCommand.Parameters.AddWithValue("$file_path", relativePath);
      deleteCommand.ExecuteNonQuery();
    }

    foreach (var span in spans)
    {
      var nodeId = ReadNodeId(connection, transaction, span.NodeQualifiedName);
      using var insertCommand = connection.CreateCommand();
      insertCommand.Transaction = transaction;
      insertCommand.CommandText = """
        INSERT INTO span(node_id, file_path, start_line, end_line)
        VALUES($node_id, $file_path, $start_line, $end_line);
        """;
      insertCommand.Parameters.AddWithValue("$node_id", nodeId);
      insertCommand.Parameters.AddWithValue("$file_path", relativePath);
      insertCommand.Parameters.AddWithValue("$start_line", span.StartLine);
      insertCommand.Parameters.AddWithValue("$end_line", span.EndLine);
      insertCommand.ExecuteNonQuery();
    }
  }

  private static void ReplaceSyntaxEdges(SqliteConnection connection, SqliteTransaction transaction, string relativePath, IReadOnlyList<EdgeUpsert> edges)
  {
    using (var deleteCommand = connection.CreateCommand())
    {
      deleteCommand.Transaction = transaction;
      deleteCommand.CommandText = """
        DELETE FROM edge
        WHERE origin_file = $origin_file
          AND kind IN ('CONTAINS', 'REGISTERED_AS', 'ROUTES_TO');
        """;
      deleteCommand.Parameters.AddWithValue("$origin_file", relativePath);
      deleteCommand.ExecuteNonQuery();
    }

    foreach (var edge in edges)
    {
      var sourceId = ReadNodeId(connection, transaction, edge.SourceQualifiedName);
      var destinationId = ReadNodeId(connection, transaction, edge.DestinationQualifiedName);
      using var command = connection.CreateCommand();
      command.Transaction = transaction;
      command.CommandText = """
        INSERT INTO edge(src, dst, kind, weight, stale, origin_file, created_at)
        VALUES($src, $dst, $kind, $weight, $stale, $origin_file, $created_at)
        ON CONFLICT(src, dst, kind) DO UPDATE SET
          weight = excluded.weight,
          stale = excluded.stale,
          origin_file = excluded.origin_file,
          created_at = excluded.created_at;
        """;
      command.Parameters.AddWithValue("$src", sourceId);
      command.Parameters.AddWithValue("$dst", destinationId);
      command.Parameters.AddWithValue("$kind", edge.Kind);
      command.Parameters.AddWithValue("$weight", edge.Weight);
      command.Parameters.AddWithValue("$stale", edge.Stale ? 1 : 0);
      command.Parameters.AddWithValue("$origin_file", (object?)edge.OriginFile ?? DBNull.Value);
      command.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
      command.ExecuteNonQuery();
    }
  }

  private static long ReadNodeId(SqliteConnection connection, SqliteTransaction transaction, string qualifiedName)
  {
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "SELECT id FROM node WHERE qualified_name = $qualified_name;";
    command.Parameters.AddWithValue("$qualified_name", qualifiedName);
    var value = command.ExecuteScalar();
    if (value is long id)
    {
      return id;
    }

    throw new InvalidOperationException($"Node not found: {qualifiedName}");
  }

  private static void MarkSemanticEdgesStale(SqliteConnection connection, string relativePath, SqliteTransaction? transaction = null)
  {
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
      UPDATE edge
      SET stale = 1
      WHERE origin_file = $origin_file
        AND kind IN ('CALLS', 'IMPLEMENTS', 'INHERITS', 'INJECTS');
      """;
    command.Parameters.AddWithValue("$origin_file", relativePath);
    command.ExecuteNonQuery();
  }

  private static void DeleteOrphanedFileNodes(
    SqliteConnection connection,
    SqliteTransaction transaction,
    string relativePath,
    IReadOnlyList<ExistingDeclaration> existing,
    ReconcileResult resolution)
  {
    var existingIds = existing.Select(item => item.NodeId).Distinct().ToArray();
    if (existingIds.Length == 0)
    {
      return;
    }

    var matchedIds = resolution.Declarations.Values
      .Where(item => item.MatchedNodeId.HasValue)
      .Select(item => item.MatchedNodeId!.Value)
      .ToHashSet();

    foreach (var nodeId in existingIds)
    {
      if (matchedIds.Contains(nodeId))
      {
        continue;
      }

      using var deleteCommand = connection.CreateCommand();
      deleteCommand.Transaction = transaction;
      deleteCommand.CommandText = """
        DELETE FROM node
        WHERE id = $node_id
          AND NOT EXISTS(SELECT 1 FROM span WHERE node_id = $node_id);
        """;
      deleteCommand.Parameters.AddWithValue("$node_id", nodeId);
      deleteCommand.ExecuteNonQuery();
    }
  }

  private static IReadOnlyList<ExistingDeclaration> LoadExistingDeclarations(SqliteConnection connection, string relativePath)
  {
    using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT
        n.id,
        n.kind,
        n.name,
        n.qualified_name,
        n.meta_json,
        s.start_line,
        s.end_line
      FROM node n
      JOIN span s ON s.node_id = n.id
      WHERE s.file_path = $file_path
        AND n.kind IN ('class','interface','struct','record','recordstruct','enum','method','property','field','event')
      ORDER BY n.id, s.start_line;
      """;
    command.Parameters.AddWithValue("$file_path", relativePath);

    var declarations = new List<ExistingDeclaration>();
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
      declarations.Add(new ExistingDeclaration(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        ContainingTypeQualifiedName: null));
    }

    using var parentCommand = connection.CreateCommand();
    parentCommand.CommandText = """
      SELECT child.id, parent.qualified_name
      FROM edge e
      JOIN node parent ON parent.id = e.src
      JOIN node child ON child.id = e.dst
      WHERE e.kind = 'CONTAINS'
        AND e.origin_file = $origin_file
        AND parent.kind IN ('class','interface','struct','record','recordstruct','enum');
      """;
    parentCommand.Parameters.AddWithValue("$origin_file", relativePath);
    using var parentReader = parentCommand.ExecuteReader();
    var parentMap = new Dictionary<long, string>();
    while (parentReader.Read())
    {
      parentMap[parentReader.GetInt64(0)] = parentReader.GetString(1);
    }

    for (var i = 0; i < declarations.Count; i++)
    {
      var item = declarations[i];
      if (parentMap.TryGetValue(item.NodeId, out var parentQualifiedName))
      {
        declarations[i] = item with { ContainingTypeQualifiedName = parentQualifiedName };
      }
    }

    return declarations;
  }

  private static string? GetExistingFileProject(SqliteConnection connection, string relativePath)
  {
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT project FROM node WHERE qualified_name = $qualified_name;";
    command.Parameters.AddWithValue("$qualified_name", ToFileQualifiedName(relativePath));
    var value = command.ExecuteScalar();
    return value as string;
  }

  private static void UpdateFileNode(SqliteConnection connection, string relativePath, string contentHash, string? existingProject)
  {
    using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT INTO node(kind, name, qualified_name, project, lang, content_hash, updated_at)
      VALUES('file', $name, $qualified_name, $project, 'csharp', $content_hash, $updated_at)
      ON CONFLICT(qualified_name) DO UPDATE SET
        content_hash = excluded.content_hash,
        updated_at = excluded.updated_at;
      """;
    command.Parameters.AddWithValue("$name", relativePath);
    command.Parameters.AddWithValue("$qualified_name", ToFileQualifiedName(relativePath));
    command.Parameters.AddWithValue("$project", (object?)existingProject ?? DBNull.Value);
    command.Parameters.AddWithValue("$content_hash", contentHash);
    command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
    command.ExecuteNonQuery();
  }

  private static IReadOnlyList<RefreshedNodeSpan> ReadFileNodes(SqliteConnection connection, string relativePath)
  {
    var spanIndex = new SpanIndex(connection);
    return spanIndex.NodesInRange(relativePath, 1, int.MaxValue)
      .Select(match => new RefreshedNodeSpan(
        match.NodeId,
        match.Kind,
        match.Name,
        match.QualifiedName,
        match.FilePath,
        match.StartLine,
        match.EndLine))
      .ToArray();
  }

  private static double OverlapRatio(ExistingDeclaration existing, SyntaxDeclaration declaration)
  {
    var overlapStart = Math.Max(existing.StartLine, declaration.StartLine);
    var overlapEnd = Math.Min(existing.EndLine, declaration.EndLine);
    if (overlapEnd < overlapStart)
    {
      return 0.0;
    }

    var overlap = overlapEnd - overlapStart + 1;
    var existingLength = existing.EndLine - existing.StartLine + 1;
    if (existingLength <= 0)
    {
      return 0.0;
    }

    return (double)overlap / existingLength;
  }

  private static string ComputeSha256(string text)
  {
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
    return Convert.ToHexString(hash).ToLowerInvariant();
  }

  private static string CombineNamespace(string left, string right)
  {
    if (string.IsNullOrWhiteSpace(left))
    {
      return right;
    }

    return string.IsNullOrWhiteSpace(right) ? left : $"{left}.{right}";
  }

  private static int GetGenericArity(BaseTypeDeclarationSyntax declaration) =>
    declaration switch
    {
      TypeDeclarationSyntax type when type.TypeParameterList is not null => type.TypeParameterList.Parameters.Count,
      _ => 0
    };

  private static string GetTypeNodeKind(BaseTypeDeclarationSyntax declaration) =>
    declaration switch
    {
      EnumDeclarationSyntax => "enum",
      InterfaceDeclarationSyntax => "interface",
      RecordDeclarationSyntax recordDeclaration => recordDeclaration.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "recordstruct" : "record",
      StructDeclarationSyntax => "struct",
      _ => "class"
    };

  private static (int StartLine, int EndLine) GetLineSpan(SyntaxNode node)
  {
    var lineSpan = node.SyntaxTree.GetLineSpan(node.Span);
    return (lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1);
  }

  private static string NormalizePath(string path) => path.Replace('\\', '/');

  private static string ToFileQualifiedName(string relativePath) => $"file:{relativePath}";

  private sealed record TypeContext(int DeclarationId, string TypePath, string FullTypePath);

  private sealed record SyntaxDeclaration(
    int Id,
    string Kind,
    string Name,
    string SynthesizedQualifiedName,
    int? ParentDeclarationId,
    int StartLine,
    int EndLine,
    SyntaxNode SyntaxNode);

  private sealed record ExistingDeclaration(
    long NodeId,
    string Kind,
    string Name,
    string QualifiedName,
    string? MetaJson,
    int StartLine,
    int EndLine,
    string? ContainingTypeQualifiedName);

  private sealed record ResolvedDeclaration(
    SyntaxDeclaration Declaration,
    string FinalQualifiedName,
    long? MatchedNodeId,
    bool IsNewNode,
    string? ParentQualifiedName);

  private sealed record ReconcileResult(
    IReadOnlyDictionary<int, ResolvedDeclaration> Declarations,
    IReadOnlyList<ExistingDeclaration> UnmatchedExisting);

  private sealed record RegistrationEdge(string ServiceQualifiedName, string ImplementationQualifiedName, string Lifetime);
}
