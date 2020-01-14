using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Text;
using Xamarin;
using Xamarin.Utils;
using System.Xml;

namespace xharness
{
	public class Jenkins
	{
		bool populating = true;

		public Harness Harness;
		public bool IncludeAll;
		public bool IncludeBcl;
		public bool IncludeMac = true;
		public bool IncludeiOS = true;
		public bool IncludeiOS64 = true;
		public bool IncludeiOS32 = true;
		public bool IncludeiOSExtensions;
		public bool ForceExtensionBuildOnly;
		public bool IncludetvOS = true;
		public bool IncludewatchOS = true;
		public bool IncludeMmpTest;
		public bool IncludeiOSMSBuild = true;
		public bool IncludeMtouch;
		public bool IncludeBtouch;
		public bool IncludeMacBindingProject;
		public bool IncludeSimulator = true;
		public bool IncludeOldSimulatorTests;
		public bool IncludeDevice;
		public bool IncludeXtro;
		public bool IncludeDocs;

		public bool CleanSuccessfulTestRuns = true;
		public bool UninstallTestApp = true;

		public Log MainLog;
		public Log SimulatorLoadLog;
		public Log DeviceLoadLog;

		string log_directory;
		public string LogDirectory {
			get {
				if (string.IsNullOrEmpty (log_directory)) {
					log_directory = Path.Combine (Harness.JENKINS_RESULTS_DIRECTORY, "tests");
					if (IsServerMode)
						log_directory = Path.Combine (log_directory, Harness.Timestamp);
				}
				return log_directory;
			}
		}

		Logs logs;
		public Logs Logs {
			get {
				return logs ?? (logs = new Logs (LogDirectory));
			}
		}

		public Simulators Simulators = new Simulators ();
		public Devices Devices = new Devices ();

		List<TestTask> Tasks = new List<TestTask> ();
		Dictionary<string, MakeTask> DependencyTasks = new Dictionary<string, MakeTask> ();

		internal static Resource DesktopResource = new Resource ("Desktop", Environment.ProcessorCount);
		internal static Resource NugetResource = new Resource ("Nuget", 1); // nuget is not parallel-safe :(

		static Dictionary<string, Resource> device_resources = new Dictionary<string, Resource> ();
		internal static Resources GetDeviceResources (IEnumerable<Device> devices)
		{
			List<Resource> resources = new List<Resource> ();
			lock (device_resources) {
				foreach (var device in devices) {
					Resource res;
					if (!device_resources.TryGetValue (device.UDID, out res))
						device_resources.Add (device.UDID, res = new Resource (device.UDID, 1, device.Name));
					resources.Add (res);
				}
			}
			return new Resources (resources);
		}

		Task LoadAsync (ref Log log, ILoadAsync loadable, string name)
		{
			loadable.Harness = Harness;
			if (log == null)
				log = Logs.Create ($"{name}-list-{Harness.Timestamp}.log", $"{name} Listing");
			log.Description = $"{name} Listing (in progress)";

			var capturedLog = log;
			return loadable.LoadAsync (capturedLog, include_locked: false, force: true).ContinueWith ((v) => {
				if (v.IsFaulted) {
					capturedLog.WriteLine ("Failed to load:");
					capturedLog.WriteLine (v.Exception);
					capturedLog.Description = $"{name} Listing {v.Exception.Message})";
				} else if (v.IsCompleted) {
					if (loadable is Devices devices) {
						var devicesTypes = new StringBuilder ();
						if (devices.Connected32BitIOS.Any ()) {
							devicesTypes.Append ("iOS 32 bit");
						}
						if (devices.Connected64BitIOS.Any ()) {
							devicesTypes.Append (devicesTypes.Length == 0 ? "iOS 64 bit" : ", iOS 64 bit");
						}
						if (devices.ConnectedTV.Any ()) {
							devicesTypes.Append (devicesTypes.Length == 0 ? "tvOS" : ", tvOS");
						}
						if (devices.ConnectedWatch.Any ()) {
							devicesTypes.Append (devicesTypes.Length == 0 ? "watchOS" : ", watchOS");
						}
						capturedLog.Description = (devicesTypes.Length == 0) ? $"{name} Listing (ok - no devices found)." : $"{name} Listing (ok). Devices types are: {devicesTypes.ToString ()}";
					}
					if (loadable is Simulators simulators) {
						var simCount = simulators.AvailableDevices.Count ();
						capturedLog.Description = ( simCount == 0) ? $"{name} Listing (ok - no simulators found)." : $"{name} Listing (ok - Found {simCount} simulators).";
					}
				}
			});
		}

		// Loads both simulators and devices in parallel
		Task LoadSimulatorsAndDevicesAsync ()
		{
			var devs = LoadAsync (ref DeviceLoadLog, Devices, "Device");
			var sims = LoadAsync (ref SimulatorLoadLog, Simulators, "Simulator");

			return Task.WhenAll (devs, sims);
		}

		AppRunnerTarget[] GetAppRunnerTargets (TestPlatform platform)
		{
			switch (platform) {
			case TestPlatform.tvOS:
				return new AppRunnerTarget [] { AppRunnerTarget.Simulator_tvOS };
			case TestPlatform.watchOS:
			case TestPlatform.watchOS_32:
			case TestPlatform.watchOS_64_32:
				return new AppRunnerTarget [] { AppRunnerTarget.Simulator_watchOS };
			case TestPlatform.iOS_Unified:
				return new AppRunnerTarget [] { AppRunnerTarget.Simulator_iOS32, AppRunnerTarget.Simulator_iOS64 };
			case TestPlatform.iOS_Unified32:
				return new AppRunnerTarget [] { AppRunnerTarget.Simulator_iOS32 };
			case TestPlatform.iOS_Unified64:
			case TestPlatform.iOS_TodayExtension64:
				return new AppRunnerTarget [] { AppRunnerTarget.Simulator_iOS64 };
			default:
				throw new NotImplementedException (platform.ToString ());
			}
		}

		string GetSimulatorMinVersion (TestPlatform platform)
		{
			switch (platform) {
			case TestPlatform.iOS:
			case TestPlatform.iOS_Unified:
			case TestPlatform.iOS_TodayExtension64:
			case TestPlatform.iOS_Unified32:
			case TestPlatform.iOS_Unified64:
				return "iOS " + Xamarin.SdkVersions.MiniOSSimulator;
			case TestPlatform.tvOS:
				return "tvOS " + Xamarin.SdkVersions.MinTVOSSimulator;
			case TestPlatform.watchOS:
			case TestPlatform.watchOS_32:
			case TestPlatform.watchOS_64_32:
				return "watchOS " + Xamarin.SdkVersions.MinWatchOSSimulator;
			default:
				throw new NotImplementedException (platform.ToString ());
			}
		}

		IEnumerable<RunSimulatorTask> CreateRunSimulatorTaskAsync (XBuildTask buildTask)
		{
			var runtasks = new List<RunSimulatorTask> ();

			AppRunnerTarget [] targets = GetAppRunnerTargets (buildTask.Platform);
			TestPlatform [] platforms;
			bool [] ignored;

			switch (buildTask.Platform) {
			case TestPlatform.tvOS:
				platforms = new TestPlatform [] { TestPlatform.tvOS };
				ignored = new [] { false };
				break;
			case TestPlatform.watchOS:
				platforms = new TestPlatform [] { TestPlatform.watchOS_32 };
				ignored = new [] { false };
				break;
			case TestPlatform.iOS_Unified:
				platforms = new TestPlatform [] { TestPlatform.iOS_Unified32, TestPlatform.iOS_Unified64 };
				ignored = new [] { !IncludeiOS32, false};
				break;
			case TestPlatform.iOS_TodayExtension64:
				targets = new AppRunnerTarget[] { AppRunnerTarget.Simulator_iOS64 };
				platforms = new TestPlatform[] { TestPlatform.iOS_TodayExtension64 };
				ignored = new [] { false };
				break;
			default:
				throw new NotImplementedException ();
			}

			for (int i = 0; i < targets.Length; i++)
				runtasks.Add (new RunSimulatorTask (buildTask, Simulators.SelectDevices (targets [i], SimulatorLoadLog, false)) { Platform = platforms [i], Ignored = ignored[i] || buildTask.Ignored });

			return runtasks;
		}

		bool IsIncluded (TestProject project)
		{
			if (!project.IsExecutableProject)
				return false;

			if (!IncludeBcl && project.IsBclTest)
				return false;

			if (Harness.IncludeSystemPermissionTests == false && project.Name == "introspection")
				return false;

			return true;
		}

		class TestData
		{
			public string Variation;
			public string MTouchExtraArgs;
			public string MonoBundlingExtraArgs; // mmp
			public string KnownFailure;
			public bool Debug;
			public bool Profiling;
			public string LinkMode;
			public string Defines;
			public string Undefines;
			public bool? Ignored;
			public bool EnableSGenConc;
			public bool UseThumb;
			public MonoNativeFlavor MonoNativeFlavor;
			public MonoNativeLinkMode MonoNativeLinkMode;
			public IEnumerable<IDevice> Candidates;
		}

		IEnumerable<TestData> GetTestData (RunTestTask test)
		{
			// This function returns additional test configurations (in addition to the default one) for the specific test

			MonoNativeFlavor flavor;
			switch (test.TestName) {
			case "mono-native-compat":
				flavor = MonoNativeFlavor.Compat;
				break;
			case "mono-native-unified":
				flavor = MonoNativeFlavor.Unified;
				break;
			default:
				flavor = MonoNativeFlavor.None;
				break;
			}

			// 32-bit interpreter doesn't work yet: https://github.com/mono/mono/issues/9871
			var supports_interpreter = test.Platform != TestPlatform.iOS_Unified32;
			var supports_dynamic_registrar_on_device = test.Platform == TestPlatform.iOS_Unified64 || test.Platform == TestPlatform.tvOS;

			switch (test.ProjectPlatform) {
			case "iPhone":
				// arm64_32 is only supported for Release builds for now.
				// arm32 bits too big for debug builds - https://github.com/xamarin/maccore/issues/2080
				var supports_debug = test.Platform != TestPlatform.watchOS_64_32 && !(test.TestName == "dont link" && test.Platform == TestPlatform.iOS_Unified32);

				/* we don't add --assembly-build-target=@all=staticobject because that's the default in all our test projects */
				if (supports_debug) {
					yield return new TestData { Variation = "AssemblyBuildTarget: dylib (debug)", MTouchExtraArgs = $"--assembly-build-target=@all=dynamiclibrary {test.TestProject.MTouchExtraArgs}", Debug = true, Profiling = false, MonoNativeLinkMode = MonoNativeLinkMode.Dynamic, MonoNativeFlavor = flavor };
					yield return new TestData { Variation = "AssemblyBuildTarget: SDK framework (debug)", MTouchExtraArgs = $"--assembly-build-target=@sdk=framework=Xamarin.Sdk --assembly-build-target=@all=staticobject {test.TestProject.MTouchExtraArgs}", Debug = true, Profiling = false, MonoNativeLinkMode = MonoNativeLinkMode.Static, MonoNativeFlavor = flavor };
					yield return new TestData { Variation = "AssemblyBuildTarget: dylib (debug, profiling)", MTouchExtraArgs = $"--assembly-build-target=@all=dynamiclibrary {test.TestProject.MTouchExtraArgs}", Debug = true, Profiling = true, MonoNativeLinkMode = MonoNativeLinkMode.Dynamic, MonoNativeFlavor = flavor };
					yield return new TestData { Variation = "AssemblyBuildTarget: SDK framework (debug, profiling)", MTouchExtraArgs = $"--assembly-build-target=@sdk=framework=Xamarin.Sdk --assembly-build-target=@all=staticobject {test.TestProject.MTouchExtraArgs}", Debug = true, Profiling = true, MonoNativeLinkMode = MonoNativeLinkMode.Static, MonoNativeFlavor = flavor };
				}

				if (test.ProjectConfiguration.Contains ("Debug"))
					yield return new TestData { Variation = "Release", MTouchExtraArgs = test.TestProject.MTouchExtraArgs, Debug = false, Profiling = false, MonoNativeLinkMode = MonoNativeLinkMode.Static };
				if (test.Platform == TestPlatform.iOS_Unified32)
					yield return new TestData { Variation = "Release: UseThumb", MTouchExtraArgs = test.TestProject.MTouchExtraArgs, Debug = false, Profiling = false, MonoNativeLinkMode = MonoNativeLinkMode.Static, UseThumb = true };
				yield return new TestData { Variation = "AssemblyBuildTarget: SDK framework (release)", MTouchExtraArgs = $"--assembly-build-target=@sdk=framework=Xamarin.Sdk --assembly-build-target=@all=staticobject {test.TestProject.MTouchExtraArgs}", Debug = false, Profiling = false, MonoNativeLinkMode = MonoNativeLinkMode.Static, MonoNativeFlavor = flavor };

				switch (test.TestName) {
				case "monotouch-test":
					if (supports_dynamic_registrar_on_device)
						yield return new TestData { Variation = "Debug (dynamic registrar)", MTouchExtraArgs = "--registrar:dynamic", Debug = true, Profiling = false };
					yield return new TestData { Variation = "Release (all optimizations)", MTouchExtraArgs = "--registrar:static --optimize:all", Debug = false, Profiling = false, Defines = "OPTIMIZEALL" };
					if (supports_debug) {
						yield return new TestData { Variation = "Debug (all optimizations)", MTouchExtraArgs = "--registrar:static --optimize:all", Debug = true, Profiling = false, Defines = "OPTIMIZEALL" };
						yield return new TestData { Variation = "Debug: SGenConc", MTouchExtraArgs = "", Debug = true, Profiling = false, MonoNativeLinkMode = MonoNativeLinkMode.Static, EnableSGenConc = true};
					}
					if (supports_interpreter) {
						if (supports_debug) {
							yield return new TestData { Variation = "Debug (interpreter)", MTouchExtraArgs = "--interpreter", Debug = true, Profiling = false, Undefines = "FULL_AOT_RUNTIME" };
							yield return new TestData { Variation = "Debug (interpreter -mscorlib)", MTouchExtraArgs = "--interpreter=-mscorlib", Debug = true, Profiling = false, Undefines = "FULL_AOT_RUNTIME" };
						}
						yield return new TestData { Variation = "Release (interpreter -mscorlib)", MTouchExtraArgs = "--interpreter=-mscorlib", Debug = false, Profiling = false, Undefines = "FULL_AOT_RUNTIME" };
					}
					break;
				case  string name when name.StartsWith ("mscorlib", StringComparison.Ordinal):
					if (supports_debug)
						yield return new TestData { Variation = "Debug: SGenConc", MTouchExtraArgs = "", Debug = true, Profiling = false, MonoNativeLinkMode = MonoNativeLinkMode.Static, EnableSGenConc = true};
					if (supports_interpreter) {
						if (supports_debug) {
							yield return new TestData { Variation = "Debug (interpreter)", MTouchExtraArgs = "--interpreter", Debug = true, Profiling = false, Undefines = "FULL_AOT_RUNTIME", KnownFailure = "<a href='https://github.com/xamarin/maccore/issues/1683'>#1683</a>" };
							yield return new TestData { Variation = "Debug (interpreter -mscorlib)", MTouchExtraArgs = "--interpreter=-mscorlib", Debug = true, Profiling = false, Undefines = "FULL_AOT_RUNTIME", KnownFailure = "<a href='https://github.com/xamarin/maccore/issues/1682'>#1682</a>" };
						}
						yield return new TestData { Variation = "Release (interpreter -mscorlib)", MTouchExtraArgs = "--interpreter=-mscorlib", Debug = false, Profiling = false, Undefines = "FULL_AOT_RUNTIME", KnownFailure = "<a href='https://github.com/xamarin/maccore/issues/1682'>#1682</a>" };
					}
					break;
				}
				break;
			case "iPhoneSimulator":
				switch (test.TestName) {
				case "monotouch-test":
					// The default is to run monotouch-test with the dynamic registrar (in the simulator), so that's already covered
					yield return new TestData { Variation = "Debug (LinkSdk)", Debug = true, Profiling = false, LinkMode = "LinkSdk" };
					yield return new TestData { Variation = "Debug (static registrar)", MTouchExtraArgs = "--registrar:static", Debug = true, Profiling = false, Undefines = "DYNAMIC_REGISTRAR" };
					yield return new TestData { Variation = "Release (all optimizations)", MTouchExtraArgs = "--registrar:static --optimize:all", Debug = false, Profiling = false, LinkMode = "Full", Defines = "OPTIMIZEALL", Undefines = "DYNAMIC_REGISTRAR" };
					yield return new TestData { Variation = "Debug (all optimizations)", MTouchExtraArgs = "--registrar:static --optimize:all,-remove-uithread-checks", Debug = true, Profiling = false, LinkMode = "Full", Defines = "OPTIMIZEALL", Undefines = "DYNAMIC_REGISTRAR", Ignored = !IncludeAll };
					break;
				case "introspection":
					foreach (var target in GetAppRunnerTargets (test.Platform))
						yield return new TestData {
							Variation = $"Debug ({GetSimulatorMinVersion (test.Platform)})",
							Debug = true,
							Candidates = Simulators.SelectDevices (target, SimulatorLoadLog, true),
							Ignored = !IncludeOldSimulatorTests, 
						};
					break;
				}
				break;
			case "AnyCPU":
			case "x86":
				switch (test.TestName) {
				case "xammac tests":
					switch (test.ProjectConfiguration) {
					case "Release":
						yield return new TestData { Variation = "Release (all optimizations)", MonoBundlingExtraArgs = "--registrar:static --optimize:all", Debug = false, LinkMode = "Full", Defines = "OPTIMIZEALL"};
						break;
					case "Debug":
						yield return new TestData { Variation = "Debug (all optimizations)", MonoBundlingExtraArgs = "--registrar:static --optimize:all,-remove-uithread-checks", Debug = true, LinkMode = "Full", Defines = "OPTIMIZEALL", Ignored = !IncludeAll };
						break;
					}
					break;
				}
				break;
			default:
				throw new NotImplementedException (test.ProjectPlatform);
			}
		}

		IEnumerable<T> CreateTestVariations<T> (IEnumerable<T> tests, Func<XBuildTask, T, IEnumerable<IDevice>, T> creator) where T: RunTestTask
		{
			foreach (var task in tests) {
				if (string.IsNullOrEmpty (task.Variation))
					task.Variation = task.ProjectConfiguration.Contains ("Debug") ? "Debug" : "Release";
			}

			var rv = new List<T> (tests);
			foreach (var task in tests.ToArray ()) {
				foreach (var test_data in GetTestData (task)) {
					var variation = test_data.Variation;
					var mtouch_extra_args = test_data.MTouchExtraArgs;
					var bundling_extra_args = test_data.MonoBundlingExtraArgs;
					var configuration = test_data.Debug ? task.ProjectConfiguration : task.ProjectConfiguration.Replace ("Debug", "Release");
					var debug = test_data.Debug;
					var profiling = test_data.Profiling;
					var link_mode = test_data.LinkMode;
					var defines = test_data.Defines;
					var undefines = test_data.Undefines;
					var ignored = test_data.Ignored;
					var known_failure = test_data.KnownFailure;
					var candidates = test_data.Candidates;

					if (!string.IsNullOrEmpty (known_failure))
						ignored = true;

					var clone = task.TestProject.Clone ();
					var clone_task = Task.Run (async () => {
						await task.BuildTask.InitialTask; // this is the project cloning above
						await clone.CreateCopyAsync (task);

						var isMac = false;
						var canSymlink = false;
						switch (task.Platform) {
						case TestPlatform.Mac:
						case TestPlatform.Mac_Modern:
						case TestPlatform.Mac_Full:
						case TestPlatform.Mac_System:
							isMac = true;
							break;
						case TestPlatform.iOS:
						case TestPlatform.iOS_TodayExtension64:
						case TestPlatform.iOS_Unified:
						case TestPlatform.iOS_Unified32:
						case TestPlatform.iOS_Unified64:
							canSymlink = true;
							break;
						}

						if (!string.IsNullOrEmpty (mtouch_extra_args))
							clone.Xml.AddExtraMtouchArgs (mtouch_extra_args, task.ProjectPlatform, configuration);
						if (!string.IsNullOrEmpty (bundling_extra_args))
							clone.Xml.AddMonoBundlingExtraArgs (bundling_extra_args, task.ProjectPlatform, configuration);
						if (!string.IsNullOrEmpty (link_mode))
							clone.Xml.SetNode (isMac ? "LinkMode" : "MtouchLink", link_mode, task.ProjectPlatform, configuration);
						if (!string.IsNullOrEmpty (defines)) {
							clone.Xml.AddAdditionalDefines (defines, task.ProjectPlatform, configuration);
							if (clone.ProjectReferences != null) {
								foreach (var pr in clone.ProjectReferences) {
									pr.Xml.AddAdditionalDefines (defines, task.ProjectPlatform, configuration);
									pr.Xml.Save (pr.Path);
								}
							}
						}
						if (!string.IsNullOrEmpty (undefines)) {
							clone.Xml.RemoveDefines (undefines, task.ProjectPlatform, configuration);
							if (clone.ProjectReferences != null) {
								foreach (var pr in clone.ProjectReferences) {
									pr.Xml.RemoveDefines (undefines, task.ProjectPlatform, configuration);
									pr.Xml.Save (pr.Path);
								}
							}
						}
						clone.Xml.SetNode (isMac ? "Profiling" : "MTouchProfiling", profiling ? "True" : "False", task.ProjectPlatform, configuration);
						if (test_data.MonoNativeFlavor != MonoNativeFlavor.None) {
							var mono_native_link = test_data.MonoNativeLinkMode;
							if (!canSymlink && mono_native_link == MonoNativeLinkMode.Symlink)
								mono_native_link = MonoNativeLinkMode.Static;
							MonoNativeHelper.AddProjectDefines (clone.Xml, test_data.MonoNativeFlavor, mono_native_link, task.ProjectPlatform, configuration);
						}
						if (test_data.EnableSGenConc)
							clone.Xml.SetNode ("MtouchEnableSGenConc", "true", task.ProjectPlatform, configuration);
						if (test_data.UseThumb) // no need to check the platform, already done at the data iterator
							clone.Xml.SetNode ("MtouchUseThumb", "true", task.ProjectPlatform, configuration);

						if (!debug && !isMac)
							clone.Xml.SetMtouchUseLlvm (true, task.ProjectPlatform, configuration);
						clone.Xml.Save (clone.Path);
					});

					var build = new XBuildTask {
						Jenkins = this,
						TestProject = clone,
						ProjectConfiguration = configuration,
						ProjectPlatform = task.ProjectPlatform,
						Platform = task.Platform,
						InitialTask = clone_task,
						TestName = clone.Name,
					};
					T newVariation = creator (build, task, candidates);
					newVariation.Variation = variation;
					newVariation.Ignored = ignored ?? task.Ignored;
					newVariation.BuildOnly = task.BuildOnly;
					newVariation.TimeoutMultiplier = task.TimeoutMultiplier;
					newVariation.KnownFailure = known_failure;
					rv.Add (newVariation);
				}
			}

			return rv;
		}

		async Task<IEnumerable<TestTask>> CreateRunSimulatorTasksAsync ()
		{
			var runSimulatorTasks = new List<RunSimulatorTask> ();

			foreach (var project in Harness.IOSTestProjects) {
				if (!project.IsExecutableProject)
					continue;

				bool ignored = !IncludeSimulator;
				if (!IsIncluded (project))
					ignored = true;

				var ps = new List<Tuple<TestProject, TestPlatform, bool>> ();
				if (!project.SkipiOSVariation)
					ps.Add (new Tuple<TestProject, TestPlatform, bool> (project, TestPlatform.iOS_Unified, ignored || !IncludeiOS64));
				if (project.MonoNativeInfo != null)
					ps.Add (new Tuple<TestProject, TestPlatform, bool> (project, TestPlatform.iOS_TodayExtension64, ignored || !IncludeiOS64));
				if (!project.SkiptvOSVariation)
					ps.Add (new Tuple<TestProject, TestPlatform, bool> (project.AsTvOSProject (), TestPlatform.tvOS, ignored || !IncludetvOS));
				if (!project.SkipwatchOSVariation)
					ps.Add (new Tuple<TestProject, TestPlatform, bool> (project.AsWatchOSProject (), TestPlatform.watchOS, ignored || !IncludewatchOS));
				
				var configurations = project.Configurations;
				if (configurations == null)
					configurations = new string [] { "Debug" };
				foreach (var config in configurations) {
					foreach (var pair in ps) {
						var derived = new XBuildTask () {
							Jenkins = this,
							ProjectConfiguration = config,
							ProjectPlatform = "iPhoneSimulator",
							Platform = pair.Item2,
							Ignored = pair.Item3,
							TestName = project.Name,
							Dependency = project.Dependency,
						};
						derived.CloneTestProject (pair.Item1);
						var simTasks = CreateRunSimulatorTaskAsync (derived);
						runSimulatorTasks.AddRange (simTasks);
						foreach (var task in simTasks) {
							if (configurations.Length > 1)
								task.Variation = config;
							task.TimeoutMultiplier = project.TimeoutMultiplier;
						}
					}
				}
			}

			var testVariations = CreateTestVariations (runSimulatorTasks, (buildTask, test, candidates) => new RunSimulatorTask (buildTask, candidates?.Cast<SimDevice> () ?? test.Candidates)).ToList ();

			foreach (var tv in testVariations) {
				if (!tv.Ignored)
					await tv.FindSimulatorAsync ();
			}

			var rv = new List<AggregatedRunSimulatorTask> ();
			foreach (var taskGroup in testVariations.GroupBy ((RunSimulatorTask task) => task.Device?.UDID ?? task.Candidates.ToString ())) {
				rv.Add (new AggregatedRunSimulatorTask (taskGroup) {
					Jenkins = this,
					TestName = $"Tests for {taskGroup.Key}",
				});
			}
			return rv;
		}

		async Task<IEnumerable<TestTask>> CreateRunDeviceTasksAsync ()
		{
			var rv = new List<RunDeviceTask> ();
			var projectTasks = new List<RunDeviceTask> ();

			foreach (var project in Harness.IOSTestProjects) {
				if (!project.IsExecutableProject)
					continue;
				
				bool ignored = !IncludeDevice;
				if (!IsIncluded (project))
					ignored = true;

				projectTasks.Clear ();
				if (!project.SkipiOSVariation) {
					var build64 = new XBuildTask {
						Jenkins = this,
						ProjectConfiguration = "Debug64",
						ProjectPlatform = "iPhone",
						Platform = TestPlatform.iOS_Unified64,
						TestName = project.Name,
					};
					build64.CloneTestProject (project);
					projectTasks.Add (new RunDeviceTask (build64, Devices.Connected64BitIOS.Where (d => d.IsSupported (project))) { Ignored = !IncludeiOS64 });

					var build32 = new XBuildTask {
						Jenkins = this,
						ProjectConfiguration = project.Name != "dont link" ? "Debug32" : "Release32",
						ProjectPlatform = "iPhone",
						Platform = TestPlatform.iOS_Unified32,
						TestName = project.Name,
					};
					build32.CloneTestProject (project);
					projectTasks.Add (new RunDeviceTask (build32, Devices.Connected32BitIOS.Where (d => d.IsSupported (project))) { Ignored = !IncludeiOS32 });

					var todayProject = project.AsTodayExtensionProject ();
					var buildToday = new XBuildTask {
						Jenkins = this,
						ProjectConfiguration = "Debug64",
						ProjectPlatform = "iPhone",
						Platform = TestPlatform.iOS_TodayExtension64,
						TestName = project.Name,
					};
					buildToday.CloneTestProject (todayProject);
					projectTasks.Add (new RunDeviceTask (buildToday, Devices.Connected64BitIOS.Where (d => d.IsSupported (project))) { Ignored = !IncludeiOSExtensions, BuildOnly = ForceExtensionBuildOnly });
				}

				if (!project.SkiptvOSVariation) {
					var tvOSProject = project.AsTvOSProject ();
					var buildTV = new XBuildTask {
						Jenkins = this,
						ProjectConfiguration = "Debug",
						ProjectPlatform = "iPhone",
						Platform = TestPlatform.tvOS,
						TestName = project.Name,
					};
					buildTV.CloneTestProject (tvOSProject);
					projectTasks.Add (new RunDeviceTask (buildTV, Devices.ConnectedTV.Where (d => d.IsSupported (project))) { Ignored = !IncludetvOS });
				}

				if (!project.SkipwatchOSVariation) {
					var watchOSProject = project.AsWatchOSProject ();
					if (!project.SkipwatchOS32Variation) {
						var buildWatch32 = new XBuildTask {
							Jenkins = this,
							ProjectConfiguration = "Debug32",
							ProjectPlatform = "iPhone",
							Platform = TestPlatform.watchOS_32,
							TestName = project.Name,
						};
						buildWatch32.CloneTestProject (watchOSProject);
						projectTasks.Add (new RunDeviceTask (buildWatch32, Devices.ConnectedWatch) { Ignored = !IncludewatchOS });
					}

					if (!project.SkipwatchOSARM64_32Variation) {
						var buildWatch64_32 = new XBuildTask {
							Jenkins = this,
							ProjectConfiguration = "Release64_32", // We don't support Debug for ARM64_32 yet.
							ProjectPlatform = "iPhone",
							Platform = TestPlatform.watchOS_64_32,
							TestName = project.Name,
						};
						buildWatch64_32.CloneTestProject (watchOSProject);
						projectTasks.Add (new RunDeviceTask (buildWatch64_32, Devices.ConnectedWatch32_64.Where (d => d.IsSupported (project))) { Ignored = !IncludewatchOS });
					}
				}
				foreach (var task in projectTasks) {
					task.TimeoutMultiplier = project.TimeoutMultiplier;
					task.BuildOnly |= project.BuildOnly;
					task.Ignored |= ignored;
				}
				rv.AddRange (projectTasks);
			}

			return CreateTestVariations (rv, (buildTask, test, candidates) => new RunDeviceTask (buildTask, candidates?.Cast<Device> () ?? test.Candidates));
		}

		static string AddSuffixToPath (string path, string suffix)
		{
			return Path.Combine (Path.GetDirectoryName (path), Path.GetFileNameWithoutExtension (path) + suffix + Path.GetExtension (path));
		}

		void SelectTests ()
		{
			int pull_request;

			if (!int.TryParse (Environment.GetEnvironmentVariable ("ghprbPullId"), out pull_request))
				MainLog.WriteLine ("The environment variable 'ghprbPullId' was not found, so no pull requests will be checked for test selection.");

			// First check if can auto-select any tests based on which files were modified.
			// This will only enable additional tests, never disable tests.
			if (pull_request > 0)
				SelectTestsByModifiedFiles (pull_request);
			
			// Then we check for labels. Labels are manually set, so those override
			// whatever we did automatically.
			SelectTestsByLabel (pull_request);

			DisableKnownFailingDeviceTests ();

			if (!Harness.INCLUDE_IOS) {
				MainLog.WriteLine ("The iOS build is disabled, so any iOS tests will be disabled as well.");
				IncludeiOS = false;
				IncludeiOS64 = false;
				IncludeiOS32 = false;
			}

			if (!Harness.INCLUDE_WATCH) {
				MainLog.WriteLine ("The watchOS build is disabled, so any watchOS tests will be disabled as well.");
				IncludewatchOS = false;
			}

			if (!Harness.INCLUDE_TVOS) {
				MainLog.WriteLine ("The tvOS build is disabled, so any tvOS tests will be disabled as well.");
				IncludetvOS = false;
			}

			if (!Harness.INCLUDE_MAC) {
				MainLog.WriteLine ("The macOS build is disabled, so any macOS tests will be disabled as well.");
				IncludeMac = false;
			}
		}

		void DisableKnownFailingDeviceTests ()
		{
			// https://github.com/xamarin/maccore/issues/1008
			ForceExtensionBuildOnly = true;
		}

		void SelectTestsByModifiedFiles (int pull_request)
		{
			var files = GitHub.GetModifiedFiles (Harness, pull_request);

			MainLog.WriteLine ("Found {0} modified file(s) in the pull request #{1}.", files.Count (), pull_request);
			foreach (var f in files)
				MainLog.WriteLine ("    {0}", f);

			// We select tests based on a prefix of the modified files.
			// Add entries here to check for more prefixes.
			var mtouch_prefixes = new string [] {
				"tests/mtouch",
				"tests/common",
				"tools/mtouch",
				"tools/common",
				"tools/linker",
				"src/ObjCRuntime/Registrar.cs",
				"mk/mono.mk",
				"msbuild",
				"runtime",
			};
			var mmp_prefixes = new string [] {
				"tests/mmptest",
				"tests/common",
				"tools/mmp",
				"tools/common",
				"tools/linker",
				"src/ObjCRuntime/Registrar.cs",
				"mk/mono.mk",
				"msbuild",
			};
			var bcl_prefixes = new string [] {
				"tests/bcl-test",
				"tests/common",
				"mk/mono.mk",
			};
			var btouch_prefixes = new string [] {
				"src/btouch.cs",
				"src/generator.cs",
				"src/generator-",
				"src/Makefile.generator",
				"tests/generator",
				"tests/common",
			};
			var mac_binding_project = new string [] {
				"msbuild",
				"tests/mac-binding-project",
				"tests/common/mac",
			}.Intersect (btouch_prefixes).ToArray ();
			var xtro_prefixes = new string [] {
				"tests/xtro-sharpie",
				"src",
				"Make.config",
			};

			SetEnabled (files, mtouch_prefixes, "mtouch", ref IncludeMtouch);
			SetEnabled (files, mmp_prefixes, "mmp", ref IncludeMmpTest);
			SetEnabled (files, bcl_prefixes, "bcl", ref IncludeBcl);
			SetEnabled (files, btouch_prefixes, "btouch", ref IncludeBtouch);
			SetEnabled (files, mac_binding_project, "mac-binding-project", ref IncludeMacBindingProject);
			SetEnabled (files, xtro_prefixes, "xtro", ref IncludeXtro);
		}

		void SetEnabled (IEnumerable<string> files, string [] prefixes, string testname, ref bool value)
		{
			foreach (var file in files) {
				foreach (var prefix in prefixes) {
					if (file.StartsWith (prefix, StringComparison.Ordinal)) {
						value = true;
						MainLog.WriteLine ("Enabled '{0}' tests because the modified file '{1}' matches prefix '{2}'", testname, file, prefix);
						return;
					}
				}
			}
		}

		void SelectTestsByLabel (int pull_request)
		{
			var labels = new HashSet<string> ();
			if (Harness.Labels.Any ()) {
				labels.UnionWith (Harness.Labels);
				MainLog.WriteLine ($"{Harness.Labels.Count} label(s) were passed on the command line.");
			} else {
				MainLog.WriteLine ($"No labels were passed on the command line.");
			}
			if (pull_request > 0) {
				var lbls = GitHub.GetLabels (Harness, pull_request);
				if (lbls.Any ()) {
					labels.UnionWith (lbls);
					MainLog.WriteLine ($"Found {lbls.Count ()} label(s) in the pull request #{pull_request}: {string.Join (", ", lbls)}");
				} else {
					MainLog.WriteLine ($"No labels were found in the pull request #{pull_request}.");
				}
			}
			var env_labels = Environment.GetEnvironmentVariable ("XHARNESS_LABELS");
			if (!string.IsNullOrEmpty (env_labels)) {
				var lbls = env_labels.Split (new char [] { ',' }, StringSplitOptions.RemoveEmptyEntries);
				labels.UnionWith (lbls);
				MainLog.WriteLine ($"Found {lbls.Count ()} label(s) in the environment variable XHARNESS_LABELS: {string.Join (", ", lbls)}");
			} else {
				MainLog.WriteLine ($"No labels were in the environment variable XHARNESS_LABELS.");
			}
			MainLog.WriteLine ($"In total found {labels.Count ()} label(s): {string.Join (", ", labels.ToArray ())}");

			// disabled by default
			SetEnabled (labels, "mtouch", ref IncludeMtouch);
			SetEnabled (labels, "mmp", ref IncludeMmpTest);
			SetEnabled (labels, "bcl", ref IncludeBcl);
			SetEnabled (labels, "btouch", ref IncludeBtouch);
			SetEnabled (labels, "mac-binding-project", ref IncludeMacBindingProject);
			SetEnabled (labels, "ios-extensions", ref IncludeiOSExtensions);
			SetEnabled (labels, "device", ref IncludeDevice);
			SetEnabled (labels, "xtro", ref IncludeXtro);
			SetEnabled (labels, "old-simulator", ref IncludeOldSimulatorTests);
			SetEnabled (labels, "all", ref IncludeAll);

			// enabled by default
			SetEnabled (labels, "ios-32", ref IncludeiOS32);
			SetEnabled (labels, "ios-64", ref IncludeiOS64);
			SetEnabled (labels, "ios", ref IncludeiOS); // Needs to be set after `ios-32` and `ios-64` (because it can reset them)
			SetEnabled (labels, "tvos", ref IncludetvOS);
			SetEnabled (labels, "watchos", ref IncludewatchOS);
			SetEnabled (labels, "mac", ref IncludeMac);
			SetEnabled (labels, "ios-msbuild", ref IncludeiOSMSBuild);
			SetEnabled (labels, "ios-simulator", ref IncludeSimulator);
			bool inc_permission_tests = false;
			if (SetEnabled (labels, "system-permission", ref inc_permission_tests))
				Harness.IncludeSystemPermissionTests = inc_permission_tests;

			// docs is a bit special:
			// - can only be executed if the Xamarin-specific parts of the build is enabled
			// - enabled by default if the current branch is master (or, for a pull request, if the target branch is master)
			var changed = SetEnabled (labels, "docs", ref IncludeDocs);
			if (Harness.ENABLE_XAMARIN) {
				if (!changed) { // don't override any value set using labels
					var branchName = Environment.GetEnvironmentVariable ("BRANCH_NAME");
					if (!string.IsNullOrEmpty (branchName)) {
						IncludeDocs = branchName == "master";
						if (IncludeDocs)
							MainLog.WriteLine ("Enabled 'docs' tests because the current branch is 'master'.");
					} else if (pull_request > 0) {
						IncludeDocs = GitHub.GetPullRequestTargetBranch (Harness, pull_request) == "master";
						if (IncludeDocs)
							MainLog.WriteLine ("Enabled 'docs' tests because the target branch is 'master'.");
					}
				}
			} else {
				if (IncludeDocs) {
					IncludeDocs = false; // could have been enabled by 'run-all-tests', so disable it if we can't run it.
					MainLog.WriteLine ("Disabled 'docs' tests because the Xamarin-specific parts of the build are not enabled.");
				}
			}

			// old simulator tests is also a bit special:
			// - enabled by default if using a beta Xcode, otherwise disabled by default
			changed = SetEnabled (labels, "old-simulator", ref IncludeOldSimulatorTests);
			if (!changed && Harness.IsBetaXcode) {
				IncludeOldSimulatorTests = true;
				MainLog.WriteLine ("Enabled 'old-simulator' tests because we're using a beta Xcode.");
			}
		}

		// Returns true if the value was changed.
		bool SetEnabled (HashSet<string> labels, string testname, ref bool value)
		{
			if (labels.Contains ("skip-" + testname + "-tests")) {
				MainLog.WriteLine ("Disabled '{0}' tests because the label 'skip-{0}-tests' is set.", testname);
				if (testname == "ios")
					IncludeiOS32 = IncludeiOS64 = false;
				value = false;
				return true;
			} else if (labels.Contains ("run-" + testname + "-tests")) {
				MainLog.WriteLine ("Enabled '{0}' tests because the label 'run-{0}-tests' is set.", testname);
				if (testname == "ios")
					IncludeiOS32 = IncludeiOS64 = true;
				value = true;
				return true;
			} else if (labels.Contains ("skip-all-tests")) {
				MainLog.WriteLine ("Disabled '{0}' tests because the label 'skip-all-tests' is set.", testname);
				value = false;
				return true;
			} else if (labels.Contains ("run-all-tests")) {
				MainLog.WriteLine ("Enabled '{0}' tests because the label 'run-all-tests' is set.", testname);
				value = true;
				return true;
			}
			// respect any default value
			return false;
		}

		async Task PopulateTasksAsync ()
		{
			// Missing:
			// api-diff

			SelectTests ();

			LoadSimulatorsAndDevicesAsync ().DoNotAwait ();

			var loadsim = CreateRunSimulatorTasksAsync ()
				.ContinueWith ((v) => { Console.WriteLine ("Simulator tasks created"); Tasks.AddRange (v.Result); });
			
			//Tasks.AddRange (await CreateRunSimulatorTasksAsync ());

			var buildiOSMSBuild = new XBuildTask ()
			{
				Jenkins = this,
				TestProject = new TestProject (Path.GetFullPath (Path.Combine (Harness.RootDirectory, "..", "msbuild", "Xamarin.MacDev.Tasks.sln"))),
				SpecifyPlatform = false,
				SpecifyConfiguration = false,
				Platform = TestPlatform.iOS,
				UseMSBuild = true,
			};
			var nunitExecutioniOSMSBuild = new NUnitExecuteTask (buildiOSMSBuild)
			{
				TestLibrary = Path.Combine (Harness.RootDirectory, "..", "msbuild", "tests", "bin", "Xamarin.iOS.Tasks.Tests.dll"),
				TestProject = new TestProject (Path.Combine (Path.GetDirectoryName (buildiOSMSBuild.TestProject.Path), "tests", "Xamarin.iOS.Tasks.Tests", "Xamarin.iOS.Tasks.Tests.csproj")),
				Platform = TestPlatform.iOS,
				TestName = "MSBuild tests",
				Mode = "iOS",
				Timeout = TimeSpan.FromMinutes (60),
				Ignored = !IncludeiOSMSBuild,
			};
			Tasks.Add (nunitExecutioniOSMSBuild);
			
			var buildInstallSources = new XBuildTask ()
			{
				Jenkins = this,
				TestProject = new TestProject (Path.GetFullPath (Path.Combine (Harness.RootDirectory, "..", "tools", "install-source", "InstallSourcesTests", "InstallSourcesTests.csproj"))),
				SpecifyPlatform = false,
				SpecifyConfiguration = false,
				Platform = TestPlatform.iOS,
			};
			buildInstallSources.SolutionPath = Path.GetFullPath (Path.Combine (Harness.RootDirectory, "..", "tools", "install-source", "install-source.sln")); // this is required for nuget restore to be executed
			var nunitExecutionInstallSource = new NUnitExecuteTask (buildInstallSources)
			{
				TestLibrary = Path.Combine (Harness.RootDirectory, "..", "tools", "install-source", "InstallSourcesTests", "bin", "Release", "InstallSourcesTests.dll"),
				TestProject = buildInstallSources.TestProject,
				Platform = TestPlatform.iOS,
				TestName = "Install Sources tests",
				Mode = "iOS",
				Timeout = TimeSpan.FromMinutes (60),
				Ignored = !IncludeMac && !IncludeSimulator,
			};
			Tasks.Add (nunitExecutionInstallSource);

			foreach (var project in Harness.MacTestProjects) {
				bool ignored = !IncludeMac;
				if (!IncludeMmpTest && project.Path.Contains ("mmptest"))
					ignored = true;

				if (!IsIncluded (project))
					ignored = true;

				var configurations = project.Configurations;
				if (configurations == null)
					configurations = new string [] { "Debug" };

				TestPlatform platform;
				switch (project.TargetFrameworkFlavors) {
				case MacFlavors.Console:
					platform = TestPlatform.Mac;
					break;
				case MacFlavors.Full:
					platform = TestPlatform.Mac_Full;
					break;
				case MacFlavors.Modern:
					platform = TestPlatform.Mac_Modern;
					break;
				case MacFlavors.System:
					platform = TestPlatform.Mac_System;
					break;
				default:
					throw new NotImplementedException (project.TargetFrameworkFlavors.ToString ());
				}
				foreach (var config in configurations) {
					XBuildTask build = new XBuildTask ();
					build.Platform = platform;
					build.CloneTestProject (project);
					build.Jenkins = this;
					build.SolutionPath = project.SolutionPath;
					build.ProjectConfiguration = config;
					build.ProjectPlatform = project.Platform;
					build.SpecifyPlatform = false;
					build.SpecifyConfiguration = build.ProjectConfiguration != "Debug";
					build.Dependency = project.Dependency;
					RunTestTask exec;
					IEnumerable<RunTestTask> execs;
					var ignored_main = ignored;
					if (project.IsNUnitProject) {
						var dll = Path.Combine (Path.GetDirectoryName (build.TestProject.Path), project.Xml.GetOutputAssemblyPath (build.ProjectPlatform, build.ProjectConfiguration).Replace ('\\', '/'));
						exec = new NUnitExecuteTask (build) {
							Ignored = ignored_main,
							TestLibrary = dll,
							TestProject = project,
							Platform = build.Platform,
							TestName = project.Name,
							Timeout = TimeSpan.FromMinutes (120),
							Mode = "macOS",
						};
						execs = new [] { exec };
					} else {
						exec = new MacExecuteTask (build) {
							Ignored = ignored_main,
							BCLTest = project.IsBclTest,
							TestName = project.Name,
							IsUnitTest = true,
						};
						execs = CreateTestVariations (new [] { exec }, (buildTask, test, candidates) => new MacExecuteTask (buildTask) { IsUnitTest = true } );
					}

					foreach (var e in execs)
						e.Variation = config;

					Tasks.AddRange (execs);
				}
			}

			var buildMTouch = new MakeTask ()
			{
				Jenkins = this,
				TestProject = new TestProject (Path.GetFullPath (Path.Combine (Harness.RootDirectory, "mtouch", "mtouch.sln"))),
				SpecifyPlatform = false,
				SpecifyConfiguration = false,
				Platform = TestPlatform.iOS,
				Target = "dependencies",
				WorkingDirectory = Path.GetFullPath (Path.Combine (Harness.RootDirectory, "mtouch")),
			};
			var nunitExecutionMTouch = new NUnitExecuteTask (buildMTouch)
			{
				TestLibrary = Path.Combine (Harness.RootDirectory, "mtouch", "bin", "Debug", "mtouch.dll"),
				TestProject = new TestProject (Path.GetFullPath (Path.Combine (Harness.RootDirectory, "mtouch", "mtouch.csproj"))),
				Platform = TestPlatform.iOS,
				TestName = "MTouch tests",
				Timeout = TimeSpan.FromMinutes (180),
				Ignored = !IncludeMtouch,
				InProcess = true,
			};
			Tasks.Add (nunitExecutionMTouch);

			var buildGenerator = new MakeTask {
				Jenkins = this,
				TestProject = new TestProject (Path.GetFullPath (Path.Combine (Harness.RootDirectory, "..", "src", "generator.sln"))),
				SpecifyPlatform = false,
				SpecifyConfiguration = false,
				Platform = TestPlatform.iOS,
				Target = "build-unit-tests",
				WorkingDirectory = Path.GetFullPath (Path.Combine (Harness.RootDirectory, "generator")),
			};
			var runGenerator = new NUnitExecuteTask (buildGenerator) {
				TestLibrary = Path.Combine (Harness.RootDirectory, "generator", "bin", "Debug", "generator-tests.dll"),
				TestProject = new TestProject (Path.GetFullPath (Path.Combine (Harness.RootDirectory, "generator", "generator-tests.csproj"))),
				Platform = TestPlatform.iOS,
				TestName = "Generator tests",
				Timeout = TimeSpan.FromMinutes (10),
				Ignored = !IncludeBtouch,
			};
			Tasks.Add (runGenerator);

			var run_mmp = new MakeTask
			{
				Jenkins = this,
				Platform = TestPlatform.Mac,
				TestName = "MMP Regression Tests",
				Target = "all", // -j" + Environment.ProcessorCount,
				WorkingDirectory = Path.Combine (Harness.RootDirectory, "mmptest", "regression"),
				Ignored = !IncludeMmpTest || !IncludeMac,
				Timeout = TimeSpan.FromMinutes (30),
				SupportsParallelExecution = false, // Already doing parallel execution by running "make -jX"
			};
			run_mmp.CompletedTask = new Task (() =>
			{
				foreach (var log in Directory.GetFiles (Path.GetFullPath (run_mmp.WorkingDirectory), "*.log", SearchOption.AllDirectories))
					run_mmp.Logs.AddFile (log, log.Substring (run_mmp.WorkingDirectory.Length + 1));
			});
			run_mmp.Environment.Add ("BUILD_REVISION", "jenkins"); // This will print "@MonkeyWrench: AddFile: <log path>" lines, which we can use to get the log filenames.
			Tasks.Add (run_mmp);

			var runMacBindingProject = new MakeTask
			{
				Jenkins = this,
				Platform = TestPlatform.Mac,
				TestName = "Mac Binding Projects",
				Target = "all",
				WorkingDirectory = Path.Combine (Harness.RootDirectory, "mac-binding-project"),
				Ignored = !IncludeMacBindingProject || !IncludeMac,
				Timeout = TimeSpan.FromMinutes (15),
			};
			Tasks.Add (runMacBindingProject);

			var buildXtroTests = new MakeTask {
				Jenkins = this,
				Platform = TestPlatform.All,
				TestName = "Xtro",
				Target = "wrench",
				WorkingDirectory = Path.Combine (Harness.RootDirectory, "xtro-sharpie"),
				Ignored = !IncludeXtro,
				Timeout = TimeSpan.FromMinutes (15),
			};
			var runXtroReporter = new RunXtroTask (buildXtroTests) {
				Jenkins = this,
				Platform = TestPlatform.Mac,
				TestName = buildXtroTests.TestName,
				Ignored = buildXtroTests.Ignored,
				WorkingDirectory = buildXtroTests.WorkingDirectory,
			};
			Tasks.Add (runXtroReporter);

			var runDocsTests = new MakeTask {
				Jenkins = this,
				Platform = TestPlatform.All,
				TestName = "Documentation",
				Target = "wrench-docs",
				WorkingDirectory = Harness.RootDirectory,
				Ignored = !IncludeDocs,
				Timeout = TimeSpan.FromMinutes (45),
			};
			Tasks.Add (runDocsTests);

			var buildSampleTests = new XBuildTask {
				Jenkins = this,
				TestProject = new TestProject (Path.GetFullPath (Path.Combine (Harness.RootDirectory, "sampletester", "sampletester.sln"))),
				SpecifyPlatform = false,
				Platform = TestPlatform.All,
				ProjectConfiguration = "Debug",
			};
			var runSampleTests = new NUnitExecuteTask (buildSampleTests) {
				TestLibrary = Path.Combine (Harness.RootDirectory, "sampletester", "bin", "Debug", "sampletester.dll"),
				TestProject = new TestProject (Path.GetFullPath (Path.Combine (Harness.RootDirectory, "sampletester", "sampletester.csproj"))),
				Platform = TestPlatform.All,
				TestName = "Sample tests",
				Timeout = TimeSpan.FromDays (1), // These can take quite a while to execute.
				InProcess = true,
				Ignored = true, // Ignored by default, can be run manually. On CI will execute if the label 'run-sample-tests' is present on a PR (but in Azure Devops on a different bot).
			};
			Tasks.Add (runSampleTests);

			var loaddev = CreateRunDeviceTasksAsync ().ContinueWith ((v) => {
				Console.WriteLine ("Got device tasks completed");
				Tasks.AddRange (v.Result);
			});
			Task.WaitAll (loadsim, loaddev);
		}

		async Task ExecutePeriodicCommandAsync (Log periodic_loc)
		{
			periodic_loc.WriteLine ($"Starting periodic task with interval {Harness.PeriodicCommandInterval.TotalMinutes} minutes.");
			while (true) {
				var watch = Stopwatch.StartNew ();
				using (var process = new Process ()) {
					process.StartInfo.FileName = Harness.PeriodicCommand;
					process.StartInfo.Arguments = Harness.PeriodicCommandArguments;
					var rv = await process.RunAsync (periodic_loc, timeout: Harness.PeriodicCommandInterval);
					if (!rv.Succeeded)
						periodic_loc.WriteLine ($"Periodic command failed with exit code {rv.ExitCode} (Timed out: {rv.TimedOut})");
				}
				var ticksLeft = watch.ElapsedTicks - Harness.PeriodicCommandInterval.Ticks;
				if (ticksLeft < 0)
					ticksLeft = Harness.PeriodicCommandInterval.Ticks;
				var wait = TimeSpan.FromTicks (ticksLeft);
				await Task.Delay (wait);
			}
		}

		public int Run ()
		{
			try {
				Directory.CreateDirectory (LogDirectory);
				Log log = Logs.Create ($"Harness-{Harness.Timestamp}.log", "Harness log");
				if (Harness.InWrench)
					log = Log.CreateAggregatedLog (log, new ConsoleLog ());
				Harness.HarnessLog = MainLog = log;

				var tasks = new List<Task> ();
				if (IsServerMode)
					tasks.Add (RunTestServer ());

				if (Harness.InJenkins) {
					Task.Factory.StartNew (async () => {
						while (true) {
							await Task.Delay (TimeSpan.FromMinutes (10));
							Console.WriteLine ("Still running tests. Please be patient.");
						}
					});
				}
				if (!string.IsNullOrEmpty (Harness.PeriodicCommand)) {
					var periodic_log = Logs.Create ("PeriodicCommand.log", "Periodic command log");
					Task.Run (async () => await ExecutePeriodicCommandAsync (periodic_log));
				}

				Task.Run (async () =>
				{
					await SimDevice.KillEverythingAsync (MainLog);
					await PopulateTasksAsync ();
					populating = false;
				}).Wait ();
				GenerateReport ();
				BuildTestLibraries ();
				if (!IsServerMode) {
					foreach (var task in Tasks)
						tasks.Add (task.RunAsync ());
				}
				Task.WaitAll (tasks.ToArray ());
				GenerateReport ();
				return Tasks.Any ((v) => v.Failed || v.DeviceNotFound) ? 1 : 0;
			} catch (Exception ex) {
				MainLog.WriteLine ("Unexpected exception: {0}", ex);
				Console.WriteLine ("Unexpected exception: {0}", ex);
				return 2;
			}
		}

		public bool IsServerMode {
			get { return Harness.JenkinsConfiguration == "server"; }
		}

		void BuildTestLibraries ()
		{
			ProcessHelper.ExecuteCommandAsync ("make", new [] { "all", $"-j{Environment.ProcessorCount}", "-C", Path.Combine (Harness.RootDirectory, "test-libraries") }, MainLog, TimeSpan.FromMinutes (10)).Wait ();
		}

		Task RunTestServer ()
		{
			var server = new HttpListener ();

			// Try and find an unused port
			int attemptsLeft = 50;
			int port = 51234; // Try this port first, to try to not vary between runs just because.
			Random r = new Random ((int) DateTime.Now.Ticks);
			while (attemptsLeft-- > 0) {
				var newPort = port != 0 ? port : r.Next (49152, 65535); // The suggested range for dynamic ports is 49152-65535 (IANA)
				server.Prefixes.Clear ();
				server.Prefixes.Add ("http://*:" + newPort + "/");
				try {
					server.Start ();
					port = newPort;
					break;
				} catch (Exception ex) {
					MainLog.WriteLine ("Failed to listen on port {0}: {1}", newPort, ex.Message);
					port = 0;
				}
			}
			MainLog.WriteLine ($"Created server on localhost:{port}");

			var tcs = new TaskCompletionSource<bool> ();
			var thread = new System.Threading.Thread (() =>
			{
				while (server.IsListening) {
					var context = server.GetContext ();
					var request = context.Request;
					var response = context.Response;
					var arguments = System.Web.HttpUtility.ParseQueryString (request.Url.Query);
					try {
						var allTasks = Tasks.SelectMany ((v) =>
						{
							var rv = new List<TestTask> ();
							var runsim = v as AggregatedRunSimulatorTask;
							if (runsim != null)
								rv.AddRange (runsim.Tasks);
							rv.Add (v);
							return rv;
						});

						IEnumerable<TestTask> find_tasks (StreamWriter writer, string ids)
						{
							IEnumerable<TestTask> tasks;
							switch (request.Url.Query) {
							case "?all":
								tasks = Tasks;
								break;
							case "?selected":
								tasks = allTasks.Where ((v) => !v.Ignored);
								break;
							case "?failed":
								tasks = allTasks.Where ((v) => v.Failed);
								break;
							case "?":
								writer.WriteLine ("No tasks specified");
								return Array.Empty<TestTask> ();
							default:
								var id_inputs = ids.Substring (1).Split (',');
								var rv = new List<TestTask> (id_inputs.Length);
								foreach (var id_input in id_inputs) {
									if (int.TryParse (id_input, out var id)) {
										var task = Tasks.FirstOrDefault ((t) => t.ID == id);
										if (task == null)
											task = Tasks.Where ((v) => v is AggregatedRunSimulatorTask).Cast<AggregatedRunSimulatorTask> ().SelectMany ((v) => v.Tasks).FirstOrDefault ((t) => t.ID == id);
										if (task == null) {
											writer.WriteLine ($"Could not find test {id}");
										} else {
											rv.Add (task);
										}
									} else {
										writer.WriteLine ($"Could not parse {arguments ["id"]}");
									}
								}
								tasks = rv;
								break;
							}
							return tasks;
						}

						string serveFile = null;
						switch (request.Url.LocalPath) {
						case "/":
							response.ContentType = System.Net.Mime.MediaTypeNames.Text.Html;
							GenerateReportImpl (response.OutputStream);
							break;
						case "/set-option":
							response.ContentType = System.Net.Mime.MediaTypeNames.Text.Plain;
							switch (request.Url.Query) {
							case "?clean":
								CleanSuccessfulTestRuns = true;
								break;
							case "?do-not-clean":
								CleanSuccessfulTestRuns = false;
								break;
							case "?uninstall-test-app":
								UninstallTestApp = true;
								break;
							case "?do-not-uninstall-test-app":
								UninstallTestApp = false;
								break;
							case "?skip-permission-tests":
								Harness.IncludeSystemPermissionTests = false;
								break;
							case "?include-permission-tests":
								Harness.IncludeSystemPermissionTests = true;
								break;
							case "?clear-permission-tests":
								Harness.IncludeSystemPermissionTests = null;
								break;
							default:
								throw new NotImplementedException (request.Url.Query);
							}
							using (var writer = new StreamWriter (response.OutputStream)) {
								writer.WriteLine ("OK");
							}
							break;
						case "/select":
						case "/deselect":
							response.ContentType = System.Net.Mime.MediaTypeNames.Text.Plain;
							using (var writer = new StreamWriter (response.OutputStream)) {
								foreach (var task in allTasks) {
									bool? is_match = null;
									if (!(task.Ignored || task.NotStarted))
										continue;
									switch (request.Url.Query) {
									case "?all":
										is_match = true;
										break;
									case "?all-device":
										is_match = task is RunDeviceTask;
										break;
									case "?all-simulator":
										is_match = task is RunSimulatorTask;
										break;
									case "?all-ios":
										switch (task.Platform) {
										case TestPlatform.iOS:
										case TestPlatform.iOS_TodayExtension64:
										case TestPlatform.iOS_Unified:
										case TestPlatform.iOS_Unified32:
										case TestPlatform.iOS_Unified64:
											is_match = true;
											break;
										default:
											if (task.Platform.ToString ().StartsWith ("iOS", StringComparison.Ordinal))
												throw new NotImplementedException ();
											break;
										}
										break;
									case "?all-tvos":
										switch (task.Platform) {
										case TestPlatform.tvOS:
											is_match = true;
											break;
										default:
											if (task.Platform.ToString ().StartsWith ("tvOS", StringComparison.Ordinal))
												throw new NotImplementedException ();
											break;
										}
										break;
									case "?all-watchos":
										switch (task.Platform) {
										case TestPlatform.watchOS:
										case TestPlatform.watchOS_32:
										case TestPlatform.watchOS_64_32:
											is_match = true;
											break;
										default:
											if (task.Platform.ToString ().StartsWith ("watchOS", StringComparison.Ordinal))
												throw new NotImplementedException ();
											break;
										}
										break;
									case "?all-mac":
										switch (task.Platform) {
										case TestPlatform.Mac:
										case TestPlatform.Mac_Modern:
										case TestPlatform.Mac_Full:
										case TestPlatform.Mac_System:
											is_match = true;
											break;
										default:
											if (task.Platform.ToString ().StartsWith ("Mac", StringComparison.Ordinal))
												throw new NotImplementedException ();
											break;
										}
										break;
									default:
										writer.WriteLine ("unknown query: {0}", request.Url.Query);
										break;
									}
									if (request.Url.LocalPath == "/select") {
										if (is_match.HasValue && is_match.Value)
											task.Ignored = false;
									} else if (request.Url.LocalPath == "/deselect") {
										if (is_match.HasValue && is_match.Value)
											task.Ignored = true;
									}
								}

								writer.WriteLine ("OK");
							}
							break;
						case "/stop":
							response.ContentType = System.Net.Mime.MediaTypeNames.Text.Plain;
							using (var writer = new StreamWriter (response.OutputStream)) {
								foreach (var task in find_tasks (writer, request.Url.Query)) {
									if (!task.Waiting) {
										writer.WriteLine ($"Test '{task.TestName}' is not in a waiting state.");
									} else {
										task.Reset ();
									}
								}
								writer.WriteLine ("OK");
							}
							break;
						case "/run":
							response.ContentType = System.Net.Mime.MediaTypeNames.Text.Plain;
							using (var writer = new StreamWriter (response.OutputStream)) {
								// We want to randomize the order the tests are added, so that we don't build first the test for one device, 
								// then for another, since that would not take advantage of running tests on several devices in parallel.
								foreach (var task in find_tasks (writer, request.Url.Query).Shuffle ()) {
									if (task.InProgress || task.Waiting) {
										writer.WriteLine ($"Test '{task.TestName}' is already executing.");
									} else {
										task.Reset ();
										task.BuildOnly = false;
										task.RunAsync ();
									}
								}
								writer.WriteLine ("OK");
							}
							break;
						case "/build":
							response.ContentType = System.Net.Mime.MediaTypeNames.Text.Plain;
							using (var writer = new StreamWriter (response.OutputStream)) {
								foreach (var task in find_tasks (writer, request.Url.Query)) {
									if (task.InProgress || task.Waiting) {
										writer.WriteLine ($"Test '{task.TestName}' is already executing.");
									} else if (task is RunTestTask rtt) {
										rtt.Reset ();
										rtt.BuildAsync ().ContinueWith ((z) => {
											if (rtt.ExecutionResult == TestExecutingResult.Built)
												rtt.ExecutionResult = TestExecutingResult.BuildSucceeded;
										 });
									} else {
										writer.WriteLine ($"Test '{task.TestName}' is not a test that can be only built.");
									}
								}

								writer.WriteLine ("OK");
							}
							break;
						case "/reload-devices":
							LoadAsync (ref DeviceLoadLog, Devices, "Device").DoNotAwait ();
							break;
						case "/reload-simulators":
							LoadAsync (ref SimulatorLoadLog, Simulators, "Simulator").DoNotAwait ();
							break;
						case "/quit":
							using (var writer = new StreamWriter (response.OutputStream)) {
								writer.WriteLine ("<!DOCTYPE html>");
								writer.WriteLine ("<html>");
								writer.WriteLine ("<body onload='close ();'>Closing web page...</body>");
								writer.WriteLine ("</html>");
							}
							server.Stop ();
							break;
						case "/favicon.ico":
							serveFile = Path.Combine (Harness.RootDirectory, "xharness", "favicon.ico");
							goto default;
						case "/index.html":
							var redirect_to = request.Url.AbsoluteUri.Replace ("/index.html", "/" + Path.GetFileName (LogDirectory) + "/index.html");
							response.Redirect (redirect_to);
							break;
						default:
							var filename = Path.GetFileName (request.Url.LocalPath);
							if (filename == "index.html" && Path.GetFileName (LogDirectory) == Path.GetFileName (Path.GetDirectoryName (request.Url.LocalPath))) {
									// We're asked for the report for the current test run, so re-generate it.
								GenerateReport ();
							}

							if (serveFile == null)
								serveFile = Path.Combine (Path.GetDirectoryName (LogDirectory), request.Url.LocalPath.Substring (1));
							var path = serveFile;
							if (File.Exists (path)) {
								var buffer = new byte [4096];
								using (var fs = new FileStream (path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
									int read;
									response.ContentLength64 = fs.Length;
									switch (Path.GetExtension (path).ToLowerInvariant ()) {
									case ".html":
										response.ContentType = System.Net.Mime.MediaTypeNames.Text.Html;
										break;
									case ".css":
										response.ContentType = "text/css";
										break;
									case ".js":
										response.ContentType = "text/javascript";
										break;
									case ".ico":
										response.ContentType = "image/png";
										break;
									default:
										response.ContentType = System.Net.Mime.MediaTypeNames.Text.Plain;
										break;
									}
									while ((read = fs.Read (buffer, 0, buffer.Length)) > 0)
										response.OutputStream.Write (buffer, 0, read);
								}
							} else {
								Console.WriteLine ($"404: {request.Url.LocalPath}");
								response.StatusCode = 404;
								response.OutputStream.WriteByte ((byte) '?');
							}
							break;
						}
					} catch (IOException ioe) {
						Console.WriteLine (ioe.Message);
					} catch (Exception e) {
						Console.WriteLine (e);
					}
					response.Close ();
				}
				tcs.SetResult (true);
			})
			{
				IsBackground = true,
			};
			thread.Start ();

			var url = $"http://localhost:{port}/" + Path.GetFileName (LogDirectory) + "/index.html";
			Console.WriteLine ($"Launching {url} in the system's default browser.");
			Process.Start ("open", url);

			return tcs.Task;
		}

		string GetTestColor (IEnumerable<TestTask> tests)
		{
			if (!tests.Any ())
				return "black";

			var first = tests.First ();
			if (tests.All ((v) => v.ExecutionResult == first.ExecutionResult))
				return GetTestColor (first);
			if (tests.Any ((v) => v.Crashed))
				return "maroon";
			else if (tests.Any ((v) => v.TimedOut))
				return "purple";
			else if (tests.Any ((v) => v.BuildFailure))
				return "darkred";
			else if (tests.Any ((v) => v.Failed))
				return "red";
			else if (tests.Any ((v) => v.NotStarted))
				return "black";
			else if (tests.Any ((v) => v.Ignored))
				return "gray";
			else if (tests.Any ((v) => v.DeviceNotFound))
				return "orangered";
			else if (tests.All ((v) => v.BuildSucceeded))
				return "lightgreen";
			else if (tests.All ((v) => v.Succeeded))
				return "green";
			else
				return "black";
		}

		string GetTestColor (TestTask test)
		{
			if (test.NotStarted) {
				return "black";
			} else if (test.InProgress) {
				if (test.Building) {
					return "darkblue";
				} else if (test.Running) {
					return "lightblue";
				} else {
					return "blue";
				}
			} else {
				if (test.Crashed) {
					return "maroon";
				} else if (test.HarnessException) {
					return "orange";
				} else if (test.TimedOut) {
					return "purple";
				} else if (test.BuildFailure) {
					return "darkred";
				} else if (test.Failed) {
					return "red";
				} else if (test.BuildSucceeded) {
					return "lightgreen";
				} else if (test.Succeeded) {
					return "green";
				} else if (test.Ignored) {
					return "gray";
				} else if (test.Waiting) {
					return "darkgray";
				} else if (test.DeviceNotFound) {
					return "orangered";
				} else {
					return "pink";
				}
			}
		}

		object report_lock = new object ();
		public void GenerateReport ()
		{
			try {
				lock (report_lock) {
					var report = Path.Combine (LogDirectory, "index.html");
					var tmpreport = Path.Combine (LogDirectory, $"index-{Harness.Timestamp}.tmp.html");
					var tmpmarkdown = string.IsNullOrEmpty (Harness.MarkdownSummaryPath) ? string.Empty : (Harness.MarkdownSummaryPath + $".{Harness.Timestamp}.tmp");
					using (var stream = new FileStream (tmpreport, FileMode.Create, FileAccess.ReadWrite)) {
						using (var markdown_writer = (string.IsNullOrEmpty (tmpmarkdown) ? null : new StreamWriter (tmpmarkdown))) {
							GenerateReportImpl (stream, markdown_writer);
						}
					}
					if (File.Exists (report))
						File.Delete (report);
					File.Move (tmpreport, report);
					if (!string.IsNullOrEmpty (tmpmarkdown)) {
						if (File.Exists (Harness.MarkdownSummaryPath))
							File.Delete (Harness.MarkdownSummaryPath);
						File.Move (tmpmarkdown, Harness.MarkdownSummaryPath);
					}
					var dependentFileLocation = Path.GetDirectoryName (System.Reflection.Assembly.GetExecutingAssembly ().Location);
					foreach (var file in new string [] { "xharness.js", "xharness.css" }) {
						File.Copy (Path.Combine (dependentFileLocation, file), Path.Combine (LogDirectory, file), true);
					}
					File.Copy (Path.Combine (Harness.RootDirectory, "xharness", "favicon.ico"), Path.Combine (LogDirectory, "favicon.ico"), true);
				}
			} catch (Exception e) {
				this.MainLog.WriteLine ("Failed to write log: {0}", e);
			}
		}

		public bool IsHE0038Error (Log log) {
			if (log == null)
				return false;
			if (File.Exists (log.FullPath) && new FileInfo (log.FullPath).Length > 0) {
				using (var reader = log.GetReader ()) {
					while (!reader.EndOfStream) {
						string line = reader.ReadLine ();
						if (line == null)
							continue;
						if (line.Contains ("error HE0038: Failed to launch the app"))
							return true;
					}
				}
			}
			return false;
		}
		
		string previous_test_runs;
		void GenerateReportImpl (Stream stream, StreamWriter markdown_summary = null)
		{
			var id_counter = 0;

			var allSimulatorTasks = new List<RunSimulatorTask> ();
			var allExecuteTasks = new List<MacExecuteTask> ();
			var allNUnitTasks = new List<NUnitExecuteTask> ();
			var allMakeTasks = new List<MakeTask> ();
			var allDeviceTasks = new List<RunDeviceTask> ();
			foreach (var task in Tasks) {
				var aggregated = task as AggregatedRunSimulatorTask;
				if (aggregated != null) {
					allSimulatorTasks.AddRange (aggregated.Tasks);
					continue;
				}

				var execute = task as MacExecuteTask;
				if (execute != null) {
					allExecuteTasks.Add (execute);
					continue;
				}

				var nunit = task as NUnitExecuteTask;
				if (nunit != null) {
					allNUnitTasks.Add (nunit);
					continue;
				}

				var make = task as MakeTask;
				if (make != null) {
					allMakeTasks.Add (make);
					continue;
				}

				var run_device = task as RunDeviceTask;
				if (run_device != null) {
					allDeviceTasks.Add (run_device);
					continue;
				}

				throw new NotImplementedException ();
			}

			var allTasks = new List<TestTask> ();
			if (!populating) {
				allTasks.AddRange (allExecuteTasks);
				allTasks.AddRange (allSimulatorTasks);
				allTasks.AddRange (allNUnitTasks);
				allTasks.AddRange (allMakeTasks);
				allTasks.AddRange (allDeviceTasks);
			}

			var failedTests = allTasks.Where ((v) => v.Failed);
			var deviceNotFound = allTasks.Where ((v) => v.DeviceNotFound);
			var unfinishedTests = allTasks.Where ((v) => !v.Finished);
			var passedTests = allTasks.Where ((v) => v.Succeeded);
			var runningTests = allTasks.Where ((v) => v.Running && !v.Waiting);
			var buildingTests = allTasks.Where ((v) => v.Building && !v.Waiting);
			var runningQueuedTests = allTasks.Where ((v) => v.Running && v.Waiting);
			var buildingQueuedTests = allTasks.Where ((v) => v.Building && v.Waiting);

			if (markdown_summary != null) {
				if (unfinishedTests.Any () || failedTests.Any () || deviceNotFound.Any ()) {
					// Don't print when all tests succeed (cleaner)
					markdown_summary.WriteLine ("# Test results");
					markdown_summary.WriteLine ();
				}
				var details = failedTests.Any ();
				if (details) {
					markdown_summary.WriteLine ("<details>");
					markdown_summary.Write ("<summary>");
				}
				if (allTasks.Count == 0) {
					markdown_summary.Write ($"Loading tests...");
				} else if (unfinishedTests.Any ()) {
					var list = new List<string> ();
					var grouped = allTasks.GroupBy ((v) => v.ExecutionResult).OrderBy ((v) => (int) v.Key);
					foreach (var @group in grouped)
						list.Add ($"{@group.Key.ToString ()}: {@group.Count ()}");
					markdown_summary.Write ($"# Test run in progress: ");
					markdown_summary.Write (string.Join (", ", list));
				} else if (failedTests.Any ()) {
					markdown_summary.Write ($"{failedTests.Count ()} tests failed, ");
					if (deviceNotFound.Any ())
						markdown_summary.Write ($"{deviceNotFound.Count ()} tests' device not found, ");
					markdown_summary.Write ($"{passedTests.Count ()} tests passed.");
				} else if (deviceNotFound.Any ()) {
					markdown_summary.Write ($"{deviceNotFound.Count ()} tests' device not found, {passedTests.Count ()} tests passed.");
				} else if (passedTests.Any ()) {
					markdown_summary.Write ($"# :tada: All {passedTests.Count ()} tests passed :tada:");
				} else {
					markdown_summary.Write ($"# No tests selected.");
				}
				if (details)
					markdown_summary.Write ("</summary>");
				markdown_summary.WriteLine ();
				markdown_summary.WriteLine ();
				if (failedTests.Any ()) {
					markdown_summary.WriteLine ("## Failed tests");
					markdown_summary.WriteLine ();
					foreach (var t in failedTests) {
						markdown_summary.Write ($" * {t.TestName}");
						if (!string.IsNullOrEmpty (t.Mode))
							markdown_summary.Write ($"/{t.Mode}");
						if (!string.IsNullOrEmpty (t.Variation))
							markdown_summary.Write ($"/{t.Variation}");
						markdown_summary.Write ($": {t.ExecutionResult}");
						if (!string.IsNullOrEmpty (t.FailureMessage))
							markdown_summary.Write ($" ({t.FailureMessage})");
						markdown_summary.WriteLine ();
					}
				}
				if (details)
					markdown_summary.WriteLine ("</details>");
			}

			using (var writer = new StreamWriter (stream)) {
				writer.WriteLine ("<!DOCTYPE html>");
				writer.WriteLine ("<html onkeypress='keyhandler(event)' lang='en'>");
				if (IsServerMode && populating)
					writer.WriteLine ("<meta http-equiv=\"refresh\" content=\"1\">");
				writer.WriteLine ("<head>");
				writer.WriteLine ("<link rel='stylesheet' href='xharness.css'>");
				writer.WriteLine ("<title>Test results</title>");
				writer.WriteLine (@"<script type='text/javascript' src='xharness.js'></script>");
				if (IsServerMode) {
					writer.WriteLine ("<script type='text/javascript'>");
					writer.WriteLine ("setTimeout (autorefresh, 1000);");
					writer.WriteLine ("</script>");
				}
				writer.WriteLine ("</head>");
				writer.WriteLine ("<body onload='oninitialload ();'>");

				if (IsServerMode) {
					writer.WriteLine ("<div id='quit' style='position:absolute; top: 20px; right: 20px;'><a href='javascript:quit()'>Quit</a><br/><a id='ajax-log-button' href='javascript:toggleAjaxLogVisibility ();'>Show log</a></div>");
					writer.WriteLine ("<div id='ajax-log' style='position:absolute; top: 200px; right: 20px; max-width: 100px; display: none;'></div>");
				}

				writer.WriteLine ("<h1>Test results</h1>");

				foreach (var log in Logs)
					writer.WriteLine ("<span id='x{2}' class='autorefreshable'> <a href='{0}' type='text/plain'>{1}</a></span><br />", log.FullPath.Substring (LogDirectory.Length + 1), log.Description, id_counter++);

				var headerColor = "black";
				if (unfinishedTests.Any ()) {
					; // default
				} else if (failedTests.Any ()) {
					headerColor = "red";
				} else if (deviceNotFound.Any ()) {
					headerColor = "orange";
				} else if (passedTests.Any ()) {
					headerColor = "green";
				} else {
					headerColor = "gray";
				}

				writer.Write ($"<h2 style='color: {headerColor}'>");
				writer.Write ($"<span id='x{id_counter++}' class='autorefreshable'>");
				if (allTasks.Count == 0) {
					writer.Write ($"Loading tests...");
				} else if (unfinishedTests.Any ()) {
					writer.Write ($"Test run in progress (");
					var list = new List<string> ();
					var grouped = allTasks.GroupBy ((v) => v.ExecutionResult).OrderBy ((v) => (int) v.Key);
					foreach (var @group in grouped)
						list.Add ($"<span style='color: {GetTestColor (@group)}'>{@group.Key.ToString ()}: {@group.Count ()}</span>");
					writer.Write (string.Join (", ", list));
					writer.Write (")");
				} else if (failedTests.Any ()) {
					writer.Write ($"{failedTests.Count ()} tests failed, ");
					if (deviceNotFound.Any ())
						writer.Write ($"{deviceNotFound.Count ()} tests' device not found, ");
					writer.Write ($"{passedTests.Count ()} tests passed");
				} else if (deviceNotFound.Any ()) {
					writer.Write ($"{deviceNotFound.Count ()} tests' device not found, {passedTests.Count ()} tests passed");
				} else if (passedTests.Any ()) {
					writer.Write ($"All {passedTests.Count ()} tests passed");
				} else {
					writer.Write ($"No tests selected.");
				}
				writer.Write ("</span>");
				writer.WriteLine ("</h2>");
				if (allTasks.Count > 0) {
					writer.WriteLine ($"<ul id='nav'>");
					if (IsServerMode) {
						writer.WriteLine (@"
	<li>Select
		<ul>
			<li class=""adminitem""><a href='javascript:sendrequest (""/select?all"");'>All tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/select?all-device"");'>All device tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/select?all-simulator"");'>All simulator tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/select?all-ios"");'>All iOS tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/select?all-tvos"");'>All tvOS tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/select?all-watchos"");'>All watchOS tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/select?all-mac"");'>All Mac tests</a></li>
		</ul>
	</li>
	<li>Deselect
		<ul>
			<li class=""adminitem""><a href='javascript:sendrequest (""/deselect?all"");'>All tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/deselect?all-device"");'>All device tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/deselect?all-simulator"");'>All simulator tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/deselect?all-ios"");'>All iOS tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/deselect?all-tvos"");'>All tvOS tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/deselect?all-watchos"");'>All watchOS tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/deselect?all-mac"");'>All Mac tests</a></li>
		</ul>
	</li>
	<li>Execute
		<ul>
			<li class=""adminitem""><a href='javascript:sendrequest (""/run?alltests"");'>Run all tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/run?selected"");'>Run all selected tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/run?failed"");'>Run all failed tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/build?all"");'>Build all tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/build?selected"");'>Build all selected tests</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/build?failed"");'>Build all failed tests</a></li>
		</ul>
	</li>");
					}
					writer.WriteLine (@"
	<li>Toggle visibility
		<ul>
			<li class=""adminitem""><a href='javascript:toggleAll (true);'>Expand all</a></li>
			<li class=""adminitem""><a href='javascript:toggleAll (false);'>Collapse all</a></li>
			<li class=""adminitem""><a href='javascript:toggleVisibility (""toggleable-ignored"");'>Hide/Show ignored tests</a></li>
		</ul>
	</li>");
					if (IsServerMode) {
						var include_system_permission_option = string.Empty;
						var include_system_permission_icon = string.Empty;
						if (Harness.IncludeSystemPermissionTests == null) {
							include_system_permission_option = "include-permission-tests";
							include_system_permission_icon = "2753";
						} else if (Harness.IncludeSystemPermissionTests.Value) {
							include_system_permission_option = "skip-permission-tests";
							include_system_permission_icon = "2705";
						} else {
							include_system_permission_option = "clear-permission-tests";
							include_system_permission_icon = "274C";
						}
						writer.WriteLine ($@"
	<li>Reload
		<ul>
			<li class=""adminitem""><a href='javascript:sendrequest (""/reload-devices"");'>Devices</a></li>
			<li class=""adminitem""><a href='javascript:sendrequest (""/reload-simulators"");'>Simulators</a></li>
		</ul>
	</li>

	<li>Options
			<ul>
				<li class=""adminitem""><span id='{id_counter++}' class='autorefreshable'><a href='javascript:sendrequest (""/set-option?{(CleanSuccessfulTestRuns ? "do-not-clean" : "clean")}"");'>&#x{(CleanSuccessfulTestRuns ? "2705" : "274C")} Clean successful test runs</a></span></li>
				<li class=""adminitem""><span id='{id_counter++}' class='autorefreshable'><a href='javascript:sendrequest (""/set-option?{(UninstallTestApp ? "do-not-uninstall-test-app" : "uninstall-test-app")}"");'>&#x{(UninstallTestApp ? "2705" : "274C")} Uninstall the app from device before and after the test run</a></span></li>
				<li class=""adminitem""><span id='{id_counter++}' class='autorefreshable'><a href='javascript:sendrequest (""/set-option?{include_system_permission_option}"");'>&#x{include_system_permission_icon} Run tests that require system permissions (might put up permission dialogs)</a></span></li>
			</ul>
	</li>
	");
						if (previous_test_runs == null) {
							var sb = new StringBuilder ();
							var previous = Directory.GetDirectories (Path.GetDirectoryName (LogDirectory)).
									Select ((v) => Path.Combine (v, "index.html")).
									    Where (File.Exists);
							if (previous.Any ()) {
								sb.AppendLine ("\t<li>Previous test runs");
								sb.AppendLine ("\t\t<ul>");
								foreach (var prev in previous.OrderBy ((v) => v).Reverse ()) {
									var dir = Path.GetFileName (Path.GetDirectoryName (prev));
									var ts = dir;
									var description = File.ReadAllLines (prev).Where ((v) => v.StartsWith ("<h2", StringComparison.Ordinal)).FirstOrDefault ();
									if (description != null) {
										description = description.Substring (description.IndexOf ('>') + 1); // <h2 ...>
										description = description.Substring (description.IndexOf ('>') + 1); // <span id= ...>

										var h2end = description.LastIndexOf ("</h2>", StringComparison.Ordinal);
										if (h2end > -1)
											description = description.Substring (0, h2end);
										description = description.Substring (0, description.LastIndexOf ('<'));
									} else {
										description = "<unknown state>";
									}
									sb.AppendLine ($"\t\t\t<li class=\"adminitem\"><a href='/{dir}/index.html'>{ts}: {description}</a></li>");
								}
								sb.AppendLine ("\t\t</ul>");
								sb.AppendLine ("\t</li>");
							}
							previous_test_runs = sb.ToString ();
						}
						if (!string.IsNullOrEmpty (previous_test_runs))
							writer.Write (previous_test_runs);
					}
					writer.WriteLine ("</ul>");
				}

				writer.WriteLine ("<div id='test-table' style='width: 100%; display: flex;'>");
				writer.WriteLine ("<div id='test-list'>");
				var orderedTasks = allTasks.GroupBy ((TestTask v) => v.TestName);

				if (IsServerMode) {
					// In server mode don't take into account anything that can change during a test run
					// when ordering, since it's confusing to have the tests reorder by themselves while
					// you're looking at the web page.
					orderedTasks = orderedTasks.OrderBy ((v) => v.Key, StringComparer.OrdinalIgnoreCase);
				} else {
					// Put failed tests at the top and ignored tests at the end.
					// Then order alphabetically.
					orderedTasks = orderedTasks.OrderBy ((v) =>
					 {
						 if (v.Any ((t) => t.Failed))
							 return -1;
						 if (v.All ((t) => t.Ignored))
							 return 1;
						 return 0;
					 }).
					ThenBy ((v) => v.Key, StringComparer.OrdinalIgnoreCase);
				}
				foreach (var group in orderedTasks) {
					var singleTask = group.Count () == 1;
					var groupId = group.Key.Replace (' ', '-');

					// Test header for multiple tests
					if (!singleTask) {
						var autoExpand = !IsServerMode && group.Any ((v) => v.Failed);
						var ignoredClass = group.All ((v) => v.Ignored) ? "toggleable-ignored" : string.Empty;
						var defaultExpander = autoExpand ? "-" : "+";
						var defaultDisplay = autoExpand ? "block" : "none";
						writer.Write ($"<div class='pdiv {ignoredClass}'>");
						writer.Write ($"<span id='button_container2_{groupId}' class='expander' onclick='javascript: toggleContainerVisibility2 (\"{groupId}\");'>{defaultExpander}</span>");
						writer.Write ($"<span id='x{id_counter++}' class='p1 autorefreshable' onclick='javascript: toggleContainerVisibility2 (\"{groupId}\");'>{group.Key}{RenderTextStates (group)}</span>");
						if (IsServerMode) {
							var groupIds = string.Join (",", group.Where ((v) => string.IsNullOrEmpty (v.KnownFailure)).Select ((v) => v.ID.ToString ()));
							writer.Write ($" <span class='runall'><a href='javascript: runtest (\"{groupIds}\");'>Run all</a> <a href='javascript: buildtest (\"{groupIds}\");'>Build all</a></span>");
						}
						writer.WriteLine ("</div>");
						writer.WriteLine ($"<div id='test_container2_{groupId}' class='togglable' style='display: {defaultDisplay}; margin-left: 20px;'>");
					}

					// Test data
					var groupedByMode = group.GroupBy ((v) => v.Mode);
					foreach (var modeGroup in groupedByMode) {
						var multipleModes = modeGroup.Count () > 1;
						if (multipleModes) {
							var modeGroupId = id_counter++.ToString ();
							var autoExpand = !IsServerMode && modeGroup.Any ((v) => v.Failed);
							var ignoredClass = modeGroup.All ((v) => v.Ignored) ? "toggleable-ignored" : string.Empty;
							var defaultExpander = autoExpand ? "-" : "+";
							var defaultDisplay = autoExpand ? "block" : "none";
							writer.Write ($"<div class='pdiv {ignoredClass}'>");
							writer.Write ($"<span id='button_container2_{modeGroupId}' class='expander' onclick='javascript: toggleContainerVisibility2 (\"{modeGroupId}\");'>{defaultExpander}</span>");
							writer.Write ($"<span id='x{id_counter++}' class='p2 autorefreshable' onclick='javascript: toggleContainerVisibility2 (\"{modeGroupId}\");'>{modeGroup.Key}{RenderTextStates (modeGroup)}</span>");
							if (IsServerMode) {
								var modeGroupIds = string.Join (",", modeGroup.Where ((v) => string.IsNullOrEmpty (v.KnownFailure)).Select ((v) => v.ID.ToString ()));
								writer.Write ($" <span class='runall'><a href='javascript: runtest (\"{modeGroupIds}\");'>Run all</a> <a href='javascript: buildtest (\"{modeGroupIds}\");'>Build all</a></span>");
							}
							writer.WriteLine ("</div>");

							writer.WriteLine ($"<div id='test_container2_{modeGroupId}' class='togglable' style='display: {defaultDisplay}; margin-left: 20px;'>");
						}
						foreach (var test in modeGroup.OrderBy ((v) => v.Variation, StringComparer.OrdinalIgnoreCase)) {
							var runTest = test as RunTestTask;
							string state;
							state = test.ExecutionResult.ToString ();
							var log_id = id_counter++;
							var logs = test.AggregatedLogs.ToList ();
							string title;
							if (multipleModes) {
								title = test.Variation ?? "Default";
							} else if (singleTask) {
								title = test.TestName;
							} else {
								title = test.Mode;
							}

							var autoExpand = !IsServerMode && test.Failed;
							var ignoredClass = test.Ignored ? "toggleable-ignored" : string.Empty;
							var defaultExpander = autoExpand ? "&nbsp;" : "+";
							var defaultDisplay = autoExpand ? "block" : "none";
							var buildOnly = test.BuildOnly ? ", BuildOnly" : string.Empty;

							writer.Write ($"<div class='pdiv {ignoredClass}'>");
							writer.Write ($"<span id='button_{log_id}' class='expander' onclick='javascript: toggleLogVisibility (\"{log_id}\");'>{defaultExpander}</span>");
							// we have a very common error we want to make this easier for the person that is dealing with the results
							var knownFailure = string.Empty;
							if (!string.IsNullOrEmpty (test.KnownFailure))
								knownFailure = $" {test.KnownFailure}";
							writer.Write ($"<span id='x{id_counter++}' class='p3 autorefreshable' onclick='javascript: toggleLogVisibility (\"{log_id}\");'>{title} (<span style='color: {GetTestColor (test)}'>{state}{knownFailure}</span>{buildOnly}) </span>");
							if (IsServerMode) {
								writer.Write ($" <span id='x{id_counter++}' class='autorefreshable'>");
								if (test.Waiting) {
									writer.Write ($" <a class='runall' href='javascript:stoptest ({test.ID})'>Stop</a> ");
								} else if (test.InProgress && !test.Built) {
									// Stopping is not implemented for tasks that are already executing
								} else {
									writer.Write ($" <a class='runall' href='javascript:runtest ({test.ID})'>Run</a> ");
									writer.Write ($" <a class='runall' href='javascript:buildtest ({test.ID})'>Build</a> ");
								}
								writer.Write ("</span> ");
							}
							writer.WriteLine ("</div>");
							writer.WriteLine ($"<div id='logs_{log_id}' class='autorefreshable logs togglable' data-onautorefresh='{log_id}' style='display: {defaultDisplay};'>");

							var testAssemblies = test.ReferencedNunitAndXunitTestAssemblies;
							if (testAssemblies.Any ())
								writer.WriteLine ($"Test assemblies:<br/>- {String.Join ("<br/>- ", testAssemblies)}<br />");

							if (!string.IsNullOrEmpty (test.KnownFailure))
								writer.WriteLine ($"Known failure: {test.KnownFailure} <br />");

							if (!string.IsNullOrEmpty (test.FailureMessage)) {
								var msg = HtmlFormat (test.FailureMessage);
								var prefix = test.Ignored ? "Ignored" : "Failure";
								if (test.FailureMessage.Contains ('\n')) {
									writer.WriteLine ($"{prefix}:<br /> <div style='margin-left: 20px;'>{msg}</div>");
								} else {
									writer.WriteLine ($"{prefix}: {msg} <br />");
								}
							}
							var progressMessage = test.ProgressMessage;
							if (!string.IsNullOrEmpty (progressMessage))
								writer.WriteLine (HtmlFormat (progressMessage) + " <br />");

							if (runTest != null) {
								if (runTest.BuildTask.Duration.Ticks > 0) {
									writer.WriteLine ($"Project file: {runTest.BuildTask.ProjectFile} <br />");
									writer.WriteLine ($"Platform: {runTest.BuildTask.ProjectPlatform} Configuration: {runTest.BuildTask.ProjectConfiguration} <br />");
									IEnumerable<IDevice> candidates = (runTest as RunDeviceTask)?.Candidates;
									if (candidates == null)
										candidates = (runTest as RunSimulatorTask)?.Candidates;
									if (candidates != null) {
										writer.WriteLine ($"Candidate devices:<br />");
										foreach (var candidate in candidates)
											writer.WriteLine ($"&nbsp;&nbsp;&nbsp;&nbsp;{candidate.Name} (Version: {candidate.OSVersion})<br />");
									}
									writer.WriteLine ($"Build duration: {runTest.BuildTask.Duration} <br />");
								}
								if (test.Duration.Ticks > 0)
									writer.WriteLine ($"Time Elapsed:  {test.TestName} - (waiting time : {test.WaitingDuration} , running time : {test.Duration}) <br />");
								var runDeviceTest = runTest as RunDeviceTask;
								if (runDeviceTest?.Device != null) {
									if (runDeviceTest.CompanionDevice != null) {
										writer.WriteLine ($"Device: {runDeviceTest.Device.Name} ({runDeviceTest.CompanionDevice.Name}) <br />");
									} else {
										writer.WriteLine ($"Device: {runDeviceTest.Device.Name} <br />");
									}
								}
							} else {
								if (test.Duration.Ticks > 0)
									writer.WriteLine ($"Duration: {test.Duration} <br />");
							}

							if (logs.Count () > 0) {
								foreach (var log in logs) {
									log.Flush ();
									var exists = File.Exists (log.FullPath);
									string log_type = System.Web.MimeMapping.GetMimeMapping (log.FullPath);
									string log_target;
									switch (log_type) {
									case "text/xml":
										log_target = "_top";
										break;
									default:
										log_target = "_self";
										break;
									}
									if (!exists) {
										writer.WriteLine ("<a href='{0}' type='{2}' target='{3}'>{1}</a> (does not exist)<br />", LinkEncode (log.FullPath.Substring (LogDirectory.Length + 1)), log.Description, log_type, log_target);
									} else if (log.Description == "Build log") {
										var binlog = log.FullPath.Replace (".txt", ".binlog");
										if (File.Exists (binlog)) {
											var textLink = string.Format ("<a href='{0}' type='{2}' target='{3}'>{1}</a>", LinkEncode (log.FullPath.Substring (LogDirectory.Length + 1)), log.Description, log_type, log_target);
											var binLink = string.Format ("<a href='{0}' type='{2}' target='{3}' style='display:{4}'>{1}</a><br />", LinkEncode (binlog.Substring (LogDirectory.Length + 1)), "Binlog download", log_type, log_target, test.Building ? "none" : "inline");
											writer.Write ("{0} {1}", textLink, binLink);
										} else {
											writer.WriteLine ("<a href='{0}' type='{2}' target='{3}'>{1}</a><br />", LinkEncode (log.FullPath.Substring (LogDirectory.Length + 1)), log.Description, log_type, log_target);
										}
									} else {
										writer.WriteLine ("<a href='{0}' type='{2}' target='{3}'>{1}</a><br />", LinkEncode (log.FullPath.Substring (LogDirectory.Length + 1)), log.Description, log_type, log_target);
									}
									if (!exists) {
										// Don't try to parse files that don't exist
									} else if (log.Description == "Test log" || log.Description == "Extension test log" || log.Description == "Execution log") {
										string summary;
										List<string> fails;
										try {
											using (var reader = log.GetReader ()) {
												Tuple<long, object> data;
												if (!log_data.TryGetValue (log, out data) || data.Item1 != reader.BaseStream.Length) {
													summary = string.Empty;
													fails = new List<string> ();
													while (!reader.EndOfStream) {
														string line = reader.ReadLine ()?.Trim ();
														if (line == null)
															continue;
														if (line.StartsWith ("Tests run:", StringComparison.Ordinal)) {
															summary = line;
														} else if (line.StartsWith ("[FAIL]", StringComparison.Ordinal)) {
															fails.Add (line);
														}
													}
												} else {
													var data_tuple = (Tuple<string, List<string>>) data.Item2;
													summary = data_tuple.Item1;
													fails = data_tuple.Item2;
												}
												if (fails.Count > 100) {
													fails.Add ("...");
													break;
												}
											}
											if (fails.Count > 0) {
												writer.WriteLine ("<div style='padding-left: 15px;'>");
												foreach (var fail in fails)
													writer.WriteLine ("{0} <br />", HtmlFormat (fail));
												writer.WriteLine ("</div>");
											}
											if (!string.IsNullOrEmpty (summary))
												writer.WriteLine ("<span style='padding-left: 15px;'>{0}</span><br />", summary);
										} catch (Exception ex) {
											writer.WriteLine ("<span style='padding-left: 15px;'>Could not parse log file: {0}</span><br />", HtmlFormat (ex.Message));
										}
									} else if (log.Description == "Build log") {
										HashSet<string> errors;
										try {
											using (var reader = log.GetReader ()) {
												Tuple<long, object> data;
												if (!log_data.TryGetValue (log, out data) || data.Item1 != reader.BaseStream.Length) {
													errors = new HashSet<string> ();
													while (!reader.EndOfStream) {
														string line = reader.ReadLine ()?.Trim ();
														if (line == null)
															continue;
														// Sometimes we put error messages in pull request descriptions
														// Then Jenkins create environment variables containing the pull request descriptions (and other pull request data)
														// So exclude any lines matching 'ghprbPull', to avoid reporting those environment variables as build errors.
														if (line.Contains (": error") && !line.Contains ("ghprbPull")) {
															errors.Add (line);
															if (errors.Count > 20) {
																errors.Add ("...");
																break;
															}
														}
													}
													log_data [log] = new Tuple<long, object> (reader.BaseStream.Length, errors);
												} else {
													errors = (HashSet<string>) data.Item2;
												}
											}
											if (errors.Count > 0) {
												writer.WriteLine ("<div style='padding-left: 15px;'>");
												foreach (var error in errors)
													writer.WriteLine ("{0} <br />", HtmlFormat (error));
												writer.WriteLine ("</div>");
											}
										} catch (Exception ex) {
											writer.WriteLine ("<span style='padding-left: 15px;'>Could not parse log file: {0}</span><br />", HtmlFormat (ex.Message));
										}
									} else if (log.Description == "NUnit results" || log.Description == "XML log") {
										try {
											if (File.Exists (log.FullPath) && new FileInfo (log.FullPath).Length > 0) {
												var doc = new System.Xml.XmlDocument ();
												doc.LoadWithoutNetworkAccess (log.FullPath);
												var failures = doc.SelectNodes ("//test-case[@result='Error' or @result='Failure']").Cast<System.Xml.XmlNode> ().ToArray ();
												if (failures.Length > 0) {
													writer.WriteLine ("<div style='padding-left: 15px;'>");
													writer.WriteLine ("<ul>");
													foreach (var failure in failures) {
														writer.WriteLine ("<li>");
														var test_name = failure.Attributes ["name"]?.Value;
														var message = failure.SelectSingleNode ("failure/message")?.InnerText;
														writer.Write (HtmlFormat (test_name));
														if (!string.IsNullOrEmpty (message)) {
															writer.Write (": ");
															writer.Write (HtmlFormat (message));
														}
														writer.WriteLine ("<br />");
														writer.WriteLine ("</li>");
													}
													writer.WriteLine ("</ul>");
													writer.WriteLine ("</div>");
												}
											}
										} catch (Exception ex) {
											writer.WriteLine ($"<span style='padding-left: 15px;'>Could not parse {log.Description}: {HtmlFormat (ex.Message)}</span><br />");
										}
									}
								}
							}
							writer.WriteLine ("</div>");
						}
						if (multipleModes)
							writer.WriteLine ("</div>");
					}
					if (!singleTask)
						writer.WriteLine ("</div>");
				}
				writer.WriteLine ("</div>");
				if (IsServerMode) {
					writer.WriteLine ("<div id='test-status' style='margin-left: 100px;' class='autorefreshable'>");
					if (failedTests.Count () == 0) {
						foreach (var group in failedTests.GroupBy ((v) => v.TestName)) {
							var enumerableGroup = group as IEnumerable<TestTask>;
							if (enumerableGroup != null) {
								writer.WriteLine ("<a href='#test_{2}'>{0}</a> ({1})<br />", group.Key, string.Join (", ", enumerableGroup.Select ((v) => string.Format ("<span style='color: {0}'>{1}</span>", GetTestColor (v), string.IsNullOrEmpty (v.Mode) ? v.ExecutionResult.ToString () : v.Mode)).ToArray ()), group.Key.Replace (' ', '-'));
								continue;
							}

							throw new NotImplementedException ();
						}
					}

					if (buildingTests.Any ()) {
						writer.WriteLine ($"<h3>{buildingTests.Count ()} building tests:</h3>");
						foreach (var test in buildingTests) {
							var runTask = test as RunTestTask;
							var buildDuration = string.Empty;
							if (runTask != null)
								buildDuration = runTask.BuildTask.Duration.ToString ();
							writer.WriteLine ($"<a href='#test_{test.TestName}'>{test.TestName} ({test.Mode})</a> {buildDuration}<br />");
						}
					}

					if (runningTests.Any ()) {
						writer.WriteLine ($"<h3>{runningTests.Count ()} running tests:</h3>");
						foreach (var test in runningTests) {
							writer.WriteLine ($"<a href='#test_{test.TestName}'>{test.TestName} ({test.Mode})</a> {test.Duration.ToString ()} {HtmlFormat ("\n\t" + test.ProgressMessage)}<br />");
						}
					}

					if (buildingQueuedTests.Any ()) {
						writer.WriteLine ($"<h3>{buildingQueuedTests.Count ()} tests in build queue:</h3>");
						foreach (var test in buildingQueuedTests) {
							writer.WriteLine ($"<a href='#test_{test.TestName}'>{test.TestName} ({test.Mode})</a><br />");
						}
					}

					if (runningQueuedTests.Any ()) {
						writer.WriteLine ($"<h3>{runningQueuedTests.Count ()} tests in run queue:</h3>");
						foreach (var test in runningQueuedTests) {
							writer.WriteLine ($"<a href='#test_{test.TestName}'>{test.TestName} ({test.Mode})</a><br />");
						}
					}

					var resources = device_resources.Values.Concat (new Resource [] { DesktopResource, NugetResource });
					if (resources.Any ()) {
						writer.WriteLine ($"<h3>Devices/Resources:</h3>");
						foreach (var dr in resources.OrderBy ((v) => v.Description, StringComparer.OrdinalIgnoreCase)) {
							writer.WriteLine ($"{dr.Description} - {dr.Users}/{dr.MaxConcurrentUsers} users - {dr.QueuedUsers} in queue<br />");
						}
					}
				}
				writer.WriteLine ("</div>");
				writer.WriteLine ("</div>");
				writer.WriteLine ("</body>");
				writer.WriteLine ("</html>");
			}
		}
		Dictionary<Log, Tuple<long, object>> log_data = new Dictionary<Log, Tuple<long, object>> ();

		static string HtmlFormat (string value)
		{
			var rv = System.Web.HttpUtility.HtmlEncode (value);
			return rv.Replace ("\t", "&nbsp;&nbsp;&nbsp;&nbsp;").Replace ("\n", "<br/>\n");
		}

		static string LinkEncode (string path)
		{
			return System.Web.HttpUtility.UrlEncode (path).Replace ("%2f", "/").Replace ("+", "%20");
		}

		string RenderTextStates (IEnumerable<TestTask> tests)
		{
			// Create a collection of all non-ignored tests in the group (unless all tests were ignored).
			var allIgnored = tests.All ((v) => v.ExecutionResult == TestExecutingResult.Ignored);
			IEnumerable<TestTask> relevantGroup;
			if (allIgnored) {
				relevantGroup = tests;
			} else {
				relevantGroup = tests.Where ((v) => v.ExecutionResult != TestExecutingResult.NotStarted);
			}
			if (!relevantGroup.Any ())
				return string.Empty;
			
			var results = relevantGroup
				.GroupBy ((v) => v.ExecutionResult)
				.Select ((v) => v.First ()) // GroupBy + Select = Distinct (lambda)
				.OrderBy ((v) => v.ID)
				.Select ((v) => $"<span style='color: {GetTestColor (v)}'>{v.ExecutionResult.ToString ()}</span>")
				.ToArray ();
			return " (" + string.Join ("; ", results) + ")";
		}
	}

	abstract class TestTask
	{
		static int counter;
		public readonly int ID = counter++;

		bool? supports_parallel_execution;

		public Jenkins Jenkins;
		public Harness Harness { get { return Jenkins.Harness; } }
		public TestProject TestProject;
		public string ProjectFile { get { return TestProject?.Path; } }
		public string ProjectConfiguration;
		public string ProjectPlatform;
		public Dictionary<string, string> Environment = new Dictionary<string, string> ();

		public Func<Task> Dependency; // a task that's feteched and awaited before this task's ExecuteAsync method
		public Task InitialTask; // a task that's executed before this task's ExecuteAsync method.
		public Task CompletedTask; // a task that's executed after this task's ExecuteAsync method.

		public bool BuildOnly;
		public string KnownFailure;

		// VerifyRun is called in RunInternalAsync/ExecuteAsync to verify that the task can be executed/run.
		// Typically used to fail tasks that don't have an available device, or if there's not enough disk space.
		public virtual Task VerifyRunAsync ()
		{
			return VerifyDiskSpaceAsync ();
		}

		static DriveInfo RootDrive;
		protected Task VerifyDiskSpaceAsync ()
		{
			if (Finished)
				return Task.CompletedTask;

			if (RootDrive == null)
				RootDrive = new DriveInfo ("/");
			var afs = RootDrive.AvailableFreeSpace;
			const long minSpaceRequirement = 1024 * 1024 * 1024; /* 1 GB */
			if (afs < minSpaceRequirement) {
				FailureMessage = $"Not enough space on the root drive '{RootDrive.Name}': {afs / (1024.0 * 1024):#.##} MB left of {minSpaceRequirement / (1024.0 * 1024):#.##} MB required";
				ExecutionResult = TestExecutingResult.Failed;
			}
			return Task.CompletedTask;
		}

		public void CloneTestProject (TestProject project)
		{
			// Don't build in the original project directory
			// We can build multiple projects in parallel, and if some of those
			// projects have the same project dependencies, then we may end up
			// building the same (dependent) project simultaneously (and they can
			// stomp on eachother).
			// So we clone the project file to a separate directory and build there instead.
			// This is done asynchronously to speed to the initial test load.
			TestProject = project.Clone ();
			InitialTask = TestProject.CreateCopyAsync ();
		}

		protected Stopwatch duration = new Stopwatch ();
		public TimeSpan Duration { 
			get {
				return duration.Elapsed;
			}
		}

		protected Stopwatch waitingDuration = new Stopwatch ();
		public TimeSpan WaitingDuration => waitingDuration.Elapsed;

		TestExecutingResult execution_result;
		public virtual TestExecutingResult ExecutionResult {
			get {
				return execution_result;
			}
			set {
				execution_result = value;
			}
		}

		string failure_message;
		public string FailureMessage {
			get { return failure_message; }
			set {
				failure_message = value;
				MainLog.WriteLine (failure_message);
			}
		}

		public virtual string ProgressMessage { get; }

		public bool NotStarted { get { return (ExecutionResult & TestExecutingResult.StateMask) == TestExecutingResult.NotStarted; } }
		public bool InProgress { get { return (ExecutionResult & TestExecutingResult.InProgress) == TestExecutingResult.InProgress; } }
		public bool Waiting { get { return (ExecutionResult & TestExecutingResult.Waiting) == TestExecutingResult.Waiting; } }
		public bool Finished { get { return (ExecutionResult & TestExecutingResult.Finished) == TestExecutingResult.Finished; } }

		public bool Building { get { return (ExecutionResult & TestExecutingResult.Building) == TestExecutingResult.Building; } }
		public bool Built { get { return (ExecutionResult & TestExecutingResult.Built) == TestExecutingResult.Built; } }
		public bool Running { get { return (ExecutionResult & TestExecutingResult.Running) == TestExecutingResult.Running; } }

		public bool BuildSucceeded { get { return (ExecutionResult & TestExecutingResult.BuildSucceeded) == TestExecutingResult.BuildSucceeded; } }
		public bool Succeeded { get { return (ExecutionResult & TestExecutingResult.Succeeded) == TestExecutingResult.Succeeded; } }
		public bool Failed { get { return (ExecutionResult & TestExecutingResult.Failed) == TestExecutingResult.Failed; } }
		public bool Ignored {
			get { return ExecutionResult == TestExecutingResult.Ignored; }
			set {
				if (ExecutionResult != TestExecutingResult.NotStarted && ExecutionResult != TestExecutingResult.Ignored)
					throw new InvalidOperationException ();
				ExecutionResult = value ? TestExecutingResult.Ignored : TestExecutingResult.NotStarted;
			}
		}
		public bool DeviceNotFound { get { return ExecutionResult == TestExecutingResult.DeviceNotFound; } }

		public bool Crashed { get { return (ExecutionResult & TestExecutingResult.Crashed) == TestExecutingResult.Crashed; } }
		public bool TimedOut { get { return (ExecutionResult & TestExecutingResult.TimedOut) == TestExecutingResult.TimedOut; } }
		public bool BuildFailure { get { return (ExecutionResult & TestExecutingResult.BuildFailure) == TestExecutingResult.BuildFailure; } }
		public bool HarnessException { get { return (ExecutionResult & TestExecutingResult.HarnessException) == TestExecutingResult.HarnessException; } }

		public virtual string Mode { get; set; }
		public virtual string Variation { get; set; }

		protected static string Timestamp {
			get {
				return Harness.Timestamp;
			}
		}

		public bool HasCustomTestName {
			get {
				return test_name != null;
			}
		}

		string test_name;
		public virtual string TestName {
			get {
				if (test_name != null)
					return test_name;
				
				var rv = Path.GetFileNameWithoutExtension (ProjectFile);
				if (rv == null)
					return $"unknown test name ({GetType ().Name}";
				switch (Platform) {
				case TestPlatform.Mac:
					return rv;
				case TestPlatform.Mac_Modern:
					return rv;//.Substring (0, rv.Length - "-unified".Length);
				case TestPlatform.Mac_Full:
					return rv.Substring (0, rv.Length - "-full".Length);
				case TestPlatform.Mac_System:
					return rv.Substring (0, rv.Length - "-system".Length);
				default:
					if (rv.EndsWith ("-watchos", StringComparison.Ordinal)) {
						return rv.Substring (0, rv.Length - 8);
					} else if (rv.EndsWith ("-tvos", StringComparison.Ordinal)) {
						return rv.Substring (0, rv.Length - 5);
					} else if (rv.EndsWith ("-unified", StringComparison.Ordinal)) {
						return rv.Substring (0, rv.Length - 8);
					} else if (rv.EndsWith ("-today", StringComparison.Ordinal)) {
						return rv.Substring (0, rv.Length - 6);
					} else {
						return rv;
					}
				}
			}
			set {
				test_name = value;
			}
		}

		public TestPlatform Platform { get; set; }

		public List<Resource> Resources = new List<Resource> ();

		Log test_log;
		public Log MainLog {
			get {
				if (test_log == null)
					test_log = Logs.Create ($"main-{Timestamp}.log", "Main log");
				return test_log;
			}
		}

		public virtual IEnumerable<Log> AggregatedLogs {
			get {
				return Logs;
			}
		}

		public string LogDirectory {
			get {
				var rv = Path.Combine (Jenkins.LogDirectory, TestName, ID.ToString ());
				Directory.CreateDirectory (rv);
				return rv;
			}
		}

		Logs logs;
		public Logs Logs {
			get {
				return logs ?? (logs = new Logs (LogDirectory));
			}
		}

		IEnumerable<string> referencedNunitAndXunitTestAssemblies;
		public IEnumerable<string> ReferencedNunitAndXunitTestAssemblies {
			get {
				if (referencedNunitAndXunitTestAssemblies != null)
					return referencedNunitAndXunitTestAssemblies;

				if (TestName.Contains ("BCL tests group")) { // avoid loading unrelated projects
					if (!File.Exists (ProjectFile))
						return Enumerable.Empty<string> ();

					var csproj = new XmlDocument ();
					try {
						csproj.LoadWithoutNetworkAccess (ProjectFile.Replace ("\\", "/"));
						referencedNunitAndXunitTestAssemblies = csproj.GetNunitAndXunitTestReferences ();
					} catch (Exception e) {
						referencedNunitAndXunitTestAssemblies = new string [] { $"Exception: {e.Message}", $"Filename: {ProjectFile}" };
					}
				} else {
					referencedNunitAndXunitTestAssemblies = Enumerable.Empty<string> ();
				}
				return referencedNunitAndXunitTestAssemblies;
			}
		}

		Task execute_task;
		async Task RunInternalAsync ()
		{
			if (Finished)
				return;
			
			ExecutionResult = (ExecutionResult & ~TestExecutingResult.StateMask) | TestExecutingResult.InProgress;

			try {
				if (Dependency != null)
 					await Dependency ();

				if (InitialTask != null)
					await InitialTask;
				
				await VerifyRunAsync ();
				if (Finished)
					return;

				duration.Start ();

				execute_task = ExecuteAsync ();
				await execute_task;

				if (CompletedTask != null) {
					if (CompletedTask.Status == TaskStatus.Created)
						CompletedTask.Start ();
					await CompletedTask;
				}

				ExecutionResult = (ExecutionResult & ~TestExecutingResult.StateMask) | TestExecutingResult.Finished;
				if ((ExecutionResult & ~TestExecutingResult.StateMask) == 0)
					throw new Exception ("Result not set!");
			} catch (Exception e) {
				using (var log = Logs.Create ($"execution-failure-{Timestamp}.log", "Execution failure")) {
					ExecutionResult = TestExecutingResult.HarnessException;
					FailureMessage = $"Harness exception for '{TestName}': {e}";
					log.WriteLine (FailureMessage);
				}
				PropagateResults ();
			} finally {
				logs?.Dispose ();
				duration.Stop ();
			}

			Jenkins.GenerateReport ();
		}

		protected virtual void PropagateResults ()
		{
		}

		public virtual void Reset ()
		{
			test_log = null;
			failure_message = null;
			logs = null;
			duration.Reset ();
			execution_result = TestExecutingResult.NotStarted;
			execute_task = null;
		}

		public Task RunAsync ()
		{
			if (execute_task == null)
				execute_task = RunInternalAsync ();
			return execute_task;
		}

		protected abstract Task ExecuteAsync ();

		public override string ToString ()
		{
			return ExecutionResult.ToString ();
		}

		protected void SetEnvironmentVariables (Process process)
		{
			var xcodeRoot = Harness.XcodeRoot;
			
			switch (Platform) {
			case TestPlatform.iOS:
			case TestPlatform.iOS_Unified:
			case TestPlatform.iOS_Unified32:
			case TestPlatform.iOS_Unified64:
			case TestPlatform.iOS_TodayExtension64:
			case TestPlatform.tvOS:
			case TestPlatform.watchOS:
			case TestPlatform.watchOS_32:
			case TestPlatform.watchOS_64_32:
				process.StartInfo.EnvironmentVariables ["MD_APPLE_SDK_ROOT"] = xcodeRoot;
				process.StartInfo.EnvironmentVariables ["MD_MTOUCH_SDK_ROOT"] = Path.Combine (Harness.IOS_DESTDIR, "Library", "Frameworks", "Xamarin.iOS.framework", "Versions", "Current");
				process.StartInfo.EnvironmentVariables ["TargetFrameworkFallbackSearchPaths"] = Path.Combine (Harness.IOS_DESTDIR, "Library", "Frameworks", "Mono.framework", "External", "xbuild-frameworks");
				process.StartInfo.EnvironmentVariables ["MSBuildExtensionsPathFallbackPathsOverride"] = Path.Combine (Harness.IOS_DESTDIR, "Library", "Frameworks", "Mono.framework", "External", "xbuild");
				break;
			case TestPlatform.Mac:
			case TestPlatform.Mac_Modern:
			case TestPlatform.Mac_Full:
			case TestPlatform.Mac_System:
				process.StartInfo.EnvironmentVariables ["MD_APPLE_SDK_ROOT"] = xcodeRoot;
				process.StartInfo.EnvironmentVariables ["TargetFrameworkFallbackSearchPaths"] = Path.Combine (Harness.MAC_DESTDIR, "Library", "Frameworks", "Mono.framework", "External", "xbuild-frameworks");
				process.StartInfo.EnvironmentVariables ["MSBuildExtensionsPathFallbackPathsOverride"] = Path.Combine (Harness.MAC_DESTDIR, "Library", "Frameworks", "Mono.framework", "External", "xbuild");
				process.StartInfo.EnvironmentVariables ["XamarinMacFrameworkRoot"] = Path.Combine (Harness.MAC_DESTDIR, "Library", "Frameworks", "Xamarin.Mac.framework", "Versions", "Current");
				process.StartInfo.EnvironmentVariables ["XAMMAC_FRAMEWORK_PATH"] = Path.Combine (Harness.MAC_DESTDIR, "Library", "Frameworks", "Xamarin.Mac.framework", "Versions", "Current");
				break;
			case TestPlatform.All:
				// Don't set:
				//     MSBuildExtensionsPath 
				//     TargetFrameworkFallbackSearchPaths
				// because these values used by both XM and XI and we can't set it to two different values at the same time.
				// Any test that depends on these values should not be using 'TestPlatform.All'
				process.StartInfo.EnvironmentVariables ["MD_APPLE_SDK_ROOT"] = xcodeRoot;
				process.StartInfo.EnvironmentVariables ["MD_MTOUCH_SDK_ROOT"] = Path.Combine (Harness.IOS_DESTDIR, "Library", "Frameworks", "Xamarin.iOS.framework", "Versions", "Current");
				process.StartInfo.EnvironmentVariables ["XamarinMacFrameworkRoot"] = Path.Combine (Harness.MAC_DESTDIR, "Library", "Frameworks", "Xamarin.Mac.framework", "Versions", "Current");
				process.StartInfo.EnvironmentVariables ["XAMMAC_FRAMEWORK_PATH"] = Path.Combine (Harness.MAC_DESTDIR, "Library", "Frameworks", "Xamarin.Mac.framework", "Versions", "Current");
				break;
			default:
				throw new NotImplementedException ();
			}

			foreach (var kvp in Environment)
				process.StartInfo.EnvironmentVariables [kvp.Key] = kvp.Value;
		}

		protected void AddWrenchLogFiles (StreamReader stream)
		{
			string line;
			while ((line = stream.ReadLine ()) != null) {
				if (!line.StartsWith ("@MonkeyWrench: ", StringComparison.Ordinal))
					continue;

				var cmd = line.Substring ("@MonkeyWrench:".Length).TrimStart ();
				var colon = cmd.IndexOf (':');
				if (colon <= 0)
					continue;
				var name = cmd.Substring (0, colon);
				switch (name) {
				case "AddFile":
					var src = cmd.Substring (name.Length + 1).Trim ();
					Logs.AddFile (src);
					break;
				default:
					Harness.HarnessLog.WriteLine ("Unknown @MonkeyWrench command in {0}: {1}", TestName, name);
					break;
				}
			}
		}

		protected void LogEvent (Log log, string text, params object[] args)
		{
			Jenkins.MainLog.WriteLine (text, args);
			log.WriteLine (text, args);
		}

		public string GuessFailureReason (Log log)
		{
			try {
				using (var reader = log.GetReader ()) {
					string line;
					var error_msg = new System.Text.RegularExpressions.Regex ("([A-Z][A-Z][0-9][0-9][0-9][0-9]:.*)");
					while ((line = reader.ReadLine ()) != null) {
						var match = error_msg.Match (line);
						if (match.Success)
							return match.Groups [1].Captures [0].Value;
					}
				}
			} catch (Exception e) {
				Harness.Log ("Failed to guess failure reason: {0}", e.Message);
			}

			return null;
		}

		// This method will set (and clear) the Waiting flag correctly while waiting on a resource
		// It will also pause the duration.
		public async Task<IAcquiredResource> NotifyBlockingWaitAsync (Task<IAcquiredResource> task)
		{
			var rv = new BlockingWait ();

			// Stop the timer while we're waiting for a resource
			duration.Stop ();
			waitingDuration.Start ();
			ExecutionResult = ExecutionResult | TestExecutingResult.Waiting;
			rv.Wrapped = await task;
			ExecutionResult = ExecutionResult & ~TestExecutingResult.Waiting;
			waitingDuration.Stop ();
			duration.Start ();
			rv.OnDispose = duration.Stop;
			return rv;
		}

		public virtual bool SupportsParallelExecution {
			get {
				return supports_parallel_execution ?? true;
			}
			set {
				supports_parallel_execution = value;
			}
		}

		protected Task<IAcquiredResource> NotifyAndAcquireDesktopResourceAsync ()
		{
			return NotifyBlockingWaitAsync ((SupportsParallelExecution ? Jenkins.DesktopResource.AcquireConcurrentAsync () : Jenkins.DesktopResource.AcquireExclusiveAsync ()));
		}

		class BlockingWait : IAcquiredResource, IDisposable
		{
			public IAcquiredResource Wrapped;
			public Action OnDispose;

			public Resource Resource { get { return Wrapped.Resource; } }

			public void Dispose ()
			{
				OnDispose ();
				Wrapped.Dispose ();
			}
		}
	}

	abstract class BuildToolTask : TestTask
	{
		public bool SpecifyPlatform = true;
		public bool SpecifyConfiguration = true;
		
		public override string Mode {
			get { return Platform.ToString (); }
			set { throw new NotSupportedException (); }
		}

		public virtual Task CleanAsync ()
		{
			Console.WriteLine ("Clean is not implemented for {0}", GetType ().Name);
			return Task.CompletedTask;
		}
	}

	abstract class BuildProjectTask : BuildToolTask
	{
		public string SolutionPath;

		public bool RestoreNugets {
			get {
				return TestProject.RestoreNugetsInProject || !string.IsNullOrEmpty (SolutionPath);
			}
		}

		public override bool SupportsParallelExecution {
			get {
				return Platform.ToString ().StartsWith ("Mac", StringComparison.Ordinal);
			}
		}

		async Task<TestExecutingResult> RestoreNugetsAsync (string projectPath, Log log, bool useXIBuild=false)
		{
			using (var resource = await Jenkins.NugetResource.AcquireExclusiveAsync ()) {
				// we do not want to use xibuild on solutions, we will have some failures with Mac Full
				var isSolution = projectPath.EndsWith (".sln", StringComparison.Ordinal);
				if (!File.Exists (projectPath))
					throw new FileNotFoundException ("Could not find the solution whose nugets to restore.", projectPath);

				using (var nuget = new Process ()) {
					nuget.StartInfo.FileName = useXIBuild && !isSolution ? Harness.XIBuildPath :
						"/Library/Frameworks/Mono.framework/Versions/Current/Commands/nuget";
					var args = new List<string> ();
					args.Add ((useXIBuild && !isSolution ? "/" : "") + "restore"); // diff param depending on the tool
					args.Add (projectPath);
					if (useXIBuild && !isSolution)
						args.Add ("/verbosity:detailed");
					else {
						args.Add ("-verbosity");
						args.Add ("detailed");
					}
					nuget.StartInfo.Arguments = StringUtils.FormatArguments (args);
					SetEnvironmentVariables (nuget);
					LogEvent (log, "Restoring nugets for {0} ({1}) on path {2}", TestName, Mode, projectPath);

					var timeout = TimeSpan.FromMinutes (15);
					var result = await nuget.RunAsync (log, true, timeout);
					if (result.TimedOut) {
						log.WriteLine ("Nuget restore timed out after {0} seconds.", timeout.TotalSeconds);
						return TestExecutingResult.TimedOut;
					} else if (!result.Succeeded) {
						return TestExecutingResult.Failed;
					}
				}

				LogEvent (log, "Restoring nugets completed for {0} ({1}) on path {2}", TestName, Mode, projectPath);
				return TestExecutingResult.Succeeded;
			}
		}
		
		List<string> GetNestedReferenceProjects (string csproj)
		{
			if (!File.Exists (csproj))
				throw new FileNotFoundException ("Could not find the project whose reference projects needed to be found.", csproj);
			var result = new List<string> ();
			var doc = new XmlDocument ();
			doc.Load (csproj.Replace ("\\", "/"));
			foreach (var referenceProject in doc.GetProjectReferences ()) {
				var fixPath = referenceProject.Replace ("\\", "/"); // do the replace in case we use win paths
				result.Add (fixPath);
				// get all possible references
				result.AddRange (GetNestedReferenceProjects (fixPath));
			}
			return result;
		}

		// This method must be called with the desktop resource acquired
		// (which is why it takes an IAcquiredResources as a parameter without using it in the function itself).
		protected async Task RestoreNugetsAsync (Log log, IAcquiredResource resource, bool useXIBuild=false)
		{
			if (!RestoreNugets)
				return;

			if (!File.Exists (SolutionPath ?? TestProject.Path))
				throw new FileNotFoundException ("Could not find the solution whose nugets to restore.", SolutionPath ?? TestProject.Path);
				
			// might happen that the project does contain reference projects with nugets, grab the reference projects and ensure
			// thast they have the nugets restored (usually, watch os test projects
			if (SolutionPath == null) {
				var references = GetNestedReferenceProjects (TestProject.Path);
				foreach (var referenceProject in references) {
					var execResult = await RestoreNugetsAsync (referenceProject, log, useXIBuild); // do the replace in case we use win paths
					if (execResult == TestExecutingResult.TimedOut) {
						ExecutionResult = execResult;
						return;
					}
				}
			}

			// restore for the main project/solution]
			ExecutionResult = await RestoreNugetsAsync (SolutionPath ?? TestProject.Path, log, useXIBuild);
		}
	}

	class MakeTask : BuildToolTask
	{
		public string Target;
		public string WorkingDirectory;
		public TimeSpan Timeout = TimeSpan.FromMinutes (5);

		protected override async Task ExecuteAsync ()
		{
			using (var resource = await NotifyAndAcquireDesktopResourceAsync ()) {
				using (var make = new Process ()) {
					make.StartInfo.FileName = "make";
					make.StartInfo.WorkingDirectory = WorkingDirectory;
					make.StartInfo.Arguments = Target;
					SetEnvironmentVariables (make);
					var log = Logs.Create ($"make-{Platform}-{Timestamp}.txt", "Build log");
					LogEvent (log, "Making {0} in {1}", Target, WorkingDirectory);
					if (!Harness.DryRun) {
						var timeout = Timeout;
						var result = await make.RunAsync (log, true, timeout);
						if (result.TimedOut) {
							ExecutionResult = TestExecutingResult.TimedOut;
							log.WriteLine ("Make timed out after {0} seconds.", timeout.TotalSeconds);
						} else if (result.Succeeded) {
							ExecutionResult = TestExecutingResult.Succeeded;
						} else {
							ExecutionResult = TestExecutingResult.Failed;
						}
					}
					using (var reader = log.GetReader ())
						AddWrenchLogFiles (reader);
					Jenkins.MainLog.WriteLine ("Made {0} ({1})", TestName, Mode);
				}
			}
		}
	}

	class XBuildTask : BuildProjectTask
	{
		public bool UseMSBuild;

		protected override async Task ExecuteAsync ()
		{
			using (var resource = await NotifyAndAcquireDesktopResourceAsync ()) {
				var log = Logs.Create ($"build-{Platform}-{Timestamp}.txt", "Build log");
				var binlogPath = log.FullPath.Replace (".txt", ".binlog");

				await RestoreNugetsAsync (log, resource, useXIBuild: true);

				using (var xbuild = new Process ()) {
					xbuild.StartInfo.FileName = Harness.XIBuildPath;
					var args = new List<string> ();
					args.Add ("--");
					args.Add ("/verbosity:diagnostic");
					args.Add ($"/bl:{binlogPath}");
					if (SpecifyPlatform)
						args.Add ($"/p:Platform={ProjectPlatform}");
					if (SpecifyConfiguration)
						args.Add ($"/p:Configuration={ProjectConfiguration}");
					args.Add (ProjectFile);
					xbuild.StartInfo.Arguments = StringUtils.FormatArguments (args);
					SetEnvironmentVariables (xbuild);
					if (UseMSBuild)
						xbuild.StartInfo.EnvironmentVariables ["MSBuildExtensionsPath"] = null;
					LogEvent (log, "Building {0} ({1})", TestName, Mode);
					if (!Harness.DryRun) {
						var timeout = TimeSpan.FromMinutes (60);
						var result = await xbuild.RunAsync (log, true, timeout);
						if (result.TimedOut) {
							ExecutionResult = TestExecutingResult.TimedOut;
							log.WriteLine ("Build timed out after {0} seconds.", timeout.TotalSeconds);
						} else if (result.Succeeded) {
							ExecutionResult = TestExecutingResult.Succeeded;
						} else {
							ExecutionResult = TestExecutingResult.Failed;
						}
					}
					Jenkins.MainLog.WriteLine ("Built {0} ({1})", TestName, Mode);
				}

				log.Dispose ();
			}
		}

		async Task CleanProjectAsync (Log log, string project_file, string project_platform, string project_configuration)
		{
			// Don't require the desktop resource here, this shouldn't be that resource sensitive
			using (var xbuild = new Process ()) {
				xbuild.StartInfo.FileName = Harness.XIBuildPath;
				var args = new List<string> ();
				args.Add ("--");
				args.Add ("/verbosity:diagnostic");
				if (project_platform != null)
					args.Add ($"/p:Platform={project_platform}");
				if (project_configuration != null)
					args.Add ($"/p:Configuration={project_configuration}");
				args.Add (project_file);
				args.Add ("/t:Clean");
				xbuild.StartInfo.Arguments = StringUtils.FormatArguments (args);
				SetEnvironmentVariables (xbuild);
				LogEvent (log, "Cleaning {0} ({1}) - {2}", TestName, Mode, project_file);
				var timeout = TimeSpan.FromMinutes (1);
				await xbuild.RunAsync (log, true, timeout);
				log.WriteLine ("Clean timed out after {0} seconds.", timeout.TotalSeconds);
				Jenkins.MainLog.WriteLine ("Cleaned {0} ({1})", TestName, Mode);
			}
		}

		public async override Task CleanAsync ()
		{
			var log = Logs.Create ($"clean-{Platform}-{Timestamp}.txt", "Clean log");
			await CleanProjectAsync (log, ProjectFile, SpecifyPlatform ? ProjectPlatform : null, SpecifyConfiguration ? ProjectConfiguration : null);

			// Iterate over all the project references as well.
			var doc = new System.Xml.XmlDocument ();
			doc.LoadWithoutNetworkAccess (ProjectFile);
			foreach (var pr in doc.GetProjectReferences ()) {
				var path = pr.Replace ('\\', '/');
				await CleanProjectAsync (log, path, SpecifyPlatform ? ProjectPlatform : null, SpecifyConfiguration ? ProjectConfiguration : null);
			}
		}
	}

	class NUnitExecuteTask : RunTestTask
	{
		public string TestLibrary;
		public string TestExecutable;
		public string WorkingDirectory;
		public bool ProduceHtmlReport = true;
		public bool InProcess;
		public TimeSpan Timeout = TimeSpan.FromMinutes (10);

		public NUnitExecuteTask (BuildToolTask build_task)
			: base (build_task)
		{
		}
			
		public void FindNUnitConsoleExecutable (Log log)
		{
			if (!string.IsNullOrEmpty (TestExecutable)) {
				log.WriteLine ("Using existing executable: {0}", TestExecutable);
				return;
			}
				
			var packages_conf = Path.Combine (Path.GetDirectoryName (TestProject.Path), "packages.config");
			var nunit_version = string.Empty;
			var is_packageref = false;
			const string default_nunit_version = "3.9.0";

			if (!File.Exists (packages_conf)) {
				var xml = new XmlDocument ();
				xml.LoadWithoutNetworkAccess (TestProject.Path);
				var packageref = xml.SelectSingleNode ("//*[local-name()='PackageReference' and @Include = 'NUnit.ConsoleRunner']");
				if (packageref != null) {
					is_packageref = true;
					nunit_version = packageref.Attributes ["Version"].InnerText;
					log.WriteLine ("Found PackageReference in {0} for NUnit.ConsoleRunner {1}", TestProject, nunit_version);
				} else {
					nunit_version = default_nunit_version;
					log.WriteLine ("No packages.config found for {0}: assuming nunit version is {1}", TestProject, nunit_version);
				}
			} else {
				using (var str = new StreamReader (packages_conf)) {
					using (var reader = System.Xml.XmlReader.Create (str)) {
						while (reader.Read ()) {
							if (reader.NodeType != System.Xml.XmlNodeType.Element)
								continue;
							if (reader.Name != "package")
								continue;
							var id = reader.GetAttribute ("id");
							if (id != "NUnit.ConsoleRunner" && id != "NUnit.Runners")
								continue;
							nunit_version = reader.GetAttribute ("version");
							break;
						}
					}
				}
				if (nunit_version == string.Empty) {
					nunit_version = default_nunit_version;
					log.WriteLine ("Could not find the NUnit.ConsoleRunner element in {0}, using the default version ({1})", packages_conf, nunit_version);
				} else {
					log.WriteLine ("Found the NUnit.ConsoleRunner/NUnit.Runners element in {0} for {2}, version is: {1}", packages_conf, nunit_version, TestProject.Path);
				}
			}

			if (is_packageref) {
				TestExecutable = Path.Combine (Harness.RootDirectory, "..", "tools", $"nunit3-console-{nunit_version}");
				if (!File.Exists (TestExecutable))
					throw new FileNotFoundException ($"The helper script to execute the unit tests does not exist: {TestExecutable}");
				WorkingDirectory = Path.GetDirectoryName (TestProject.Path);
			} else if (nunit_version [0] == '2') {
				TestExecutable = Path.Combine (Harness.RootDirectory, "..", "packages", "NUnit.Runners." + nunit_version, "tools", "nunit-console.exe");
				WorkingDirectory = Path.Combine (Path.GetDirectoryName (TestExecutable), "lib");
			} else {
				TestExecutable = Path.Combine (Harness.RootDirectory, "..", "packages", "NUnit.ConsoleRunner." + nunit_version, "tools", "nunit3-console.exe");
				WorkingDirectory = Path.GetDirectoryName (TestLibrary);
			}
			TestExecutable = Path.GetFullPath (TestExecutable);
			WorkingDirectory = Path.GetFullPath (WorkingDirectory);
			if (!File.Exists (TestExecutable))
				throw new FileNotFoundException ($"The nunit executable '{TestExecutable}' doesn't exist.");
		}

		public bool IsNUnit3 {
			get {
				return Path.GetFileName (TestExecutable).Contains ("unit3-console");
			}
		}
		public override IEnumerable<Log> AggregatedLogs {
			get {
				return base.AggregatedLogs.Union (BuildTask.Logs);
			}
		}

		public override string Mode {
			get {
				return base.Mode ?? "NUnit";
			}
			set {
				base.Mode = value;
			}
		}

		protected override async Task RunTestAsync ()
		{
			using (var resource = await NotifyAndAcquireDesktopResourceAsync ()) {
				var xmlLog = Logs.CreateFile ($"log-{Timestamp}.xml", "XML log");
				var log = Logs.Create ($"execute-{Timestamp}.txt", "Execution log");
				FindNUnitConsoleExecutable (log);
				using (var proc = new Process ()) {

					proc.StartInfo.WorkingDirectory = WorkingDirectory;
					proc.StartInfo.FileName = Harness.XIBuildPath;
					var args = new List<string> ();
					args.Add ("-t");
					args.Add ("--");
					args.Add (Path.GetFullPath (TestExecutable));
					args.Add (Path.GetFullPath (TestLibrary));
					if (IsNUnit3) {
						args.Add ("-result=" + xmlLog + ";format=nunit2");
						args.Add ("--labels=All");
						if (InProcess)
							args.Add ("--inprocess");
					} else {
						args.Add ("-xml=" + xmlLog);
						args.Add ("-labels");
					}
					proc.StartInfo.Arguments = StringUtils.FormatArguments (args);
					SetEnvironmentVariables (proc);
					foreach (DictionaryEntry de in proc.StartInfo.EnvironmentVariables)
						log.WriteLine ($"export {de.Key}={de.Value}");
					Jenkins.MainLog.WriteLine ("Executing {0} ({1})", TestName, Mode);
					if (!Harness.DryRun) {
						ExecutionResult = TestExecutingResult.Running;
						var result = await proc.RunAsync (log, true, Timeout);
						if (result.TimedOut) {
							FailureMessage = $"Execution timed out after {Timeout.TotalMinutes} minutes.";
							log.WriteLine (FailureMessage);
							ExecutionResult = TestExecutingResult.TimedOut;
						} else if (result.Succeeded) {
							ExecutionResult = TestExecutingResult.Succeeded;
						} else {
							ExecutionResult = TestExecutingResult.Failed;
							FailureMessage = $"Execution failed with exit code {result.ExitCode}";
						}
					}
					Jenkins.MainLog.WriteLine ("Executed {0} ({1})", TestName, Mode);
				}

				if (ProduceHtmlReport) {
					try {
						var output = Logs.Create ($"Log-{Timestamp}.html", "HTML log");
						using (var srt = new StringReader (File.ReadAllText (Path.Combine (Harness.RootDirectory, "HtmlTransform.xslt")))) {
							using (var sri = File.OpenRead (xmlLog)) {
								using (var xrt = System.Xml.XmlReader.Create (srt)) {
									using (var xri = System.Xml.XmlReader.Create (sri)) {
										var xslt = new System.Xml.Xsl.XslCompiledTransform ();
										xslt.Load (xrt);
										using (var xwo = System.Xml.XmlWriter.Create (output, xslt.OutputSettings)) // use OutputSettings of xsl, so it can be output as HTML
										{
											xslt.Transform (xri, xwo);
										}
									}
								}
							}
						}
					} catch (Exception e) {
						log.WriteLine ("Failed to produce HTML report: {0}", e);
					}
				}
			}
		}

		public override void Reset ()
		{
			base.Reset ();
			BuildTask?.Reset ();
		}
	}

	abstract class MacTask : RunTestTask
	{
		public MacTask (BuildToolTask build_task)
			: base (build_task)
		{
		}

		public override string Mode {
			get {
				switch (Platform) {
				case TestPlatform.Mac:
					return "Mac";
				case TestPlatform.Mac_Modern:
					return "Mac Modern";
				case TestPlatform.Mac_Full:
					return "Mac Full";
				case TestPlatform.Mac_System:
					return "Mac System";
				default:
					throw new NotImplementedException (Platform.ToString ());
				}
			}
			set {
				throw new NotSupportedException ();
			}
		}
	}

	class MacExecuteTask : MacTask
	{
		public string Path;
		public bool BCLTest;
		public bool IsUnitTest;

		public MacExecuteTask (BuildToolTask build_task)
			: base (build_task)
		{ 
		}

		public override bool SupportsParallelExecution {
			get {
				if (TestName.Contains ("xammac")) {
					// We run the xammac tests in both Debug and Release configurations.
					// These tests are not written to support parallel execution
					// (there are hard coded paths used for instance), so disable
					// parallel execution for these tests.
					return false;
				}
				if (BCLTest) {
					// We run the BCL tests in multiple flavors (Full/Modern),
					// and the BCL tests are not written to support parallel execution,
					// so disable parallel execution for these tests.
					return false;
				}

				return base.SupportsParallelExecution;
			}
		}

		public override IEnumerable<Log> AggregatedLogs {
			get {
				return base.AggregatedLogs.Union (BuildTask.Logs);
			}
		}

		protected override async Task RunTestAsync ()
		{
			var projectDir = System.IO.Path.GetDirectoryName (ProjectFile);
			var name = System.IO.Path.GetFileName (projectDir);
			if (string.Equals ("mac", name, StringComparison.OrdinalIgnoreCase))
				name = System.IO.Path.GetFileName (System.IO.Path.GetDirectoryName (projectDir));
			var suffix = string.Empty;
			switch (Platform) {
			case TestPlatform.Mac_Modern:
				suffix = "-modern";
				break;
			case TestPlatform.Mac_Full:
				suffix = "-full";
				break;
			case TestPlatform.Mac_System:
				suffix = "-system";
				break;
			}
			if (ProjectFile.EndsWith (".sln", StringComparison.Ordinal)) {
				Path = System.IO.Path.Combine (System.IO.Path.GetDirectoryName (ProjectFile), "bin", BuildTask.ProjectPlatform, BuildTask.ProjectConfiguration + suffix, name + ".app", "Contents", "MacOS", name);
			} else {
				var project = new System.Xml.XmlDocument ();
				project.LoadWithoutNetworkAccess (ProjectFile);
				var outputPath = project.GetOutputPath (BuildTask.ProjectPlatform, BuildTask.ProjectConfiguration).Replace ('\\', '/');
				var assemblyName = project.GetAssemblyName ();
				Path = System.IO.Path.Combine (System.IO.Path.GetDirectoryName (ProjectFile), outputPath, assemblyName + ".app", "Contents", "MacOS", assemblyName);
			}

			using (var resource = await NotifyAndAcquireDesktopResourceAsync ()) {
				using (var proc = new Process ()) {
					proc.StartInfo.FileName = Path;
					if (IsUnitTest) {
						var xml = Logs.CreateFile ($"test-{Platform}-{Timestamp}.xml", "NUnit results");
						proc.StartInfo.Arguments = StringUtils.FormatArguments ($"-result=" + xml);
					}
					if (!Harness.GetIncludeSystemPermissionTests (Platform, false))
						proc.StartInfo.EnvironmentVariables ["DISABLE_SYSTEM_PERMISSION_TESTS"] = "1";
					proc.StartInfo.EnvironmentVariables ["MONO_DEBUG"] = "no-gdb-backtrace";
					Jenkins.MainLog.WriteLine ("Executing {0} ({1})", TestName, Mode);
					var log = Logs.Create ($"execute-{Platform}-{Timestamp}.txt", "Execution log");
					if (!Harness.DryRun) {
						ExecutionResult = TestExecutingResult.Running;

						var snapshot = new CrashReportSnapshot () { Device = false, Harness = Harness, Log = log, Logs = Logs, LogDirectory = LogDirectory };
						await snapshot.StartCaptureAsync ();

						ProcessExecutionResult result = null;
						try {
							var timeout = TimeSpan.FromMinutes (20);

							result = await proc.RunAsync (log, true, timeout);
							if (result.TimedOut) {
								FailureMessage = $"Execution timed out after {timeout.TotalSeconds} seconds.";
								log.WriteLine (FailureMessage);
								ExecutionResult = TestExecutingResult.TimedOut;
							} else if (result.Succeeded) {
								ExecutionResult = TestExecutingResult.Succeeded;
							} else {
								ExecutionResult = TestExecutingResult.Failed;
								FailureMessage = result.ExitCode != 1 ? $"Test run crashed (exit code: {result.ExitCode})." : "Test run failed.";
								log.WriteLine (FailureMessage);
							}
						} finally {
							await snapshot.EndCaptureAsync (TimeSpan.FromSeconds (Succeeded ? 0 : (result?.ExitCode > 1 ? 120 : 5)));
						}
					}
					Jenkins.MainLog.WriteLine ("Executed {0} ({1})", TestName, Mode);
				}
			}
		}
	}

	class RunXtroTask : MacExecuteTask {

		public string WorkingDirectory;

		public RunXtroTask (BuildToolTask build_task) : base (build_task)
		{
		}

		protected override async Task RunTestAsync ()
		{
			var projectDir = System.IO.Path.GetDirectoryName (ProjectFile);
			var name = System.IO.Path.GetFileName (projectDir);

			using (var resource = await NotifyAndAcquireDesktopResourceAsync ()) {
				using (var proc = new Process ()) {
					proc.StartInfo.FileName = "/Library/Frameworks/Mono.framework/Commands/mono";
					var reporter = System.IO.Path.Combine (WorkingDirectory, "xtro-report/bin/Debug/xtro-report.exe");
					var results = System.IO.Path.Combine (Logs.Directory, $"xtro-{Timestamp}");
					proc.StartInfo.Arguments = $"--debug {reporter} {WorkingDirectory} {results}";

					Jenkins.MainLog.WriteLine ("Executing {0} ({1})", TestName, Mode);
					var log = Logs.Create ($"execute-xtro-{Timestamp}.txt", "Execution log");
					log.WriteLine ("{0} {1}", proc.StartInfo.FileName, proc.StartInfo.Arguments);
					if (!Harness.DryRun) {
						ExecutionResult = TestExecutingResult.Running;

						var snapshot = new CrashReportSnapshot () { Device = false, Harness = Harness, Log = log, Logs = Logs, LogDirectory = LogDirectory };
						await snapshot.StartCaptureAsync ();

						try {
							var timeout = TimeSpan.FromMinutes (20);

							var result = await proc.RunAsync (log, true, timeout);
							if (result.TimedOut) {
								FailureMessage = $"Execution timed out after {timeout.TotalSeconds} seconds.";
								log.WriteLine (FailureMessage);
								ExecutionResult = TestExecutingResult.TimedOut;
							} else if (result.Succeeded) {
								ExecutionResult = TestExecutingResult.Succeeded;
							} else {
								ExecutionResult = TestExecutingResult.Failed;
								FailureMessage = result.ExitCode != 1 ? $"Test run crashed (exit code: {result.ExitCode})." : "Test run failed.";
								log.WriteLine (FailureMessage);
							}
						} finally {
							await snapshot.EndCaptureAsync (TimeSpan.FromSeconds (Succeeded ? 0 : 5));
						}
					}
					Jenkins.MainLog.WriteLine ("Executed {0} ({1})", TestName, Mode);

					Logs.AddFile (System.IO.Path.Combine (results, "index.html"), "HTML Report");
				}
			}
		}
	}

	abstract class RunTestTask : TestTask
	{
		public readonly BuildToolTask BuildTask;
		public double TimeoutMultiplier { get; set; } = 1;

		public RunTestTask (BuildToolTask build_task)
		{
			this.BuildTask = build_task;

			Jenkins = build_task.Jenkins;
			TestProject = build_task.TestProject;
			Platform = build_task.Platform;
			ProjectPlatform = build_task.ProjectPlatform;
			ProjectConfiguration = build_task.ProjectConfiguration;
			if (build_task.HasCustomTestName)
				TestName = build_task.TestName;
		}

		public override IEnumerable<Log> AggregatedLogs {
			get {
				var rv = base.AggregatedLogs;
				if (BuildTask != null)
					rv = rv.Union (BuildTask.AggregatedLogs);
				return rv;
			}
		}

		public override TestExecutingResult ExecutionResult {
			get {
				// When building, the result is the build result.
				if ((BuildTask.ExecutionResult & (TestExecutingResult.InProgress | TestExecutingResult.Waiting)) != 0)
					return (BuildTask.ExecutionResult & ~TestExecutingResult.InProgressMask) | TestExecutingResult.Building;
				return base.ExecutionResult;
			}
			set {
				base.ExecutionResult = value;
			}
		}

		public async Task<bool> BuildAsync ()
		{
			if (Finished)
				return true;
			
			await VerifyBuildAsync ();
			if (Finished)
				return BuildTask.Succeeded;

			ExecutionResult = TestExecutingResult.Building;
			await BuildTask.RunAsync ();
			if (!BuildTask.Succeeded) {
				if (BuildTask.TimedOut) {
					ExecutionResult = TestExecutingResult.TimedOut;
				} else {
					ExecutionResult = TestExecutingResult.BuildFailure;
				}
				FailureMessage = BuildTask.FailureMessage;
			} else {
				ExecutionResult = TestExecutingResult.Built;
			}
			return BuildTask.Succeeded;
		}

		protected override async Task ExecuteAsync ()
		{
			if (Finished)
				return;

			await VerifyRunAsync ();
			if (Finished)
				return;

			if (!await BuildAsync ())
				return;

			if (BuildOnly) {
				ExecutionResult = TestExecutingResult.BuildSucceeded;
				return;
			}

			ExecutionResult = TestExecutingResult.Running;
			duration.Restart (); // don't count the build time.
			await RunTestAsync ();
		}

		protected abstract Task RunTestAsync ();
		// VerifyBuild is called in BuildAsync to verify that the task can be built.
		// Typically used to fail tasks if there's not enough disk space.
		public virtual Task VerifyBuildAsync ()
		{
			return VerifyDiskSpaceAsync ();
		}

		public override void Reset ()
		{
			base.Reset ();
			BuildTask.Reset ();
		}
	}

	abstract class RunXITask<TDevice> : RunTestTask where TDevice: class, IDevice
	{
		IEnumerable<TDevice> candidates;
		TDevice device;
		TDevice companion_device;
		public AppRunnerTarget AppRunnerTarget;

		protected AppRunner runner;
		protected AppRunner additional_runner;

		public IEnumerable<TDevice> Candidates => candidates;

		public TDevice Device {
			get { return device; }
			protected set { device = value; }
		}

		public TDevice CompanionDevice {
			get { return companion_device; }
			protected set { companion_device = value; }
		}

		public string BundleIdentifier {
			get { return runner.BundleIdentifier; }
		}

		public RunXITask (BuildToolTask build_task, IEnumerable<TDevice> candidates)
			: base (build_task)
		{
			this.candidates = candidates;
		}

		public override IEnumerable<Log> AggregatedLogs {
			get {
				var rv = base.AggregatedLogs;
				if (runner != null)
					rv = rv.Union (runner.Logs);
				if (additional_runner != null)
					rv = rv.Union (additional_runner.Logs);
				return rv;
			}
		}

		public override string Mode {
			get {
				
				switch (Platform) {
				case TestPlatform.tvOS:
				case TestPlatform.watchOS:
					return Platform.ToString () + " - " + XIMode;
				case TestPlatform.watchOS_32:
					return "watchOS 32-bits - " + XIMode;
				case TestPlatform.watchOS_64_32:
					return "watchOS 64-bits (ARM64_32) - " + XIMode;
				case TestPlatform.iOS_Unified32:
					return "iOS Unified 32-bits - " + XIMode;
				case TestPlatform.iOS_Unified64:
					return "iOS Unified 64-bits - " + XIMode;
				case TestPlatform.iOS_TodayExtension64:
					return "iOS Unified Today Extension 64-bits - " + XIMode;
				case TestPlatform.iOS_Unified:
					return "iOS Unified - " + XIMode;
				default:
					throw new NotImplementedException ();
				}
			}
			set { throw new NotImplementedException (); }
		}

		public override async Task VerifyRunAsync ()
		{
			await base.VerifyRunAsync ();
			if (Finished)
				return;

			var enumerable = candidates;
			var asyncEnumerable = enumerable as IAsyncEnumerable;
			if (asyncEnumerable != null)
				await asyncEnumerable.ReadyTask;
			if (!enumerable.Any ()) {
				ExecutionResult = TestExecutingResult.DeviceNotFound;
				FailureMessage = "No applicable devices found.";
			}
		}

		protected abstract string XIMode { get; }

		public override void Reset ()
		{
			base.Reset ();
			runner = null;
			additional_runner = null;
		}
	}

	class RunDeviceTask : RunXITask<Device>
	{
		AppInstallMonitorLog install_log;
		public override string ProgressMessage {
			get {
				var log = install_log;
				if (log == null)
					return base.ProgressMessage;

				var percent_complete = log.CopyingApp ? log.AppPercentComplete : log.WatchAppPercentComplete;
				var bytes = log.CopyingApp ? log.AppBytes : log.WatchAppBytes;
				var total_bytes = log.CopyingApp ? log.AppTotalBytes : log.WatchAppTotalBytes;
				var elapsed = log.CopyingApp ? log.AppCopyDuration : log.WatchAppCopyDuration;
				var speed_bps = elapsed.Ticks == 0 ? -1 : bytes / elapsed.TotalSeconds;
				var estimated_left = TimeSpan.FromSeconds ((total_bytes - bytes) / speed_bps);
				var transfer_percent = 100 * (double) bytes / (double) total_bytes;
				var str = log.CopyingApp ? "App" : "Watch App";
				var rv = $"{str} installation: {percent_complete}% done.\n" +
					$"\tApp size: {total_bytes:N0} bytes ({total_bytes/1024.0/1024.0:N2} MB)\n" +
					$"\tTransferred: {bytes:N0} bytes ({bytes / 1024.0 / 1024.0:N2} MB)\n" +
					$"\tTransferred in {elapsed.TotalSeconds:#.#}s ({elapsed})\n" +
					$"\tTransfer speed: {speed_bps:N0} B/s ({speed_bps / 1024.0 / 1024.0:N} MB/s, {60 * speed_bps / 1024.0 / 1024.0:N2} MB/m)\n" +
					$"\tEstimated time left: {estimated_left.TotalSeconds:#.#}s ({estimated_left})";
				return rv;
			}
		}

		public RunDeviceTask (XBuildTask build_task, IEnumerable<Device> candidates)
			: base (build_task, candidates.OrderBy ((v) => v.DebugSpeed))
		{
			switch (build_task.Platform) {
			case TestPlatform.iOS:
			case TestPlatform.iOS_Unified:
			case TestPlatform.iOS_Unified32:
			case TestPlatform.iOS_Unified64:
				AppRunnerTarget = AppRunnerTarget.Device_iOS;
				break;
			case TestPlatform.iOS_TodayExtension64:
				AppRunnerTarget = AppRunnerTarget.Device_iOS;
				break;
			case TestPlatform.tvOS:
				AppRunnerTarget = AppRunnerTarget.Device_tvOS;
				break;
			case TestPlatform.watchOS:
			case TestPlatform.watchOS_32:
			case TestPlatform.watchOS_64_32:
				AppRunnerTarget = AppRunnerTarget.Device_watchOS;
				break;
			default:
				throw new NotImplementedException ();
			}
		}

		protected override async Task RunTestAsync ()
		{
			Jenkins.MainLog.WriteLine ("Running '{0}' on device (candidates: '{1}')", ProjectFile, string.Join ("', '", Candidates.Select ((v) => v.Name).ToArray ()));

			var uninstall_log = Logs.Create ($"uninstall-{Timestamp}.log", "Uninstall log");
			using (var device_resource = await NotifyBlockingWaitAsync (Jenkins.GetDeviceResources (Candidates).AcquireAnyConcurrentAsync ())) {
				try {
					// Set the device we acquired.
					Device = Candidates.First ((d) => d.UDID == device_resource.Resource.Name);
					if (Device.DevicePlatform == DevicePlatform.watchOS)
						CompanionDevice = Jenkins.Devices.FindCompanionDevice (Jenkins.DeviceLoadLog, Device);
					Jenkins.MainLog.WriteLine ("Acquired device '{0}' for '{1}'", Device.Name, ProjectFile);

					runner = new AppRunner
					{
						Harness = Harness,
						ProjectFile = ProjectFile,
						Target = AppRunnerTarget,
						LogDirectory = LogDirectory,
						MainLog = uninstall_log,
						DeviceName = Device.Name,
						CompanionDeviceName = CompanionDevice?.Name,
						Configuration = ProjectConfiguration,
						TimeoutMultiplier = TimeoutMultiplier,
					};

					// Sometimes devices can't upgrade (depending on what has changed), so make sure to uninstall any existing apps first.
					if (Jenkins.UninstallTestApp) {
						runner.MainLog = uninstall_log;
						var uninstall_result = await runner.UninstallAsync ();
						if (!uninstall_result.Succeeded)
							MainLog.WriteLine ($"Pre-run uninstall failed, exit code: {uninstall_result.ExitCode} (this hopefully won't affect the test result)");
					} else {
						uninstall_log.WriteLine ($"Pre-run uninstall skipped.");
					}

					if (!Failed) {
						// Install the app
						this.install_log = new AppInstallMonitorLog (Logs.Create ($"install-{Timestamp}.log", "Install log"));
						try {
							runner.MainLog = this.install_log;
							var install_result = await runner.InstallAsync (install_log.CancellationToken );
							if (!install_result.Succeeded) {
								FailureMessage = $"Install failed, exit code: {install_result.ExitCode}.";
								ExecutionResult = TestExecutingResult.Failed;
							}
						} finally {
							this.install_log.Dispose ();
							this.install_log = null;
						}
					}

					if (!Failed) {
						// Run the app
						runner.MainLog = Logs.Create ($"run-{Device.UDID}-{Timestamp}.log", "Run log");
						await runner.RunAsync ();

						if (!string.IsNullOrEmpty (runner.FailureMessage))
							FailureMessage = runner.FailureMessage;
						else if (runner.Result != TestExecutingResult.Succeeded)
							FailureMessage = GuessFailureReason (runner.MainLog);

						if (runner.Result == TestExecutingResult.Succeeded && Platform == TestPlatform.iOS_TodayExtension64) {
							// For the today extension, the main app is just a single test.
							// This is because running the today extension will not wake up the device,
							// nor will it close & reopen the today app (but launching the main app
							// will do both of these things, preparing the device for launching the today extension).

							AppRunner todayRunner = new AppRunner
							{
								Harness = Harness,
								ProjectFile = TestProject.GetTodayExtension ().Path,
								Target = AppRunnerTarget,
								LogDirectory = LogDirectory,
								MainLog = Logs.Create ($"extension-run-{Device.UDID}-{Timestamp}.log", "Extension run log"),
								DeviceName = Device.Name,
								CompanionDeviceName = CompanionDevice?.Name,
								Configuration = ProjectConfiguration,
							};
							additional_runner = todayRunner;
							await todayRunner.RunAsync ();
							foreach (var log in todayRunner.Logs.Where ((v) => !v.Description.StartsWith ("Extension ", StringComparison.Ordinal)))
								log.Description = "Extension " + log.Description [0].ToString ().ToLower () + log.Description.Substring (1);
							ExecutionResult = todayRunner.Result;

							if (!string.IsNullOrEmpty (todayRunner.FailureMessage))
								FailureMessage = todayRunner.FailureMessage;
						} else {
							ExecutionResult = runner.Result;
						}
					}
				} finally {
					// Uninstall again, so that we don't leave junk behind and fill up the device.
					if (Jenkins.UninstallTestApp) {
						runner.MainLog = uninstall_log;
						var uninstall_result = await runner.UninstallAsync ();
						if (!uninstall_result.Succeeded)
							MainLog.WriteLine ($"Post-run uninstall failed, exit code: {uninstall_result.ExitCode} (this won't affect the test result)");
					} else {
						uninstall_log.WriteLine ($"Post-run uninstall skipped.");
					}

					// Also clean up after us locally.
					if (Harness.InJenkins || Harness.InWrench || (Jenkins.CleanSuccessfulTestRuns && Succeeded))
						await BuildTask.CleanAsync ();
				}
			}
		}

		protected override string XIMode {
			get {
				return "device";
			}
		}
	}

	class RunSimulatorTask : RunXITask<SimDevice>
	{
		public IAcquiredResource AcquiredResource;

		public SimDevice [] Simulators {
			get {
				if (Device == null) {
					return new SimDevice [] { };
				} else if (CompanionDevice == null) {
					return new SimDevice [] { Device };
				} else {
					return new SimDevice [] { Device, CompanionDevice };
				}
			}
		}

		public RunSimulatorTask (XBuildTask build_task, IEnumerable<SimDevice> candidates = null)
			: base (build_task, candidates)
		{
			var project = Path.GetFileNameWithoutExtension (ProjectFile);
			if (project.EndsWith ("-tvos", StringComparison.Ordinal)) {
				AppRunnerTarget = AppRunnerTarget.Simulator_tvOS;
			} else if (project.EndsWith ("-watchos", StringComparison.Ordinal)) {
				AppRunnerTarget = AppRunnerTarget.Simulator_watchOS;
			} else {
				AppRunnerTarget = AppRunnerTarget.Simulator_iOS;
			}
		}

		public async Task FindSimulatorAsync ()
		{
			if (Device != null)
				return;

			var asyncEnumerable = Candidates as IAsyncEnumerable;
			if (asyncEnumerable != null)
				await asyncEnumerable.ReadyTask;

			if (!Candidates.Any ()) {
				ExecutionResult = TestExecutingResult.DeviceNotFound;
				FailureMessage = "No applicable devices found.";
			} else {
				Device = Candidates.First ();
				if (Platform == TestPlatform.watchOS)
					CompanionDevice = Jenkins.Simulators.FindCompanionDevice (Jenkins.SimulatorLoadLog, Device);
			}

		}

		public async Task SelectSimulatorAsync ()
		{
			if (Finished)
				return;
			
			if (!BuildTask.Succeeded) {
				ExecutionResult = TestExecutingResult.BuildFailure;
				return;
			}

			await FindSimulatorAsync ();

			var clean_state = false;//Platform == TestPlatform.watchOS;
			runner = new AppRunner ()
			{
				Harness = Harness,
				ProjectFile = ProjectFile,
				EnsureCleanSimulatorState = clean_state,
				Target = AppRunnerTarget,
				LogDirectory = LogDirectory,
				MainLog = Logs.Create ($"run-{Device.UDID}-{Timestamp}.log", "Run log"),
				Configuration = ProjectConfiguration,
				TimeoutMultiplier = TimeoutMultiplier,
			};
			runner.Simulators = Simulators;
			runner.Initialize ();
		}

		class NondisposedResource : IAcquiredResource
		{
			public IAcquiredResource Wrapped;

			public Resource Resource {
				get {
					return Wrapped.Resource;
				}
			}

			public void Dispose ()
			{
				// Nope, no disposing here.
			}
		}

		Task<IAcquiredResource> AcquireResourceAsync ()
		{
			if (AcquiredResource != null) {
				// We don't own the acquired resource, so wrap it in a class that won't dispose it.
				return Task.FromResult<IAcquiredResource> (new NondisposedResource () { Wrapped = AcquiredResource });
			} else {
				return Jenkins.DesktopResource.AcquireExclusiveAsync ();
			}
		}

		protected override async Task RunTestAsync ()
		{
			Jenkins.MainLog.WriteLine ("Running XI on '{0}' ({2}) for {1}", Device?.Name, ProjectFile, Device?.UDID);

			ExecutionResult = (ExecutionResult & ~TestExecutingResult.InProgressMask) | TestExecutingResult.Running;
			await BuildTask.RunAsync ();
			if (!BuildTask.Succeeded) {
				ExecutionResult = TestExecutingResult.BuildFailure;
				return;
			}
			using (var resource = await NotifyBlockingWaitAsync (AcquireResourceAsync ())) {
				if (runner == null)
					await SelectSimulatorAsync ();
				await runner.RunAsync ();
			}
			ExecutionResult = runner.Result;

			KnownFailure = null;
			if (Jenkins.IsHE0038Error (runner.MainLog))
				KnownFailure = $"<a href='https://github.com/xamarin/maccore/issues/581'>HE0038</a>";
		}

		protected override string XIMode {
			get {
				return "simulator";
			}
		}
	}

	// This class groups simulator run tasks according to the
	// simulator they'll run from, so that we minimize switching
	// between different simulators (which is slow).
	class AggregatedRunSimulatorTask : TestTask
	{
		public IEnumerable<RunSimulatorTask> Tasks;

		// Due to parallelization this isn't the same as the sum of the duration for all the build tasks.
		Stopwatch build_timer = new Stopwatch ();
		public TimeSpan BuildDuration { get { return build_timer.Elapsed; } }

		Stopwatch run_timer = new Stopwatch ();
		public TimeSpan RunDuration { get { return run_timer.Elapsed; } }

		public AggregatedRunSimulatorTask (IEnumerable<RunSimulatorTask> tasks)
		{
			this.Tasks = tasks;
		}

		protected override void PropagateResults ()
		{
			foreach (var task in Tasks) {
				task.ExecutionResult = ExecutionResult;
				task.FailureMessage = FailureMessage;
			}
		}

		protected override async Task ExecuteAsync ()
		{
			if (Tasks.All ((v) => v.Ignored)) {
				ExecutionResult = TestExecutingResult.Ignored;
				return;
			}

			// First build everything. This is required for the run simulator
			// task to properly configure the simulator.
			build_timer.Start ();
			await Task.WhenAll (Tasks.Select ((v) => v.BuildAsync ()).Distinct ());
			build_timer.Stop ();

			var executingTasks = Tasks.Where ((v) => !v.Ignored && !v.Failed);
			if (!executingTasks.Any ()) {
				ExecutionResult = TestExecutingResult.Failed;
				return;
			}

			using (var desktop = await NotifyBlockingWaitAsync (Jenkins.DesktopResource.AcquireExclusiveAsync ())) {
				run_timer.Start ();

				// We need to set the dialog permissions for all the apps
				// before launching the simulator, because once launched
				// the simulator caches the values in-memory.
				foreach (var task in executingTasks) {
					await task.VerifyRunAsync ();
					await task.SelectSimulatorAsync ();
				}

				var devices = executingTasks.First ().Simulators;
				Jenkins.MainLog.WriteLine ("Selected simulator: {0}", devices.Length > 0 ? devices [0].Name : "none");

				foreach (var dev in devices)
					await dev.PrepareSimulatorAsync (Jenkins.MainLog, executingTasks.Select ((v) => v.BundleIdentifier).ToArray ());

				foreach (var task in executingTasks) {
					task.AcquiredResource = desktop;
					try {
						await task.RunAsync ();
					} finally {
						task.AcquiredResource = null;
					}
				}

				foreach (var dev in devices)
					await dev.ShutdownAsync (Jenkins.MainLog);

				await SimDevice.KillEverythingAsync (Jenkins.MainLog);

				run_timer.Stop ();
			}

			if (Tasks.All ((v) => v.Ignored)) {
				ExecutionResult = TestExecutingResult.Ignored;
			} else {
				ExecutionResult = Tasks.Any ((v) => v.Failed) ? TestExecutingResult.Failed : TestExecutingResult.Succeeded;
			}
		}
	}

	// This is a very simple class to manage the general concept of 'resource'.
	// Performance isn't important, so this is very simple.
	// Currently it's only used to make sure everything that happens on the desktop
	// is serialized (Jenkins.DesktopResource), but in the future the idea is to
	// make each connected device a separate resource, which will make it possible
	// to run tests in parallel across devices (and at the same time use the desktop
	// to build the next test project).
	class Resource
	{
		public string Name;
		public string Description;
		ConcurrentQueue<TaskCompletionSource<IAcquiredResource>> queue = new ConcurrentQueue<TaskCompletionSource<IAcquiredResource>> ();
		ConcurrentQueue<TaskCompletionSource<IAcquiredResource>> exclusive_queue = new ConcurrentQueue<TaskCompletionSource<IAcquiredResource>> ();
		int users;
		int max_concurrent_users = 1;
		bool exclusive;

		public int Users => users;
		public int QueuedUsers => queue.Count + exclusive_queue.Count;
		public int MaxConcurrentUsers {
			get {
				return max_concurrent_users;
			}
			set {
				max_concurrent_users = value;
			}
		}

		public Resource (string name, int max_concurrent_users = 1, string description = null)
		{
			this.Name = name;
			this.max_concurrent_users = max_concurrent_users;
			this.Description = description ?? name;
		}

		public Task<IAcquiredResource> AcquireConcurrentAsync ()
		{
			lock (queue) {
				if (!exclusive && users < max_concurrent_users) {
					users++;
					return Task.FromResult<IAcquiredResource> (new AcquiredResource (this));
				} else {
					var tcs = new TaskCompletionSource<IAcquiredResource> (new AcquiredResource (this));
					queue.Enqueue (tcs);
					return tcs.Task;
				}
			}
		}

		public Task<IAcquiredResource> AcquireExclusiveAsync ()
		{
			lock (queue) {
				if (users == 0) {
					users++;
					exclusive = true;
					return Task.FromResult<IAcquiredResource> (new AcquiredResource (this));
				} else {
					var tcs = new TaskCompletionSource<IAcquiredResource> (new AcquiredResource (this));
					exclusive_queue.Enqueue (tcs);
					return tcs.Task;
				}
			}
		}

		void Release ()
		{
			TaskCompletionSource<IAcquiredResource> tcs;

			lock (queue) {
				users--;
				exclusive = false;
				if (queue.TryDequeue (out tcs)) {
					users++;
					tcs.SetResult ((IAcquiredResource) tcs.Task.AsyncState);
				} else if (users == 0 && exclusive_queue.TryDequeue (out tcs)) {
					users++;
					exclusive = true;
					tcs.SetResult ((IAcquiredResource) tcs.Task.AsyncState);
				}
			}
		}

		class AcquiredResource : IAcquiredResource
		{
			Resource resource;

			public AcquiredResource (Resource resource)
			{
				this.resource = resource;
			}

			void IDisposable.Dispose ()
			{
				resource.Release ();
			}

			public Resource Resource { get { return resource; } }
		}
	}

	interface IAcquiredResource : IDisposable
	{
		Resource Resource { get; }
	}

	class Resources
	{
		readonly Resource [] resources;

		public Resources (IEnumerable<Resource> resources)
		{
			this.resources = resources.ToArray ();
		}

		public Task<IAcquiredResource> AcquireAnyConcurrentAsync ()
		{
			if (resources.Length == 0)
				throw new Exception ("No resources");

			if (resources.Length == 1)
				return resources [0].AcquireConcurrentAsync ();

			// We try to acquire every resource
			// When the first one succeeds, we set the result to true
			// We immediately release any other resources we acquire.
			var tcs = new TaskCompletionSource<IAcquiredResource> ();
			for (int i = 0; i < resources.Length; i++) {
				resources [i].AcquireConcurrentAsync ().ContinueWith ((v) =>
				{
					var ar = v.Result;
					if (!tcs.TrySetResult (ar))
						ar.Dispose ();
				});
			}

			return tcs.Task;
		}
	}

	public enum TestPlatform
	{
		None,
		All,

		iOS,
		iOS_Unified,
		iOS_Unified32,
		iOS_Unified64,
		iOS_TodayExtension64,
		tvOS,
		watchOS,
		watchOS_32,
		watchOS_64_32,

		Mac,
		Mac_Modern,
		Mac_Full,
		Mac_System,
	}

	[Flags]
	public enum TestExecutingResult
	{
		NotStarted = 0,
		InProgress = 0x1,
		Finished   = 0x2,
		Waiting    = 0x4,
		StateMask  = NotStarted + InProgress + Waiting + Finished,

		// In progress state
		Building         =   0x10 + InProgress,
		BuildQueued      =   0x10 + InProgress + Waiting,
		Built            =   0x20 + InProgress,
		Running          =   0x40 + InProgress,
		RunQueued        =   0x40 + InProgress + Waiting,
		InProgressMask   =   0x10 + 0x20 + 0x40,

		// Finished results
		Succeeded        =  0x100 + Finished,
		Failed           =  0x200 + Finished,
		Ignored          =  0x400 + Finished,
		DeviceNotFound   =  0x800 + Finished,

		// Finished & Failed results
		Crashed          = 0x1000 + Failed,
		TimedOut         = 0x2000 + Failed,
		HarnessException = 0x4000 + Failed,
		BuildFailure     = 0x8000 + Failed,

		// Other results
		BuildSucceeded   = 0x10000 + Succeeded,
	}
}
