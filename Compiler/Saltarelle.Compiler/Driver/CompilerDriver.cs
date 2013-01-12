﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security;
using System.Security.Policy;
using System.Text;
using Castle.MicroKernel.Registration;
using Castle.Windsor;
using ICSharpCode.NRefactory;
using ICSharpCode.NRefactory.TypeSystem;
using System.Linq;
using Mono.CSharp;
using Mono.Cecil;
using Saltarelle.Compiler.Compiler;
using Saltarelle.Compiler.JSModel;
using Saltarelle.Compiler.JSModel.Expressions;
using Saltarelle.Compiler.JSModel.Minification;
using Saltarelle.Compiler.JSModel.Statements;
using Saltarelle.Compiler.JSModel.TypeSystem;
using Saltarelle.Compiler.MetadataImporter;
using Saltarelle.Compiler.OOPEmulator;
using Saltarelle.Compiler.Linker;
using Saltarelle.Compiler.RuntimeLibrary;
using ArrayType = ICSharpCode.NRefactory.TypeSystem.ArrayType;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;

namespace Saltarelle.Compiler.Driver {
	public class CompilerDriver {
		private readonly IErrorReporter _errorReporter;

		private static string GetAssemblyName(CompilerOptions options) {
			if (options.OutputAssemblyPath != null)
				return Path.GetFileNameWithoutExtension(options.OutputAssemblyPath);
			else if (options.SourceFiles.Count > 0)
				return Path.GetFileNameWithoutExtension(options.SourceFiles[0]);
			else
				return null;
		}

		private static string ResolveReference(string filename, IEnumerable<string> paths, IErrorReporter er) {
			// Code taken from mcs, so it should match that behavior.
			bool? hasExtension = null;
			foreach (var path in paths) {
				var file = Path.Combine(path, filename);

				if (!File.Exists(file)) {
					if (!hasExtension.HasValue)
						hasExtension = filename.EndsWith(".dll", StringComparison.Ordinal) || filename.EndsWith(".exe", StringComparison.Ordinal);

					if (hasExtension.Value)
						continue;

					file += ".dll";
					if (!File.Exists(file))
						continue;
				}

				return Path.GetFullPath(file);
			}
			er.Region = DomRegion.Empty;
			er.Message(7997, filename);
			return null;
		}

		private static CompilerSettings MapSettings(CompilerOptions options, string outputAssemblyPath, string outputDocFilePath, IErrorReporter er) {
			var allPaths = options.AdditionalLibPaths.Concat(new[] { Environment.CurrentDirectory }).ToList();

			var result = new CompilerSettings {
				Target                    = (options.HasEntryPoint ? Target.Exe : Target.Library),
				Platform                  = Platform.AnyCPU,
				TargetExt                 = (options.HasEntryPoint ? ".exe" : ".dll"),
				MainClass                 = options.EntryPointClass,
				VerifyClsCompliance       = false,
				Optimize                  = false,
				Version                   = LanguageVersion.V_5,
				EnhancedWarnings          = false,
				LoadDefaultReferences     = false,
				TabSize                   = 1,
				WarningsAreErrors         = options.TreatWarningsAsErrors,
				FatalCounter              = 100,
				WarningLevel              = options.WarningLevel,
				AssemblyReferences        = options.References.Where(r => r.Alias == null).Select(r => ResolveReference(r.Filename, allPaths, er)).ToList(),
				AssemblyReferencesAliases = options.References.Where(r => r.Alias != null).Select(r => Tuple.Create(r.Alias, ResolveReference(r.Filename, allPaths, er))).ToList(),
				Encoding                  = Encoding.UTF8,
				DocumentationFile         = !string.IsNullOrEmpty(options.DocumentationFile) ? outputDocFilePath : null,
				OutputFile                = outputAssemblyPath,
				AssemblyName              = GetAssemblyName(options),
				StdLib                    = false,
				StdLibRuntimeVersion      = RuntimeVersion.v4,
				StrongNameKeyContainer    = options.KeyContainer,
				StrongNameKeyFile         = options.KeyFile,
			};
			result.SourceFiles.AddRange(options.SourceFiles.Select((f, i) => new SourceFile(f, f, i + 1)));
			foreach (var c in options.DefineConstants)
				result.AddConditionalSymbol(c);
			foreach (var w in options.DisabledWarnings)
				result.SetIgnoreWarning(w);
			result.SetIgnoreWarning(660);	// 660 and 661: class defines operator == or operator != but does not override Equals / GetHashCode. These warnings don't really apply, since we have no Equals / GetHashCode methods to override.
			result.SetIgnoreWarning(661);
			foreach (var w in options.WarningsAsErrors)
				result.AddWarningAsError(w);
			foreach (var w in options.WarningsNotAsErrors)
				result.AddWarningOnly(w);

			if (result.AssemblyReferencesAliases.Count > 0) {	// NRefactory does currently not support reference aliases, this check will hopefully go away in the future.
				er.Region = DomRegion.Empty;
				er.Message(7998, "aliased reference");
			}

			return result;
		}

		private class ConvertingReportPrinter : ReportPrinter {
			private readonly IErrorReporter _errorReporter;

			public ConvertingReportPrinter(IErrorReporter errorReporter) {
				_errorReporter = errorReporter;
			}

			public override void Print(AbstractMessage msg, bool showFullPath) {
				base.Print(msg, showFullPath);
				_errorReporter.Region = new DomRegion(msg.Location.NameFullPath, msg.Location.Row, msg.Location.Column, msg.Location.Row, msg.Location.Column);
				_errorReporter.Message(msg.IsWarning ? MessageSeverity.Warning : MessageSeverity.Error, msg.Code, msg.Text.Replace("{", "{{").Replace("}", "}}"));
			}
		}

		private class SimpleSourceFile : ISourceFile {
			private readonly Encoding _encoding;
			private readonly string _filename;

			public SimpleSourceFile(string filename, Encoding encoding) {
				_filename = filename;
				_encoding = encoding;
			}

			public string Filename {
				get { return _filename; }
			}

			public TextReader Open() {
				return new StreamReader(Filename, _encoding);
			}
		}

		public class ErrorReporterWrapper : MarshalByRefObject, IErrorReporter {
			private readonly IErrorReporter _er;
			private readonly TextWriter _actualConsoleOut;

			public bool HasErrors { get; private set; }

			public ErrorReporterWrapper(IErrorReporter er, TextWriter actualConsoleOut) {
				_er = er;
				_actualConsoleOut = actualConsoleOut;
			}

			private void WithActualOut(Action a) {
				TextWriter old = Console.Out;
				try {
					Console.SetOut(_actualConsoleOut);
					a();
				}
				finally {
					Console.SetOut(old);
				}
			}

			public DomRegion Region {
				get { return _er.Region; }
				set { _er.Region = value; }
			}

			public void Message(MessageSeverity severity, int code, string message, params object[] args) {
				WithActualOut(() => _er.Message(severity, code, message, args));
				if (severity == MessageSeverity.Error)
					HasErrors = true;
			}

			public void InternalError(string text) {
				WithActualOut(() => _er.InternalError(text));
				HasErrors = true;
			}

			public void InternalError(Exception ex, string additionalText = null) {
				WithActualOut(() => _er.InternalError(ex, additionalText));
				HasErrors = true;
			}
		}

		public CompilerDriver(IErrorReporter errorReporter) {
			_errorReporter = errorReporter;
		}

		private class Executor : MarshalByRefObject {
			private bool IsEntryPointCandidate(IMethod m) {
				if (m.Name != "Main" || !m.IsStatic || m.DeclaringTypeDefinition.TypeParameterCount > 0 || m.TypeParameters.Count > 0)	// Must be a static, non-generic Main
					return false;
				if (!m.ReturnType.IsKnownType(KnownTypeCode.Void) && !m.ReturnType.IsKnownType(KnownTypeCode.Int32))	// Must return void or int.
					return false;
				if (m.Parameters.Count == 0)	// Can have 0 parameters.
					return true;
				if (m.Parameters.Count > 1)	// May not have more than 1 parameter.
					return false;
				if (m.Parameters[0].IsRef || m.Parameters[0].IsOut)	// The single parameter must not be ref or out.
					return false;

				var at = m.Parameters[0].Type as ArrayType;
				return at != null && at.Dimensions == 1 && at.ElementType.IsKnownType(KnownTypeCode.String);	// The single parameter must be a one-dimensional array of strings.
			}

			private static IEnumerable<Assembly> TopologicalSortPlugins(IEnumerable<Tuple<IAssembly, Assembly>> references) {
#warning TODO: Implement
				return references.Where(r => r.Item2 != null).Select(r => r.Item2).ToList();
			}

			private static void RegisterPlugins(IWindsorContainer container, IEnumerable<Tuple<IAssembly, Assembly>> references) {
				foreach (var asm in TopologicalSortPlugins(references)) {
					container.Register(AllTypes.FromAssembly(asm).BasedOn<IJSTypeSystemRewriter>().WithServices(typeof(IJSTypeSystemRewriter)));
				}
			}

			public bool Compile(CompilerOptions options, ErrorReporterWrapper er) {
				string intermediateAssemblyFile = Path.GetTempFileName(), intermediateDocFile = Path.GetTempFileName();
				try {
					// Compile the assembly
					var settings = MapSettings(options, intermediateAssemblyFile, intermediateDocFile, er);
					if (er.HasErrors)
						return false;

					if (!options.AlreadyCompiled) {
						// Compile the assembly
						var ctx = new CompilerContext(settings, new ConvertingReportPrinter(er));
						var d = new Mono.CSharp.Driver(ctx);
						d.Compile();
						if (er.HasErrors)
							return false;
					}

					var references = LoadReferences(settings.AssemblyReferences, er);
					if (references == null)
						return false;

					PreparedCompilation compilation = PreparedCompilation.CreateCompilation(options.SourceFiles.Select(f => new SimpleSourceFile(f, settings.Encoding)), references.Select(r => r.Item1), options.DefineConstants);

					IMethod entryPoint = FindEntryPoint(options, er, compilation);

					var container = new WindsorContainer();
					RegisterPlugins(container, references.Select(r => Tuple.Create(r.Item1.Resolve(compilation.Compilation.TypeResolveContext), r.Item2)).ToList());
					RegisterPlugins(container, new[] { Tuple.Create(default(IAssembly), Assembly.GetExecutingAssembly()) });

					// Compile the script
					container.Register(Component.For<IMetadataImporter, IScriptSharpMetadataImporter>().ImplementedBy<ScriptSharpMetadataImporter>(),
					                   Component.For<INamer>().ImplementedBy<DefaultNamer>(),
					                   Component.For<IErrorReporter>().Instance(er),
									   Component.For<ICompilation>().Instance(compilation.Compilation),
					                   Component.For<IRuntimeLibrary>().ImplementedBy<ScriptSharpRuntimeLibrary>(),
					                   Component.For<IOOPEmulator>().ImplementedBy<ScriptSharpOOPEmulator>(),
					                   Component.For<ICompiler>().ImplementedBy<Compiler.Compiler>(),
					                   Component.For<ILinker>().ImplementedBy<DefaultLinker>()
					                  );

					container.Resolve<IMetadataImporter>().Prepare(compilation.Compilation.GetAllTypeDefinitions(), options.MinimizeScript, compilation.Compilation.MainAssembly);
					var compiledTypes = container.Resolve<ICompiler>().Compile(compilation);

					foreach (var rewriter in container.ResolveAll<IJSTypeSystemRewriter>())
						compiledTypes = rewriter.Rewrite(compiledTypes);

					var js = container.Resolve<IOOPEmulator>().Process(compiledTypes, entryPoint);
					js = container.Resolve<ILinker>().Process(js, compilation.Compilation.MainAssembly);

					if (er.HasErrors)
						return false;

					string outputAssemblyPath = !string.IsNullOrEmpty(options.OutputAssemblyPath) ? options.OutputAssemblyPath : Path.ChangeExtension(options.SourceFiles[0], ".dll");
					string outputScriptPath   = !string.IsNullOrEmpty(options.OutputScriptPath)   ? options.OutputScriptPath   : Path.ChangeExtension(options.SourceFiles[0], ".js");

					if (!options.AlreadyCompiled) {
						try {
							File.Copy(intermediateAssemblyFile, outputAssemblyPath, true);
						}
						catch (IOException ex) {
							er.Region = DomRegion.Empty;
							er.Message(7950, ex.Message);
							return false;
						}
						if (!string.IsNullOrEmpty(options.DocumentationFile)) {
							try {
								File.Copy(intermediateDocFile, options.DocumentationFile, true);
							}
							catch (IOException ex) {
								er.Region = DomRegion.Empty;
								er.Message(7952, ex.Message);
								return false;
							}
						}
					}

					if (options.MinimizeScript) {
						js = ((JsBlockStatement)Minifier.Process(new JsBlockStatement(js))).Statements;
					}

					string script = string.Join("", js.Select(s => options.MinimizeScript ? OutputFormatter.FormatMinified(s) : OutputFormatter.Format(s)));
					try {
						File.WriteAllText(outputScriptPath, script, settings.Encoding);
					}
					catch (IOException ex) {
						er.Region = DomRegion.Empty;
						er.Message(7951, ex.Message);
						return false;
					}
					return true;
				}
				catch (Exception ex) {
					er.Region = DomRegion.Empty;
					er.InternalError(ex.ToString());
					return false;
				}
				finally {
					if (!options.AlreadyCompiled) {
						try { File.Delete(intermediateAssemblyFile); } catch {}
						try { File.Delete(intermediateDocFile); } catch {}
					}
				}
			}

			private IMethod FindEntryPoint(CompilerOptions options, ErrorReporterWrapper er, PreparedCompilation compilation) {
				if (options.HasEntryPoint) {
					List<IMethod> candidates;
					if (!string.IsNullOrEmpty(options.EntryPointClass)) {
						var t = compilation.Compilation.MainAssembly.GetTypeDefinition(new FullTypeName(options.EntryPointClass));
						if (t == null) {
							er.Region = DomRegion.Empty;
							er.Message(7950, "Could not find the entry point class " + options.EntryPointClass + ".");
							return null;
						}
						candidates = t.Methods.Where(IsEntryPointCandidate).ToList();
					}
					else {
						candidates =
							compilation.Compilation.MainAssembly.GetAllTypeDefinitions()
							           .SelectMany(t => t.Methods)
							           .Where(IsEntryPointCandidate)
							           .ToList();
					}
					if (candidates.Count != 1) {
						er.Region = DomRegion.Empty;
						er.Message(7950, "Could not find a unique entry point.");
						return null;
					}
					return candidates[0];
				}

				return null;
			}
		}

		/// <param name="options">Compile options</param>
		/// <param name="createAppDomain">If not null, a function that should return a new app domain which is setup correctly to perform a compilation.</param>
		public bool Compile(CompilerOptions options, Func<AppDomain> createAppDomain) {
			try {
				AppDomain ad = null;
				var actualOut = Console.Out;
				try {
					Console.SetOut(new StringWriter());	// I don't trust the third-party libs to not generate spurious random messages, so make sure that any of those messages are suppressed.

					var er = new ErrorReporterWrapper(_errorReporter, actualOut);

					Executor executor;
					if (createAppDomain != null) {
						ad = createAppDomain();
						executor = (Executor)ad.CreateInstanceAndUnwrap(typeof(Executor).Assembly.FullName, typeof(Executor).FullName);
					}
					else {
						executor = new Executor();
					}
					return executor.Compile(options, er);
				}
				finally {
					if (ad != null) {
						AppDomain.Unload(ad);
					}
					if (actualOut != null) {
						Console.SetOut(actualOut);
					}
				}
			}
			catch (Exception ex) {
				_errorReporter.Region = new DomRegion();
				_errorReporter.InternalError(ex);
				return false;
			}
		}

		private static Assembly LoadPlugin(AssemblyDefinition def) {
			foreach (var r in def.Modules.SelectMany(m => m.Resources).OfType<EmbeddedResource>()) {
				if (r.Name.EndsWith(".dll", StringComparison.InvariantCultureIgnoreCase)) {
					var data = r.GetResourceData();
					var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(data));

					var result = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == asm.Name.Name);
					if (result == null)
						result = Assembly.Load(data);
					return result;
				}
			}
			return null;
		}

		private static IList<Tuple<IUnresolvedAssembly, Assembly>> LoadReferences(IEnumerable<string> references, IErrorReporter er) {
			var loader = new CecilLoader { IncludeInternalMembers = true };
			var assemblies = references.Select(r => AssemblyDefinition.ReadAssembly(r)).ToList(); // Shouldn't result in errors because mcs would have caught it.

			var indirectReferences = (  from a in assemblies
			                            from m in a.Modules
			                            from r in m.AssemblyReferences
			                          select r.Name)
			                         .Distinct();

			var directReferences = from a in assemblies select a.Name.Name;

			var missingReferences = indirectReferences.Except(directReferences).ToList();

			if (missingReferences.Count > 0) {
				er.Region = DomRegion.Empty;
				foreach (var r in missingReferences)
					er.Message(7996, r);
				return null;
			}

			return assemblies.Select(asm => Tuple.Create(loader.LoadAssembly(asm), LoadPlugin(asm))).ToList();
		}
	}
}
