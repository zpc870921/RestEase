﻿#if SOURCE_GENERATOR
extern alias SourceGenerator;
using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.CodeAnalysis;
using RestEase;
using SourceGenerator::RestEase.SourceGenerator.Implementation;
using Microsoft.CodeAnalysis.CSharp;
using System.IO;
using System.Reflection;
using Moq;
using Xunit;
using RestEase.Implementation;
using System.Collections.Generic;
using RestEaseUnitTests.ImplementationFactoryTests.Helpers;
using Xunit.Abstractions;
#else
using System;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using RestEase;
using RestEase.Implementation;
using RestEaseUnitTests.ImplementationFactoryTests.Helpers;
using Xunit;
#endif

namespace RestEaseUnitTests.ImplementationFactoryTests
{
    public abstract class ImplementationFactoryTestsBase
    {
        private readonly Mock<IRequester> requester = new Mock<IRequester>(MockBehavior.Strict);
        private readonly ITestOutputHelper output;

        public ImplementationFactoryTestsBase()
        {
        }

        public ImplementationFactoryTestsBase(ITestOutputHelper output)
        {
            this.output = output;
        }

#if SOURCE_GENERATOR
        private static readonly Compilation executionCompilation;
        private static readonly Compilation diagnosticsCompilation;
        private readonly RoslynImplementationFactory implementationFactory = new RoslynImplementationFactory();

        static ImplementationFactoryTestsBase()
        {
            var thisAssembly = typeof(ImplementationFactoryTestsBase).Assembly;
            string dotNetDir = Path.GetDirectoryName(typeof(object).GetTypeInfo().Assembly.Location);

            // For actually executing code, we need to reference the compiled test assembly, so that the types we're seeing at the same
            // types as the ones the unit tests are seeing.
            // However, this doesn't give us source locations, which we need in order to test diagnostics. So for testing these, we include
            // the test project's files as source, rather than referencing the test project.

            var executionProject = new AdhocWorkspace()
                .AddProject("Execution", LanguageNames.CSharp)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddMetadataReference(MetadataReference.CreateFromFile(Path.Join(dotNetDir, "netstandard.dll")))
                .AddMetadataReference(MetadataReference.CreateFromFile(Path.Join(dotNetDir, "System.Runtime.dll")))
                .AddMetadataReference(MetadataReference.CreateFromFile(Path.Join(dotNetDir, "System.Net.Http.dll")))
                .AddMetadataReference(MetadataReference.CreateFromFile(typeof(RestClient).Assembly.Location))
                .AddMetadataReference(MetadataReference.CreateFromFile(thisAssembly.Location));
            executionCompilation = executionProject.GetCompilationAsync().Result;

            var diagnosticsProject = new AdhocWorkspace()
                .AddProject("Diagnostics", LanguageNames.CSharp)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddMetadataReference(MetadataReference.CreateFromFile(Path.Join(dotNetDir, "netstandard.dll")))
                .AddMetadataReference(MetadataReference.CreateFromFile(Path.Join(dotNetDir, "System.Runtime.dll")))
                .AddMetadataReference(MetadataReference.CreateFromFile(Path.Join(dotNetDir, "System.Net.Http.dll")))
                .AddMetadataReference(MetadataReference.CreateFromFile(typeof(RestClient).Assembly.Location));
            diagnosticsCompilation = diagnosticsProject.GetCompilationAsync().Result;

            var syntaxTrees = new List<SyntaxTree>();
            foreach (string resourceName in thisAssembly.GetManifestResourceNames())
            {
                if (resourceName.EndsWith(".cs"))
                {
                    using (var reader = new StreamReader(thisAssembly.GetManifestResourceStream(resourceName)))
                    {
                        syntaxTrees.Add(SyntaxFactory.ParseSyntaxTree(reader.ReadToEnd()));
                    }
                }
            }

            diagnosticsCompilation = diagnosticsCompilation.AddSyntaxTrees(syntaxTrees);
        }

        protected T CreateImplementation<T>()
        {
            var namedTypeSymbol = executionCompilation.GetTypeByMetadataName(typeof(T).FullName);
            var (sourceText, _) = this.implementationFactory.CreateImplementation(namedTypeSymbol);

            Assert.NotNull(sourceText);
            this.output?.WriteLine(sourceText.ToString());

            var syntaxTree = SyntaxFactory.ParseSyntaxTree(sourceText);
            var updatedCompilation = executionCompilation.AddSyntaxTrees(syntaxTree);
            using (var peStream = new MemoryStream())
            {
                var emitResult = updatedCompilation.Emit(peStream);
                Assert.True(emitResult.Success, "Emit failed:\r\n\r\n" + string.Join("\r\n", emitResult.Diagnostics.Select(x => x.ToString())));
                var assembly = Assembly.Load(peStream.GetBuffer());
                var implementationType = assembly.GetCustomAttributes<RestEaseInterfaceImplementationAttribute>()
                    .FirstOrDefault(x => x.InterfaceType == typeof(T))?.ImplementationType;
                Assert.NotNull(implementationType);
                return (T)Activator.CreateInstance(implementationType, this.requester.Object);
            }
        }

        protected void VerifyDiagnostics<T>(params DiagnosticResult[] expected)
        {
            var namedTypeSymbol = diagnosticsCompilation.GetTypeByMetadataName(typeof(T).FullName);
            var (_, diagnostics) = this.implementationFactory.CreateImplementation(namedTypeSymbol);
            DiagnosticVerifier.VerifyDiagnostics(diagnostics, expected);
        }

#else
        private readonly EmitImplementationFactory factory = EmitImplementationFactory.Instance;

        protected T CreateImplementation<T>()
        {
            return this.factory.CreateImplementation<T>(this.requester.Object);
        }

        protected void VerifyDiagnostics<T>(params DiagnosticResult[] expected)
        {
            if (expected.Length == 0)
            {
                // Check doesn't throw
                this.CreateImplementation<T>();
            }
            else
            {
                var ex = Assert.Throws<ImplementationCreationException>(() => this.CreateImplementation<T>());
                if (!expected.Any(x => x.Code == ex.Code))
                {
                    Assert.Equal(expected[0].Code, ex.Code);
                }
            }
        }
#endif

        protected static DiagnosticResult Diagnostic(DiagnosticCode code, string squiggledText)
        {
            return new DiagnosticResult(code, squiggledText);
        }

        protected IRequestInfo Request<T>(T implementation, Func<T, Task> method)
        {
            IRequestInfo requestInfo = null;
            var expectedResponse = Task.FromResult(false);

            this.requester.Setup(x => x.RequestVoidAsync(It.IsAny<IRequestInfo>()))
                .Callback((IRequestInfo r) => requestInfo = r)
                .Returns(expectedResponse)
                .Verifiable();

            var response = method(implementation);

            Assert.Equal(expectedResponse, response);
            this.requester.Verify();

            return requestInfo;
        }


        protected IRequestInfo Request<T>(Func<T, Task> method)
        {
            return this.Request(this.CreateImplementation<T>(), method);
        }
    }
}
