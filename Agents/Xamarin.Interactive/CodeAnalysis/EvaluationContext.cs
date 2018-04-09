//
// Author:
//   Aaron Bockover <abock@xamarin.com>
//
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Xamarin.Interactive.CodeAnalysis.Resolving;
using Xamarin.Interactive.Core;
using Xamarin.Interactive.Representations;
using Xamarin.Interactive.Representations.Reflection;

namespace Xamarin.Interactive.CodeAnalysis
{
    sealed class EvaluationContext : IEvaluationContext
    {
        static int lastEvaluationContextId = -1;

        readonly Agent agent;

        public object GlobalState { get; }
        public EvaluationContextId Id { get; }

        object [] submissionStates;

        readonly Observable<EvaluationInFlight> evaluations = new Observable<EvaluationInFlight> ();
        public IObservable<EvaluationInFlight> Evaluations => evaluations;

        public EvaluationAssemblyContext AssemblyContext { get; } = new EvaluationAssemblyContext ();

        bool initializedAgentIntegration;
        List<AssemblyDefinition> loadedAssemblies = new List<AssemblyDefinition> ();

        volatile Thread currentRunThread;
        public Thread CurrentRunThread => currentRunThread;

        public EvaluationContext (Agent agent, object globalState)
        {
            this.agent = agent ?? throw new ArgumentNullException (nameof (agent));

            GlobalState = globalState;
            Id = ++lastEvaluationContextId;

            submissionStates = new [] { GlobalState, null };

            AssemblyContext.AssemblyResolvedHandler = HandleAssemblyResolved;
        }

        public void Dispose ()
        {
            evaluations.Observers.OnCompleted ();

            AssemblyContext.Dispose ();
            GC.SuppressFinalize (this);
        }

        void HandleAssemblyResolved (Assembly assembly, AssemblyDefinition remoteAssembly)
        {
            initializedAgentIntegration |= CheckLoadedAssemblyForAgentIntegration (assembly) != null;
            loadedAssemblies.Add (remoteAssembly);
            agent.LoadExternalDependencies (assembly, remoteAssembly.ExternalDependencies);
        }

        public IAgentIntegration CheckLoadedAssemblyForAgentIntegration (Assembly assembly)
        {
            if (assembly?.GetReferencedAssemblies ().Any (r => r.Name == "Xamarin.Interactive") == false)
                return null;

            var integrationType = assembly
                .GetCustomAttribute<AgentIntegrationAttribute> ()
                ?.AgentIntegrationType;

            if (integrationType == null)
                return null;

            if (!typeof (IAgentIntegration).IsAssignableFrom (integrationType))
                throw new InvalidOperationException (
                    $"encountered [assembly:{typeof (AgentIntegrationAttribute).FullName}" +
                    $"({integrationType.FullName})] on assembly '{assembly.FullName}', " +
                    $"but type specified does not implement " +
                    $"{typeof (IAgentIntegration).FullName}");

            var integration = (IAgentIntegration)Activator.CreateInstance (integrationType);
            integration.IntegrateWith (agent);

            if (integration is IEvaluationContextIntegration evalContextIntegration)
                evalContextIntegration.IntegrateWith (this);

            ReflectionRepresentationProvider.AgentHasIntegratedWith (integration);

            return integration;
        }

        public async Task RunAsync (
            Guid evaluationRequestId,
            Compilation compilation,
            CancellationToken cancellationToken = default (CancellationToken))
        {
            foreach (var assembly in new AssemblyDefinition [] { compilation.ExecutableAssembly }
                .Concat (compilation.References ?? new AssemblyDefinition [] { }))
                agent.LoadExternalDependencies (null, assembly?.ExternalDependencies);

            Exception evaluationException = null;
            ExceptionNode evaluationExceptionToReturn = null;

            var inFlight = new EvaluationInFlight (EvaluationPhase.Compiled, compilation);

            loadedAssemblies = new List<AssemblyDefinition> ();

            var savedStdout = Console.Out;
            var savedStderr = Console.Error;

            var capturedOutputWriter = new CapturedOutputWriter (compilation.CodeCellId);
            Console.SetOut (capturedOutputWriter.StandardOutput);
            Console.SetError (capturedOutputWriter.StandardError);
            capturedOutputWriter.SegmentCaptured += CapturedOutputWriter_SegmentCaptured;

            var stopwatch = new Stopwatch ();

            currentRunThread = Thread.CurrentThread;
            currentRunThread.CurrentCulture = InteractiveCulture.CurrentCulture;
            currentRunThread.CurrentUICulture = InteractiveCulture.CurrentUICulture;

            initializedAgentIntegration = false;
            var interrupted = false;

            try {
                // We _only_ want to capture exceptions from user-code here but
                // allow our own bugs to propagate out to the normal exception
                // handling. The only reason we capture these exceptions is in
                // case anything has been written to stdout/stderr by user-code
                // before the exception was raised... we still want to send that
                // captured output back to the client for rendering.
                stopwatch.Start ();
                inFlight = inFlight.With (originalValue: await CoreRunAsync (compilation, cancellationToken));
            } catch (AggregateException e) when (e.InnerExceptions?.Count == 1) {
                evaluationException = e;
                // the Roslyn-emitted script delegates are async, so all exceptions
                // raised within a delegate should be AggregateException; if there's
                // just one inner exception, unwind it since the async nature of the
                // script delegates is a REPL implementation detail.
                evaluationExceptionToReturn = ExceptionNode.Create (e.InnerExceptions [0]);
            } catch (ThreadAbortException e) {
                evaluationException = e;
                interrupted = true;
                Thread.ResetAbort ();
            } catch (ThreadInterruptedException e) {
                evaluationException = e;
                interrupted = true;
            } catch (Exception e) {
                evaluationException = e;
                evaluationExceptionToReturn = ExceptionNode.Create (e);
            } finally {
                stopwatch.Stop ();
                currentRunThread = null;

                inFlight = inFlight.With (phase: EvaluationPhase.Evaluated);
                evaluations.Observers.OnNext (inFlight);

                capturedOutputWriter.SegmentCaptured -= CapturedOutputWriter_SegmentCaptured;
                Console.SetOut (savedStdout);
                Console.SetError (savedStderr);
            }

            var evaluation = new Evaluation (
                evaluationRequestId,
                compilation.CodeCellId,
                EvaluationResultHandling.Replace,
                // an exception in the call to Prepare should not be explicitly caught
                // here (see above) since it'll be handled at a higher level and can be
                // flagged as being a bug in our code since this method should never throw.
                agent.RepresentationManager.Prepare (inFlight.OriginalValue),
                evaluationExceptionToReturn,
                stopwatch.Elapsed,
                InteractiveCulture.CurrentCulture.LCID,
                InteractiveCulture.CurrentUICulture.LCID,
                interrupted,
                initializedAgentIntegration,
                loadedAssemblies.ToArray ());

            inFlight = inFlight.With (EvaluationPhase.Represented, evaluation: evaluation);
            evaluations.Observers.OnNext (inFlight);

            agent.PublishEvaluation (evaluation);

            inFlight = inFlight.With (phase: EvaluationPhase.Completed);
            evaluations.Observers.OnNext (inFlight);
        }

        async Task<object> CoreRunAsync (
            Compilation compilation,
            CancellationToken cancellationToken)
        {
            if (compilation == null)
                throw new ArgumentNullException (nameof(compilation));

            if (compilation.References != null)
                AssemblyContext.AddRange (compilation.References);

            if (compilation?.ExecutableAssembly?.Content.PEImage == null)
                return null;

            var assembly = Assembly.Load (compilation.ExecutableAssembly.Content.PEImage);
            AssemblyContext.Add (assembly);

            var entryPointType = assembly.GetType (
                compilation.ExecutableAssembly.EntryPoint.TypeName, true);

            var entryPointMethod = entryPointType.GetMethod (
                compilation.ExecutableAssembly.EntryPoint.MethodName);

            var submission = (Func<object[], Task>)entryPointMethod.CreateDelegate (
                typeof (Func<object[], Task>));

            if (compilation.SubmissionNumber >= submissionStates.Length)
                Array.Resize (
                    ref submissionStates,
                    Math.Max (compilation.SubmissionNumber, submissionStates.Length * 2));

            cancellationToken.ThrowIfCancellationRequested ();

            return await ((Task<object>)submission (submissionStates)).ConfigureAwait (false);
        }

        void CapturedOutputWriter_SegmentCaptured (CapturedOutputSegment segment)
            => agent.MessageChannel.Push (segment);

        public struct Variable
        {
            public FieldInfo Field { get; set; }
            public object Value { get; set; }
            public Exception ValueReadException { get; set; }
        }

        public IReadOnlyCollection<Variable> GetGlobalVariables ()
        {
            var globalVars = new Dictionary<string, Variable> ();

            for (int i = 1; i < submissionStates.Length; i++) {
                var state = submissionStates [i];
                if (state == null)
                    continue;

                foreach (var field in state.GetType ().GetFields (
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)) {
                    // <host-object> and <Script> fields are readonly
                    if (field.IsInitOnly)
                        continue;

                    var variable = new Variable {
                        Field = field
                    };

                    try {
                        variable.Value = field.GetValue (state);
                    } catch (Exception e) {
                        variable.ValueReadException = e;
                    }

                    globalVars [field.Name] = variable;
                }
            }

            return globalVars.Values as IReadOnlyCollection<Variable>;
        }
    }
}