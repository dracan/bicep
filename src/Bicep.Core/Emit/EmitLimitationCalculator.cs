// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Azure.Deployments.Core.Definitions.Identifiers;
using Bicep.Core.DataFlow;
using Bicep.Core.Diagnostics;
using Bicep.Core.Extensions;
using Bicep.Core.Semantics;
using Bicep.Core.Syntax;
using Bicep.Core.TypeSystem;
using Bicep.Core.Utils;

namespace Bicep.Core.Emit
{
    public static class EmitLimitationCalculator
    {
        // the top-level properties whose values are expected to depend on the item or index variable in a resource loop
        private static readonly ImmutableArray<string> ExpectedVariantResourceProperties = new[]
        {
            LanguageConstants.ResourceNamePropertyName,
            LanguageConstants.ResourceParentPropertyName,
            LanguageConstants.ResourceScopePropertyName
        }.ToImmutableArray();

        private static readonly ImmutableArray<string> ExpectedVariantModuleProperties = new[]
        {
            LanguageConstants.ResourceNamePropertyName,
            LanguageConstants.ResourceScopePropertyName
        }.ToImmutableArray();

        public static EmitLimitationInfo Calculate(SemanticModel model)
        {
            var diagnosticWriter = ToListDiagnosticWriter.Create();

            var moduleScopeData = ScopeHelper.GetModuleScopeInfo(model, diagnosticWriter);
            var resourceScopeData = ScopeHelper.GetResoureScopeInfo(model, diagnosticWriter);
            DeployTimeConstantVisitor.ValidateDeployTimeConstants(model, diagnosticWriter);

            ForSyntaxValidatorVisitor.Validate(model, diagnosticWriter);
            DetectDuplicateNames(model, diagnosticWriter, resourceScopeData, moduleScopeData);
            DetectIncorrectlyFormattedNames(model, diagnosticWriter);
            DetectUnexpectedResourceLoopInvariantProperties(model, diagnosticWriter);
            DetectUnexpectedModuleLoopInvariantProperties(model, diagnosticWriter);

            return new EmitLimitationInfo(diagnosticWriter.GetDiagnostics(), moduleScopeData, resourceScopeData);
        }

        private static void DetectDuplicateNames(SemanticModel semanticModel, IDiagnosticWriter diagnosticWriter, ImmutableDictionary<ResourceSymbol, ScopeHelper.ScopeData> resourceScopeData, ImmutableDictionary<ModuleSymbol, ScopeHelper.ScopeData> moduleScopeData)
        {
            // This method only checks, if in one deployment we do not have 2 or more resources with this same name in one deployment to avoid template validation error
            // This will not check resource constraints such as necessity of having unique virtual network names within resource group

            var duplicateResources = GetResourceDefinitions(semanticModel, resourceScopeData)
                .GroupBy(x => x, ResourceDefinition.EqualityComparer)
                .Where(group => group.Count() > 1);

            foreach (var duplicatedResourceGroup in duplicateResources)
            {
                var duplicatedResourceNames = duplicatedResourceGroup.Select(x => x.ResourceName).ToArray();
                foreach (var duplicatedResource in duplicatedResourceGroup)
                {
                    diagnosticWriter.Write(duplicatedResource.ResourceNamePropertyValue, x => x.ResourceMultipleDeclarations(duplicatedResourceNames));
                }
            }

            var duplicateModules = GetModuleDefinitions(semanticModel, moduleScopeData)
                .GroupBy(x => x, ModuleDefinition.EqualityComparer)
                .Where(group => group.Count() > 1);

            foreach (var duplicatedModuleGroup in duplicateModules)
            {
                var duplicatedModuleNames = duplicatedModuleGroup.Select(x => x.ModuleName).ToArray();
                foreach (var duplicatedModule in duplicatedModuleGroup)
                {
                    diagnosticWriter.Write(duplicatedModule.ModulePropertyNameValue, x => x.ModuleMultipleDeclarations(duplicatedModuleNames));
                }
            }
        }

        private static IEnumerable<ModuleDefinition> GetModuleDefinitions(SemanticModel semanticModel, ImmutableDictionary<ModuleSymbol, ScopeHelper.ScopeData> moduleScopeData)
        {
            foreach (var module in semanticModel.Root.ModuleDeclarations)
            {
                if (!moduleScopeData.TryGetValue(module, out var scopeData))
                {
                    //module has invalid scope provided, ignoring from duplicate check
                    continue;
                }
                if (module.SafeGetBodyPropertyValue(LanguageConstants.ResourceNamePropertyName) is not StringSyntax propertyNameValue)
                {
                    //currently limiting check to 'name' property values that are strings, although it can be references or other syntaxes
                    continue;
                }

                var propertyScopeValue = (module.SafeGetBodyPropertyValue(LanguageConstants.ResourceScopePropertyName) as FunctionCallSyntax)?.Arguments.Select(x => x.Expression as StringSyntax).ToImmutableArray();

                yield return new ModuleDefinition(module.Name, scopeData.RequestedScope, propertyScopeValue, propertyNameValue);
            }
        }

        private static IEnumerable<ResourceDefinition> GetResourceDefinitions(SemanticModel semanticModel, ImmutableDictionary<ResourceSymbol, ScopeHelper.ScopeData> resourceScopeData)
        {
            foreach (var resource in semanticModel.Root.GetAllResourceDeclarations())
            {
                if (resource.DeclaringResource.IsExistingResource())
                {
                    // 'existing' resources are not being deployed so duplicates are allowed
                    continue;
                }

                // Determine the scope - this is either something like a resource group/subscription or another resource
                ResourceSymbol? scopeSymbol;
                if (resourceScopeData.TryGetValue(resource, out var scopeData) && scopeData.ResourceScopeSymbol is ResourceSymbol)
                {
                    scopeSymbol = scopeData.ResourceScopeSymbol;
                }
                else
                {
                    scopeSymbol = semanticModel.ResourceAncestors.GetAncestors(resource).LastOrDefault()?.Resource;
                }

                var resourceType = resource.Type switch
                {
                    ResourceType singleType => singleType,
                    ArrayType { Item: ResourceType itemType } => itemType,
                    _ => null
                };

                if (resourceType is null || resource.SafeGetBodyPropertyValue(LanguageConstants.ResourceNamePropertyName) is not StringSyntax namePropertyValue)
                {
                    //currently limiting check to 'name' property values that are strings, although it can be references or other syntaxes
                    continue;
                }

                yield return new ResourceDefinition(resource.Name, scopeSymbol, resourceType.TypeReference.FullyQualifiedType, namePropertyValue);
            }
        }

        public static void DetectIncorrectlyFormattedNames(SemanticModel semanticModel, IDiagnosticWriter diagnosticWriter)
        {
            foreach (var resource in semanticModel.Root.GetAllResourceDeclarations())
            {
                var resourceType = semanticModel.GetTypeInfo(resource.DeclaringSyntax) switch
                {
                    ResourceType singletype => singletype,
                    ArrayType { Item: ResourceType itemType } => itemType,
                    _ => null
                };

                if(resourceType is null)
                {
                    continue;
                }

                var resourceName = resource.SafeGetBodyPropertyValue(LanguageConstants.ResourceNamePropertyName);
                if (resourceName is not StringSyntax resourceNameString)
                {
                    // not easy to do analysis if it's not a string!
                    continue;
                }

                var ancestors = semanticModel.ResourceAncestors.GetAncestors(resource);
                if (ancestors.Any())
                {
                    // try to detect cases where someone has applied top-level resource declaration naming to a nested/parent resource
                    // e.g. '{parent.name}/child' or 'parent/child'
                    if (resourceNameString.SegmentValues.Any(v => v.IndexOf('/') > -1))
                    {
                        diagnosticWriter.Write(resourceName, x => x.ChildResourceNameContainsQualifiers());
                    }
                }
                else
                {
                    if (resourceNameString.TryGetLiteralValue() is {} typeString)
                    {
                        // try to detect cases where someone has applied nested/parent resource declaration naming to a top-level resource - e.g. 'child'.
                        // this unfortunately limits us to only validating uninterpolated strings, as we can't do analysis on the number of '/' characters
                        // being pulled in from variables.
                        
                        var slashCount = typeString.Count(x => x == '/');
                        var expectedSlashCount = resourceType.TypeReference.Types.Length - 1;
                        if (slashCount != expectedSlashCount)
                        {
                            diagnosticWriter.Write(resourceName, x => x.TopLevelChildResourceNameMissingQualifiers(expectedSlashCount));
                        }
                    }
                }
            }
        }

        public static void DetectUnexpectedResourceLoopInvariantProperties(SemanticModel semanticModel, IDiagnosticWriter diagnosticWriter)
        {
            foreach (var resource in semanticModel.Root.GetAllResourceDeclarations())
            {
                if(resource.DeclaringResource.IsExistingResource())
                {
                    // existing resource syntax doesn't result in deployment but instead is
                    // used as a convenient way of constructing a symbolic name
                    // as such, invariant names aren't really a concern here
                    // (and may even be desirable)
                    continue;
                }

                if(resource.DeclaringResource.Value is not ForSyntax @for || @for.ItemVariable is not { } itemVariable)
                {
                    // invariant identifiers are only a concern for resource loops
                    // this is not a resource loop OR the item variable is malformed
                    continue;
                }

                if(resource.TryGetBodyObjectType() is not { } bodyType)
                {
                    // unable to get the object type
                    continue;
                }

                // collect the values of the expected variant properties
                // provided that they exist on the type
                var expectedVariantPropertiesForType = ExpectedVariantResourceProperties.Where(propertyName => bodyType.Properties.ContainsKey(propertyName));
                var propertyValues = expectedVariantPropertiesForType
                    .Select(propertyName => resource.SafeGetBodyPropertyValue(propertyName))
                    .Where(value => value is not null)
                    .Select(value => value!)
                    .ToImmutableArray();

                if (!propertyValues.Any())
                {
                    // we should not add a warning before they filled in at least some of the properties we're interested in
                    continue;
                }

                var indexVariable = @for.IndexVariable;
                if (propertyValues.Any(pair => IsInvariant(semanticModel, itemVariable, indexVariable, pair)))
                {
                    diagnosticWriter.Write(DiagnosticBuilder.ForPosition(resource.NameSyntax).ForExpressionContainsLoopInvariants(itemVariable, indexVariable, expectedVariantPropertiesForType));
                }
            }
        }

        public static void DetectUnexpectedModuleLoopInvariantProperties(SemanticModel semanticModel, IDiagnosticWriter diagnosticWriter)
        {
            foreach (var module in semanticModel.Root.ModuleDeclarations)
            {
                if (module.DeclaringModule.Value is not ForSyntax @for || @for.ItemVariable is not { } itemVariable)
                {
                    // invariant identifiers are only a concern for module loops
                    // this is not a module loop OR the item variable is malformed
                    continue;
                }

                if (module.TryGetBodyObjectType() is not { } bodyType)
                {
                    // unable to get the object type
                    continue;
                }

                // collect the values of the expected variant properties
                // provided that they exist on the type
                var expectedVariantPropertiesForType = ExpectedVariantModuleProperties.Where(propertyName => bodyType.Properties.ContainsKey(propertyName));
                var propertyValues = expectedVariantPropertiesForType
                    .Select(propertyName => module.SafeGetBodyPropertyValue(propertyName))
                    .Where(value => value is not null)
                    .Select(value => value!)
                    .ToImmutableArray();

                if (!propertyValues.Any())
                {
                    // we should not add a warning before they filled in at least some of the expected variant properties
                    continue;
                }

                var indexVariable = @for.IndexVariable;
                if (propertyValues.All(pair => IsInvariant(semanticModel, itemVariable, indexVariable, pair)))
                {
                    // all the expected variant properties are loop invariant
                    diagnosticWriter.Write(DiagnosticBuilder.ForPosition(module.NameSyntax).ForExpressionContainsLoopInvariants(itemVariable, indexVariable, expectedVariantPropertiesForType));
                }
            }
        }

        private static bool IsInvariant(SemanticModel semanticModel, LocalVariableSyntax itemVariable, LocalVariableSyntax? indexVariable, SyntaxBase expression)
        {
            var referencedLocals = LocalSymbolDependencyVisitor.GetLocalSymbolDependencies(semanticModel, expression);

            bool IsLocalInvariant(LocalVariableSyntax? local) =>
                local is { } &&
                semanticModel.GetSymbolInfo(local) is LocalVariableSymbol localSymbol &&
                !referencedLocals.Contains(localSymbol);

            return indexVariable is null
                ? IsLocalInvariant(itemVariable)
                : IsLocalInvariant(itemVariable) && IsLocalInvariant(indexVariable);
        }
    }
}
