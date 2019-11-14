using ObjCRuntime;
using Foundation;
using CoreFoundation;
using System;

namespace OSLog {
	
	/* [Flags, Mac (10,15)] */
	[Mac (10,15)]
	[Native]
	public enum OSLogEnumeratorOptions : ulong
	{
		Reverse = 0x1
	}
	
	[Mac (10,15)]
	[BaseType (typeof(NSObject))]
	interface OSLogEntry
	{
		[Export ("composedMessage")]
		string ComposedMessage { get; }

		[Export ("date")]
		NSDate Date { get; }

		[Export ("storeCategory")]
		EntryCategory StoreCategory { get; }
	}

	[Mac (10,15)]
	[Protocol]
	interface OSLogEntryFromProcess
	{
		[Abstract]
		[Export ("activityIdentifier")]
		ulong ActivityIdentifier { get; }

		[Abstract]
		[Export ("process")]
		string Process { get; }

		[Abstract]
		[Export ("processIdentifier")]
		int ProcessIdentifier { get; }

		[Abstract]
		[Export ("sender")]
		string Sender { get; }

		[Abstract]
		[Export ("threadIdentifier")]
		ulong ThreadIdentifier { get; }
	}

	[Mac (10,15)]
	[Protocol]
	interface OSLogEntryWithPayload
	{
		[Abstract]
		[Export ("category")]
		string Category { get; }

		[Abstract]
		[Export ("components")]
		OSLogMessageComponent[] Components { get; }

		[Abstract]
		[Export ("formatString")]
		string FormatString { get; }

		[Abstract]
		[Export ("subsystem")]
		string Subsystem { get; }
	}

	[Mac (10,15)]
	[BaseType (typeof(OSLogEntry))]
	interface OSLogEntryActivity : OSLogEntryFromProcess
	{
		[Export ("parentActivityIdentifier")]
		ulong ParentActivityIdentifier { get; }
	}


	[Mac (10,15)]
	[BaseType (typeof(OSLogEntry))]
	interface OSLogEntryLog : OSLogEntryFromProcess, OSLogEntryWithPayload
	{
		[Export ("level")]
		LogLevel Level { get; }
	}


	[Mac (10,15)]
	[BaseType (typeof(OSLogEntry))]
	interface OSLogEntrySignpost : OSLogEntryFromProcess, OSLogEntryWithPayload
	{
		[Export ("signpostIdentifier")]
		ulong SignpostIdentifier { get; }

		[Export ("signpostName")]
		string SignpostName { get; }

		[Export ("signpostType")]
		EntrySignpostType SignpostType { get; }
	}


	[Mac (10,15)]
	[BaseType (typeof(NSObject))]
	interface OSLogMessageComponent
	{
		[Export ("formatSubstring")]
		string FormatSubstring { get; }

		[Export ("placeholder")]
		string Placeholder { get; }

		[Export ("argumentCategory")]
		ComponentArgumentCategory ArgumentCategory { get; }

		[NullAllowed, Export ("argumentDataValue")]
		NSData ArgumentDataValue { get; }

		[Export ("argumentDoubleValue")]
		double ArgumentDoubleValue { get; }

		[Export ("argumentInt64Value")]
		long ArgumentInt64Value { get; }

		[NullAllowed, Export ("argumentNumberValue")]
		NSNumber ArgumentNumberValue { get; }

		[NullAllowed, Export ("argumentStringValue")]
		string ArgumentStringValue { get; }

		[Export ("argumentUInt64Value")]
		ulong ArgumentUInt64Value { get; }
	}

	[Mac (10,15)]
	[BaseType (typeof(NSEnumerator))]
	[DisableDefaultCtor]
	interface OSLogEnumerator { }

	[Mac (10,15)]
	[BaseType (typeof(NSObject))]
	[DisableDefaultCtor]
	interface OSLogPosition { }

	[Mac (10,15)]
	[BaseType (typeof(NSObject))]
	[DisableDefaultCtor]
	interface OSLogStore
	{
		[Static]
		[Export ("localStoreAndReturnError:")]
		[return: NullAllowed]
		OSLogStore LocalStoreAndReturnError ([NullAllowed] out NSError error);

		[Static]
		[Export ("storeWithURL:error:")]
		[return: NullAllowed]
		OSLogStore StoreWithURL (NSUrl url, [NullAllowed] out NSError error);

		[Export ("entriesEnumeratorWithOptions:position:predicate:error:")]
		[return: NullAllowed]
		OSLogEnumerator EntriesEnumeratorWithOptions (OSLogEnumeratorOptions options, [NullAllowed] NSObject position, [NullAllowed] NSPredicate predicate, [NullAllowed] out NSError error);

		[Export ("entriesEnumeratorAndReturnError:")]
		[return: NullAllowed]
		OSLogEnumerator EntriesEnumeratorAndReturnError ([NullAllowed] out NSError error);

		[Export ("positionWithDate:")]
		OSLogPosition PositionWithDate (NSDate date);

		[Export ("positionWithTimeIntervalSinceEnd:")]
		OSLogPosition PositionWithTimeIntervalSinceEnd (double seconds);

		[Export ("positionWithTimeIntervalSinceLatestBoot:")]
		OSLogPosition PositionWithTimeIntervalSinceLatestBoot (double seconds);
	}
}
