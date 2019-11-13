using ObjCRuntime;
using Foundation;
using CoreFoundation;
using System;

namespace SystemExtenisons {
	[Mac (10,15)]
	[Native]
	public enum OSSystemExtensionErrorCode : long
	{
		Unknown = 1,
		MissingEntitlement,
		UnsupportedParentBundleLocation,
		ExtensionNotFound,
		ExtensionMissingIdentifier,
		DuplicateExtensionIdentifer,
		UnknownExtensionCategory,
		CodeSignatureInvalid,
		ValidationFailed,
		ForbiddenBySystemPolicy,
		RequestCanceled,
		RequestSuperseded,
		AuthorizationRequired
	}

	[Mac (10,15)]
	[Native]
	public enum OSSystemExtensionReplacementAction : long
	{
		Cancel,
		Replace
	}

	[Mac (10,15)]
	[Native]
	public enum OSSystemExtensionRequestResult : long
	{
		Completed,
		WillCompleteAfterReboot
	}
}
