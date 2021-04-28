// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Bicep.Core.Diagnostics;
using Bicep.Core.UnitTests.Assertions;
using Bicep.Core.UnitTests.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Xml.Linq;

namespace Bicep.Core.IntegrationTests
{
    [TestClass]
    public class LoopInvariantTests
    {
        [TestMethod]
        public void UsingLoopItemVariableInResourceNamePropertyShouldProduceNoWarnings()
        {
            const string text = @"
resource foos 'Microsoft.Network/dnsZones@2018-05-01' = [for (item, i) in []: {
  name: 's2${item.name}'
  location:'s'
}]

resource ds2 'Microsoft.Resources/deploymentScripts@2020-10-01' = [for script in []: {
  kind: 'AzureCLI'
  name: script.name
  properties: {
    azCliVersion: 's'
    retentionInterval: 's'
  }
  location: resourceGroup().location
}]";

            var result = CompilationHelper.Compile(text);
            result.Should().NotHaveDiagnostics();
        }

        [TestMethod]
        public void UsingLoopIndexVariableInResourceNamePropertyShouldProduceNoWarnings()
        {
            const string text = @"
resource foos 'Microsoft.Network/dnsZones@2018-05-01' = [for (item, i) in []: {
  name: 's2${i}'
  location:'s'
}]

resource ds2 'Microsoft.Resources/deploymentScripts@2020-10-01' = [for (script, i) in []: {
  kind: 'AzureCLI'
  name: string(i)
  properties: {
    azCliVersion: 's'
    retentionInterval: 's'
  }
  location: resourceGroup().location
}]
";
            var result = CompilationHelper.Compile(text);
            result.Should().NotHaveDiagnostics();
        }

        [TestMethod]
        public void UsingLoopItemVariableInModuleNamePropertyShouldProduceNoWarnings()
        {
            const string text = @"
module mod 'mod.bicep' = [for a in []: {
  name: 's${a}'
  scope: resourceGroup()
  params: {
    foo: 's'
  }
}]
";
            var result = CompilationHelper.Compile(
                ("main.bicep", text),
                ("mod.bicep", "param foo string"));

            result.Should().NotHaveDiagnostics();
        }

        [TestMethod]
        public void UsingLoopIndexVariableInModuleNamePropertyShouldProduceNoWarnings()
        {
            const string text = @"
module mod 'mod.bicep' = [for (a,i) in []: {
  name: 's${i}'
  scope: resourceGroup()
  params: {
    foo: 's'
  }
}]
";
            var result = CompilationHelper.Compile(
                ("main.bicep", text),
                ("mod.bicep", "param foo string"));

            result.Should().NotHaveDiagnostics();
        }

        [TestMethod]
        public void ResourceDeclarationWithoutExpectedVariantPropertiesShouldNotProduceTheWarning()
        {
            const string text = @"
resource foos 'Microsoft.Network/dnsZones@2018-05-01' = [for (item, i) in []: {
  location:'s'
}]";
            var result = CompilationHelper.Compile(text);
            result.Should().HaveDiagnostics(new[]
{
                ("BCP035", DiagnosticLevel.Error, "The specified \"resource\" declaration is missing the following required properties: \"name\".")
            });
        }

        [TestMethod]
        public void ModuleDeclarationWithoutExpectedVariantPropertiesShouldNotProduceTheWarning()
        {
            const string text = @"
module mod 'mod.bicep' = [for a in []: {
  params: {
    foo: 's'
  }
}]";
            //bicep(BCP035)
            var result = CompilationHelper.Compile(
                ("main.bicep", text),
                ("mod.bicep", "param foo string"));
            result.Should().HaveDiagnostics(new[]
{
                ("BCP035", DiagnosticLevel.Error, "The specified \"module\" declaration is missing the following required properties: \"name\".")
            });
        }
    }
}
