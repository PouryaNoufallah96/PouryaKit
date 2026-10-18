using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql.Internals;
using Wolverine.Runtime.Handlers;

namespace PouryaKit.Messaging.Wolverine.Sagas;

// Each declared saga's chain opens its storage through the Kit, first in its middleware, in place of the storage frame
// Wolverine's transactional policy may already have added and so that none is added later
// (LightweightSagaPersistenceFrameProvider.ApplyTransactionSupport returns when the chain has one); the Kit's guard runs
// next, inside the step's transaction and before Wolverine loads the saga. The frame is Wolverine's own, since the marker
// interface its provider checks is internal; only the helper it calls is the Kit's (SagaSchemaCodegen.HelperType). A
// policy runs at every start, under static code generation too, and its frames are what codegen write generates.
internal sealed class SagaStepPolicy(SagaDeclarations sagas) : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.OfType<SagaChain>().Where(chain => sagas.Declares(chain.SagaType)))
        {
            chain.Middleware.RemoveAll(frame => frame is EnrollAndFetchSagaStorageFrame);
            var codegen = new SagaSchemaCodegen(typeof(DatabaseSagaSchema<,>).MakeGenericType(chain.SagaType, typeof(string)), typeof(SagaStorage));
            chain.Middleware.Insert(0, new EnrollAndFetchSagaStorageFrame(typeof(string), chain.SagaType, codegen));
            // An input whose saga is missing starts one, or reaches its NotFound step; any other is the Kit's refusal.
            var startsSaga = chain.Handlers.Any(handler =>
                handler.Method.Name is SagaChain.Start or SagaChain.Starts or SagaChain.StartOrHandle or SagaChain.StartsOrHandles or SagaChain.NotFound
            );
            chain.Middleware.Insert(1, new EnterStepFrame(chain.SagaType, startsSaga));
        }
    }

    // if (SagaStorage.Enter(storage, startsSaga)) return; leaving before the commit disposes the storage, which rolls the
    // step's transaction back: no state, no emission, and the envelope is marked handled.
    private sealed class EnterStepFrame(Type sagaType, bool startsSaga) : SyncFrame
    {
        private Variable? _storage;

        public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
        {
            _storage = chain.FindVariable(typeof(ISagaStorage<,>).MakeGenericType(typeof(string), sagaType));
            yield return _storage;
        }

        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            writer.Write(
                $"if ({typeof(SagaStorage).FullNameInCode()}.{nameof(SagaStorage.Enter)}({_storage!.Usage}, {(startsSaga ? "true" : "false")})) return;"
            );
            Next?.GenerateCode(method, writer);
        }
    }
}
