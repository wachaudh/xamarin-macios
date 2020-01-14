// Copyright 2015 Xamarin Inc. All rights reserved.
// Copyright Microsoft Corp.
using System;
using System.Collections.Generic;
using IKVM.Reflection;
using Type = IKVM.Reflection.Type;

using Foundation;
using ObjCRuntime;

public partial class Generator {

	List<string> filters = new List<string> ();

	string GetVisibility (MethodAttributes attributes)
	{
		if ((attributes & MethodAttributes.FamORAssem) == MethodAttributes.FamORAssem)
			return "protected internal ";
		if ((attributes & MethodAttributes.Public) == MethodAttributes.Public)
			return "public ";
		if ((attributes & MethodAttributes.Family) == MethodAttributes.Family)
			return "protected ";
		return String.Empty;
	}

	public void GenerateFilter (Type type)
	{
		var is_abstract = AttributeManager.HasAttribute<AbstractAttribute> (type);
		var filter = AttributeManager.GetCustomAttribute<CoreImageFilterAttribute> (type);
		var base_type = AttributeManager.GetCustomAttribute<BaseTypeAttribute> (type);
		var type_name = type.Name;
		var native_name = base_type.Name ?? type_name;
		var base_name = base_type.BaseType.Name;

		// internal static CIFilter FromName (string filterName, IntPtr handle)
		filters.Add (type_name);

		// filters are now exposed as protocols so we need to conform to them
		var interfaces = String.Empty;
		foreach (var i in type.GetInterfaces ()) {
			interfaces += $", I{i.Name}";
		}

		// type declaration
		print ("public{0} partial class {1} : {2}{3} {{",
			is_abstract ? " abstract" : String.Empty,
			type_name, base_name, interfaces);
		print ("");
		indent++;

		// default constructor - if type is not abstract
		string v;
		if (!is_abstract) {
			v = GetVisibility (filter.DefaultCtorVisibility);
			if (v.Length > 0) {
				print_generated_code ();
				print ("{0}{1} () : base (\"{2}\")", v, type.Name, native_name);
				PrintEmptyBody ();
			}
		}

		// IntPtr constructor - always present
		var intptrctor_visibility = filter.IntPtrCtorVisibility;
		if (intptrctor_visibility == MethodAttributes.PrivateScope) {
			// since it was not generated code we never fixed the .ctor(IntPtr) visibility for unified
			if (XamcoreVersion >= 3) {
				intptrctor_visibility = MethodAttributes.FamORAssem;
			} else {
				intptrctor_visibility = MethodAttributes.Public;
			}
		}
		print_generated_code ();
		print ("{0}{1} (IntPtr handle) : base (handle)", GetVisibility (intptrctor_visibility), type_name);
		PrintEmptyBody ();

		// NSObjectFlag constructor - always present (needed to implement NSCoder for subclasses)
		print_generated_code ();
		print ("[EditorBrowsable (EditorBrowsableState.Advanced)]");
		print ("protected {0} (NSObjectFlag t) : base (t)", type_name);
		PrintEmptyBody ();

		// NSCoder constructor - all filters conforms to NSCoding
		print_generated_code ();
		print ("[EditorBrowsable (EditorBrowsableState.Advanced)]");
		print ("[Export (\"initWithCoder:\")]");
		print ("public {0} (NSCoder coder) : base (NSObjectFlag.Empty)", type_name);
		print ("{");
		indent++;
		print ("IntPtr h;");
		print ("if (IsDirectBinding) {");
		indent++;
		print ("h = global::{0}.Messaging.IntPtr_objc_msgSend_IntPtr (this.Handle, Selector.GetHandle (\"initWithCoder:\"), coder.Handle);", ns.CoreObjCRuntime);
		indent--;
		print ("} else {");
		indent++;
		print ("h = global::{0}.Messaging.IntPtr_objc_msgSendSuper_IntPtr (this.SuperHandle, Selector.GetHandle (\"initWithCoder:\"), coder.Handle);", ns.CoreObjCRuntime);
		indent--;
		print ("}");
		print ("InitializeHandle (h, \"initWithCoder:\");");
		indent--;
		print ("}");
		print ("");

		// string constructor
		// default is protected (for abstract) but backward compatibility (XAMCORE_2_0) requires some hacks
		v = GetVisibility (filter.StringCtorVisibility);
		if (is_abstract && (v.Length == 0))
			v = "protected ";
		if (v.Length > 0) {
			print_generated_code ();
			print ("{0} {1} (string name) : base (CreateFilter (name))", v, type_name);
			PrintEmptyBody ();
		}

		// properties
		GenerateProperties (type);

		// protocols
		GenerateProtocolProperties (type, new HashSet<string> ());

		indent--;
		print ("}");

		// namespace closing (it's optional to use namespaces even if it's a bad practice, ref #35283)
		if (indent > 0) {
			indent--;
			print ("}");
		}
	}

	void GenerateProtocolProperties (Type type, HashSet<string> processed)
	{
		foreach (var i in type.GetInterfaces ()) {
			if (!IsProtocolInterface (i, false, out var protocol))
				continue;

			// the same protocol can be included more than once (interfaces) - but we must generate only once
			var pname = i.Name;
			if (processed.Contains (pname))
				continue;
			processed.Add (pname);

			print ("");
			print ($"// {pname} protocol members ");
			GenerateProperties (i);

			// also include base interfaces/protocols
			GenerateProtocolProperties (i, processed);
		}
	}

	void GenerateProperties (Type type)
	{
		foreach (var p in type.GetProperties (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
			if (p.IsUnavailable (this))
				continue;
			if (AttributeManager.HasAttribute<StaticAttribute> (p))
				continue;
			
			print ("");
			PrintPropertyAttributes (p);
			print_generated_code ();

			var ptype = p.PropertyType.Name;
			// keep C# names as they are reserved keywords (e.g. Boolean also exists in OpenGL for Mac)
			switch (ptype) {
			case "Boolean":
				ptype = "bool";
				break;
			case "Int32":
				ptype = "int";
				break;
			case "Single":
				ptype = "float";
				break;
			case "String":
				ptype = "string";
				break;
			// adding `using ImageIO;` would lead to `error CS0104: 'CGImageProperties' is an ambiguous reference between 'CoreGraphics.CGImageProperties' and 'ImageIO.CGImageProperties'`
			case "CGImageMetadata":
				ptype = "ImageIO.CGImageMetadata";
				break;
			}
			print ("public {0} {1} {{", ptype, p.Name);
			indent++;

			// an export will be present (only) if it's defined in a protocol
			var export = AttributeManager.GetCustomAttribute<ExportAttribute> (p);

			var name = AttributeManager.GetCustomAttribute<CoreImageFilterPropertyAttribute> (p)?.Name;
			// we can skip the name when it's identical to a protocol selector
			if (name == null) {
				if (export == null)
					throw new BindingException (1074, true, type.Name, p.Name);

				var sel = export.Selector;
				if (sel.StartsWith ("input", StringComparison.Ordinal))
					name = sel;
				else
					name = "input" + Capitalize (sel);
			}

			if (p.GetGetMethod () != null) {
				PrintFilterExport (p, export, setter: false);
				GenerateFilterGetter (ptype, name);
			}
			if (p.GetSetMethod () != null) {
				PrintFilterExport (p, export, setter: true);
				GenerateFilterSetter (ptype, name);
			}
			
			indent--;
			print ("}");
		}
	}

	void PrintFilterExport (PropertyInfo p, ExportAttribute export, bool setter)
	{
		if (export == null)
			return;

		var selector = export.Selector;
		if (setter)
			selector = "set" + Capitalize (selector) + ":";

		if (export.ArgumentSemantic != ArgumentSemantic.None && !p.PropertyType.IsPrimitive)
			print ($"[Export (\"{selector}\", ArgumentSemantic.{export.ArgumentSemantic})]");
		else
			print ($"[Export (\"{selector}\")]");
	}

	void GenerateFilterGetter (string propertyType, string propertyName)
	{
		print ("get {");
		indent++;
		switch (propertyType) {
		case "bool":
			print ("return GetBool (\"{0}\");", propertyName);
			break;
		// NSValue should not be added - the only case that is used (right now) if for CGAffineTransform
		case "CGAffineTransform":
			print ("var val = ValueForKey (\"{0}\");", propertyName);
			print ("var nsv = (val as NSValue);");
			print ("if (nsv != null)");
			indent++;
			print ("return nsv.CGAffineTransformValue;");
			indent--;
			print ("return CGAffineTransform.MakeIdentity ();");
			break;
		// NSObject should not be added
		// NSNumber should not be added - it should be bound as a float (common), int32 or bool
		case "AVCameraCalibrationData":
		case "CGColorSpace":
		case "CGImage":
		case "ImageIO.CGImageMetadata":
		case "CIBarcodeDescriptor":
		case "MLModel":
		case "NSAttributedString":
		case "NSData":
			print ("return Runtime.GetINativeObject <{0}> (GetHandle (\"{1}\"), false);", propertyType, propertyName);
			break;
		case "CIColor":
		case "CIImage":
		case "CIVector":
			print ($"return ValueForKey (\"{propertyName}\") as {propertyType};");
			break;
		case "CGPoint":
			print ("return GetPoint (\"{0}\");", propertyName);
			break;
		case "CGRect":
			print ("return GetRect (\"{0}\");", propertyName);
			break;
		case "float":
			print ("return GetFloat (\"{0}\");", propertyName);
			break;
		case "int":
			print ("return GetInt (\"{0}\");", propertyName);
			break;
		case "nint":
			print ("return GetNInt (\"{0}\");", propertyName);
			break;
		case "string":
			// NSString should not be added - it should be bound as a string
			print ("return (string) (ValueForKey (\"{0}\") as NSString);", propertyName);
			break;
		case "CIVector[]":
			print ($"var handle = GetHandle (\"{propertyName}\");");
			print ("return NSArray.ArrayFromHandle<CIVector> (handle);");
			break;
		default:
			throw new BindingException (1075, true, propertyType);
		}
		indent--;
		print ("}");
	}

	void GenerateFilterSetter (string propertyType, string propertyName)
	{
		print ("set {");
		indent++;
		switch (propertyType) {
		case "bool":
			print ("SetBool (\"{0}\", value);", propertyName);
			break;
		// NSValue should not be added - the only case that is used (right now) if for CGAffineTransform
		case "CGAffineTransform":
			print ("SetValue (\"{0}\", NSValue.FromCGAffineTransform (value));", propertyName);
			break;
		// NSNumber should not be added - it should be bound as a int or a float
		case "float":
			print ("SetFloat (\"{0}\", value);", propertyName);
			break;
		case "int":
			print ("SetInt (\"{0}\", value);", propertyName);
			break;
		case "nint":
			print ("SetNInt (\"{0}\", value);", propertyName);
			break;
		// NSObject should not be added
		case "AVCameraCalibrationData":
		case "CGColorSpace":
		case "CIBarcodeDescriptor":
		case "CGImage":
		case "ImageIO.CGImageMetadata":
			print ($"SetHandle (\"{propertyName}\", value.GetHandle ());");
			break;
		case "CGPoint":
		case "CGRect":
		case "CIColor":
		case "CIImage":
		case "CIVector":
		case "MLModel":
		case "NSAttributedString":
		case "NSData":
		// NSNumber should not be added - it should be bound as a int or a float
			print ("SetValue (\"{0}\", value);", propertyName);
			break;
		case "string":
			// NSString should not be added - it should be bound as a string
			print ("using (var ns = new NSString (value))");
			indent++;
			print ("SetValue (\"{0}\", ns);", propertyName);
			indent--;
			break;
		case "CIVector[]":
			print ("if (value == null) {");
			indent++;
			print ($"SetHandle (\"{propertyName}\", IntPtr.Zero);");
			indent--;
			print ("} else {");
			indent++;
			print ("using (var array = NSArray.FromNSObjects (value))");
			indent++;
			print ($"SetHandle (\"{propertyName}\", array.GetHandle ());");
			indent--;
			indent--;
			print ("}");
			break;
		default:
			throw new BindingException (1075, true, propertyType);
		}
		indent--;
		print ("}");
	}

	void PrintEmptyBody ()
	{
		print ("{");
		print ("}");
		print ("");
	}
}
