#if __MACOS__
#define MONOMAC
#endif
#if __UNIFIED__
#define XAMCORE_2_0
#endif

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Reflection.Emit;

#if XAMCORE_2_0
using AVFoundation;
using CoreBluetooth;
using Foundation;
#if !__TVOS__
using Contacts;
#endif
#if MONOMAC
using AppKit;
using EventKit;
#else
#if !__TVOS__ && !__WATCHOS__
using AddressBook;
#endif
#if !__WATCHOS__
using MediaPlayer;
#endif
using UIKit;
#endif
using ObjCRuntime;
#else
using nint=global::System.Int32;
#if MONOMAC
using MonoMac;
using MonoMac.ObjCRuntime;
using MonoMac.Foundation;
using MonoMac.AppKit;
using MonoMac.AVFoundation;
#else
using MonoTouch.ObjCRuntime;
using MonoTouch.Foundation;
using MonoTouch.UIKit;
#endif
#endif

partial class TestRuntime
{

	[DllImport (Constants.CoreFoundationLibrary)]
	public extern static nint CFGetRetainCount (IntPtr handle);

	[DllImport ("/usr/lib/system/libdyld.dylib")]
	static extern int dyld_get_program_sdk_version ();

	[DllImport ("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr IntPtr_objc_msgSend (IntPtr receiver, IntPtr selector);

	public const string BuildVersion_iOS9_GM = "13A340";

	public static string GetiOSBuildVersion ()
	{
#if __WATCHOS__
		throw new Exception ("Can't get iOS Build version on watchOS.");
#elif MONOMAC
		throw new Exception ("Can't get iOS Build version on OSX.");
#else
		return NSString.FromHandle (IntPtr_objc_msgSend (UIDevice.CurrentDevice.Handle, Selector.GetHandle ("buildVersion")));
#endif
	}

#if MONOMAC
	const int sys1 = 1937339185;
	const int sys2 = 1937339186;
	const int sys3 = 1937339187;

	// Deprecated in OSX 10.8 - but no good alternative is (yet) available
	[System.Runtime.InteropServices.DllImport ("/System/Library/Frameworks/Carbon.framework/Versions/Current/Carbon")]
	static extern int Gestalt (int selector, out int result);

	static Version version;

	public static Version OSXVersion {
		get {
			if (version == null) {
				int major, minor, build;
				Gestalt (sys1, out major);
				Gestalt (sys2, out minor);
				Gestalt (sys3, out build);
				version = new Version (major, minor, build);
			}
			return version;
		}
	}
#endif

	public static Version GetSDKVersion ()
	{
		var v = dyld_get_program_sdk_version ();
		var major = v >> 16;
		var minor = (v >> 8) & 0xFF;
		var build = v & 0xFF;
		return new Version (major, minor, build);
	}

	public static void IgnoreInCI (string message)
	{
		var in_ci = !string.IsNullOrEmpty (Environment.GetEnvironmentVariable ("BUILD_REVISION"));
		if (!in_ci)
			return;
		NUnit.Framework.Assert.Ignore (message);
	}

	static AssemblyName assemblyName = new AssemblyName ("DynamicAssemblyExample"); 
	public static bool CheckExecutingWithInterpreter ()
	{
		// until System.Runtime.CompilerServices.RuntimeFeature.IsSupported("IsDynamicCodeCompiled") returns a valid result, atm it
		// always return true, try to build an object of a class that should fail without introspection, and catch the exception to do the
		// right thing
		try {
			AssemblyBuilder ab = AppDomain.CurrentDomain.DefineDynamicAssembly (assemblyName, AssemblyBuilderAccess.RunAndSave);
			return true;
		} catch (PlatformNotSupportedException) {
			// we do not have the interpreter, lets continue
			return false;
		}
	}

	public static void AssertXcodeVersion (int major, int minor, int build = 0)
	{
		if (CheckXcodeVersion (major, minor, build))
			return;

		NUnit.Framework.Assert.Ignore ("Requires the platform version shipped with Xcode {0}.{1}", major, minor);
	}

	public static void AssertDevice ()
	{
#if !MONOMAC
		if (ObjCRuntime.Runtime.Arch == Arch.SIMULATOR)
			NUnit.Framework.Assert.Ignore ("This test only runs on device.");
#endif
	}

	// This function checks if the current Xcode version is exactly (neither higher nor lower) the requested one.
	public static bool CheckExactXcodeVersion (int major, int minor, int beta = 0)
	{
		// Add the Build number minus the one last character, sometimes Apple releases
		// different builds from the same Beta, for example in Xcode 9 Beta 3 we have
		// 15A5318g on device and 15A5318e on the simulator
		var nineb1 = new {
			Xcode = new { Major = 9, Minor = 0, Beta = 1 },
			iOS = new { Major = 11, Minor = 0, Build = "15A5278" },
			tvOS = new { Major = 11, Minor = 0, Build = "?" },
			macOS = new { Major = 10, Minor = 13, Build = "?" },
			watchOS = new { Major = 4, Minor = 0, Build = "?" },
		};
		var nineb2 = new {
			Xcode = new { Major = 9, Minor = 0, Beta = 2 },
			iOS = new { Major = 11, Minor = 0, Build = "15A5304" },
			tvOS = new { Major = 11, Minor = 0, Build = "?" },
			macOS = new { Major = 10, Minor = 13, Build = "?" },
			watchOS = new { Major = 4, Minor = 0, Build = "?" },
		};
		var nineb3 = new {
			Xcode = new { Major = 9, Minor = 0, Beta = 3 },
			iOS = new { Major = 11, Minor = 0, Build = "15A5318" },
			tvOS = new { Major = 11, Minor = 0, Build = "?" },
			macOS = new { Major = 10, Minor = 13, Build = "?" },
			watchOS = new { Major = 4, Minor = 0, Build = "?" },
		};
		var elevenb5 = new {
			Xcode = new { Major = 11, Minor = 0, Beta = 5 },
			iOS = new { Major = 13, Minor = 0, Build = "17A5547" },
			tvOS = new { Major = 13, Minor = 0, Build = "?" },
			macOS = new { Major = 10, Minor = 15, Build = "?" },
			watchOS = new { Major = 6, Minor = 0, Build = "?" },
		};
		var elevenb6 = new {
			Xcode = new { Major = 11, Minor = 0, Beta = 6 },
			iOS = new { Major = 13, Minor = 0, Build = "17A5565b" },
			tvOS = new { Major = 13, Minor = 0, Build = "?" },
			macOS = new { Major = 10, Minor = 15, Build = "?" },
			watchOS = new { Major = 6, Minor = 0, Build = "?" },
		};

		var versions = new [] {
			nineb1,
			nineb2,
			nineb3,
			elevenb5,
			elevenb6,
		};

		foreach (var v in versions) {
			if (v.Xcode.Major != major)
				continue;
			if (v.Xcode.Minor != minor)
				continue;
			if (v.Xcode.Beta != beta)
				continue;

#if __IOS__
			if (!CheckExactiOSSystemVersion (v.iOS.Major, v.iOS.Minor))
				return false;
			if (v.iOS.Build == "?")
				throw new NotImplementedException ($"Build number for iOS {v.iOS.Major}.{v.iOS.Minor} beta {beta} (candidate: {GetiOSBuildVersion ()})");
			var actual = GetiOSBuildVersion ();
			Console.WriteLine (actual);
			return actual.StartsWith (v.iOS.Build, StringComparison.Ordinal);
#else
			throw new NotImplementedException ();
#endif
		}

		throw new NotImplementedException ($"Build information for Xcode version {major}.{minor} beta {beta} not found");
	}

	public static bool CheckXcodeVersion (int major, int minor, int build = 0)
	{
		switch (major) {
		case 11:
			switch (minor) {
			case 0:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (6, 0);
#elif __TVOS__
				return ChecktvOSSystemVersion (13, 0);
#elif __IOS__
				return CheckiOSSystemVersion (13, 0);
#elif MONOMAC
				return CheckMacSystemVersion (10, 15, 0);
#else
				throw new NotImplementedException ();
#endif
			case 1:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (6, 0);
#elif __TVOS__
				return ChecktvOSSystemVersion (13, 0);
#elif __IOS__
				return CheckiOSSystemVersion (13, 1);
#elif MONOMAC
				return CheckMacSystemVersion (10, 15, 0);
#else
				throw new NotImplementedException ();
#endif
			case 2:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (6, 1);
#elif __TVOS__
				return ChecktvOSSystemVersion (13, 2);
#elif __IOS__
				return CheckiOSSystemVersion (13, 2);
#elif MONOMAC
				return CheckMacSystemVersion (10, 15, 1);
#else
				throw new NotImplementedException ();
#endif
			case 3:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (6, 1, 1);
#elif __TVOS__
				return ChecktvOSSystemVersion (13, 3);
#elif __IOS__
				return CheckiOSSystemVersion (13, 3);
#elif MONOMAC
				return CheckMacSystemVersion (10, 15, 2);
#else
				throw new NotImplementedException ();
#endif
			default:
				throw new NotImplementedException ();
			}
		case 10:
			switch (minor) {
			case 0:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (5, 0);
#elif __TVOS__
				return ChecktvOSSystemVersion (12, 0);
#elif __IOS__
				return CheckiOSSystemVersion (12, 0);
#elif MONOMAC
				return CheckMacSystemVersion (10, 14, 0);
#else
				throw new NotImplementedException ();
#endif
			case 1:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (5, 1);
#elif __TVOS__
				return ChecktvOSSystemVersion (12, 1);
#elif __IOS__
				return CheckiOSSystemVersion (12, 1);
#elif MONOMAC
				return CheckMacSystemVersion (10, 14, 3);
#else
				throw new NotImplementedException ();
#endif
			case 2:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (5, 2);
#elif __TVOS__
				return ChecktvOSSystemVersion (12, 2);
#elif __IOS__
				return CheckiOSSystemVersion (12, 2);
#elif MONOMAC
				return CheckMacSystemVersion (10, 14, 4);
#else
				throw new NotImplementedException ();
#endif
			default:
				throw new NotImplementedException ();
			}
		case 9:
			switch (minor) {
			case 0:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (4, 0);
#elif __TVOS__
				return ChecktvOSSystemVersion (11, 0);
#elif __IOS__
				return CheckiOSSystemVersion (11, 0);
#elif MONOMAC
				return CheckMacSystemVersion (10, 13, 0);
#else
				throw new NotImplementedException ();
#endif
			case 2:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (4, 2);
#elif __TVOS__
				return ChecktvOSSystemVersion (11, 2);
#elif __IOS__
				return CheckiOSSystemVersion (11, 2);
#elif MONOMAC
				return CheckMacSystemVersion (10, 13, 2);
#else
				throw new NotImplementedException ();
#endif
			case 3:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (4, 3);
#elif __TVOS__
				return ChecktvOSSystemVersion (11, 3);
#elif __IOS__
				return CheckiOSSystemVersion (11, 3);
#elif MONOMAC
				return CheckMacSystemVersion (10, 13, 4);
#else
				throw new NotImplementedException ();
#endif
			default:
				throw new NotImplementedException ();
			}
		case 8:
			switch (minor) {
			case 0:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (3, 0);
#elif __TVOS__
				return ChecktvOSSystemVersion (10, 0);
#elif __IOS__
				return CheckiOSSystemVersion (10, 0);
#elif MONOMAC
				return CheckMacSystemVersion (10, 12, 0);
#else
				throw new NotImplementedException ();
#endif
			case 1:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (3, 1);
#elif __TVOS__
				return ChecktvOSSystemVersion (10, 0);
#elif __IOS__
				return CheckiOSSystemVersion (10, 1);
#elif MONOMAC
				return CheckMacSystemVersion (10, 12, 1);
#else
				throw new NotImplementedException ();
#endif
			case 2:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (3, 1);
#elif __TVOS__
				return ChecktvOSSystemVersion (10, 1);
#elif __IOS__
				return CheckiOSSystemVersion (10, 2);
#elif MONOMAC
				return CheckMacSystemVersion (10, 12, 2);
#else
				throw new NotImplementedException ();
#endif
			case 3:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (3, 2);
#elif __TVOS__
				return ChecktvOSSystemVersion (10, 2);
#elif __IOS__
				return CheckiOSSystemVersion (10, 3);
#elif MONOMAC
				return CheckMacSystemVersion (10, 12, 4);
#else
				throw new NotImplementedException ();
#endif
			default:
				throw new NotImplementedException ();
			}
		case 7:
			switch (minor) {
			case 0:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (2, 0);
#elif __TVOS__
				return ChecktvOSSystemVersion (9, 0);
#elif __IOS__
				return CheckiOSSystemVersion (9, 0);
#elif MONOMAC
				return CheckMacSystemVersion (10, 11, 0);
#else
				throw new NotImplementedException ();
#endif
			case 1:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (2, 0);
#elif __TVOS__
				return ChecktvOSSystemVersion (9, 0);
#elif __IOS__
				return CheckiOSSystemVersion (9, 1);
#elif MONOMAC
				return CheckMacSystemVersion (10, 11, 0 /* yep */);
#else
				throw new NotImplementedException ();
#endif
			case 2:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (2, 1);
#elif __TVOS__
				return ChecktvOSSystemVersion (9, 1);
#elif __IOS__
				return CheckiOSSystemVersion (9, 2);
#elif MONOMAC
				return CheckMacSystemVersion (10, 11, 2);
#else
				throw new NotImplementedException ();
#endif
			case 3:
#if __WATCHOS__
				return CheckWatchOSSystemVersion (2, 2);
#elif __TVOS__
				return ChecktvOSSystemVersion (9, 2);
#elif __IOS__
				return CheckiOSSystemVersion (9, 3);
#elif MONOMAC
				return CheckMacSystemVersion (10, 11, 4);
#else
				throw new NotImplementedException ();
#endif
			default:
				throw new NotImplementedException ();
			}
		case 6:
#if __IOS__
			switch (minor) {
			case 0:
				return CheckiOSSystemVersion (8, 0);
			case 1:
				return CheckiOSSystemVersion (8, 1);
			case 2:
				return CheckiOSSystemVersion (8, 2);
			case 3:
				return CheckiOSSystemVersion (8, 3);
			default:
				throw new NotImplementedException ();
			}
#elif __TVOS__ || __WATCHOS__
			return true;
#elif MONOMAC
			switch (minor) {
			case 0:
				return CheckMacSystemVersion (10, 9, 0);
			case 1:
				return CheckMacSystemVersion (10, 10, 0);
			case 2:
				return CheckMacSystemVersion (10, 10, 0);
			case 3:
				return CheckMacSystemVersion (10, 10, 0);
			default:
				throw new NotImplementedException ();
			}
#else
			throw new NotImplementedException ();
#endif
		case 5:
#if __IOS__
			switch (minor) {
			case 0:
				return CheckiOSSystemVersion (7, 0);
			case 1:
				return CheckiOSSystemVersion (7, 1);
			default:
				throw new NotImplementedException ();
			}
#elif __TVOS__ || __WATCHOS__
			return true;
#elif MONOMAC
			switch (minor) {
			case 0:
				// Xcode 5.0.1 ships OSX 10.9 SDK
				return CheckMacSystemVersion (10, build > 0 ? 9 : 8, 0);
			case 1:
				return CheckMacSystemVersion (10, 9, 0);
			default:
				throw new NotImplementedException ();
			}
#else
			throw new NotImplementedException ();
#endif
		case 4:
#if __IOS__
			switch (minor) {
			case 1:
				return true; // iOS 4.3.2
			case 5:
				return CheckiOSSystemVersion (6, 0);
			case 6:
				return CheckiOSSystemVersion (6, 1);
			default:
				throw new NotImplementedException ();
			}
#elif __TVOS__ || __WATCHOS__
			return true;
#elif MONOMAC
			switch (minor) {
			case 1:
				return CheckMacSystemVersion (10, 7, 0);
			case 5:
			case 6:
				return CheckMacSystemVersion (10, 8, 0);
			default:
				throw new NotImplementedException ();
			}
#else
			throw new NotImplementedException ();
#endif
		default:
			throw new NotImplementedException ();
		}
	}

	public static bool CheckSystemVersion (PlatformName platform, int major, int minor, int build = 0, bool throwIfOtherPlatform = true)
	{
		switch (platform) {
		case PlatformName.iOS:
			return CheckiOSSystemVersion (major, minor, throwIfOtherPlatform);
		case PlatformName.MacOSX:
			return CheckMacSystemVersion (major, minor, build, throwIfOtherPlatform);
		case PlatformName.TvOS:
			return ChecktvOSSystemVersion (major, minor, throwIfOtherPlatform);
		case PlatformName.WatchOS:
			return CheckWatchOSSystemVersion (major, minor, throwIfOtherPlatform);
		default:
			throw new Exception ($"Unknown platform: {platform}");
		}
	}

	public static void AssertSystemVersion (PlatformName platform, int major, int minor, int build = 0, bool throwIfOtherPlatform = true)
	{
		switch (platform) {
		case PlatformName.iOS:
			AssertiOSSystemVersion (major, minor, throwIfOtherPlatform);
			break;
		case PlatformName.MacOSX:
			AssertMacSystemVersion (major, minor, build, throwIfOtherPlatform);
			break;
		case PlatformName.TvOS:
			AsserttvOSSystemVersion (major, minor, throwIfOtherPlatform);
			break;
		case PlatformName.WatchOS:
			AssertWatchOSSystemVersion (major, minor, throwIfOtherPlatform);
			break;
		default:
			throw new Exception ($"Unknown platform: {platform}");
		}
	}

	// This method returns true if:
	// system version >= specified version
	// AND
	// sdk version >= specified version
	static bool CheckiOSSystemVersion (int major, int minor, bool throwIfOtherPlatform = true)
	{
#if __IOS__
		return UIDevice.CurrentDevice.CheckSystemVersion (major, minor);
#else
		if (throwIfOtherPlatform)
			throw new Exception ("Can't get iOS System version on other platforms.");
		return true;
#endif
	}

	static void AssertiOSSystemVersion (int major, int minor, bool throwIfOtherPlatform = true)
	{
		if (!CheckiOSSystemVersion (major, minor, throwIfOtherPlatform))
			NUnit.Framework.Assert.Ignore ($"This test requires iOS {major}.{minor}");
	}

	static bool CheckExactiOSSystemVersion (int major, int minor)
	{
#if __IOS__
		var version = Version.Parse (UIDevice.CurrentDevice.SystemVersion);
		return version.Major == major && version.Minor == minor;
#else
		throw new Exception ("Can't get iOS System version on other platforms.");
#endif
	}

	// This method returns true if:
	// system version >= specified version
	// AND
	// sdk version >= specified version
	static bool ChecktvOSSystemVersion (int major, int minor, bool throwIfOtherPlatform = true)
	{
#if __TVOS__
		return UIDevice.CurrentDevice.CheckSystemVersion (major, minor);
#else
		if (throwIfOtherPlatform)
			throw new Exception ("Can't get tvOS System version on other platforms.");
		return true;
#endif
	}

	static void AsserttvOSSystemVersion (int major, int minor, bool throwIfOtherPlatform = true)
	{
		if (!ChecktvOSSystemVersion (major, minor, throwIfOtherPlatform))
			NUnit.Framework.Assert.Ignore ($"This test requires tvOS {major}.{minor}");
	}

	// This method returns true if:
	// system version >= specified version
	// AND
	// sdk version >= specified version
	static bool CheckWatchOSSystemVersion (int major, int minor, bool throwIfOtherPlatform = true)
	{
#if __WATCHOS__
		return WatchKit.WKInterfaceDevice.CurrentDevice.CheckSystemVersion (major, minor);
#else
		if (throwIfOtherPlatform)
			throw new Exception ("Can't get watchOS System version on iOS/tvOS.");
		// This is both iOS and tvOS
		return true;
#endif
	}

	// This method returns true if:
	// system version >= specified version
	// AND
	// sdk version >= specified version
	static bool CheckWatchOSSystemVersion (int major, int minor, int build, bool throwIfOtherPlatform = true)
	{
#if __WATCHOS__
		return WatchKit.WKInterfaceDevice.CurrentDevice.CheckSystemVersion (major, minor, build);
#else
		if (throwIfOtherPlatform)
			throw new Exception ("Can't get watchOS System version on iOS/tvOS.");
		// This is both iOS and tvOS
		return true;
#endif
	}

	static void AssertWatchOSSystemVersion (int major, int minor, bool throwIfOtherPlatform = true)
	{
		if (CheckWatchOSSystemVersion (major, minor, throwIfOtherPlatform))
			return;

		NUnit.Framework.Assert.Ignore ($"This test requires watchOS {major}.{minor}");
	}

	static bool CheckMacSystemVersion (int major, int minor, int build = 0, bool throwIfOtherPlatform = true)
	{
#if MONOMAC
		return OSXVersion >= new Version (major, minor, build);
#else
		if (throwIfOtherPlatform)
			throw new Exception ("Can't get iOS System version on other platforms.");
		return true;
#endif
	}

	static void AssertMacSystemVersion (int major, int minor, int build = 0, bool throwIfOtherPlatform = true)
	{
		if (!CheckMacSystemVersion (major, minor, build, throwIfOtherPlatform))
			NUnit.Framework.Assert.Ignore ($"This test requires macOS {major}.{minor}.{build}");
	}

	public static bool CheckSDKVersion (int major, int minor)
	{
#if __WATCHOS__
		throw new Exception ("Can't get iOS SDK version on WatchOS.");
#elif !MONOMAC
		if (Runtime.Arch == Arch.SIMULATOR || !UIDevice.CurrentDevice.CheckSystemVersion (6, 0)) {
			// dyld_get_program_sdk_version was introduced with iOS 6.0, so don't do the SDK check on older deviecs.
			return true; // dyld_get_program_sdk_version doesn't return what we're looking for on the mac.
		}
#endif

		var sdk = GetSDKVersion ();
		if (sdk.Major > major)
			return true;
		if (sdk.Major == major && sdk.Minor >= minor)
			return true;
		return false;
	}

	public static void IgnoreOnTVOS ()
	{
#if __TVOS__
		NUnit.Framework.Assert.Ignore ("This test is disabled on TVOS.");
#endif
	}

	public static bool IsTVOS {
		get {
#if __TVOS__
			return true;
#else
			return false;
#endif
		}
	}

	public static bool IgnoreTestThatRequiresSystemPermissions ()
	{
		return !string.IsNullOrEmpty (Environment.GetEnvironmentVariable ("DISABLE_SYSTEM_PERMISSION_TESTS"));
	}

	public static void CheckBluetoothPermission (bool assert_granted = false)
	{
		// New in Xcode11
		switch (CBManager.Authorization) {
		case CBManagerAuthorization.NotDetermined:
			if (IgnoreTestThatRequiresSystemPermissions ())
				NUnit.Framework.Assert.Ignore ("This test would show a dialog to ask for permission to use bluetooth.");
			break;
		case CBManagerAuthorization.Denied:
		case CBManagerAuthorization.Restricted:
			if (assert_granted)
				NUnit.Framework.Assert.Fail ("This test requires permission to use bluetooth.");
			break;
		}
	}

#if !MONOMAC && !__TVOS__ && !__WATCHOS__
	public static void RequestCameraPermission (NSString mediaTypeToken, bool assert_granted = false)
	{
		if (AVCaptureDevice.GetAuthorizationStatus (mediaTypeToken) == AVAuthorizationStatus.NotDetermined) {
			if (IgnoreTestThatRequiresSystemPermissions ())
				NUnit.Framework.Assert.Ignore ("This test would show a dialog to ask for permission to access the camera.");

			AVCaptureDevice.RequestAccessForMediaType (mediaTypeToken, (accessGranted) =>
			{
				Console.WriteLine ("Camera permission {0}", accessGranted ? "granted" : "denied");
			});
		}

		switch (AVCaptureDevice.GetAuthorizationStatus (AVMediaType.Video)) {
		case AVAuthorizationStatus.Restricted:
		case AVAuthorizationStatus.Denied:
			if (assert_granted)
				NUnit.Framework.Assert.Fail ("This test requires permission to access the camera.");
			break;
		}
	}
#endif // !!MONOMAC && !__TVOS__ && !__WATCHOS__

#if XAMCORE_2_0 && !__TVOS__
	public static void CheckContactsPermission (bool assert_granted = false)
	{
		switch (CNContactStore.GetAuthorizationStatus (CNEntityType.Contacts)) {
		case CNAuthorizationStatus.NotDetermined:
			if (IgnoreTestThatRequiresSystemPermissions ())
				NUnit.Framework.Assert.Ignore ("This test would show a dialog to ask for permission to access the contacts.");
			// We don't request access here, because there's no global method to request access (an contact store instance is required).
			// Interestingly there is a global method to determine if access has been granted...
			break;
		case CNAuthorizationStatus.Restricted:
		case CNAuthorizationStatus.Denied:
			if (assert_granted)
				NUnit.Framework.Assert.Fail ("This test requires permission to access the contacts.");
			break;
		}
	}

	public static void RequestContactsPermission (bool assert_granted = false)
	{
		switch (CNContactStore.GetAuthorizationStatus (CNEntityType.Contacts)) {
		case CNAuthorizationStatus.NotDetermined:
			if (IgnoreTestThatRequiresSystemPermissions ())
				NUnit.Framework.Assert.Ignore ("This test would show a dialog to ask for permission to access the contacts.");

			// There's a static method to check for permission, but an instance method to ask for permission
			using (var store = new CNContactStore ()) {
				store.RequestAccess (CNEntityType.Contacts, (granted, error) => {
					Console.WriteLine ("Contacts permission {0} (error: {1})", granted ? "granted" : "denied", error);
				});
			}
			break;
		}

		CheckContactsPermission (assert_granted);
	}

#endif // XAMCORE_2_0

#if !MONOMAC && !__TVOS__ && !__WATCHOS__
	public static void CheckAddressBookPermission (bool assert_granted = false)
	{
		switch (ABAddressBook.GetAuthorizationStatus ()) {
		case ABAuthorizationStatus.NotDetermined:
			if (IgnoreTestThatRequiresSystemPermissions ())
				NUnit.Framework.Assert.Ignore ("This test would show a dialog to ask for permission to access the address book.");
			// We don't request access here, because there's no global method to request access (an addressbook instance is required).
			// Interestingly there is a global method to determine if access has been granted...
			break;
		case ABAuthorizationStatus.Restricted:
		case ABAuthorizationStatus.Denied:
			if (assert_granted)
				NUnit.Framework.Assert.Fail ("This test requires permission to access the address book.");
			break;
		}
	}
#endif // !MONOMAC && !__TVOS__ && !__WATCHOS__

#if !__WATCHOS__
	public static void RequestMicrophonePermission (bool assert_granted = false)
	{
#if MONOMAC
		// It looks like macOS does not restrict access to the microphone.
#elif __TVOS__
		// tvOS doesn't have a (developer-accessible) microphone, but it seems to have API that requires developers 
		// to request microphone access on other platforms (which means that it makes sense to both run those tests
		// on tvOS (because the API's there) and to request microphone access (because that's required on other platforms).
#else
		if (!CheckXcodeVersion (6, 0))
			return; // The API to check/request permission isn't available in earlier versions, the dialog will just pop up.

		if (AVAudioSession.SharedInstance ().RecordPermission == AVAudioSessionRecordPermission.Undetermined) {
			if (IgnoreTestThatRequiresSystemPermissions ())
				NUnit.Framework.Assert.Ignore ("This test would show a dialog to ask for permission to access the microphone.");

			AVAudioSession.SharedInstance ().RequestRecordPermission ((bool granted) =>
			{
				Console.WriteLine ("Microphone permission {0}", granted ? "granted" : "denied");
			});
		}

		switch (AVAudioSession.SharedInstance ().RecordPermission) { // iOS 8+
		case AVAudioSessionRecordPermission.Denied:
			if (assert_granted)
				NUnit.Framework.Assert.Fail ("This test requires permission to access the microphone.");
			break;
		}
#endif // !MONOMAC && !__TVOS__
	}
#endif // !__WATCHOS__

#if !MONOMAC && !__TVOS__ && !__WATCHOS__
	public static void RequestMediaLibraryPermission (bool assert_granted = false)
	{
		if (!CheckXcodeVersion (7, 3)) {
			if (IgnoreTestThatRequiresSystemPermissions ())
				NUnit.Framework.Assert.Ignore ("This test might show a dialog to ask for permission to access the media library, but the API to check if a dialog is required (or to request permission) is not available in this OS version.");
			return;
		}

		if (MPMediaLibrary.AuthorizationStatus == MPMediaLibraryAuthorizationStatus.NotDetermined) {
			if (IgnoreTestThatRequiresSystemPermissions ())
				NUnit.Framework.Assert.Ignore ("This test would show a dialog to ask for permission to access the media library.");

			MPMediaLibrary.RequestAuthorization ((access) =>
			{
				Console.WriteLine ("Media library permission: {0}", access);
			});
		}

		switch (MPMediaLibrary.AuthorizationStatus) {
		case MPMediaLibraryAuthorizationStatus.Denied:
		case MPMediaLibraryAuthorizationStatus.Restricted:
			if (assert_granted)
				NUnit.Framework.Assert.Fail ("This test requires permission to access the media library.");
			break;
		}
	}
#endif // !MONOMAC && !__TVOS__

#if __MACOS__
	public static void RequestEventStorePermission (EKEntityType entityType, bool assert_granted = false)
	{
		TestRuntime.AssertMacSystemVersion (10, 9, throwIfOtherPlatform: false);

		var status = EKEventStore.GetAuthorizationStatus (entityType);
		Console.WriteLine ("EKEventStore.GetAuthorizationStatus ({1}): {0}", status, entityType);
		switch (status) {
		case EKAuthorizationStatus.Authorized:
		case EKAuthorizationStatus.Restricted:
			return;
		case EKAuthorizationStatus.NotDetermined:
			// There's an instance method on EKEventStore to request permission,
			// but creating the instance can end up blocking the app showing a permission dialog...
			// (on Mavericks at least)
			if (TestRuntime.CheckMacSystemVersion (10, 10))
				return; // Crossing fingers that this won't hang.
			NUnit.Framework.Assert.Ignore ("This test requires permission to access events, but there's no API to request access without potentially showing dialogs.");
			break;
		case EKAuthorizationStatus.Denied:
			if (assert_granted)
				NUnit.Framework.Assert.Ignore ("This test requires permission to access events.");
			break;
		}
	}
#endif

#if __UNIFIED__
#if __MACOS__
	public static global::CoreGraphics.CGColor GetCGColor (NSColor color)
#else
	public static global::CoreGraphics.CGColor GetCGColor (UIColor color)
#endif
	{
#if __MACOS__
		var components = new nfloat [color.ComponentCount];
		color.GetComponents (out components);
		NSApplication.CheckForIllegalCrossThreadCalls = false;
		var cs = color.ColorSpace.ColorSpace;
		NSApplication.CheckForIllegalCrossThreadCalls = true;
		return new global::CoreGraphics.CGColor (cs, components);
#else
		return color.CGColor;
#endif
	}
#endif // __UNIFIED__
	
	// Determine if linkall was enabled by checking if an unused class in this assembly is still here.
	static bool? link_all;
	public static bool IsLinkAll {
		get {
			if (!link_all.HasValue)
				link_all = typeof (TestRuntime).Assembly.GetType (typeof (TestRuntime).FullName + "+LinkerSentinel") == null;
			return link_all.Value;
		}
	}
	class LinkerSentinel { }

	public static bool IsOptimizeAll {
		get {
#if OPTIMIZEALL
			return true;
#else
			return false;
#endif
		}
	}
}
