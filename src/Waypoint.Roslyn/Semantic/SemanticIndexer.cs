using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Waypoint.Core.Indexing;
using Waypoint.Core.Paths;
using Waypoint.Core.Storage;
using IndexWorkspaceDiagnostic = Waypoint.Core.Indexing.WorkspaceDiagnostic;

namespace Waypoint.Roslyn.Semantic;

public sealed class SemanticIndexer : ICodeIndexer
{
  private static readonly object MsBuildGate = new();
  private static bool _msBuildRegistered;

  public async Task<IndexReport> IndexAsync(IndexOptions options, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode is not IndexMode.MsBuild)
    {
      throw new NotSupportedException($"Index mode '{options.Mode}' is not implemented yet.");
    }

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var repoRoot = Path.GetFullPath(options.RepoRoot);
    var solutionPath = ResolveSolutionPath(repoRoot, options.SolutionPath);
    var diagnostics = new List<IndexWorkspaceDiagnostic>();

    var paths = WaypointPaths.ForRepo(repoRoot);
    using var connection = WaypointDb.Open(paths.DbPath);
    var graphStore = new GraphStore(connection);

    var solutionFileName = Path.GetFileName(solutionPath);
    var solutionQualifiedName = $"sln:{solutionFileName}";
    graphStore.UpsertNode(new NodeUpsert("solution", solutionFileName, solutionQualifiedName, Project: solutionFileName, Lang: "csharp"));

    var openResult = await OpenSolutionAsync(solutionPath, repoRoot, diagnostics, cancellationToken);
    using var workspace = openResult.Workspace;
    var solution = openResult.Solution;
    var rootProjects = openResult.RootProjectIds
      .Select(projectId => solution.GetProject(projectId))
      .Where(project => project is not null)
      .Cast<Project>();
    var filteredProjects = FilterProjects(rootProjects, options.Projects);
    var projectsById = solution.Projects.ToDictionary(project => project.Id);
    var filesSeen = 0;
    var filesSkipped = 0;
    var changedFiles = new List<PendingFile>();

    foreach (var project in filteredProjects)
    {
      var projectQualifiedName = ToProjectQualifiedName(project.Name);
      graphStore.UpsertNode(new NodeUpsert("project", project.Name, projectQualifiedName, Project: project.Name, Lang: "csharp"));
      graphStore.UpsertEdge(new EdgeUpsert(solutionQualifiedName, projectQualifiedName, "CONTAINS"));
    }

    foreach (var project in filteredProjects)
    {
      var projectQualifiedName = ToProjectQualifiedName(project.Name);
      foreach (var reference in project.ProjectReferences)
      {
        if (!projectsById.TryGetValue(reference.ProjectId, out var targetProject))
        {
          continue;
        }

        graphStore.UpsertNode(new NodeUpsert("project", targetProject.Name, ToProjectQualifiedName(targetProject.Name), Project: targetProject.Name, Lang: "csharp"));
        graphStore.UpsertEdge(new EdgeUpsert(projectQualifiedName, ToProjectQualifiedName(targetProject.Name), "REFERENCES"));
      }

      var compilation = await project.GetCompilationAsync(cancellationToken);
      if (compilation is null)
      {
        diagnostics.Add(new IndexWorkspaceDiagnostic("project", $"Compilation failed for '{project.Name}'."));
        graphStore.UpsertNode(new NodeUpsert(
          "project",
          project.Name,
          projectQualifiedName,
          Project: project.Name,
          Lang: "csharp",
          MetaJson: JsonSerializer.Serialize(new Dictionary<string, string> { ["load_error"] = "Compilation unavailable" })));
        continue;
      }

      foreach (var document in project.Documents)
      {
        if (document.SourceCodeKind != SourceCodeKind.Regular || string.IsNullOrWhiteSpace(document.FilePath))
        {
          continue;
        }

        if (!document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
          continue;
        }

        if (!IsPathInRepo(repoRoot, document.FilePath))
        {
          continue;
        }

        filesSeen++;
        var relativePath = ToRepoRelativePath(repoRoot, document.FilePath);
        var text = await document.GetTextAsync(cancellationToken);
        var contentHash = ComputeSha256(text.ToString());
        var fileQualifiedName = ToFileQualifiedName(relativePath);
        var previousHash = GetNodeContentHash(connection, fileQualifiedName);
        if (!options.Force && string.Equals(contentHash, previousHash, StringComparison.Ordinal))
        {
          filesSkipped++;
          continue;
        }

        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (syntaxRoot is not CompilationUnitSyntax root || semanticModel is null)
        {
          diagnostics.Add(new IndexWorkspaceDiagnostic("document", $"Skipped '{relativePath}' because syntax/semantic analysis was unavailable."));
          continue;
        }

        var fileFacts = ExtractFacts(
          repoRoot,
          project.Name,
          relativePath,
          contentHash,
          root,
          semanticModel);
        changedFiles.Add(new PendingFile(relativePath, fileFacts));
      }
    }

    foreach (var changed in changedFiles)
    {
      foreach (var node in changed.Facts.Nodes)
      {
        graphStore.UpsertNode(node);
      }
    }

    foreach (var changed in changedFiles)
    {
      graphStore.ReplaceFile(changed.RelativePath, changed.Facts.Nodes, changed.Facts.Spans, changed.Facts.Edges);
    }

    SetMeta(connection, "last_index_at", DateTimeOffset.UtcNow.ToString("O"));
    SetMeta(connection, "index_mode", options.Mode.ToString().ToLowerInvariant());

    var nodeCount = CountNodes(connection);
    var edgesByKind = CountEdgesByKind(connection);
    stopwatch.Stop();

    return new IndexReport(
      options.Mode,
      filteredProjects.Count,
      filesSeen,
      filesSkipped,
      nodeCount,
      edgesByKind,
      diagnostics,
      stopwatch.Elapsed);
  }

  private static List<Project> FilterProjects(IEnumerable<Project> projects, IReadOnlyList<string> filters)
  {
    if (filters.Count == 0)
    {
      return projects.ToList();
    }

    var include = filters.ToHashSet(StringComparer.OrdinalIgnoreCase);
    return projects.Where(project => include.Contains(project.Name)).ToList();
  }

  private static FileFacts ExtractFacts(
    string repoRoot,
    string projectName,
    string relativePath,
    string contentHash,
    CompilationUnitSyntax root,
    SemanticModel semanticModel)
  {
    var facts = new FileFacts(relativePath);
    var fileQualifiedName = ToFileQualifiedName(relativePath);
    facts.AddNode(new NodeUpsert("file", relativePath, fileQualifiedName, projectName, "csharp", ContentHash: contentHash));
    facts.AddEdge(new EdgeUpsert(ToProjectQualifiedName(projectName), fileQualifiedName, "CONTAINS", OriginFile: relativePath));

    AddTypeDeclarations(repoRoot, projectName, relativePath, semanticModel, root, facts);
    AddMemberDeclarations(repoRoot, relativePath, semanticModel, root, facts);
    AddDiRegistrations(relativePath, semanticModel, root, facts);
    AddControllerEndpoints(repoRoot, projectName, relativePath, semanticModel, root, facts);

    return facts;
  }

  private static void AddTypeDeclarations(
    string repoRoot,
    string projectName,
    string relativePath,
    SemanticModel semanticModel,
    CompilationUnitSyntax root,
    FileFacts facts)
  {
    foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
    {
      if (semanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol)
      {
        continue;
      }

      if (!TryGetDocumentationId(symbol, out var qualifiedName))
      {
        continue;
      }

      var nodeKind = GetTypeNodeKind(symbol, declaration);
      facts.AddNode(new NodeUpsert(nodeKind, symbol.Name, qualifiedName, projectName, "csharp"));
      AddDeclaringSpansForFile(symbol, relativePath, facts);

      var containerQualifiedName = symbol.ContainingType is not null && TryGetDocumentationId(symbol.ContainingType, out var typeContainer)
        ? typeContainer
        : ToFileQualifiedName(relativePath);
      facts.AddEdge(new EdgeUpsert(containerQualifiedName, qualifiedName, "CONTAINS", OriginFile: relativePath));

      if (symbol.BaseType is not null &&
          symbol.BaseType.SpecialType != SpecialType.System_Object &&
          IsSymbolInRepo(repoRoot, symbol.BaseType) &&
          TryGetDocumentationId(symbol.BaseType, out var baseTypeQualifiedName))
      {
        facts.AddEdge(new EdgeUpsert(qualifiedName, baseTypeQualifiedName, "INHERITS", OriginFile: relativePath));
      }

      foreach (var implementedInterface in symbol.Interfaces)
      {
        if (!IsSymbolInRepo(repoRoot, implementedInterface))
        {
          continue;
        }

        if (!TryGetDocumentationId(implementedInterface, out var implementedInterfaceQualifiedName))
        {
          continue;
        }

        facts.AddEdge(new EdgeUpsert(qualifiedName, implementedInterfaceQualifiedName, "IMPLEMENTS", OriginFile: relativePath));
      }

      foreach (var constructor in symbol.Constructors.Where(constructor => !constructor.IsStatic))
      {
        foreach (var parameter in constructor.Parameters)
        {
          if (parameter.Type is not INamedTypeSymbol namedType || !IsSymbolInRepo(repoRoot, namedType))
          {
            continue;
          }

          var targetType = namedType.IsGenericType ? namedType.ConstructedFrom : namedType;
          if (!TryGetDocumentationId(targetType, out var dependencyQualifiedName))
          {
            continue;
          }

          facts.AddEdge(new EdgeUpsert(qualifiedName, dependencyQualifiedName, "INJECTS", OriginFile: relativePath));
        }
      }
    }
  }

  private static void AddMemberDeclarations(
    string repoRoot,
    string relativePath,
    SemanticModel semanticModel,
    CompilationUnitSyntax root,
    FileFacts facts)
  {
    foreach (var declaration in root.DescendantNodes())
    {
      switch (declaration)
      {
        case MethodDeclarationSyntax methodDeclaration:
          AddMemberSymbol(repoRoot, relativePath, semanticModel.GetDeclaredSymbol(methodDeclaration), "method", facts);
          break;
        case ConstructorDeclarationSyntax constructorDeclaration:
          AddMemberSymbol(repoRoot, relativePath, semanticModel.GetDeclaredSymbol(constructorDeclaration), "method", facts);
          break;
        case PropertyDeclarationSyntax propertyDeclaration:
          AddMemberSymbol(repoRoot, relativePath, semanticModel.GetDeclaredSymbol(propertyDeclaration), "property", facts);
          break;
        case FieldDeclarationSyntax fieldDeclaration:
          foreach (var variable in fieldDeclaration.Declaration.Variables)
          {
            AddMemberSymbol(repoRoot, relativePath, semanticModel.GetDeclaredSymbol(variable), "field", facts);
          }

          break;
        case EventDeclarationSyntax eventDeclaration:
          AddMemberSymbol(repoRoot, relativePath, semanticModel.GetDeclaredSymbol(eventDeclaration), "event", facts);
          break;
        case EventFieldDeclarationSyntax eventFieldDeclaration:
          foreach (var variable in eventFieldDeclaration.Declaration.Variables)
          {
            AddMemberSymbol(repoRoot, relativePath, semanticModel.GetDeclaredSymbol(variable), "event", facts);
          }

          break;
      }
    }
  }

  private static void AddMemberSymbol(string repoRoot, string relativePath, ISymbol? symbol, string nodeKind, FileFacts facts)
  {
    if (symbol is null || symbol.ContainingType is null)
    {
      return;
    }

    if (!TryGetDocumentationId(symbol, out var qualifiedName))
    {
      return;
    }

    facts.AddNode(new NodeUpsert(nodeKind, symbol.Name, qualifiedName, symbol.ContainingAssembly?.Name, "csharp"));
    AddDeclaringSpansForFile(symbol, relativePath, facts);

    if (TryGetDocumentationId(symbol.ContainingType, out var containingTypeQualifiedName))
    {
      facts.AddEdge(new EdgeUpsert(containingTypeQualifiedName, qualifiedName, "CONTAINS", OriginFile: relativePath));
    }

    AddMemberImplementationEdges(repoRoot, symbol, relativePath, facts, qualifiedName);
    AddOverrideEdges(repoRoot, symbol, relativePath, facts, qualifiedName);
  }

  private static void AddMemberImplementationEdges(string repoRoot, ISymbol symbol, string relativePath, FileFacts facts, string sourceQualifiedName)
  {
    if (symbol.ContainingType is null)
    {
      return;
    }

    foreach (var interfaceSymbol in symbol.ContainingType.AllInterfaces)
    {
      foreach (var interfaceMember in interfaceSymbol.GetMembers())
      {
        var implementation = symbol.ContainingType.FindImplementationForInterfaceMember(interfaceMember);
        if (!SymbolEqualityComparer.Default.Equals(implementation?.OriginalDefinition, symbol.OriginalDefinition))
        {
          continue;
        }

        if (!IsSymbolInRepo(repoRoot, interfaceMember))
        {
          continue;
        }

        if (!TryGetDocumentationId(interfaceMember, out var destinationQualifiedName))
        {
          continue;
        }

        facts.AddEdge(new EdgeUpsert(sourceQualifiedName, destinationQualifiedName, "IMPLEMENTS", OriginFile: relativePath));
      }
    }
  }

  private static void AddOverrideEdges(string repoRoot, ISymbol symbol, string relativePath, FileFacts facts, string sourceQualifiedName)
  {
    ISymbol? overridden = symbol switch
    {
      IMethodSymbol method => method.OverriddenMethod,
      IPropertySymbol property => property.OverriddenProperty,
      IEventSymbol eventSymbol => eventSymbol.OverriddenEvent,
      _ => null
    };

    if (overridden is not null && !IsSymbolInRepo(repoRoot, overridden))
    {
      return;
    }

    if (overridden is null || !TryGetDocumentationId(overridden, out var destinationQualifiedName))
    {
      return;
    }

    facts.AddEdge(new EdgeUpsert(sourceQualifiedName, destinationQualifiedName, "OVERRIDES", OriginFile: relativePath));
  }

  private static void AddDiRegistrations(
    string relativePath,
    SemanticModel semanticModel,
    CompilationUnitSyntax root,
    FileFacts facts)
  {
    foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
      var symbolInfo = semanticModel.GetSymbolInfo(invocation);
      var targetMethod = symbolInfo.Symbol as IMethodSymbol
        ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
      if (!TryResolveRegistration(targetMethod, invocation, semanticModel, out var serviceType, out var implementationType, out var lifetime))
      {
        continue;
      }

      var serviceQualifiedName = GetTypeQualifiedName(serviceType);
      var implementationQualifiedName = GetTypeQualifiedName(implementationType);

      facts.AddEdge(new EdgeUpsert(serviceQualifiedName, implementationQualifiedName, "REGISTERED_AS", OriginFile: relativePath));
      facts.AddNode(new NodeUpsert(
        serviceType.TypeKind == TypeKind.Interface ? "interface" : "class",
        serviceType.Name,
        serviceQualifiedName,
        serviceType.ContainingAssembly?.Name,
        "csharp"));
      facts.AddNode(new NodeUpsert(
        implementationType.TypeKind == TypeKind.Interface ? "interface" : "class",
        implementationType.Name,
        implementationQualifiedName,
        implementationType.ContainingAssembly?.Name,
        "csharp"));

      // Persist at least one lifetime hint for query/debug output even though edge metadata is not in schema v1.
      var registrationLocation = $"{relativePath}:{GetLineNumber(invocation.GetLocation())}";
      var meta = JsonSerializer.Serialize(new Dictionary<string, string>
      {
        ["lifetime"] = lifetime,
        ["registered_in"] = registrationLocation
      });
      facts.AddNode(new NodeUpsert(serviceType.TypeKind == TypeKind.Interface ? "interface" : "class", serviceType.Name, serviceQualifiedName, MetaJson: meta));
    }
  }

  private static bool TryResolveRegistration(
    IMethodSymbol? method,
    InvocationExpressionSyntax invocation,
    SemanticModel semanticModel,
    out INamedTypeSymbol serviceType,
    out INamedTypeSymbol implementationType,
    out string lifetime)
  {
    serviceType = null!;
    implementationType = null!;
    lifetime = string.Empty;

    var methodName = method?.Name ?? GetInvokedMethodName(invocation);
    if (string.IsNullOrWhiteSpace(methodName))
    {
      return false;
    }

    var typeArguments = ResolveTypeArguments(method, invocation, semanticModel);
    if (!TryResolveLifetime(methodName, out lifetime))
    {
      if (!string.Equals(methodName, "AddDbContext", StringComparison.Ordinal) &&
          !string.Equals(methodName, "AddHostedService", StringComparison.Ordinal))
      {
        return false;
      }
    }

    if (string.Equals(methodName, "AddDbContext", StringComparison.Ordinal))
    {
      if (typeArguments.Count == 1)
      {
        lifetime = "Scoped";
        serviceType = typeArguments[0];
        implementationType = typeArguments[0];
        return true;
      }

      return false;
    }

    if (string.Equals(methodName, "AddHostedService", StringComparison.Ordinal))
    {
      if (typeArguments.Count == 1)
      {
        lifetime = "Singleton";
        serviceType = typeArguments[0];
        implementationType = typeArguments[0];
        return true;
      }

      return false;
    }

    if (typeArguments.Count == 2)
    {
      serviceType = typeArguments[0];
      implementationType = typeArguments[1];
      return true;
    }

    if (TryResolveTypeOfArguments(invocation.ArgumentList.Arguments, semanticModel, out var serviceFromTypeOf, out var implementationFromTypeOf))
    {
      serviceType = serviceFromTypeOf;
      implementationType = implementationFromTypeOf;
      return true;
    }

    if (typeArguments.Count == 1 &&
        TryResolveImplementationFromFactory(invocation.ArgumentList.Arguments, semanticModel, out var lambdaImplementationType))
    {
      serviceType = typeArguments[0];
      implementationType = lambdaImplementationType;
      return true;
    }

    if (typeArguments.Count == 1)
    {
      serviceType = typeArguments[0];
      implementationType = typeArguments[0];
      return true;
    }

    return false;
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

  private static IReadOnlyList<INamedTypeSymbol> ResolveTypeArguments(
    IMethodSymbol? method,
    InvocationExpressionSyntax invocation,
    SemanticModel semanticModel)
  {
    var result = new List<INamedTypeSymbol>();
    if (method is not null)
    {
      foreach (var typeArgument in method.TypeArguments.OfType<INamedTypeSymbol>())
      {
        result.Add(typeArgument);
      }
    }

    if (result.Count > 0)
    {
      return result;
    }

    GenericNameSyntax? genericName = invocation.Expression switch
    {
      MemberAccessExpressionSyntax memberAccess => memberAccess.Name as GenericNameSyntax,
      GenericNameSyntax generic => generic,
      _ => null
    };

    if (genericName is null)
    {
      return result;
    }

    foreach (var typeSyntax in genericName.TypeArgumentList.Arguments)
    {
      if (semanticModel.GetTypeInfo(typeSyntax).Type is INamedTypeSymbol namedType)
      {
        result.Add(namedType);
      }
    }

    return result;
  }

  private static bool TryResolveTypeOfArguments(
    SeparatedSyntaxList<ArgumentSyntax> arguments,
    SemanticModel semanticModel,
    out INamedTypeSymbol serviceType,
    out INamedTypeSymbol implementationType)
  {
    serviceType = null!;
    implementationType = null!;

    if (arguments.Count < 2 ||
        arguments[0].Expression is not TypeOfExpressionSyntax serviceTypeOf ||
        arguments[1].Expression is not TypeOfExpressionSyntax implementationTypeOf)
    {
      return false;
    }

    var serviceInfo = semanticModel.GetTypeInfo(serviceTypeOf.Type).Type as INamedTypeSymbol;
    var implementationInfo = semanticModel.GetTypeInfo(implementationTypeOf.Type).Type as INamedTypeSymbol;
    if (serviceInfo is null || implementationInfo is null)
    {
      return false;
    }

    serviceType = serviceInfo;
    implementationType = implementationInfo;
    return true;
  }

  private static bool TryResolveImplementationFromFactory(
    SeparatedSyntaxList<ArgumentSyntax> arguments,
    SemanticModel semanticModel,
    out INamedTypeSymbol implementationType)
  {
    implementationType = null!;
    if (arguments.Count == 0)
    {
      return false;
    }

    LambdaExpressionSyntax? lambdaExpression = arguments[0].Expression switch
    {
      ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized,
      SimpleLambdaExpressionSyntax simple => simple,
      _ => null
    };

    if (lambdaExpression is null)
    {
      return false;
    }

    var body = lambdaExpression switch
    {
      ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.Body,
      SimpleLambdaExpressionSyntax simple => simple.Body,
      _ => null
    };

    ObjectCreationExpressionSyntax? objectCreation = body switch
    {
      ObjectCreationExpressionSyntax expression => expression,
      BlockSyntax block => block.Statements
        .OfType<ReturnStatementSyntax>()
        .Select(statement => statement.Expression)
        .OfType<ObjectCreationExpressionSyntax>()
        .FirstOrDefault(),
      _ => null
    };

    if (objectCreation is null)
    {
      return false;
    }

    implementationType = semanticModel.GetTypeInfo(objectCreation).Type as INamedTypeSymbol ?? null!;
    return implementationType is not null;
  }

  private static bool TryResolveLifetime(string methodName, out string lifetime)
  {
    if (methodName.Contains("Scoped", StringComparison.Ordinal))
    {
      lifetime = "Scoped";
      return true;
    }

    if (methodName.Contains("Singleton", StringComparison.Ordinal))
    {
      lifetime = "Singleton";
      return true;
    }

    if (methodName.Contains("Transient", StringComparison.Ordinal))
    {
      lifetime = "Transient";
      return true;
    }

    lifetime = string.Empty;
    return false;
  }

  private static void AddControllerEndpoints(
    string repoRoot,
    string projectName,
    string relativePath,
    SemanticModel semanticModel,
    CompilationUnitSyntax root,
    FileFacts facts)
  {
    foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
    {
      if (semanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol classSymbol)
      {
        continue;
      }

      if (!IsController(classSymbol))
      {
        continue;
      }

      var classRoute = ResolveRouteTemplate(classSymbol.GetAttributes())
        ?? ResolveRouteTemplateFromSyntax(declaration.AttributeLists);
      foreach (var methodDeclaration in declaration.Members.OfType<MethodDeclarationSyntax>())
      {
        if (semanticModel.GetDeclaredSymbol(methodDeclaration) is not IMethodSymbol methodSymbol)
        {
          continue;
        }

        if (methodSymbol.DeclaredAccessibility != Accessibility.Public || methodSymbol.IsAbstract)
        {
          continue;
        }

        if (!TryGetDocumentationId(methodSymbol, out var methodQualifiedName))
        {
          continue;
        }

        var verbs = ResolveHttpVerbs(methodSymbol.GetAttributes())
          .Concat(ResolveHttpVerbsFromSyntax(methodDeclaration.AttributeLists))
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();
        if (verbs.Length == 0)
        {
          continue;
        }

        var methodRoute = ResolveRouteTemplate(methodSymbol.GetAttributes())
          ?? ResolveRouteTemplateFromSyntax(methodDeclaration.AttributeLists);
        foreach (var verb in verbs)
        {
          var route = BuildRoute(classRoute, methodRoute, classSymbol.Name, methodSymbol.Name);
          var endpointQualifiedName = $"ep:{verb} {route}";
          var endpointName = $"{verb} {route}";

          facts.AddNode(new NodeUpsert(
            "endpoint",
            endpointName,
            endpointQualifiedName,
            projectName,
            "csharp",
            MetaJson: JsonSerializer.Serialize(new Dictionary<string, string> { ["verb"] = verb, ["route"] = route })));
          facts.AddSpan(new SpanUpsert(endpointQualifiedName, relativePath, GetLineNumber(methodDeclaration.GetLocation()), GetEndLineNumber(methodDeclaration.GetLocation())));
          facts.AddEdge(new EdgeUpsert(endpointQualifiedName, methodQualifiedName, "ROUTES_TO", OriginFile: relativePath));
        }
      }
    }
  }

  private static bool IsController(INamedTypeSymbol typeSymbol)
  {
    if (typeSymbol.TypeKind != TypeKind.Class)
    {
      return false;
    }

    if (DerivesFrom(typeSymbol, "ControllerBase"))
    {
      return true;
    }

    if (typeSymbol.GetAttributes().Any(attribute => attribute.AttributeClass?.Name is "ApiControllerAttribute" or "ApiController"))
    {
      return true;
    }

    if (!typeSymbol.Name.EndsWith("Controller", StringComparison.Ordinal))
    {
      return false;
    }

    return typeSymbol.GetMembers()
      .OfType<IMethodSymbol>()
      .Any(member => member.DeclaredAccessibility == Accessibility.Public && !member.IsAbstract && member.MethodKind == MethodKind.Ordinary);
  }

  private static bool DerivesFrom(INamedTypeSymbol typeSymbol, string baseTypeName)
  {
    for (var current = typeSymbol.BaseType; current is not null; current = current.BaseType)
    {
      if (string.Equals(current.Name, baseTypeName, StringComparison.Ordinal))
      {
        return true;
      }
    }

    return false;
  }

  private static IEnumerable<string> ResolveHttpVerbs(ImmutableArray<AttributeData> attributes)
  {
    foreach (var attribute in attributes)
    {
      var attributeName = attribute.AttributeClass?.Name;
      if (attributeName is null)
      {
        continue;
      }

      if (attributeName.StartsWith("Http", StringComparison.Ordinal) && attributeName.EndsWith("Attribute", StringComparison.Ordinal))
      {
        var verb = attributeName["Http".Length..^"Attribute".Length].ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(verb))
        {
          yield return verb;
        }
      }

      if (attributeName is "AcceptVerbsAttribute" or "AcceptVerbs")
      {
        foreach (var argument in attribute.ConstructorArguments)
        {
          if (argument.Kind == TypedConstantKind.Array)
          {
            foreach (var value in argument.Values)
            {
              if (value.Value is string verbString && !string.IsNullOrWhiteSpace(verbString))
              {
                yield return verbString.ToUpperInvariant();
              }
            }
          }
          else if (argument.Value is string singleVerb && !string.IsNullOrWhiteSpace(singleVerb))
          {
            yield return singleVerb.ToUpperInvariant();
          }
        }
      }
    }
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
            literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression))
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

  private static string? ResolveRouteTemplate(ImmutableArray<AttributeData> attributes)
  {
    foreach (var attribute in attributes)
    {
      if (attribute.AttributeClass?.Name is not ("RouteAttribute" or "Route" or "HttpGetAttribute" or "HttpPostAttribute" or "HttpPutAttribute" or "HttpDeleteAttribute" or "HttpPatchAttribute"))
      {
        continue;
      }

      if (attribute.ConstructorArguments.Length > 0 &&
          attribute.ConstructorArguments[0].Value is string template &&
          !string.IsNullOrWhiteSpace(template))
      {
        return template;
      }
    }

    return null;
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
          literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression))
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
    var effectiveMethodRoute = methodRoute;
    var effectiveClassRoute = classRoute;
    string combinedRoute;
    if (!string.IsNullOrWhiteSpace(effectiveMethodRoute) &&
        (effectiveMethodRoute.StartsWith("/", StringComparison.Ordinal) || effectiveMethodRoute.StartsWith("~/", StringComparison.Ordinal)))
    {
      combinedRoute = effectiveMethodRoute.StartsWith("~/", StringComparison.Ordinal)
        ? effectiveMethodRoute[1..]
        : effectiveMethodRoute;
    }
    else
    {
      var classPart = effectiveClassRoute ?? string.Empty;
      var methodPart = effectiveMethodRoute ?? string.Empty;
      combinedRoute = string.IsNullOrWhiteSpace(classPart)
        ? methodPart
        : string.IsNullOrWhiteSpace(methodPart) ? classPart : $"{classPart.TrimEnd('/')}/{methodPart.TrimStart('/')}";
    }

    var controllerName = className.EndsWith("Controller", StringComparison.Ordinal)
      ? className[..^"Controller".Length]
      : className;
    combinedRoute = combinedRoute.Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase);
    combinedRoute = combinedRoute.Replace("[action]", actionName, StringComparison.OrdinalIgnoreCase);
    combinedRoute = Regex.Replace(combinedRoute, @"\{([^}:]+):[^}]+\}", "{$1}");

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

  private static void AddDeclaringSpansForFile(ISymbol symbol, string relativePath, FileFacts facts)
  {
    if (!TryGetDocumentationId(symbol, out var qualifiedName))
    {
      return;
    }

    foreach (var reference in symbol.DeclaringSyntaxReferences)
    {
      var path = reference.SyntaxTree.FilePath;
      if (string.IsNullOrWhiteSpace(path))
      {
        continue;
      }

      var referencePath = path.Replace('\\', '/');
      if (!referencePath.EndsWith(relativePath, StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      var lineSpan = reference.SyntaxTree.GetLineSpan(reference.Span);
      facts.AddSpan(new SpanUpsert(
        qualifiedName,
        relativePath,
        lineSpan.StartLinePosition.Line + 1,
        lineSpan.EndLinePosition.Line + 1));
    }
  }

  private static bool TryGetDocumentationId(ISymbol symbol, out string documentationId)
  {
    documentationId = symbol.GetDocumentationCommentId() ?? string.Empty;
    return !string.IsNullOrWhiteSpace(documentationId);
  }

  private static string GetTypeQualifiedName(INamedTypeSymbol type)
  {
    var documentationId = type.GetDocumentationCommentId();
    if (!string.IsNullOrWhiteSpace(documentationId))
    {
      return documentationId;
    }

    var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
      .Replace("global::", string.Empty, StringComparison.Ordinal)
      .Replace(" ", string.Empty, StringComparison.Ordinal);
    return $"T:{fullName}";
  }

  private static string GetTypeNodeKind(INamedTypeSymbol symbol, BaseTypeDeclarationSyntax declaration) =>
    declaration switch
    {
      EnumDeclarationSyntax => "enum",
      InterfaceDeclarationSyntax => "interface",
      RecordDeclarationSyntax recordDeclaration => recordDeclaration.ClassOrStructKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StructKeyword) ? "recordstruct" : "record",
      StructDeclarationSyntax => "struct",
      _ when symbol.TypeKind == TypeKind.Interface => "interface",
      _ => "class"
    };

  private static string ResolveSolutionPath(string repoRoot, string? explicitSolutionPath)
  {
    if (!string.IsNullOrWhiteSpace(explicitSolutionPath))
    {
      var explicitPath = Path.GetFullPath(Path.IsPathRooted(explicitSolutionPath)
        ? explicitSolutionPath
        : Path.Combine(repoRoot, explicitSolutionPath));
      if (!File.Exists(explicitPath))
      {
        throw new FileNotFoundException($"Solution file '{explicitPath}' does not exist.");
      }

      return explicitPath;
    }

    var candidates = Directory.GetFiles(repoRoot, "*.sln").Concat(Directory.GetFiles(repoRoot, "*.slnx")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (candidates.Count == 0)
    {
      throw new InvalidOperationException($"No .sln or .slnx file found in '{repoRoot}'.");
    }

    if (candidates.Count == 1)
    {
      return candidates[0];
    }

    var folderName = Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    var match = candidates.FirstOrDefault(candidate =>
      string.Equals(Path.GetFileNameWithoutExtension(candidate), folderName, StringComparison.OrdinalIgnoreCase));
    if (match is not null)
    {
      return match;
    }

    var listed = string.Join(", ", candidates.Select(Path.GetFileName));
    throw new InvalidOperationException($"Multiple solutions found at repo root: {listed}. Provide --solution.");
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static async Task<(MSBuildWorkspace Workspace, Solution Solution, IReadOnlyList<ProjectId> RootProjectIds)> OpenSolutionAsync(
    string solutionPath,
    string repoRoot,
    List<IndexWorkspaceDiagnostic> diagnostics,
    CancellationToken cancellationToken)
  {
    EnsureMsBuild();

    var workspace = MSBuildWorkspace.Create();
    workspace.WorkspaceFailed += (_, args) =>
    {
      diagnostics.Add(new IndexWorkspaceDiagnostic(args.Diagnostic.Kind.ToString(), args.Diagnostic.Message));
    };

    if (string.Equals(Path.GetExtension(solutionPath), ".slnx", StringComparison.OrdinalIgnoreCase))
    {
      var listedProjects = ParseSlnxProjects(solutionPath)
        .Select(path => Path.GetFullPath(Path.Combine(repoRoot, path)))
        .ToList();

      var rootProjectIds = new List<ProjectId>(listedProjects.Count);
      foreach (var projectPath in listedProjects)
      {
        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
        rootProjectIds.Add(project.Id);
      }

      return (workspace, workspace.CurrentSolution, rootProjectIds);
    }

    var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
    return (workspace, solution, solution.ProjectIds);
  }

  private static void EnsureMsBuild()
  {
    if (_msBuildRegistered)
    {
      return;
    }

    lock (MsBuildGate)
    {
      if (_msBuildRegistered)
      {
        return;
      }

      MSBuildLocator.RegisterDefaults();
      _msBuildRegistered = true;
    }
  }

  private static string? GetNodeContentHash(Microsoft.Data.Sqlite.SqliteConnection connection, string qualifiedName)
  {
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT content_hash FROM node WHERE qualified_name = $qualified_name;";
    command.Parameters.AddWithValue("$qualified_name", qualifiedName);
    var result = command.ExecuteScalar();
    return result as string;
  }

  private static void SetMeta(Microsoft.Data.Sqlite.SqliteConnection connection, string key, string value)
  {
    using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT INTO meta(key, value)
      VALUES($key, $value)
      ON CONFLICT(key) DO UPDATE SET value = excluded.value;
      """;
    command.Parameters.AddWithValue("$key", key);
    command.Parameters.AddWithValue("$value", value);
    command.ExecuteNonQuery();
  }

  private static int CountNodes(Microsoft.Data.Sqlite.SqliteConnection connection)
  {
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM node;";
    return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static IReadOnlyDictionary<string, int> CountEdgesByKind(Microsoft.Data.Sqlite.SqliteConnection connection)
  {
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT kind, COUNT(*) FROM edge GROUP BY kind;";
    using var reader = command.ExecuteReader();
    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
    while (reader.Read())
    {
      counts[reader.GetString(0)] = reader.GetInt32(1);
    }

    return counts;
  }

  private static bool IsPathInRepo(string repoRoot, string filePath)
  {
    var fullPath = Path.GetFullPath(filePath);
    var fullRoot = Path.GetFullPath(repoRoot);
    return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
  }

  private static bool IsSymbolInRepo(string repoRoot, ISymbol symbol) =>
    symbol.Locations.Any(location =>
      location.IsInSource &&
      location.SourceTree?.FilePath is { } path &&
      IsPathInRepo(repoRoot, path));

  private static string ToRepoRelativePath(string repoRoot, string filePath) =>
    Path.GetRelativePath(repoRoot, filePath).Replace('\\', '/');

  private static string ToProjectQualifiedName(string projectName) => $"proj:{projectName}";
  private static string ToFileQualifiedName(string relativePath) => $"file:{relativePath}";

  private static int GetLineNumber(Location location) => location.GetLineSpan().StartLinePosition.Line + 1;
  private static int GetEndLineNumber(Location location) => location.GetLineSpan().EndLinePosition.Line + 1;

  private static string ComputeSha256(string text)
  {
    var bytes = Encoding.UTF8.GetBytes(text);
    var hash = SHA256.HashData(bytes);
    return Convert.ToHexString(hash).ToLowerInvariant();
  }

  private sealed class PendingFile(string relativePath, FileFacts facts)
  {
    public string RelativePath { get; } = relativePath;
    public FileFacts Facts { get; } = facts;
  }

  private sealed class FileFacts(string relativePath)
  {
    private readonly Dictionary<string, NodeUpsert> _nodesByQualifiedName = new(StringComparer.Ordinal);
    private readonly HashSet<(string NodeQualifiedName, string FilePath, int StartLine, int EndLine)> _spanSet = [];
    private readonly HashSet<(string Source, string Destination, string Kind)> _edgeSet = [];

    public List<NodeUpsert> Nodes { get; } = [];
    public List<SpanUpsert> Spans { get; } = [];
    public List<EdgeUpsert> Edges { get; } = [];

    public void AddNode(NodeUpsert node)
    {
      if (_nodesByQualifiedName.TryGetValue(node.QualifiedName, out var existing))
      {
        var merged = existing with
        {
          Kind = node.Kind,
          Name = node.Name,
          Project = node.Project ?? existing.Project,
          Lang = node.Lang ?? existing.Lang,
          MetaJson = node.MetaJson ?? existing.MetaJson,
          ContentHash = node.ContentHash ?? existing.ContentHash
        };
        _nodesByQualifiedName[node.QualifiedName] = merged;
        for (var i = 0; i < Nodes.Count; i++)
        {
          if (string.Equals(Nodes[i].QualifiedName, node.QualifiedName, StringComparison.Ordinal))
          {
            Nodes[i] = merged;
            break;
          }
        }

        return;
      }

      _nodesByQualifiedName[node.QualifiedName] = node;
      Nodes.Add(node);
    }

    public void AddSpan(SpanUpsert span)
    {
      var normalized = span with { FilePath = relativePath };
      if (_spanSet.Add((normalized.NodeQualifiedName, normalized.FilePath, normalized.StartLine, normalized.EndLine)))
      {
        Spans.Add(normalized);
      }
    }

    public void AddEdge(EdgeUpsert edge)
    {
      var normalized = edge with { OriginFile = edge.OriginFile ?? relativePath };
      if (_edgeSet.Add((normalized.SourceQualifiedName, normalized.DestinationQualifiedName, normalized.Kind)))
      {
        Edges.Add(normalized);
      }
    }
  }

  private static IReadOnlyList<string> ParseSlnxProjects(string slnxPath)
  {
    var document = XDocument.Load(slnxPath, LoadOptions.None);
    var paths = document.Root?
      .Elements("Project")
      .Select(element => element.Attribute("Path")?.Value)
      .Where(path => !string.IsNullOrWhiteSpace(path))
      .Cast<string>()
      .ToList();
    if (paths is null || paths.Count == 0)
    {
      throw new InvalidOperationException($"No projects found in solution file '{slnxPath}'.");
    }

    return paths;
  }
}
