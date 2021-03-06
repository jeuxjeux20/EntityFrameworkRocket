using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
namespace EntityFrameworkRocket.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class UnsupportedLinqAnalyzer : DiagnosticAnalyzer
    {
        internal const string DiagnosticId = "EFX0001";
        private const string Title = "Unsupported LINQ expression";
        private const string MessageFormat = "This version of Entity Framework does not support {0}, this expression may throw an exception at runtime.";

        private const string Category = "LINQ";

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat,
            Category, DiagnosticSeverity.Warning, true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.RegisterSyntaxNodeAction(AnalyzeNode, SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeNode(SyntaxNodeAnalysisContext context)
        {
            var query = context.GetLinqQuery();
            if (query is null) return;
            var sourceCollectionType = context.SemanticModel.GetTypeInfo(query.SourceCollection).Type;
            // We ensure that it comes from a DbSet to avoid conflict with other libraries that may use IQueryable.
            if (sourceCollectionType?.Name != EntityFrameworkConstants.DbSet) return;
            foreach (var step in query.Steps)
            {
                if (step.Symbol.ReceiverType.Name != nameof(IQueryable)) return; // If it has been used as en IEnumerable, it is executed client side.
                switch (step.Name)
                {
                    case nameof(Enumerable.Select):
                    case nameof(Enumerable.Where):
                    case nameof(Enumerable.SelectMany):
                    case nameof(Enumerable.SkipWhile):
                    case nameof(Enumerable.TakeWhile):
                        // Checks if the Func<T, int> has been used.
                        if (step.Symbol.Parameters.FirstOrDefault()?.Type is INamedTypeSymbol func)
                        {
                            var funcValue = func.GetUnderlyingExpressionType();
                            // It is maybe using a select statement having a return type of int. Check if there is more than 2 args.
                            if (funcValue.TypeArguments.Length <= 2 || funcValue.TypeArguments[1].Name != nameof(Int32)) return;
                            // Take i from (x, i)
                            var locationTarget =
                                (step.Invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression as
                                    ParenthesizedLambdaExpressionSyntax)?.ParameterList.Parameters.ElementAtOrDefault(1);
                            if (locationTarget is null) return; // no second parameter used? then it's fine. but weird.
                            var diagnostic = Diagnostic.Create(Rule, locationTarget.GetLocation(),
                                $"using the second index parameter in {step.Name}");
                            context.ReportDiagnostic(diagnostic);
                        }
                        break;
                    default:
                        continue;
                }
            }
        }
    }
}
