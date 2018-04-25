//
// Author:
//   Aaron Bockover <abock@xamarin.com>
//
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Xamarin.Interactive.CodeAnalysis;
using Xamarin.Interactive.CodeAnalysis.Evaluating;
using Xamarin.Interactive.CodeAnalysis.Models;
using Xamarin.Interactive.CodeAnalysis.Resolving;
using Xamarin.Interactive.CodeAnalysis.Roslyn;

using InteractiveDiagnostic = Xamarin.Interactive.CodeAnalysis.Models.Diagnostic;
using InteractiveDiagnosticSeverity = Xamarin.Interactive.CodeAnalysis.Models.DiagnosticSeverity;

[assembly: Xamarin.Interactive.CodeAnalysis.WorkspaceService (
    "csharp",
    typeof (Xamarin.Interactive.CodeAnalysis.Roslyn.RoslynCompilationWorkspace.Activator))]

namespace Xamarin.Interactive.CodeAnalysis.Roslyn
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Completion;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Host;
    using Microsoft.CodeAnalysis.Host.Mef;
    using Microsoft.CodeAnalysis.Options;
    using Microsoft.CodeAnalysis.Text;

    sealed class RoslynCompilationWorkspace : CodeAnalysis.IWorkspaceService, IDisposable
    {
        public sealed class Activator : IWorkspaceServiceActivator
        {
            public Task<CodeAnalysis.IWorkspaceService> CreateNew (
                LanguageDescription languageDescription,
                WorkspaceConfiguration workspaceConfiguration,
                CancellationToken cancellationToken)
                => Task.FromResult<CodeAnalysis.IWorkspaceService> (
                    new RoslynCompilationWorkspace (workspaceConfiguration));
        }

        static class HostServicesFactory
        {
            public static readonly Assembly CodeAnalysisFeaturesAssembly
                = Assembly.Load ("Microsoft.CodeAnalysis.Features");

            public static readonly Assembly CodeAnalysisCSharpFeaturesAssembly
                = Assembly.Load ("Microsoft.CodeAnalysis.CSharp.Features");

            public static HostServices Create ()
                => MefHostServices.Create (MefHostServices.DefaultAssemblies
                    .Add (CodeAnalysisFeaturesAssembly)
                    .Add (CodeAnalysisCSharpFeaturesAssembly));
        }

        sealed class InteractiveWorkspace : Workspace
        {
            sealed class DocumentState
            {
                public bool IsSubmissionComplete;
                public Dictionary<string, DateTime> LoadDirectiveFiles;
            }

            ImmutableDictionary<DocumentId, DocumentState> openDocumentState
                = ImmutableDictionary<DocumentId, DocumentState>.Empty;

            internal InteractiveWorkspace () : base (HostServicesFactory.Create (), "Interactive")
            {
            }

            #region Adopted from AdhocWorkspace

            public Project AddProject (ProjectInfo projectInfo)
            {
                if (projectInfo == null)
                    throw new ArgumentNullException (nameof(projectInfo));

                OnProjectAdded (projectInfo);
                UpdateReferencesAfterAdd ();

                return CurrentSolution.GetProject (projectInfo.Id);
            }

            public void RemoveProject (ProjectId projectId)
            {
                if (projectId == null)
                    throw new ArgumentNullException (nameof (projectId));

                OnProjectRemoved (projectId);
            }

            public void RemoveProjectReferences (ProjectId projectId)
            {
                if (projectId == null)
                    throw new ArgumentNullException (nameof (projectId));

                var project = CurrentSolution.GetProject (projectId);

                foreach (var projectReference in project.ProjectReferences.ToImmutableArray ())
                    OnProjectReferenceRemoved (projectId, projectReference);
            }

            public void RemoveMetadataReferences (ProjectId projectId)
            {
                if (projectId == null)
                    throw new ArgumentNullException (nameof (projectId));

                var project = CurrentSolution.GetProject (projectId);

                foreach (var metadataReference in project.MetadataReferences.ToImmutableArray ())
                    OnMetadataReferenceRemoved (projectId, metadataReference);
            }

            public void AddProjectReference (ProjectId projectId, ProjectReference projectReference)
            {
                if (projectId == null)
                    throw new ArgumentNullException (nameof (projectId));

                if (projectReference == null)
                    throw new ArgumentNullException (nameof (projectReference));

                OnProjectReferenceAdded (projectId, projectReference);
            }

            public void AddProjectReferences (ProjectId projectId,
                IEnumerable<ProjectReference> projectReferences)
            {
                if (projectId == null)
                    throw new ArgumentNullException (nameof (projectId));

                if (projectReferences == null)
                    throw new ArgumentNullException (nameof (projectReferences));

                foreach (var projectReference in projectReferences)
                    OnProjectReferenceAdded (projectId, projectReference);
            }

            public void AddMetadataReferences (ProjectId projectId,
                IEnumerable<MetadataReference> metadataReferences)
            {
                if (projectId == null)
                    throw new ArgumentNullException (nameof (projectId));

                if (metadataReferences == null)
                    throw new ArgumentNullException (nameof (metadataReferences));

                foreach (var metadataReference in metadataReferences)
                    OnMetadataReferenceAdded (projectId, metadataReference);
            }

            public void AddImports (ProjectId projectId, ImmutableArray<string> imports)
            {
                if (projectId == null)
                    throw new ArgumentNullException (nameof (projectId));

                var project = CurrentSolution.GetProject (projectId);
                var compilationOptions = (CSharpCompilationOptions)project.CompilationOptions;

                SetCurrentSolution (
                    CurrentSolution.WithProjectCompilationOptions (
                        projectId,
                        compilationOptions.WithUsings (imports)));
            }

            public Document AddDocument (DocumentInfo documentInfo)
            {
                if (documentInfo == null)
                    throw new ArgumentNullException (nameof(documentInfo));

                OnDocumentAdded (documentInfo);

                return CurrentSolution.GetDocument (documentInfo.Id);
            }

            public new void ClearSolution ()
            {
                base.ClearSolution ();
            }

            public override bool CanOpenDocuments {
                get { return true; }
            }

            public override void OpenDocument (DocumentId documentId, bool activate = true)
            {
                var document = CurrentSolution.GetDocument (documentId);
                if (document != null) {
                    var sourceText = document.GetTextAsync (CancellationToken.None).Result;
                    openDocumentState = openDocumentState.Add (documentId, new DocumentState ());
                    OnDocumentOpened (documentId, sourceText.Container, activate);
                }
            }

            public override void CloseDocument (DocumentId documentId)
            {
                openDocumentState = openDocumentState.Remove (documentId);
                var document = CurrentSolution.GetDocument (documentId);
                if (document != null) {
                    var text = document.GetTextAsync ().Result;
                    var version = document.GetTextVersionAsync ().Result;
                    var loader = TextLoader.From (TextAndVersion.Create (
                        text, version, document.FilePath));
                    OnDocumentClosed (documentId, loader);
                }
            }

            public new void ApplyDocumentTextChanged (DocumentId documentId, SourceText newText)
                => base.ApplyDocumentTextChanged (documentId, newText);

            #endregion

            public new Solution SetCurrentSolution (Solution solution)
            {
                return base.SetCurrentSolution (solution);
            }

            public override bool CanApplyChange (ApplyChangesKind feature)
            {
                switch (feature) {
                case ApplyChangesKind.AddDocument:
                case ApplyChangesKind.ChangeDocument:
                    return true;
                }

                return false;
            }

            protected override void OnDocumentTextChanged (Document document)
            {
                DocumentState documentState;
                if (!openDocumentState.TryGetValue (document.Id, out documentState))
                    return;

                var fixup = TryFixDocumentSyntaxTreeAsync (document).Result;
                documentState.IsSubmissionComplete = fixup.Item1;

                if (fixup.Item2 != null)
                    SetCurrentSolution (CurrentSolution
                        .WithDocumentSyntaxRoot (
                            document.Id,
                            fixup.Item2.GetRoot ()));
            }

            static async Task<Tuple<bool, SyntaxTree>> TryFixDocumentSyntaxTreeAsync (Document document,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                // bail early if already complete
                var syntaxRoot = await document.GetSyntaxRootAsync (cancellationToken);
                if (SyntaxFactory.IsCompleteSubmission (syntaxRoot.SyntaxTree))
                    return Tuple.Create (true, default(SyntaxTree));

                // bail if the end of the code is some kind of statement (foreach,
                // for, while, etc. that if completed with a ';' could technically
                // be a valid submission, but would imply the wrong semantics
                if (syntaxRoot.GetLastToken ().Parent is StatementSyntax)
                    return Tuple.Create (false, default(SyntaxTree));

                // otherwise add a semicolon to the end, reparse, and check again,
                // keeping the updated tree if it's complete
                var parseOptions = document.Project.ParseOptions;
                var sourceText = await document.GetTextAsync (cancellationToken);
                var syntaxTree = SyntaxFactory.ParseSyntaxTree (sourceText + ";", parseOptions);
                if (SyntaxFactory.IsCompleteSubmission (syntaxTree) &&
                    !syntaxTree.GetDiagnostics ().Any ())
                    return Tuple.Create (true, syntaxTree);

                // still incomplete, so just let it fail
                return Tuple.Create (false, default(SyntaxTree));
            }

            public bool IsDocumentSubmissionComplete (DocumentId documentId)
            {
                if (openDocumentState.TryGetValue (documentId, out var documentState))
                    return documentState.IsSubmissionComplete;
                return false;
            }

            public void SetLoadDirectiveFiles (
                DocumentId documentId,
                SourceReferenceResolver sourceReferenceResolver,
                ImmutableList<LoadDirectiveTriviaSyntax> loadDirectives)
            {
                if (loadDirectives == null || loadDirectives.Count == 0)
                    return;

                if (!openDocumentState.TryGetValue (documentId, out var documentState))
                    return;

                documentState.LoadDirectiveFiles = null;

                foreach (var directive in loadDirectives) {
                    var filePath = directive.File.ValueText;

                    if (string.IsNullOrEmpty (filePath))
                        continue;

                    filePath = sourceReferenceResolver.ResolveReference (
                        directive.File.ValueText,
                        null);

                    if (string.IsNullOrEmpty (filePath) || !File.Exists (filePath))
                        continue;

                    if (documentState.LoadDirectiveFiles == null)
                        documentState.LoadDirectiveFiles
                            = new Dictionary<string, DateTime> ();

                    documentState.LoadDirectiveFiles [filePath]
                        = File.GetLastWriteTimeUtc (filePath);
                }
            }

            public bool HaveAnyLoadDirectiveFilesChanged (DocumentId documentId)
            {
                if (!openDocumentState.TryGetValue (documentId, out var documentState) ||
                    documentState.LoadDirectiveFiles == null)
                    return false;

                foreach (var file in documentState.LoadDirectiveFiles) {
                    if (!File.Exists (file.Key) ||
                        File.GetLastWriteTimeUtc (file.Key) != file.Value)
                        return true;
                }

                return false;
            }
        }

        readonly InteractiveWorkspace workspace;
        readonly InteractiveSourceReferenceResolver sourceReferenceResolver;
        readonly InteractiveMetadataReferenceResolver metadataReferenceResolver;
        readonly MonoScriptCompilationPatcher monoScriptCompilationPatcher;

        readonly Type hostObjectType;
        readonly ImmutableArray<string> initialImports;
        readonly ImmutableArray<string> initialWarningSuppressions;
        readonly ImmutableArray<PortableExecutableReference> initialReferences;
        readonly ImmutableDictionary<string, ReportDiagnostic> initialDiagnosticOptions;

        readonly CSharpParseOptions parseOptions = new CSharpParseOptions (
            LanguageVersion.CSharp7_2,
            DocumentationMode.None,
            SourceCodeKind.Script);

        readonly bool includePeImagesInResolution;

        public EvaluationContextId EvaluationContextId { get; }
        public InteractiveDependencyResolver DependencyResolver { get; }
        public CompletionService CompletionService { get; }
        public OptionSet Options => workspace.Options;

        int submissionCount;

        public WorkspaceConfiguration Configuration { get; }

        static readonly string [] byNameImplicitReferences = {
            // for 'dynamic':
            "Microsoft.CSharp",

            // for when corlib does not provide SVT; see
            // https://github.com/Microsoft/workbooks/issues/211
            typeof (object).Assembly.GetType ("System.ValueTuple") == null
                ? "System.ValueTuple"
                : null
        };

        public RoslynCompilationWorkspace (WorkspaceConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException (nameof (configuration));

            Configuration = configuration;

            var dependencyResolver = configuration.DependencyResolver;
            var compilationConfiguration = configuration.CompilationConfiguration;

            workspace = new InteractiveWorkspace ();
            sourceReferenceResolver = new InteractiveSourceReferenceResolver (dependencyResolver);
            metadataReferenceResolver = new InteractiveMetadataReferenceResolver (dependencyResolver);
            monoScriptCompilationPatcher = new MonoScriptCompilationPatcher (
                assemblyNamePrefixBytes);

            DependencyResolver = dependencyResolver;

            hostObjectType = compilationConfiguration.GlobalStateType.ResolvedType;
            EvaluationContextId = compilationConfiguration.EvaluationContextId;
            includePeImagesInResolution = compilationConfiguration.IncludePEImagesInDependencyResolution;

            initialImports = compilationConfiguration.DefaultImports.ToImmutableArray ();
            initialWarningSuppressions = compilationConfiguration.DefaultWarningSuppressions.ToImmutableArray ();
            initialDiagnosticOptions = initialWarningSuppressions.ToImmutableDictionary (
                warningId => warningId,
                warningId => ReportDiagnostic.Suppress);
            initialReferences = dependencyResolver
                .ResolveDefaultReferences ()
                .Select (r => PortableExecutableReference.CreateFromFile (r.Path))
                .ToImmutableArray ();

            foreach (var implicitReference in byNameImplicitReferences) {
                if (implicitReference == null)
                    continue;

                var assembly = DependencyResolver.ResolveWithoutReferences (
                    new AssemblyName (implicitReference));
                if (assembly != null)
                    initialReferences = initialReferences.Add (
                        MetadataReference.CreateFromFile (assembly.Path));
            }

            CompletionService = workspace
                .Services
                .GetLanguageServices (LanguageNames.CSharp)
                .GetService<CompletionService> ();
        }

        public void Dispose ()
        {
            workspace.Dispose ();
            GC.SuppressFinalize (this);
        }

        #region Submission Management

        public DocumentId GetSubmissionDocumentId (SourceTextContainer buffer) =>
            workspace.GetDocumentIdInCurrentContext (buffer);

        public Document GetSubmissionDocument (SourceTextContainer buffer) =>
            workspace.CurrentSolution.GetDocument (GetSubmissionDocumentId (buffer));

        Project GetProject (DocumentId documentId) =>
            workspace.CurrentSolution.GetDocument (documentId).Project;

        public DocumentId AddSubmission (DocumentId previousDocumentId, DocumentId nextDocumentId)
        {
            var project = workspace.AddProject (CreateSubmissionProjectInfo ());

            // AdhocWorkspace.AddDocument will add text as SourceCodeKind.Regular
            // not Script, so we need to do this the long way here...
            var documentId = DocumentId.CreateNewId (project.Id, project.Name);
            workspace.AddDocument (DocumentInfo.Create (documentId,
                project.Name,
                null,
                SourceCodeKind.Script));

            workspace.OpenDocument (documentId);

            ConfigureSubmission (project.Id, previousDocumentId);

            if (nextDocumentId != null) {
                var nextProject = GetProject (nextDocumentId);
                workspace.RemoveProjectReferences (nextProject.Id);
                workspace.RemoveMetadataReferences (nextProject.Id);
                workspace.AddProjectReference (nextProject.Id, new ProjectReference (project.Id));
            }

            return documentId;
        }

        void ConfigureSubmission (ProjectId submissionProjectId, DocumentId previousDocumentId)
        {
            if (submissionProjectId == null)
                return;

            if (previousDocumentId == null) {
                workspace.AddImports (submissionProjectId, initialImports);
                workspace.AddMetadataReferences (submissionProjectId, initialReferences);
            } else {
                var previousProject = GetProject (previousDocumentId);
                workspace.AddProjectReference (
                    submissionProjectId,
                    new ProjectReference (previousProject.Id));
            }
        }

        public void RemoveSubmission (DocumentId documentId, DocumentId nextDocumentId)
        {
            if (documentId == null)
                throw new ArgumentNullException (nameof (documentId));

            var project = GetProject (documentId);
            var projectReferences = project.ProjectReferences.ToImmutableArray ();
            var metadataReferences = project.MetadataReferences.ToImmutableArray ();

            if (nextDocumentId != null) {
                var nextProjectId = GetProject (nextDocumentId).Id;

                workspace.RemoveProjectReferences (nextProjectId);
                workspace.RemoveMetadataReferences (nextProjectId);

                workspace.AddProjectReferences (nextProjectId, projectReferences);
                workspace.AddMetadataReferences (nextProjectId, metadataReferences);
                workspace.AddImports (
                    nextProjectId,
                    ((CSharpCompilationOptions)project.CompilationOptions).Usings);
            }

            workspace.CloseDocument (documentId);
            workspace.RemoveProject (project.Id);
        }

        const string assemblyNamePrefix = "🐵🐻";
        static readonly byte [] assemblyNamePrefixBytes = Encoding.UTF8.GetBytes (assemblyNamePrefix);

        ProjectInfo CreateSubmissionProjectInfo ()
        {
            var name = $"{assemblyNamePrefix}#{EvaluationContextId}-{submissionCount++}";
            return ProjectInfo.Create (
                ProjectId.CreateNewId (debugName: name),
                VersionStamp.Create (),
                name: name,
                assemblyName: name,
                language: LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions (
                    OutputKind.DynamicallyLinkedLibrary,
                    scriptClassName: name,
                    allowUnsafe: true,
                    usings: ImmutableArray<string>.Empty,
                    sourceReferenceResolver: sourceReferenceResolver,
                    metadataReferenceResolver: metadataReferenceResolver,
                    assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default,
                    specificDiagnosticOptions: initialDiagnosticOptions),
                parseOptions: parseOptions,
                documents: null,
                hostObjectType: hostObjectType,
                isSubmission: true);
        }

        #endregion

        #region Compilation

        public ImmutableArray<PortableExecutableReference> GetExplicitMetadataReferences (
            CancellationToken cancellationToken = default (CancellationToken))
            => workspace
                .CurrentSolution
                .Projects
                .SelectMany (project => {
                    cancellationToken.ThrowIfCancellationRequested ();
                    return project.MetadataReferences;
                })
                .OfType<PortableExecutableReference> ()
                .ToImmutableArray ();

        public bool HaveAnyLoadDirectiveFilesChanged (DocumentId submissionDocumentId)
            => workspace.HaveAnyLoadDirectiveFilesChanged (submissionDocumentId);

        async Task<Compilation> GetCompilationAsync (DocumentId submissionDocumentId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var project = workspace
                ?.CurrentSolution
                ?.GetDocument (submissionDocumentId)
                ?.Project;

            if (project == null)
                return null;

            return await project.GetCompilationAsync (cancellationToken);
        }

        public async Task<ImmutableArray<Diagnostic>> GetSubmissionCompilationDiagnosticsAsync (
            DocumentId submissionDocumentId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var compilation = await GetCompilationAsync (
                submissionDocumentId,
                cancellationToken).ConfigureAwait (false);

            return compilation == null
                ? ImmutableArray<Diagnostic>.Empty
                : compilation.GetDiagnostics (cancellationToken);
        }

        public bool IsDocumentSubmissionComplete (DocumentId documentId) =>
            workspace.IsDocumentSubmissionComplete (documentId);

        CompilationUnitSyntax FixupSubmissionWithTrailingFieldDeclarationForEmission (
            CompilationUnitSyntax compilationUnit, FieldDeclarationSyntax trailingField)
        {
            var vars = trailingField.Declaration.Variables;
            if (vars.Count != 1)
                return compilationUnit;

            // FIXME: The public SyntaxFactory API enforces ExpressionStatementSyntax having
            // a semicolon token. For interactive/scripting, this is optional, but we have
            // no way of generating a tree by hand that makes it so. The parser uses the
            // internal SyntaxFactory, which does not do argument checking. Ideally we could
            // use this:
            //
            // var exprStatement = SyntaxFactory.ExpressionStatement (
            //  SyntaxFactory.IdentifierName (vars [0].Identifier),
            //  SyntaxFactory.Token (SyntaxKind.None)
            // );
            //
            // instead of this hack:
            var exprStatement = SyntaxFactory.ParseStatement (
                vars [0].Identifier.Text,
                options: parseOptions);

            return compilationUnit.AddMembers (
                SyntaxFactory.GlobalStatement (exprStatement));
        }

        CompilationUnitSyntax FixupSubmissionWithTrailingSemicolon (
            CompilationUnitSyntax compilationUnit, SyntaxToken semicolonToken)
        {
            var exprStatement = semicolonToken.Parent as ExpressionStatementSyntax;
            if (exprStatement == null)
                return compilationUnit;

            // FIXME: again, the public SyntaxFactory API enforces ExpressionStatementSyntax having an
            // actual semicolon token. See FixupSubmissionWithTrailingFieldDeclarationForEmission for
            // details. This would be preferential:
            //
            // return compilationUnit.ReplaceNode (
            //     exprStatement,
            //     exprStatement.WithSemicolonToken (SyntaxFactory.Token (SyntaxKind.None)));
            //
            // instead of this hack:
            return compilationUnit.ReplaceNode (
                exprStatement,
                SyntaxFactory.ParseStatement (
                    exprStatement.ToFullString ()?.TrimEnd ()?.TrimEnd (';'),
                    options: parseOptions));
        }

        Task<CompilationUnitSyntax> FixupSubmissionForEmissionAsync (
            CompilationUnitSyntax compilationUnit,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var lastToken = compilationUnit.GetLastToken ();
            var trailingField = lastToken.Parent as FieldDeclarationSyntax;
            if (trailingField != null)
                compilationUnit = FixupSubmissionWithTrailingFieldDeclarationForEmission (
                    compilationUnit, trailingField);
            else if (lastToken.Kind () == SyntaxKind.SemicolonToken)
                compilationUnit = FixupSubmissionWithTrailingSemicolon (compilationUnit, lastToken);

            return Task.FromResult (compilationUnit);
        }

        ImmutableArray<Diagnostic>? lastEmitDiagnostics;

        public async Task<CodeAnalysis.Compilation>
            EmitSubmissionCompilationAsync (
                DocumentId submissionDocumentId,
                EvaluationEnvironment evaluationEnvironment,
                CancellationToken cancellationToken = default)
        {
            if (submissionDocumentId == null)
                throw new ArgumentNullException (nameof (submissionDocumentId));

            var submissionDocument = workspace.CurrentSolution.GetDocument (submissionDocumentId);

            var semanticModel = await submissionDocument
                .GetSemanticModelAsync (cancellationToken)
                .ConfigureAwait (false);

            var syntaxRewriter = new InteractiveSyntaxRewriter (semanticModel);

            var compilationUnit = (CompilationUnitSyntax)syntaxRewriter.VisitCompilationUnit (
                semanticModel.SyntaxTree.GetCompilationUnitRoot (cancellationToken));

            workspace.SetLoadDirectiveFiles (
                submissionDocumentId,
                sourceReferenceResolver,
                syntaxRewriter.LoadDirectives);

            var fixedUpCompilationUnit = await FixupSubmissionForEmissionAsync (
                compilationUnit,
                cancellationToken)
                .ConfigureAwait (false);

            if (fixedUpCompilationUnit != compilationUnit)
                workspace.SetCurrentSolution (
                    workspace.CurrentSolution.WithDocumentSyntaxRoot (
                        submissionDocument.Id, fixedUpCompilationUnit));

            var compilation = await GetCompilationAsync (submissionDocument.Id, cancellationToken)
                .ConfigureAwait (false);

            using (var stream = new MemoryStream ()) {
                byte[] peImage = null;

                var emitResult = compilation.Emit (stream, cancellationToken: cancellationToken);
                if (emitResult.Success) {
                    stream.Position = 0;
                    peImage = stream.ToArray ();
                }

                lastEmitDiagnostics = emitResult.Diagnostics;

                AssemblyDefinition executableAssembly = null;
                if (peImage != null) {
                    var entryPoint = compilation.GetEntryPoint (cancellationToken);
                    var ns = entryPoint.ContainingNamespace;
                    var type = entryPoint.ContainingType;

                    executableAssembly = new AssemblyDefinition (
                        new AssemblyName (compilation.AssemblyName),
                        null,
                        string.IsNullOrEmpty (ns?.MetadataName)
                            ? type.MetadataName
                            : ns.MetadataName + "." + type.MetadataName,
                        entryPoint.MetadataName,
                        peImage);
                }

                return new CodeAnalysis.Compilation (
                    submissionDocumentId.ToCodeCellId (),
                    submissionCount,
                    evaluationEnvironment,
                    DetermineIfResultIsAnExpression (
                        compilation,
                        compilation.SyntaxTrees.Last (),
                        cancellationToken),
                    monoScriptCompilationPatcher.Patch (
                        submissionDocumentId,
                        executableAssembly),
                    await DependencyResolver.ResolveReferencesAsync (
                        compilation.References,
                        includePeImagesInResolution,
                        cancellationToken).ConfigureAwait (false));
            }
        }

        bool DetermineIfResultIsAnExpression (Compilation compilation, SyntaxTree syntaxTree,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var expr = ((syntaxTree
                ?.GetRoot (cancellationToken)
                ?.ChildNodes ()
                ?.LastOrDefault () as GlobalStatementSyntax)
                ?.Statement as ExpressionStatementSyntax)
                ?.Expression;

            // bail early if we don't have an expression as
            // the last child node of the compilation unit
            if (expr == null)
                return false;

            return expr.Accept (new ShouldRenderResultOfExpressionVisitor (compilation));
        }

        #endregion

        Document GetDocument (CodeCellId codeCellId)
        {
            var document = workspace.CurrentSolution?.GetDocument (codeCellId.ToDocumentId ());
            if (document == null)
                throw new ArgumentException (
                    $"documnent {codeCellId} does not exist in workspace",
                    nameof (codeCellId));
            return document;
        }

        #region IWorkspaceService

        public IReadOnlyList<CodeCellId> GetTopologicallySortedCellIds ()
            => workspace
                .CurrentSolution
                .GetProjectDependencyGraph ()
                .GetTopologicallySortedProjects ()
                .Select (workspace.CurrentSolution.GetProject)
                .SelectMany (p => p.DocumentIds)
                .Select (ConversionExtensions.ToCodeCellId)
                .ToImmutableList ();

        public CodeCellId InsertCell (
            CodeCellId previousCellId,
            CodeCellId nextCellId)
            => AddSubmission (
                previousCellId.ToDocumentId (),
                nextCellId.ToDocumentId ()).ToCodeCellId ();

        public void RemoveCell (CodeCellId cellId, CodeCellId nextCellId)
            => RemoveSubmission (
                cellId.ToDocumentId (),
                nextCellId.ToDocumentId ());

        public bool IsCellComplete (CodeCellId cellId)
            => IsDocumentSubmissionComplete (cellId.ToDocumentId ());

        public async Task<bool> IsCellOutdatedAsync (
            CodeCellId cellId,
            CancellationToken cancellationToken = default)
        {
            var documentId = cellId.ToDocumentId ();
            if (HaveAnyLoadDirectiveFilesChanged (documentId)) {
                // A trick to force Roslyn into invalidating the tree it's holding on
                // to representing code pulled in via any #load directives in the cell.
                // Unfortunately we have to go from SourceText → string → SourceText
                // to force SourceText.Container.TextChanged to be raised.
                // See https://github.com/dotnet/roslyn/issues/21964
                SetCellBuffer (
                    cellId,
                    await GetCellBufferAsync (cellId, cancellationToken));
                return true;
            }

            return false;
        }

        public void SetCellBuffer (CodeCellId cellId, string buffer)
            => workspace.ApplyDocumentTextChanged (
                cellId.ToDocumentId (),
                SourceText.From (buffer ?? string.Empty));

        public async Task<string> GetCellBufferAsync (
            CodeCellId cellId,
            CancellationToken cancellationToken = default)
            => (await GetDocument (cellId).GetTextAsync (cancellationToken)).ToString ();

        public async Task<IReadOnlyList<InteractiveDiagnostic>> GetCellDiagnosticsAsync (
            CodeCellId cellId,
            CancellationToken cancellationToken = default)
        {
            ImmutableArray<Diagnostic> diagnostics;

            if (lastEmitDiagnostics != null) {
                diagnostics = lastEmitDiagnostics.Value;
                lastEmitDiagnostics = null;
            } else {
                diagnostics = await GetSubmissionCompilationDiagnosticsAsync (
                    cellId.ToDocumentId (),
                    cancellationToken);
            }

            return diagnostics
                .Filter ()
                .Select (ConversionExtensions.ToInteractiveDiagnostic)
                .ToImmutableList ();
        }

        public Task<CodeAnalysis.Compilation> EmitCellCompilationAsync (
            CodeCellId cellId,
            EvaluationEnvironment evaluationEnvironment,
            CancellationToken cancellationToken = default)
            => EmitSubmissionCompilationAsync (
                cellId.ToDocumentId (),
                evaluationEnvironment,
                cancellationToken);

        HoverController hoverController;
        CompletionController completionController;
        SignatureHelpController signatureHelpController;

        public async Task<Hover> GetHoverAsync (
            CodeCellId cellId,
            Position position,
            CancellationToken cancellationToken = default)
        {
            if (hoverController == null)
                hoverController = new HoverController (this);

            return await hoverController.ProvideHoverAsync (
                GetDocument (cellId),
                position.ToRoslyn (),
                cancellationToken);
        }

        public async Task<IEnumerable<Models.CompletionItem>> GetCompletionsAsync (
            CodeCellId cellId,
            Position position,
            CancellationToken cancellationToken = default)
        {
            if (completionController == null)
                completionController = new CompletionController (this);

            return await completionController.ProvideFilteredCompletionItemsAsync (
                GetDocument (cellId),
                position.ToRoslyn (),
                cancellationToken);
        }

        public async Task<SignatureHelp> GetSignatureHelpAsync (
            CodeCellId cellId,
            Position position,
            CancellationToken cancellationToken = default)
        {
            if (signatureHelpController == null)
                signatureHelpController = new SignatureHelpController (this);

            return await signatureHelpController.ComputeSignatureHelpAsync (
                GetDocument (cellId),
                position.ToRoslyn (),
                cancellationToken);
        }

        public IEnumerable<ExternalDependency> GetExternalDependencies ()
        {
            var dependencies = ImmutableList<ExternalDependency>.Empty;

            var documents = workspace
                .CurrentSolution
                .Projects
                .SelectMany (project => project.Documents);

            foreach (var document in documents) {
                if (!document.TryGetSyntaxRoot (out var syntaxRoot))
                    continue;

                dependencies = dependencies.AddRange (syntaxRoot
                    .DescendantTrivia ()
                    .Where (trivia => trivia.HasStructure && (
                        trivia.IsKind (SyntaxKind.LoadDirectiveTrivia) ||
                        trivia.IsKind (SyntaxKind.ReferenceDirectiveTrivia)))
                    .Select (trivia => trivia.GetStructure ())
                    .SelectMany (node => node.ChildTokens ())
                    .Where (token => token.IsKind (SyntaxKind.StringLiteralToken))
                    .Select (token => new ExternalDependency (token.ValueText)));
            }

            return dependencies;
        }

        #endregion
    }
}