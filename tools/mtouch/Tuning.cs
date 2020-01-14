using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.XPath;
using System.Text;

using Mono.Linker;
using Mono.Linker.Steps;

using Mono.Cecil;
using Mono.Tuner;

using Xamarin.Bundler;
using Xamarin.Linker;
using Xamarin.Linker.Steps;
using Xamarin.Tuner;

namespace MonoTouch.Tuner {

	public class LinkerOptions {
		public IEnumerable<AssemblyDefinition> MainAssemblies { get; set; }
		public string OutputDirectory { get; set; }
		public LinkMode LinkMode { get; set; }
		public AssemblyResolver Resolver { get; set; }
		public IEnumerable<string> SkippedAssemblies { get; set; }
		public I18nAssemblies I18nAssemblies { get; set; }
		public bool LinkSymbols { get; set; }
		public bool LinkAway { get; set; }
		public bool Device { get; set; }
		public IList<string> ExtraDefinitions { get; set; }
		public bool DebugBuild { get; set; }
		public bool IsDualBuild { get; set; }
		public bool DumpDependencies { get; set; }
		internal PInvokeWrapperGenerator MarshalNativeExceptionsState { get; set; }
		internal RuntimeOptions RuntimeOptions { get; set; }

		public MonoTouchLinkContext LinkContext { get; set; }
		public Target Target { get; set; }
		public Application Application { get { return Target.App; } }

		public static I18nAssemblies ParseI18nAssemblies (string i18n)
		{
			var assemblies = I18nAssemblies.None;

			foreach (var part in i18n.Split (',')) {
				var assembly = part.Trim ();
				if (string.IsNullOrEmpty (assembly))
					continue;

				try {
					assemblies |= (I18nAssemblies) Enum.Parse (typeof (I18nAssemblies), assembly, true);
				} catch {
					throw new FormatException ("Unknown value for i18n: " + assembly);
				}
			}

			return assemblies;
		}
	}

	static partial class Linker {

		public static void Process (LinkerOptions options, out MonoTouchLinkContext context, out List<AssemblyDefinition> assemblies)
		{
			var pipeline = CreatePipeline (options);

			foreach (var ad in options.MainAssemblies)
				pipeline.PrependStep (new MobileResolveMainAssemblyStep (ad, options.Application.Embeddinator));

			context = CreateLinkContext (options, pipeline);
			context.Resolver.AddSearchDirectory (options.OutputDirectory);

			if (options.DumpDependencies) {
				var prepareDependenciesDump = context.Annotations.GetType ().GetMethod ("PrepareDependenciesDump", new Type[1] { typeof (string) });
				if (prepareDependenciesDump != null)
					prepareDependenciesDump.Invoke (context.Annotations, new object[1] { string.Format ("{0}{1}linker-dependencies.xml.gz", options.OutputDirectory, Path.DirectorySeparatorChar) });
			}

			Process (pipeline, context);

			assemblies = ListAssemblies (context);
		}

		static MonoTouchLinkContext CreateLinkContext (LinkerOptions options, Pipeline pipeline)
		{
			var context = new MonoTouchLinkContext (pipeline, options.Resolver);
			context.CoreAction = options.LinkMode == LinkMode.None ? AssemblyAction.Copy : AssemblyAction.Link;
			context.LinkSymbols = options.LinkSymbols;
			context.OutputDirectory = options.OutputDirectory;
			context.SetParameter ("debug-build", options.DebugBuild.ToString ());
			context.StaticRegistrar = options.Target.StaticRegistrar;
			context.Target = options.Target;
			context.ExcludedFeatures = new [] { "remoting", "com", "sre" };
			context.SymbolWriterProvider = new CustomSymbolWriterProvider ();
			if (options.Application.Optimizations.StaticConstructorBeforeFieldInit == false)
				context.DisabledOptimizations |= CodeOptimizations.BeforeFieldInit;
			options.LinkContext = context;

			return context;
		}
		
		static SubStepDispatcher GetSubSteps (LinkerOptions options)
		{
			SubStepDispatcher sub = new SubStepDispatcher ();
			sub.Add (new ApplyPreserveAttribute ());
			sub.Add (new CoreRemoveSecurity ());
			sub.Add (new OptimizeGeneratedCodeSubStep (options));
			sub.Add (new RemoveUserResourcesSubStep (options));
			if (options.Application.Optimizations.CustomAttributesRemoval == true)
				sub.Add (new RemoveAttributes ());
			// http://bugzilla.xamarin.com/show_bug.cgi?id=1408
			if (options.LinkAway)
				sub.Add (new RemoveCode (options));
			sub.Add (new MarkNSObjects ());
			sub.Add (new PreserveSoapHttpClients ());
			sub.Add (new CoreHttpMessageHandler (options));
			sub.Add (new InlinerSubStep ());
			sub.Add (new PreserveSmartEnumConversionsSubStep ());
			return sub;
		}

		static SubStepDispatcher GetPostLinkOptimizations (LinkerOptions options)
		{
			SubStepDispatcher sub = new SubStepDispatcher ();
			sub.Add (new MetadataReducerSubStep ());
			if (options.Application.Optimizations.SealAndDevirtualize == true)
				sub.Add (new SealerSubStep ());
			return sub;
		}

		static Pipeline CreatePipeline (LinkerOptions options)
		{
			var pipeline = new Pipeline ();

			pipeline.Append (new LoadReferencesStep ());

			if (options.I18nAssemblies != I18nAssemblies.None)
				pipeline.Append (new LoadI18nAssemblies (options.I18nAssemblies));

			// that must be done early since the XML files can "add" new assemblies [#15878]
			// and some of the assemblies might be (directly or referenced) SDK assemblies
			foreach (string definition in options.ExtraDefinitions)
				pipeline.Append (GetResolveStep (definition));

			if (options.LinkMode != LinkMode.None)
				pipeline.Append (new BlacklistStep ());

			pipeline.Append (new CustomizeIOSActions (options.LinkMode, options.SkippedAssemblies));

			// We need to store the Field attribute in annotations, since it may end up removed.
			pipeline.Append (new ProcessExportedFields ());

			// We remove incompatible bitcode from all assemblies, not only the linked assemblies.
			RemoveBitcodeIncompatibleCodeStep remove_incompatible_bitcode = null;
			if (options.Application.Optimizations.RemoveUnsupportedILForBitcode == true)
				remove_incompatible_bitcode = new RemoveBitcodeIncompatibleCodeStep (options);

			if (options.LinkMode != LinkMode.None) {
				pipeline.Append (new CoreTypeMapStep ());

				pipeline.Append (GetSubSteps (options));

				pipeline.Append (new PreserveCode (options));

				pipeline.Append (new RemoveResources (options.I18nAssemblies)); // remove collation tables

				pipeline.Append (new MonoTouchMarkStep ());

				// We only want to remove from methods that aren't already linked away, so we need to do this
				// after the mark step. If we remove any incompatible code, we'll mark
				// the NotSupportedException constructor we need, so we need to do this before the sweep step.
				if (remove_incompatible_bitcode != null)
					pipeline.AppendStep (new SubStepDispatcher { remove_incompatible_bitcode });
				
				pipeline.Append (new MonoTouchSweepStep (options));
				pipeline.Append (new CleanStep ());

				if (!options.DebugBuild)
					pipeline.AppendStep (GetPostLinkOptimizations (options));

				pipeline.Append (new FixModuleFlags ());
			} else {
				SubStepDispatcher sub = new SubStepDispatcher () {
					new RemoveUserResourcesSubStep (options),
				};
				if (remove_incompatible_bitcode != null)
					sub.Add (remove_incompatible_bitcode);
				pipeline.Append (sub);
			}

			pipeline.Append (new ListExportedSymbols (options.MarshalNativeExceptionsState));

			pipeline.Append (new OutputStep ());

			return pipeline;
		}

		static void Append (this Pipeline self, IStep step)
		{
			self.AppendStep (step);
			if (Driver.WatchLevel > 0)
				self.AppendStep (new TimeStampStep (step));
		}

		static List<AssemblyDefinition> ListAssemblies (MonoTouchLinkContext context)
		{
			var list = new List<AssemblyDefinition> ();
			foreach (var assembly in context.GetAssemblies ()) {
				if (context.Annotations.GetAction (assembly) == AssemblyAction.Delete)
					continue;

				list.Add (assembly);
			}

			return list;
		}

		static ResolveFromXmlStep GetResolveStep (string filename)
		{
			filename = Path.GetFullPath (filename);

			if (!File.Exists (filename))
				throw new MonoTouchException (2004, true, mtouch.mtouchErrors.MT2004, filename);

			try {
				using (StreamReader sr = new StreamReader (filename)) {
					return new ResolveFromXmlStep (new XPathDocument (new StringReader (sr.ReadToEnd ())));
				}
			}
			catch (Exception e) {
				throw new MonoTouchException (2005, true, e, mtouch.mtouchErrors.MT2005, filename);
			}
		}
	}

	public class TimeStampStep : IStep {
		string message;

		public TimeStampStep (IStep step)
		{
			message = step.ToString ();
		}

		public void Process (LinkContext context)
		{
			Driver.Watch (message, 2);
		}
	}

	public class MonoTouchLinkContext : DerivedLinkContext {
		public MonoTouchLinkContext (Pipeline pipeline, AssemblyResolver resolver)
			: base (pipeline, resolver)
		{
		}
	}

	public class CustomizeIOSActions : CustomizeActions
	{
		LinkMode link_mode;

		public CustomizeIOSActions (LinkMode mode, IEnumerable<string> skipped_assemblies)
			: base (mode == LinkMode.SDKOnly, skipped_assemblies)
		{
			link_mode = mode;
		}

		protected override bool IsLinked (AssemblyDefinition assembly)
		{
			if (link_mode == LinkMode.None)
				return false;
			
			return base.IsLinked (assembly);
		}

		protected override void ProcessAssembly (AssemblyDefinition assembly)
		{
			if (link_mode == LinkMode.None) {
				Annotations.SetAction (assembly, AssemblyAction.Copy);
				return;
			}

			try {
				base.ProcessAssembly (assembly);
			}
			catch (Exception e) {
				throw new MonoTouchException (2103, true, e, mtouch.mtouchErrors.MT2103, assembly.FullName, e);
			}
		}
	}
}
